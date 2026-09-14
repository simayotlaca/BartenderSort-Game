using BartenderSort.Core;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(520)]
    public sealed class BartenderRoundFeedbackPresenter : MonoBehaviour
    {
        private const int FailureContinueCoinCost =
            BartenderProgressService.FailureContinueCoinCost;

        private const float LifeLostCueDelay = 1.45f;
        private const float LifeLostVolume = 0.85f;
        private const float LifeRegainedVolume = 0.80f;
        private const float LifeRefillVolume = 0.90f;
        private const float FailWaitMusicCommandFadeSeconds = 0.30f;
        private const double EntranceTimeoutGraceSeconds = 2d;

        [SerializeField] private BartenderSession session;
        [SerializeField] private BartenderLevelController controller;

        [Header("Authored UI")]
        [SerializeField] private Canvas feedbackCanvas;
        [SerializeField] private BartenderResultPopupView successView;
        [SerializeField] private BartenderFailurePopupView failureView;
        [SerializeField] private BartenderMoreLivesPopupView moreLivesView;

        [Header("Motion")]
        [SerializeField, Min(0.01f)] private float scrimFadeDuration = 0.22f;
        [SerializeField, Min(0.01f)] private float cardEntranceDuration = 0.40f;
        [SerializeField, Range(0.5f, 1f)] private float cardStartScale = 0.78f;
        [SerializeField, Min(0f)] private float cardStartOffset = 54f;
        [SerializeField, Min(760f)] private float successCardStartOffset = 1080f;

        private CanvasGroup canvasGroup;
        private Image scrim;
        private Image flash;
        private RectTransform card;
        private readonly BsPurchaseOverlayStateMachine moreLivesFlow =
            new BsPurchaseOverlayStateMachine();
        private Button actionButton;
        private Button paidContinueButton;
        private Button closeButton;
        private Button rewardButton;
        private Sequence activeSequence;
        private double entranceDeadline;
        private int presentationRevision;
        private BsRoundOutcome shownOutcome;
        private BartenderTerminalPresentationReceipt shownPresentation;
        private bool terminalCommandPending;
        private bool lifeRefillPending;
        private BartenderTerminalCommandReceipt pendingTerminalCommandReceipt;
        private int lastKnownLives = -1;
        private bool lifeLostPending;
        private Tween lifeLostCue;

        private void Awake()
        {
            ResolveDependencies();
            BindAuthoredViews();
        }

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
            lastKnownLives = BartenderProgressService.Lives;

            if (session != null
                && session.TryGetCurrentTerminalPresentation(
                    out BartenderTerminalPresentationReceipt receipt))
            {
                if (receipt.Outcome == BsRoundOutcome.Won)
                    BsAudio.Instance?.PlayResult(BsSfx.Win);
                else if (receipt.Outcome == BsRoundOutcome.Failed)
                    BsAudio.Instance?.ResumeFailWaitMusic();
                Present(receipt);
            }
            else if (session != null && session.TryGetPendingTerminalCommand(
                         out BartenderTerminalPresentationReceipt pendingPresentation,
                         out BartenderTerminalCommandReceipt pendingCommand))
            {
                Present(pendingPresentation, pendingCommand);
            }
        }

        private void OnDisable()
        {
            Unsubscribe();
            ResetPresentation();
        }

        private void Update()
        {
            Sequence sequence = activeSequence;
            if (sequence == null) return;
            if (!IsShownPresentationCurrent() || feedbackCanvas == null
                || !feedbackCanvas.gameObject.activeInHierarchy
                || canvasGroup == null || !canvasGroup.gameObject.activeInHierarchy)
            {
                ResetPresentation();
                return;
            }
            if (Time.realtimeSinceStartupAsDouble >= entranceDeadline)
                InterruptEntrance(sequence, shownPresentation, true);
        }

        private void ResolveDependencies()
        {
            if (session == null) session = GetComponent<BartenderSession>();
            if (controller == null && session != null) controller = session.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
        }

        private void Subscribe()
        {
            if (session != null)
            {
                session.TerminalReady -= HandleTerminalReady;
                session.TerminalReady += HandleTerminalReady;
                session.TerminalCommandCompleted -= HandleTerminalCommandCompleted;
                session.TerminalCommandCompleted += HandleTerminalCommandCompleted;
            }
            if (controller != null)
            {
                controller.LevelLoaded -= HandleLevelLoaded;
                controller.LevelLoaded += HandleLevelLoaded;
            }
            BartenderProgressService.LivesChanged -= HandleLivesChanged;
            BartenderProgressService.LivesChanged += HandleLivesChanged;
            BartenderProgressService.CoinsChanged -= HandleCoinsChanged;
            BartenderProgressService.CoinsChanged += HandleCoinsChanged;
            BartenderProgressService.LifeTimerChanged -= HandleLifeTimerChanged;
            BartenderProgressService.LifeTimerChanged += HandleLifeTimerChanged;
        }

        private void Unsubscribe()
        {
            if (session != null)
            {
                session.TerminalReady -= HandleTerminalReady;
                session.TerminalCommandCompleted -= HandleTerminalCommandCompleted;
            }
            if (controller != null) controller.LevelLoaded -= HandleLevelLoaded;
            BartenderProgressService.LivesChanged -= HandleLivesChanged;
            BartenderProgressService.CoinsChanged -= HandleCoinsChanged;
            BartenderProgressService.LifeTimerChanged -= HandleLifeTimerChanged;
        }

        private void HandleTerminalReady(
            BartenderTerminalPresentationReceipt receipt)
        {
            if (session == null
                || !session.IsTerminalPresentationCurrent(receipt)) return;
            Present(receipt);
        }

        private void HandleLevelLoaded(BsLevel level) => ResetPresentation();

        private void HandleLivesChanged(int lives)
        {
            int previous = lastKnownLives;
            lastKnownLives = lives;
            if (previous >= 0 && lives != previous) PlayLifeChangeCue(previous, lives);

            if (lives > 0 && moreLivesFlow.State == BsPurchaseOverlayState.Visible)
            {
                CloseMoreLivesOffer();
                return;
            }
            RefreshMoreLivesView();
            RefreshFailureButtons();
        }

        private void PlayLifeChangeCue(int previous, int lives)
        {
            if (lives < previous)
            {
                lifeLostPending = true;
                return;
            }

            if (lives == previous + 1
                && moreLivesFlow.State == BsPurchaseOverlayState.Visible)
                BsAudio.Instance?.Play(BsSfx.LifeRegained, LifeRegainedVolume);
        }

        private void HandleCoinsChanged(int coins)
        {
            RefreshMoreLivesView();
            RefreshFailureButtons();
        }

        private void HandleLifeTimerChanged(System.TimeSpan remaining) =>
            RefreshMoreLivesView();

        private void RefreshFailureButtons()
        {
            if (shownOutcome != BsRoundOutcome.Failed || feedbackCanvas == null
                || !feedbackCanvas.gameObject.activeSelf || canvasGroup == null
                || !canvasGroup.interactable || terminalCommandPending
                || !IsShownPresentationCurrent()
                || moreLivesFlow.State != BsPurchaseOverlayState.Hidden)
                return;
            SetActiveButtonsInteractable(true);
        }

        private void HandleTerminalCommandCompleted(
            BartenderTerminalCommandCompletion completion)
        {
            if (!terminalCommandPending
                || completion.Receipt != pendingTerminalCommandReceipt
                || !TerminalCommandMatchesPresentation(
                    completion.Receipt, shownPresentation))
                return;

            terminalCommandPending = false;
            pendingTerminalCommandReceipt = default;
            if (!completion.Succeeded)
            {
                if (!IsShownPresentationCurrent())
                {
                    ResetPresentation();
                    return;
                }
                // No error text on the card (save/technical reasons read as noise); the shake below
                // tells the player the tap did not go through.
                SetTerminalFeedback(string.Empty);
                if (shownOutcome == BsRoundOutcome.Failed)
                    BsAudio.Instance?.ResumeFailWaitMusic();
                if (activeSequence != null) return;
                if (canvasGroup != null) canvasGroup.interactable = true;
                SetActiveButtonsInteractable(true);
                ShakeCard();
                return;
            }

            // Close the overlay when returning home so it cannot invisibly block menu input.
            ResetPresentation();
        }

        private void Present(BartenderTerminalPresentationReceipt receipt,
            BartenderTerminalCommandReceipt pendingCommand = default)
        {
            if (!isActiveAndEnabled || session == null) return;
            if (feedbackCanvas == null) return;
            if (!session.IsTerminalPresentationCurrent(receipt)) return;
            if (TerminalPresentationsMatch(shownPresentation, receipt)
                && (terminalCommandPending || activeSequence != null
                    || (canvasGroup != null && canvasGroup.gameObject.activeInHierarchy)))
                return;

            KillActiveSequence();
            presentationRevision++;
            BsRoundOutcome outcome = receipt.Outcome;
            shownOutcome = outcome;
            shownPresentation = receipt;
            terminalCommandPending = TerminalCommandMatchesPresentation(pendingCommand, receipt);
            pendingTerminalCommandReceipt = terminalCommandPending ? pendingCommand : default;
            SetTerminalFeedback(string.Empty);
            ResetMoreLivesPresentation();
            bool won = outcome == BsRoundOutcome.Won;
            BsLevel level = controller != null ? controller.CurrentLevel : null;
            if (won && successView != null)
            {
                successView.SetLevel(level);
                successView.SetRewardValue(BartenderProgressService.WinCoinReward);
            }
            if (!won && failureView != null) failureView.SetLevel(level);
            if (!won && lifeLostPending) ScheduleLifeLostCue(receipt);
            lifeLostPending = false;
            SelectPresentationView(won);
            if (canvasGroup == null || scrim == null || flash == null
                || card == null || actionButton == null) return;

            BartenderCheersSequence cheers = won && successView != null
                ? successView.CheersSequence
                : null;
            bool useCheersIntro = !terminalCommandPending && cheers != null && cheers.IsReady;

            ResetFlash(flash);
            feedbackCanvas.gameObject.SetActive(true);
            canvasGroup.alpha = 0f;
            // Block world taps during the card entrance so the terminal fallback cannot queue the next level
            // early.
            canvasGroup.blocksRaycasts = true;
            canvasGroup.interactable = false;
            if (useCheersIntro)
                cheers.Prepare(successCardStartOffset, cardStartScale);
            else
            {
                card.anchoredPosition = new Vector2(0f, -cardStartOffset);
                card.localScale = Vector3.one * cardStartScale;
            }
            SetActiveButtonsInteractable(false);

            Sequence sequence = DOTween.Sequence()
                .SetTarget(this)
                .SetUpdate(true)
                .SetRecyclable(true);
            activeSequence = sequence;
            sequence.Append(canvasGroup.DOFade(1f, scrimFadeDuration)
                .SetEase(Ease.OutQuad).SetRecyclable(true));

            if (useCheersIntro)
            {
                cheers.InsertInto(sequence);
                sequence.InsertCallback(BartenderCheersSequence.ContactTime,
                    () => HandleCheersContact(receipt));
                sequence.InsertCallback(BartenderCheersSequence.CardRevealTime,
                    () => HandleWinCardReveal(receipt));
            }
            else
            {
                sequence.Join(card.DOAnchorPos(Vector2.zero, cardEntranceDuration)
                    .SetEase(Ease.OutCubic).SetRecyclable(true));
                sequence.Join(card.DOScale(Vector3.one, cardEntranceDuration)
                    .SetEase(Ease.OutBack).SetRecyclable(true));
                if (won)
                {
                    sequence.Insert(0f, flash.DOFade(0.30f, 0.09f)
                        .SetEase(Ease.OutQuad).SetRecyclable(true));
                    sequence.Insert(0.09f, flash.DOFade(0f, 0.34f)
                        .SetEase(Ease.InSine).SetRecyclable(true));
                }
            }
            if (!won)
                sequence.Insert(0.18f, card.DOShakeAnchorPos(
                        0.26f, new Vector2(14f, 0f), 11, 0f, false, true)
                    .SetRecyclable(true));
            entranceDeadline = Time.realtimeSinceStartupAsDouble
                + sequence.Duration() + EntranceTimeoutGraceSeconds;
            sequence.OnComplete(() =>
            {
                if (!ReferenceEquals(activeSequence, sequence)
                    || !IsShownPresentationCurrent(receipt)) return;
                // Clear auto-killed tween references because DOTween may reuse them for another animation.
                activeSequence = null;
                entranceDeadline = 0d;
                canvasGroup.blocksRaycasts = true;
                bool canUseCard = !terminalCommandPending && !lifeRefillPending
                    && moreLivesFlow.State == BsPurchaseOverlayState.Hidden;
                canvasGroup.interactable = canUseCard;
                SetActiveButtonsInteractable(canUseCard);
            });
            sequence.OnKill(() =>
            {
                InterruptEntrance(sequence, receipt, false);
            });

            if (won && !terminalCommandPending && !useCheersIntro) HandleWinCardReveal(receipt);
        }

        private void InterruptEntrance(Sequence sequence,
            BartenderTerminalPresentationReceipt receipt, bool stopTween)
        {
            if (!ReferenceEquals(activeSequence, sequence)) return;
            activeSequence = null;
            entranceDeadline = 0d;
            if (stopTween && sequence.IsActive()) sequence.Kill(false);
            if (!isActiveAndEnabled || !IsShownPresentationCurrent(receipt)
                || feedbackCanvas == null || !feedbackCanvas.gameObject.activeInHierarchy
                || canvasGroup == null || !canvasGroup.gameObject.activeInHierarchy
                || card == null)
            {
                ResetPresentation();
                return;
            }

            // A cancelled intro leaves the result usable; it never submits a terminal command.
            if (shownOutcome == BsRoundOutcome.Won) successView?.ResetCheersPresentation();
            ResetCardTransform(card);
            ResetFlash(flash);
            canvasGroup.alpha = 1f;
            canvasGroup.blocksRaycasts = true;
            bool canUseCard = !terminalCommandPending && !lifeRefillPending
                && moreLivesFlow.State == BsPurchaseOverlayState.Hidden;
            canvasGroup.interactable = canUseCard;
            SetActiveButtonsInteractable(canUseCard);
            Debug.LogWarning("Result-card entrance was interrupted; the card was restored without submitting a command.", this);
        }

        private void HandleCheersContact(
            BartenderTerminalPresentationReceipt receipt)
        {
            if (!IsShownPresentationCurrent(receipt)) return;
            // The win cue already includes contact at 0.30 seconds. Add only haptics here to avoid a second
            // hit.
            BartenderHaptics.Light();
        }

        private void HandleWinCardReveal(
            BartenderTerminalPresentationReceipt receipt)
        {
            if (!IsShownPresentationCurrent(receipt)) return;
            BsAudio.Instance?.PlayWinCardAccent();
        }

        private void SelectPresentationView(bool won)
        {
            bool useAuthoredSuccess = won && successView != null && successView.IsReady;
            bool useAuthoredFailure = !won && failureView != null && failureView.IsReady;
            if (successView != null) successView.SetVisible(useAuthoredSuccess);
            if (failureView != null) failureView.SetVisible(useAuthoredFailure);

            if (useAuthoredSuccess)
            {
                canvasGroup = successView.OverlayGroup;
                scrim = successView.Dimmer;
                flash = successView.Flash;
                card = successView.CardPivot;
                actionButton = successView.ContinueButton;
                paidContinueButton = null;
                closeButton = successView.CloseButton;
                rewardButton = successView.RewardButton;
                RebindButton(actionButton, HandleActionPressed);
                RebindButton(closeButton, HandleClosePressed);
                return;
            }

            if (useAuthoredFailure)
            {
                canvasGroup = failureView.OverlayGroup;
                scrim = failureView.Dimmer;
                flash = failureView.Flash;
                card = failureView.CardPivot;
                actionButton = failureView.RetryButton;
                paidContinueButton = failureView.PaidContinueButton;
                closeButton = failureView.CloseButton;
                rewardButton = null;
                RebindButton(actionButton, HandleActionPressed);
                RebindButton(paidContinueButton, HandlePaidContinuePressed);
                RebindButton(closeButton, HandleClosePressed);
                return;
            }

            canvasGroup = null;
            scrim = null;
            flash = null;
            card = null;
            actionButton = null;
            paidContinueButton = null;
            closeButton = null;
            rewardButton = null;
        }

        private void HandleActionPressed()
        {
            if (lifeRefillPending || session == null || actionButton == null || !actionButton.interactable) return;
            if (!IsShownPresentationCurrent())
            {
                ResetPresentation();
                return;
            }

            if (shownOutcome == BsRoundOutcome.Failed)
            {
                if (BartenderProgressService.Lives <= 0)
                    OpenMoreLivesOffer();
                else
                    RequestFailureRetry();
                return;
            }

            RequestRewardedWinReturn();
        }

        private void RequestRewardedWinReturn()
        {
            BartenderTerminalPresentationReceipt expected = shownPresentation;
            int revision = presentationRevision;
            if (!CanUpdatePresentation(expected, revision)) return;
            SetTerminalFeedback(string.Empty);
            SetActiveButtonsInteractable(false);
            terminalCommandPending = true;
            bool accepted = session != null && session.RequestContinueAfterWin(
                expected, CaptureRewardSourceViewportPoint());
            if (!CanUpdatePresentation(expected, revision)) return;
            if (CaptureQueuedTerminalCommand(accepted, expected)) return;
            terminalCommandPending = false;
            pendingTerminalCommandReceipt = default;
            SetActiveButtonsInteractable(true);
            ShakeCard();
        }

        internal static bool CloseUsesRewardedWinFlow(BsRoundOutcome outcome) =>
            outcome == BsRoundOutcome.Won;

        private Vector2 CaptureRewardSourceViewportPoint()
        {
            RectTransform source = successView != null
                ? successView.RewardPresentationAnchor
                : null;
            if (source == null) return new Vector2(0.5f, 0.5f);

            Canvas sourceCanvas = source.GetComponentInParent<Canvas>();
            Camera sourceCamera = sourceCanvas != null
                                  && sourceCanvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? sourceCanvas.worldCamera
                : null;
            Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(
                sourceCamera, source.position);
            if (Screen.width <= 0 || Screen.height <= 0
                || float.IsNaN(screenPoint.x) || float.IsInfinity(screenPoint.x)
                || float.IsNaN(screenPoint.y) || float.IsInfinity(screenPoint.y))
                return new Vector2(0.5f, 0.5f);

            return new Vector2(
                Mathf.Clamp01(screenPoint.x / Screen.width),
                Mathf.Clamp01(screenPoint.y / Screen.height));
        }

        private void RequestFailureRetry()
        {
            BartenderTerminalPresentationReceipt expected = shownPresentation;
            int revision = presentationRevision;
            if (!CanUpdatePresentation(expected, revision)) return;
            SetActiveButtonsInteractable(false);
            terminalCommandPending = true;
            bool accepted = session != null
                && session.RequestRetryAfterFailure(expected);
            if (!CanUpdatePresentation(expected, revision)) return;
            if (CaptureQueuedTerminalCommand(accepted, expected)) return;

            terminalCommandPending = false;
            pendingTerminalCommandReceipt = default;
            SetActiveButtonsInteractable(true);
            ShakeCard();
        }

        private void HandlePaidContinuePressed()
        {
            if (lifeRefillPending || session == null || paidContinueButton == null
                || !paidContinueButton.interactable) return;
            if (!IsShownPresentationCurrent())
            {
                ResetPresentation();
                return;
            }

            if (!BartenderProgressService.CanAfford(FailureContinueCoinCost))
            {
                BsAudio.NotEnoughCoins();
                BartenderHaptics.Light();
                if (!BartenderShopPresenter.TryOpenCurrentScene())
                    ShakeCard();
                return;
            }

            BartenderTerminalPresentationReceipt expected = shownPresentation;
            int revision = presentationRevision;
            SetActiveButtonsInteractable(false);
            terminalCommandPending = true;
            bool accepted = session.RequestPaidRetryAfterFailure(
                expected, FailureContinueCoinCost);
            if (!CanUpdatePresentation(expected, revision)) return;
            if (CaptureQueuedTerminalCommand(accepted, expected)) return;
            terminalCommandPending = false;
            pendingTerminalCommandReceipt = default;
            SetActiveButtonsInteractable(true);
            ShakeCard();
        }

        private void HandleClosePressed()
        {
            if (lifeRefillPending || session == null || closeButton == null || !closeButton.interactable) return;
            if (!IsShownPresentationCurrent())
            {
                ResetPresentation();
                return;
            }
            if (CloseUsesRewardedWinFlow(shownOutcome))
            {
                RequestRewardedWinReturn();
                return;
            }

            BartenderTerminalPresentationReceipt expected = shownPresentation;
            int revision = presentationRevision;
            SetActiveButtonsInteractable(false);
            terminalCommandPending = true;
            bool accepted = session.RequestReturnToMainMenuFromTerminal(expected);
            if (!CanUpdatePresentation(expected, revision)) return;
            if (CaptureQueuedTerminalCommand(accepted, expected)) return;
            terminalCommandPending = false;
            pendingTerminalCommandReceipt = default;
            SetActiveButtonsInteractable(true);
            ShakeCard();
        }

        private bool CaptureQueuedTerminalCommand(bool requestAccepted,
            BartenderTerminalPresentationReceipt expected)
        {
            if (!requestAccepted || session == null) return false;
            if (session.TryGetQueuedTerminalCommandReceipt(
                    expected,
                    out BartenderTerminalCommandReceipt receipt)
                && TerminalCommandMatchesPresentation(
                    receipt, expected))
            {
                pendingTerminalCommandReceipt = receipt;
                SetTerminalFeedback(string.Empty);
                if (shownOutcome == BsRoundOutcome.Failed)
                    BsAudio.Instance?.StopFailWaitMusic(FailWaitMusicCommandFadeSeconds);
                return true;
            }

            Debug.LogError(
                "Session accepted a terminal command without reserving its receipt.",
                this);
            return false;
        }

        private void SetTerminalFeedback(string message)
        {
            if (shownOutcome == BsRoundOutcome.Won) successView?.SetFeedback(message);
            else failureView?.SetFeedback(message);
        }

        private bool CanUpdatePresentation(BartenderTerminalPresentationReceipt expected,
            int revision)
        {
            if (!isActiveAndEnabled || revision != presentationRevision
                || !TerminalPresentationsMatch(shownPresentation, expected))
                return false;
            if (IsShownPresentationCurrent(expected)) return true;
            ResetPresentation();
            return false;
        }

        private bool IsShownPresentationCurrent() =>
            IsShownPresentationCurrent(shownPresentation);

        private bool IsShownPresentationCurrent(
            BartenderTerminalPresentationReceipt receipt)
        {
            return session != null
                   && TerminalPresentationsMatch(shownPresentation, receipt)
                   && session.IsTerminalPresentationCurrent(receipt);
        }

        private static bool TerminalPresentationsMatch(
            BartenderTerminalPresentationReceipt left,
            BartenderTerminalPresentationReceipt right)
        {
            return left.IsValid && right.IsValid
                   && left.AttemptId == right.AttemptId
                   && left.RoundOperationId == right.RoundOperationId
                   && left.Revision == right.Revision
                   && left.BoardRevision == right.BoardRevision
                   && left.Cause == right.Cause
                   && left.Outcome == right.Outcome
                   && left.Token == right.Token;
        }

        private static bool TerminalCommandMatchesPresentation(
            BartenderTerminalCommandReceipt command,
            BartenderTerminalPresentationReceipt presentation)
        {
            return command.IsValid && presentation.IsValid
                   && command.AttemptId == presentation.AttemptId
                   && command.RoundOperationId == presentation.RoundOperationId
                   && command.Revision == presentation.Revision
                   && command.BoardRevision == presentation.BoardRevision
                   && command.Cause == presentation.Cause
                   && command.Outcome == presentation.Outcome
                   && command.Token == presentation.Token;
        }

        private void OpenMoreLivesOffer()
        {
            if (lifeRefillPending) return;
            if (moreLivesView == null || !moreLivesView.IsReady)
            {
                ShakeCard();
                return;
            }
            if (!moreLivesFlow.Dispatch(BsPurchaseOverlayTrigger.Show)) return;
            BsAudio.Instance?.Play(BsSfx.PopupOpen, 0.8f);

            moreLivesView.SetFeedback(string.Empty);
            RefreshMoreLivesView();
            SetActiveButtonsInteractable(false);
            if (canvasGroup != null) canvasGroup.interactable = false;
            moreLivesView.SetVisible(true);
            SetFailureCardBehindOffer(true);
            moreLivesView.SetButtonsInteractable(true, true);
        }

        private void SetFailureCardBehindOffer(bool covered)
        {
            if (failureView == null || failureView.CardPivot == null) return;
            failureView.CardPivot.gameObject.SetActive(!covered);
        }

        private void CloseMoreLivesOffer()
        {
            if (!moreLivesFlow.Dispatch(BsPurchaseOverlayTrigger.Dismiss)) return;

            if (moreLivesView != null) moreLivesView.SetVisible(false);
            SetFailureCardBehindOffer(false);
            if (canvasGroup != null)
            {
                canvasGroup.blocksRaycasts = true;
                canvasGroup.interactable = true;
            }
            if (!terminalCommandPending) SetActiveButtonsInteractable(true);
        }

        private async void HandleMoreLivesRefillPressed()
        {
            BartenderTerminalPresentationReceipt expected = shownPresentation;
            int revision = presentationRevision;
            if (lifeRefillPending || !CanUpdatePresentation(expected, revision)) return;
            if (moreLivesView == null
                || moreLivesFlow.State != BsPurchaseOverlayState.Visible
                || !moreLivesFlow.Dispatch(BsPurchaseOverlayTrigger.BeginPurchase))
                return;

            lifeRefillPending = true;
            BartenderSaveResult result;
            try
            {
                moreLivesView.SetFeedback(string.Empty);
                moreLivesView.SetButtonsInteractable(false, false);
                result = await BartenderProgressService.RefillLivesToMaximumAsync(
                    BartenderProgressService.FullLifeRefillCoinCost);
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, this);
                result = new BartenderSaveResult(false, "The life refill could not be saved");
            }
            finally
            {
                if (this != null) lifeRefillPending = false;
            }
            if (this == null) return;
            if (!CanUpdatePresentation(expected, revision)
                || moreLivesFlow.State != BsPurchaseOverlayState.PurchasePending)
            {
                RestoreInputAfterLifeRefill();
                return;
            }
            // Resolve ownership before optional audio/view callbacks, which may throw or disable us.
            _ = moreLivesFlow.Dispatch(result.Succeeded
                ? BsPurchaseOverlayTrigger.PurchaseSucceeded
                : BsPurchaseOverlayTrigger.PurchaseRejected);
            try
            {
                if (result.Succeeded)
                {
                    moreLivesView.SetVisible(false);
                    SetFailureCardBehindOffer(false);
                    BsAudio.Instance?.Play(BsSfx.LifeRefill, LifeRefillVolume);
                    if (CanUpdatePresentation(expected, revision)) RequestFailureRetry();
                    return;
                }
                RefreshMoreLivesView();
                if (BartenderProgressService.Lives > 0)
                {
                    CloseMoreLivesOffer();
                    return;
                }
                moreLivesView.SetFeedback(string.IsNullOrWhiteSpace(result.RejectionReason)
                    ? MoreLivesFeedback() : result.RejectionReason.ToUpperInvariant());
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, this);
            }
            finally { RestoreInputAfterLifeRefill(); }
        }

        private void RestoreInputAfterLifeRefill()
        {
            if (this == null || !isActiveAndEnabled || activeSequence != null
                || canvasGroup == null || !IsShownPresentationCurrent()) return;
            bool canUseCard = !terminalCommandPending && !lifeRefillPending
                && moreLivesFlow.State == BsPurchaseOverlayState.Hidden;
            canvasGroup.interactable = canUseCard;
            SetActiveButtonsInteractable(canUseCard);
            if (moreLivesFlow.State == BsPurchaseOverlayState.Visible)
                RefreshMoreLivesView();
        }

        private static string MoreLivesFeedback()
        {
            if (BartenderProgressService.Coins
                < BartenderProgressService.FullLifeRefillCoinCost)
                return "NOT ENOUGH COINS";
            if (BartenderProgressService.Lives >= BartenderProgressService.MaxLives)
                return "LIVES ARE FULL";
            return "REFILL UNAVAILABLE";
        }

        private void RefreshMoreLivesView()
        {
            if (moreLivesView == null) return;
            moreLivesView.SetValues(
                BartenderProgressService.Lives,
                BartenderProgressService.LifeTimer,
                BartenderProgressService.FullLifeRefillCoinCost);

            bool visible = !lifeRefillPending && moreLivesFlow.State == BsPurchaseOverlayState.Visible;
            moreLivesView.SetButtonsInteractable(visible, visible);
        }

        private void SetActiveButtonsInteractable(bool interactable)
        {
            interactable &= !lifeRefillPending;
            bool canUseAction = interactable;
            bool failed = shownOutcome == BsRoundOutcome.Failed;
            int lives = failed ? BartenderProgressService.Lives : 0;
            if (actionButton != null) actionButton.interactable = canUseAction;
            if (paidContinueButton != null)
            {
                paidContinueButton.interactable = interactable && failed
                    && lives < BartenderProgressService.MaxLives;
            }
            if (closeButton != null) closeButton.interactable = interactable;

            if (rewardButton != null) rewardButton.interactable = false;
        }

        private void ShakeCard()
        {
            if (card == null) return;
            card.DOKill(false);
            card.anchoredPosition = Vector2.zero;
            card.DOShakeAnchorPos(0.22f, new Vector2(12f, 0f), 10, 0f, false, true)
                .SetUpdate(true).SetRecyclable(true);
        }

        private void ResetPresentation()
        {
            presentationRevision++;
            terminalCommandPending = false;
            pendingTerminalCommandReceipt = default;
            shownPresentation = default;
            lifeLostPending = false;
            ResetMoreLivesPresentation();
            KillLifeLostCue();
            BsAudio.Instance?.StopFailWaitMusic(shortenRunningFade: false);
            KillActiveSequence();
            ResetCardTransform(card);
            if (successView != null) ResetCardTransform(successView.CardPivot);
            if (failureView != null) ResetCardTransform(failureView.CardPivot);
            if (flash != null) ResetFlash(flash);
            if (successView != null && successView.Flash != null)
                ResetFlash(successView.Flash);
            if (failureView != null && failureView.Flash != null)
                ResetFlash(failureView.Flash);

            if (successView != null)
            {
                successView.ResetCheersPresentation();
                RebindButton(successView.ContinueButton, HandleActionPressed);
                RebindButton(successView.CloseButton, HandleClosePressed);
                successView.SetButtonsInteractable(false, false, false);
                successView.SetVisible(false);
            }
            if (failureView != null)
            {
                RebindButton(failureView.PaidContinueButton,
                    HandlePaidContinuePressed);
                RebindButton(failureView.RetryButton, HandleActionPressed);
                RebindButton(failureView.CloseButton, HandleClosePressed);
                failureView.SetButtonsInteractable(false, false, false);
                failureView.SetVisible(false);
            }

            if (feedbackCanvas != null) feedbackCanvas.gameObject.SetActive(false);
        }

        private static void ResetFlash(Image target)
        {
            if (target == null) return;
            target.DOKill(false);
            Color colour = target.color;
            colour.a = 0f;
            target.color = colour;
        }

        private void ResetMoreLivesPresentation()
        {
            _ = moreLivesFlow.Dispatch(BsPurchaseOverlayTrigger.Reset);
            SetFailureCardBehindOffer(false);
            if (moreLivesView == null) return;
            moreLivesView.SetFeedback(string.Empty);
            moreLivesView.SetButtonsInteractable(false, false);
            moreLivesView.SetVisible(false);
        }

        private static void ResetCardTransform(RectTransform target)
        {
            if (target == null) return;
            target.DOKill(false);
            target.anchoredPosition = Vector2.zero;
            target.localScale = Vector3.one;
        }

        private static void RebindButton(Button button, UnityEngine.Events.UnityAction action)
        {
            if (button == null) return;
            button.onClick.RemoveListener(action);
            button.onClick.AddListener(action);
        }

        private void ScheduleLifeLostCue(
            BartenderTerminalPresentationReceipt receipt)
        {
            KillLifeLostCue();
            Tween cue = DOVirtual.DelayedCall(
                LifeLostCueDelay,
                () =>
                {
                    if (IsShownPresentationCurrent(receipt))
                        BsAudio.Instance?.Play(BsSfx.LifeLost, LifeLostVolume);
                },
                ignoreTimeScale: true);
            lifeLostCue = cue;
            cue.OnKill(() =>
            {
                if (ReferenceEquals(lifeLostCue, cue)) lifeLostCue = null;
            });
        }

        private void KillLifeLostCue()
        {
            Tween cue = lifeLostCue;
            lifeLostCue = null;
            if (cue != null && cue.IsActive()) cue.Kill(false);
        }

        private void KillActiveSequence()
        {
            Sequence sequence = activeSequence;
            activeSequence = null;
            entranceDeadline = 0d;
            if (sequence != null && sequence.IsActive()) sequence.Kill(false);
        }

        private void BindAuthoredViews()
        {
            if (feedbackCanvas == null)
                Debug.LogError("Feedback Canvas missing.", this);

            BindSuccessView();
            BindFailureView();
            BindMoreLivesView();

            if (feedbackCanvas != null) feedbackCanvas.gameObject.SetActive(false);
        }

        private void BindSuccessView()
        {
            if (successView == null || !successView.IsReady)
            {
                Debug.LogError("Success popup bindings missing.",
                    this);
                return;
            }

            RebindButton(successView.ContinueButton, HandleActionPressed);
            RebindButton(successView.CloseButton, HandleClosePressed);

            PreserveArtworkWhenDisabled(successView.ContinueButton);
            PreserveArtworkWhenDisabled(successView.CloseButton);
            PreserveArtworkWhenDisabled(successView.RewardButton);
            successView.SetButtonsInteractable(false, false, false);
            successView.CoinBalance.SetMoment(
                BartenderCoinBalanceView.BalanceMoment.BeforeRoundReward);
            BartenderCheersSequence cheers = successView.CheersSequence;
            if (cheers == null || !cheers.IsReady)
            {
                Debug.LogError("Cheers bindings missing.", this);
            }
            successView.SetVisible(false);
        }

        private void BindFailureView()
        {
            if (failureView == null || !failureView.IsReady)
            {
                Debug.LogError("Failure popup bindings missing.", this);
                return;
            }

            RebindButton(failureView.PaidContinueButton,
                HandlePaidContinuePressed);
            RebindButton(failureView.RetryButton, HandleActionPressed);
            RebindButton(failureView.CloseButton, HandleClosePressed);
            failureView.CoinBalance.SetMoment(
                BartenderCoinBalanceView.BalanceMoment.Current);
            failureView.SetPurchaseCost(FailureContinueCoinCost);
            PreserveArtworkWhenDisabled(failureView.CloseButton);
            failureView.SetButtonsInteractable(false, false, false);
            failureView.SetVisible(false);
        }

        private void BindMoreLivesView()
        {
            if (moreLivesView == null || !moreLivesView.IsReady)
            {
                Debug.LogError("More Lives popup bindings missing.", this);
                return;
            }

            RebindButton(moreLivesView.CloseButton, CloseMoreLivesOffer);
            RebindButton(moreLivesView.RefillButton,
                HandleMoreLivesRefillPressed);
            PreserveArtworkWhenDisabled(moreLivesView.CloseButton);
            PreserveArtworkWhenDisabled(moreLivesView.RefillButton);
            moreLivesView.CoinBalance.SetMoment(
                BartenderCoinBalanceView.BalanceMoment.Current);
            ResetMoreLivesPresentation();
        }

        private static void PreserveArtworkWhenDisabled(Button button)
        {
            if (button == null) return;
            ColorBlock colours = button.colors;
            colours.disabledColor = Color.white;
            button.colors = colours;
        }
    }
}
