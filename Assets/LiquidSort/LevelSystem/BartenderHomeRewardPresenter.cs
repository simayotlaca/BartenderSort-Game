using System;
using System.Collections;
using System.Globalization;
using BartenderSort.Core;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Shows the saved win reward using its old and new balance snapshots. Animate the level button first, then
    /// pooled coins; balances never change here.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BartenderHomeRewardPresenter : MonoBehaviour
    {
        private const int CoinPoolSize = 8;
        private const float ReferenceWidth = 1080f;
        private const float ReferenceHeight = 1920f;
        private const float CoinSize = 116f;
        private const float StackScatterDuration = 0.36f;
        private const float StackHoldDuration = 0.24f;
        private const float CoinFlightDuration = 0.76f;
        private const float StackSpreadScale = 1.18f;
        // Reveal the whole burst before collection; landing cadence still follows the counter audio.
        private const float CoinEmissionStagger = 0.016f;
        // Start the clip 12 ms early so its first hit matches the first coin landing.
        private const float CoinCounterRiseLeadIn = 0.012f;
        // Space eight coins by 0.049 seconds to match the clip's 0.343-second hit span.
        private const float CoinStagger = 0.049f;
        private const float CollectStartTime = StackScatterDuration + StackHoldDuration;
        private const float RewardMotionDuration = CollectStartTime
                                                 + CoinFlightDuration
                                                 + CoinStagger
                                                 * (CoinPoolSize - 1);
        private const float TargetPunchDuration = 0.18f;
        private const float ArrivalSettleDelay = 0.18f;
        private const float CompletedLevelHoldDuration = 0.36f;
        private const float CoinOnlyHoldDuration = 0.12f;
        internal const double PresentationTimeoutSeconds = 8d;

        private static readonly Vector2[] CoinStackOffsets =
        {
            new Vector2(-172f, 56f),
            new Vector2(128f, -146f),
            new Vector2(-74f, 184f),
            new Vector2(206f, 66f),
            new Vector2(-166f, -152f),
            new Vector2(106f, 190f),
            new Vector2(-236f, -34f),
            new Vector2(6f, -236f),
        };

        private readonly BsHomeRewardPresentationStateMachine flow =
            new BsHomeRewardPresentationStateMachine();

        private Canvas ownerCanvas;
        private RectTransform canvasRect;
        private Image coinTarget;
        private RectTransform coinTargetRect;
        private Text balanceLabel;
        private Button levelButton;
        private Text levelLabel;
        private BartenderMainMenuLevelButtonView levelButtonView;

        [Header("Authored reward coin pool")]
        [Tooltip("Full-screen RectTransform under the same Canvas as the menu.")]
        [SerializeField] private RectTransform flyLayer;
        [Tooltip("Eight authored coin RectTransforms, in animation order.")]
        [SerializeField] private RectTransform[] coinRects = new RectTransform[CoinPoolSize];
        [Tooltip("Images belonging to the eight authored coin RectTransforms.")]
        [SerializeField] private Image[] coinImages = new Image[CoinPoolSize];

        private BartenderPendingHomeReward activeReward;
        private bool hasActiveReward;
        private bool levelProjectionReleased;
        private int authoritativeCoins;
        private int displayedPreviousCoins;
        private long playbackRevision;
        private double presentationDeadline;
        private bool settlingPresentation;
        private Coroutine beginRoutine;
        private Sequence activeSequence;
        private Tween targetPunchTween;
        private Vector3 coinTargetRestScale = Vector3.one;
        private bool poolBindingErrorReported;

        public bool IsPresenting => hasActiveReward;
        public bool SuppressesCoinProjection => hasActiveReward;
        public bool SuppressesLevelProjection => hasActiveReward && !levelProjectionReleased;

        /// <summary>Raised at the button swap to reveal the next level, before coins start moving.</summary>
        public event Action LevelAdvanceReady;

        /// <summary>Raised only after the button and coin animations finish in order.</summary>
        public event Action PresentationCompleted;

        /// <summary>Releases menu input after interruption; the unshown receipt remains available.</summary>
        public event Action PresentationInterrupted;

        /// <summary>
        /// Binds the existing menu and fixed coin pool without creating, moving parents or destroying UI
        /// objects.
        /// </summary>
        public void Bind(
            Canvas canvas,
            Image targetCoinImage,
            Text currencyLabel,
            Button playButton,
            Text playLevelLabel,
            BartenderMainMenuLevelButtonView playLevelButtonView)
        {
            RectTransform nextCanvasRect = canvas != null
                ? canvas.transform as RectTransform
                : null;
            bool hierarchyChanged = ownerCanvas != canvas
                                    || canvasRect != nextCanvasRect
                                    || coinTarget != targetCoinImage
                                    || balanceLabel != currencyLabel
                                    || levelButton != playButton
                                    || levelLabel != playLevelLabel
                                    || levelButtonView != playLevelButtonView;
            if (hierarchyChanged && hasActiveReward) CancelAndSnap();

            ownerCanvas = canvas;
            canvasRect = nextCanvasRect;
            coinTarget = targetCoinImage;
            coinTargetRect = targetCoinImage != null
                ? targetCoinImage.rectTransform
                : null;
            balanceLabel = currencyLabel;
            levelButton = playButton;
            levelLabel = playLevelLabel;
            levelButtonView = playLevelButtonView;

            if (!hasActiveReward)
            {
                coinTargetRestScale = coinTargetRect != null
                    ? coinTargetRect.localScale
                    : Vector3.one;
            }

            EnsurePool();
            ApplyCoinSprite();
        }

        /// <summary>
        /// Claims the pending reward and sets starting text and input immediately so final values cannot flash
        /// before animation.
        /// </summary>
        public bool TryPresentPending()
        {
            if (settlingPresentation) return false;
            if (hasActiveReward) return true;
            if (!isActiveAndEnabled || !gameObject.activeInHierarchy || !CanPresentCoins())
                return false;
            if (!BartenderPendingHomeRewardStore.TryConsume(out activeReward))
                return false;
            if (activeReward.IncludesLevelCelebration && !CanPresentLevel())
            {
                BartenderPendingHomeRewardStore.TryRelease(activeReward.Revision);
                activeReward = default;
                return false;
            }

            _ = flow.Dispatch(BsHomeRewardPresentationTrigger.Reset);
            if (!flow.Dispatch(BsHomeRewardPresentationTrigger.Prepare))
            {
                BartenderPendingHomeRewardStore.TryRelease(activeReward.Revision);
                activeReward = default;
                return false;
            }

            hasActiveReward = true;
            levelProjectionReleased = !activeReward.IncludesLevelCelebration;
            playbackRevision++;
            presentationDeadline = Time.realtimeSinceStartupAsDouble + PresentationTimeoutSeconds;
            long revision = playbackRevision;
            try
            {
                // A retained receipt may return after purchases. Display its increment against the live
                // saved balance; no presentation path grants or spends coins.
                authoritativeCoins = Mathf.Max(0, BartenderProgressService.Coins);
                int rewardAmount = activeReward.FinalCoins - activeReward.PreviousCoins;
                displayedPreviousCoins = Mathf.Max(0, authoritativeCoins - rewardAmount);
                coinTargetRestScale = coinTargetRect != null
                    ? coinTargetRect.localScale : Vector3.one;
                WriteBalance(displayedPreviousCoins);
                if (activeReward.IncludesLevelCelebration)
                    WriteLevel(activeReward.CompletedLevelNumber);
                if (levelButton != null) levelButton.interactable = false;
                HideCoins();
                if (!IsCurrent(revision)) return false;
                Coroutine started = StartCoroutine(BeginAfterLayout(revision));
                if (!IsCurrent(revision))
                {
                    if (started != null) StopCoroutine(started);
                    return false;
                }
                beginRoutine = started;
                if (beginRoutine != null) return true;
                InterruptPresentation("The menu layout wait could not start.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                if (IsCurrent(revision))
                    InterruptPresentation("The menu reward could not be prepared.");
            }
            return false;
        }

        /// <summary>
        /// Tracks saved balance changes without revealing them until this reward finishes or snaps on
        /// interruption.
        /// </summary>
        public void SetAuthoritativeCoinBalance(int value)
        {
            if (hasActiveReward) authoritativeCoins = Mathf.Max(0, value);
        }

        /// <summary>
        /// Stops motion and shows saved values, retaining the visual receipt for the next menu entry.
        /// </summary>
        public void CancelAndSnap()
        {
            if (settlingPresentation) return;
            if (!hasActiveReward)
            {
                StopOwnedMotion();
                ResetTransientVisuals();
                _ = flow.Dispatch(BsHomeRewardPresentationTrigger.Reset);
                return;
            }

            InterruptPresentation("Menu reward presentation was closed.", false);
        }

        private void Update()
        {
            TryInterruptUnavailablePresentation(Time.realtimeSinceStartupAsDouble);
        }

        internal bool TryInterruptUnavailablePresentation(double now)
        {
            if (!hasActiveReward || settlingPresentation
                || double.IsNaN(now) || double.IsInfinity(now)) return false;
            if (now >= presentationDeadline)
            {
                InterruptPresentation("The reward presentation timed out waiting for its animation.");
                return true;
            }
            if (!CanPresent())
            {
                InterruptPresentation("The reward presentation lost its visible targets.");
                return true;
            }
            return false;
        }

        private IEnumerator BeginAfterLayout(long revision)
        {
            // Safe-area fitters and the menu's responsive projection settle in this frame.
            yield return null;
            if (!IsCurrent(revision)) yield break;

            // The home HUD is prepared immediately, but its reward must be visible after the
            // preserved result frame has been replaced by the ready menu.
            while (BartenderHomeSceneTransition.IsHoldingResultFrame)
            {
                if (!IsCurrent(revision)) yield break;
                yield return null;
            }
            Canvas.ForceUpdateCanvases();
            if (!activeReward.IncludesLevelCelebration)
            {
                // Let the closed daily panel clear before the reward leaves its former button.
                yield return new WaitForSecondsRealtime(CoinOnlyHoldDuration);
                if (!IsCurrent(revision)) yield break;
                beginRoutine = null;
                if (!CanPresent()
                    || !flow.Dispatch(BsHomeRewardPresentationTrigger.BeginCoinReward))
                {
                    InterruptPresentation("The coin reward is not ready after menu layout.");
                    yield break;
                }
                StartCoinSequence(revision);
                yield break;
            }
            // Let the player see the won level at rest before its celebration transforms the button.
            // Keep the old balance and input lock throughout this beat, including while timeScale is zero.
            yield return new WaitForSecondsRealtime(CompletedLevelHoldDuration);
            if (!IsCurrent(revision)) yield break;
            beginRoutine = null;
            if (!CanPresent()
                || !flow.Dispatch(BsHomeRewardPresentationTrigger.BeginLevelAdvance))
            {
                InterruptPresentation("The level celebration is not ready after menu layout.");
                yield break;
            }

            PlayLevelTransition(revision);
        }

        private void PlayLevelTransition(long revision)
        {
            // Every accepted win celebrates, including replay wins whose menu destination is unchanged.
            try
            {
                bool started = levelButtonView != null
                    && levelButtonView.PlayLevelAdvance(
                        activeReward.CompletedLevelNumber,
                        activeReward.NextLevelNumber,
                        () => HandleLevelSkinSwapped(revision),
                        () => HandleLevelTransitionCompleted(revision),
                        () => HandleLevelTransitionInterrupted(revision));
                if (!started && IsCurrent(revision))
                    InterruptPresentation("The level celebration could not start.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                if (IsCurrent(revision))
                    InterruptPresentation("The level celebration failed to start.");
            }
        }

        private void HandleLevelSkinSwapped(long revision)
        {
            if (!IsCurrent(revision)) return;
            if (!flow.Dispatch(BsHomeRewardPresentationTrigger.LevelSkinSwapped))
            {
                InterruptPresentation("The level button was revealed out of order.");
                return;
            }
            levelProjectionReleased = true;
            WriteLevel(activeReward.NextLevelNumber);
            NotifySafely(LevelAdvanceReady, revision);
            if (!IsCurrent(revision)) return;
            if (levelButton != null) levelButton.interactable = false;
        }

        private void HandleLevelTransitionCompleted(long revision)
        {
            if (!IsCurrent(revision)) return;
            if (!flow.Dispatch(BsHomeRewardPresentationTrigger.LevelAdvanceCompleted))
            {
                InterruptPresentation("The level celebration finished without revealing the next button.");
                return;
            }

            // Win rewards reach this point only after the new level button settles.
            StartCoinSequence(revision);
        }

        private void StartCoinSequence(long revision)
        {
            try
            {
                PlayCoinSequence(revision);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                if (IsCurrent(revision))
                    InterruptPresentation("The reward coin animation failed to start.");
            }
        }

        private void HandleLevelTransitionInterrupted(long revision)
        {
            if (!IsCurrent(revision)) return;
            InterruptPresentation("The level celebration was interrupted.");
        }

        private void PlayCoinSequence(long revision)
        {
            if (!IsCurrent(revision)) return;
            if (flow.State != BsHomeRewardPresentationState.Scattering || !EnsurePool())
            {
                InterruptPresentation("The reward coin animation is not ready.");
                return;
            }
            ApplyCoinSprite();

            flyLayer.gameObject.SetActive(true);

            Vector2 source = ViewportToLocal(activeReward.SourceViewportPoint);
            if (!TryGetTargetPosition(out Vector2 target))
            {
                InterruptPresentation("The reward coin destination is unavailable.");
                return;
            }

            float scale = ResolveReferenceScale();
            Sequence sequence = DOTween.Sequence()
                .SetTarget(this)
                .SetUpdate(true)
                .SetRecyclable(true);
            activeSequence = sequence;

            for (int i = 0; i < CoinPoolSize; i++)
            {
                RectTransform coinRect = coinRects[i];
                Image coinImage = coinImages[i];
                coinRect.anchoredPosition = source;
                coinRect.localScale = Vector3.zero;
                coinRect.localRotation = Quaternion.Euler(0f, 0f, i * -10f);
                coinRect.sizeDelta = Vector2.one * (CoinSize * scale);
                coinImage.color = Color.white;
                coinImage.enabled = false;
            }

            Vector2 liveTarget = target;
            float elapsedTime = 0f;
            ApplyStackMotion(revision, source, liveTarget, scale, 0f);
            Tween stackFlight = DOTween.To(
                    () => elapsedTime,
                    value =>
                    {
                        elapsedTime = value;
                        if (TryGetTargetPosition(out Vector2 sampledTarget))
                            liveTarget = sampledTarget;
                        ApplyStackMotion(
                            revision, source, liveTarget, scale, value);
                    },
                    RewardMotionDuration,
                    RewardMotionDuration)
                .SetEase(Ease.Linear)
                .SetRecyclable(true);
            sequence.Append(stackFlight);

            sequence.InsertCallback(CollectStartTime, () =>
            {
                if (IsCurrent(revision)
                    && !flow.Dispatch(BsHomeRewardPresentationTrigger.BeginCollect))
                    InterruptPresentation("The reward collection began out of order.");
            });

            // Play the counter clip once around the first coin landing; it already contains the full run of
            // clicks.
            sequence.InsertCallback(
                CollectStartTime + CoinFlightDuration - CoinCounterRiseLeadIn,
                () =>
                {
                    if (IsCurrent(revision)) BsAudio.Instance?.Play(BsSfx.CoinCounterRise, 0.9f);
                });

            for (int i = 0; i < CoinPoolSize; i++)
            {
                int coinIndex = i;
                float arrivalTime = CollectStartTime
                                  + CoinFlightDuration
                                  + CoinStagger * coinIndex;
                sequence.InsertCallback(arrivalTime,
                    () => HandleCoinArrival(revision, coinIndex));
            }
            sequence.AppendInterval(ArrivalSettleDelay);

            sequence.OnComplete(() => FinishNormally(sequence, revision));
            sequence.OnKill(() =>
            {
                if (!ReferenceEquals(activeSequence, sequence)
                    || !IsCurrent(revision)) return;
                activeSequence = null;
                InterruptPresentation("The reward coin animation was interrupted.");
            });
        }

        private void ApplyStackMotion(
            long revision,
            Vector2 source,
            Vector2 liveTarget,
            float referenceScale,
            float elapsedTime)
        {
            if (!IsCurrent(revision)) return;
            for (int i = 0; i < CoinPoolSize; i++)
            {
                RectTransform coinRect = coinRects[i];
                Image coinImage = coinImages[i];
                if (coinRect == null || coinImage == null) continue;

                float emissionTime = CoinEmissionStagger * i;
                float arrivalTime = CollectStartTime
                                  + CoinFlightDuration
                                  + CoinStagger * i;
                bool visible = elapsedTime >= emissionTime
                            && elapsedTime < arrivalTime;
                if (!visible)
                {
                    if (coinImage.enabled) coinImage.enabled = false;
                    if (coinRect.localScale != Vector3.zero)
                        coinRect.localScale = Vector3.zero;
                    continue;
                }
                if (!coinImage.enabled) coinImage.enabled = true;

                float iconElapsed = Mathf.Max(0f, elapsedTime - emissionTime);
                float iconProgress = BsHomeRewardStackMotion.ResolveIconProgress(
                    elapsedTime,
                    StackScatterDuration,
                    StackHoldDuration,
                    CoinFlightDuration,
                    CoinEmissionStagger,
                    CoinStagger,
                    i);
                float driftProgress = Mathf.InverseLerp(
                    StackScatterDuration,
                    CollectStartTime + (CoinStagger - CoinEmissionStagger) * i,
                    iconElapsed);
                float driftScale = Mathf.Lerp(1f, 1.06f,
                    Mathf.SmoothStep(0f, 1f, driftProgress));
                BsHomeRewardStackMotionSample sample =
                    BsHomeRewardStackMotion.Sample(
                        source,
                        liveTarget,
                        CoinStackOffsets[i] * (referenceScale * StackSpreadScale * driftScale),
                        iconProgress);
                coinRect.anchoredPosition = sample.Position;
                coinRect.localScale = Vector3.one * sample.Scale;
                float spin = ((i & 1) == 0 ? -1f : 1f)
                             * (58f + i * 7f)
                             * iconElapsed;
                coinRect.localRotation = Quaternion.Euler(
                    0f, 0f, i * -10f + spin);
            }
        }

        private void HandleCoinArrival(long revision, int coinIndex)
        {
            if (!IsCurrent(revision)
                || coinIndex < 0
                || coinIndex >= CoinPoolSize) return;
            if (flow.State != BsHomeRewardPresentationState.Collecting)
            {
                InterruptPresentation("A reward coin arrived outside the collection phase.");
                return;
            }

            if (coinImages != null
                && coinIndex < coinImages.Length
                && coinImages[coinIndex] != null)
                coinImages[coinIndex].enabled = false;
            if (coinRects != null
                && coinIndex < coinRects.Length
                && coinRects[coinIndex] != null)
                coinRects[coinIndex].localScale = Vector3.zero;

            bool finalArrival = coinIndex == CoinPoolSize - 1;
            int balanceBeat = coinIndex >= 7
                ? 3
                : coinIndex >= 5
                    ? 2
                    : coinIndex >= 2 ? 1 : 0;
            bool updatesBalance = coinIndex == 2 || coinIndex == 5 || finalArrival;
            if (updatesBalance)
            {
                int targetBalance = Mathf.Max(0, authoritativeCoins);
                int nextBalance = finalArrival
                    ? targetBalance
                    : Mathf.RoundToInt(Mathf.Lerp(
                        displayedPreviousCoins,
                        targetBalance,
                        balanceBeat / 3f));
                WriteBalance(nextBalance);
                PlayTargetPunch();
            }

            if (!finalArrival) return;
            HideCoins();
            if (!flow.Dispatch(BsHomeRewardPresentationTrigger.BeginSettle))
                InterruptPresentation("The reward collection finished out of order.");
        }

        private void FinishNormally(Sequence sequence, long revision)
        {
            if (!ReferenceEquals(activeSequence, sequence) || !IsCurrent(revision)) return;
            activeSequence = null;
            if (flow.State != BsHomeRewardPresentationState.Settling)
            {
                InterruptPresentation("The reward animation finished before settling.");
                return;
            }
            SettleReceiptAndNotify(true);
        }

        private void InterruptPresentation(string reason, bool reportWarning = true)
        {
            if (!hasActiveReward || settlingPresentation) return;
            BsHomeRewardPresentationState interruptedState = flow.State;
            _ = flow.Dispatch(BsHomeRewardPresentationTrigger.PresentationInterrupted);
            if (reportWarning)
                Debug.LogWarning($"Home reward {activeReward.CompletedLevelNumber} -> "
                    + $"{activeReward.NextLevelNumber} interrupted at {interruptedState}: {reason}", this);
            SettleReceiptAndNotify(false);
        }

        private void SettleReceiptAndNotify(bool completed)
        {
            if (!hasActiveReward || settlingPresentation) return;
            BartenderPendingHomeReward settledReward = activeReward;
            Button settledButton = levelButton;
            int finalBalance = authoritativeCoins;
            // Invalidate callbacks before any tween kill or hierarchy change can re-enter this presenter.
            settlingPresentation = true;
            long settledRevision = ++playbackRevision;
            hasActiveReward = false;
            levelProjectionReleased = true;
            activeReward = default;
            presentationDeadline = 0d;
            try
            {
                bool cleaned = StopOwnedMotion();
                cleaned = ResetTransientVisuals() && cleaned;
                cleaned = CleanupSafely(
                    () => WriteBalance(finalBalance),
                    () =>
                    {
                        if (!completed && settledReward.IncludesLevelCelebration)
                            WriteLevel(settledReward.NextLevelNumber);
                    },
                    () => { if (settledButton != null) settledButton.interactable = true; }) && cleaned;
                if (completed)
                {
                    completed = cleaned
                        && flow.Dispatch(BsHomeRewardPresentationTrigger.PresentationCompleted);
                    if (!completed)
                    {
                        _ = flow.Dispatch(BsHomeRewardPresentationTrigger.PresentationInterrupted);
                        Debug.LogWarning("The reward could not settle cleanly; its visual receipt was retained.", this);
                    }
                }
            }
            finally
            {
                if (completed)
                    BartenderPendingHomeRewardStore.TryAcknowledge(settledReward.Revision);
                else
                    BartenderPendingHomeRewardStore.TryRelease(settledReward.Revision);
                settlingPresentation = false;
            }
            NotifySafely(completed ? PresentationCompleted : PresentationInterrupted, settledRevision);
        }

        private bool StopOwnedMotion()
        {
            Coroutine routine = beginRoutine;
            beginRoutine = null;
            Sequence sequence = activeSequence;
            activeSequence = null;
            BartenderMainMenuLevelButtonView ownedLevelView = levelButtonView;
            bool levelStopped = true;
            return CleanupSafely(
                () => { if (routine != null) StopCoroutine(routine); },
                () => { if (sequence != null && sequence.IsActive()) sequence.Kill(false); },
                () =>
                {
                    if (ownedLevelView != null)
                        levelStopped = ownedLevelView.TryCancelLevelTransition();
                }) && levelStopped;
        }

        private bool ResetTransientVisuals()
        {
            return CleanupSafely(() => StopTargetPunch(true), HideCoins);
        }

        private bool CleanupSafely(params Action[] steps)
        {
            bool succeeded = true;
            for (int i = 0; i < steps.Length; i++)
            {
                try { steps[i]?.Invoke(); }
                catch (Exception exception)
                {
                    succeeded = false;
                    Debug.LogException(exception, this);
                }
            }
            return succeeded;
        }

        private void NotifySafely(Action handlers, long revision)
        {
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                if (revision != playbackRevision) return;
                try { handler(); }
                catch (Exception exception) { Debug.LogException(exception, this); }
            }
        }

        private void PlayTargetPunch()
        {
            if (coinTargetRect == null) return;
            // Closely spaced arrivals share one pulse instead of resetting its scale mid-motion.
            if (targetPunchTween != null && targetPunchTween.IsActive()
                && targetPunchTween.IsPlaying()) return;
            StopTargetPunch(true);

            Sequence punch = DOTween.Sequence()
                .SetTarget(this)
                .SetUpdate(true)
                .SetRecyclable(true);
            punch.Append(coinTargetRect.DOScale(coinTargetRestScale * 1.13f,
                    TargetPunchDuration * 0.5f).SetEase(Ease.InOutSine));
            punch.Append(coinTargetRect.DOScale(coinTargetRestScale,
                    TargetPunchDuration * 0.5f).SetEase(Ease.InOutSine));
            targetPunchTween = punch;
            punch.OnComplete(() =>
            {
                if (!ReferenceEquals(targetPunchTween, punch)) return;
                targetPunchTween = null;
                if (coinTargetRect != null) coinTargetRect.localScale = coinTargetRestScale;
            });
            punch.OnKill(() =>
            {
                if (ReferenceEquals(targetPunchTween, punch)) targetPunchTween = null;
            });
        }

        private void StopTargetPunch(bool restoreScale)
        {
            Tween punch = targetPunchTween;
            targetPunchTween = null;
            if (punch != null && punch.IsActive()) punch.Kill(false);
            if (restoreScale && coinTargetRect != null)
                coinTargetRect.localScale = coinTargetRestScale;
        }

        private bool EnsurePool()
        {
            if (canvasRect == null)
            {
                ReportPoolBindingError("Menu Canvas/RectTransform referansı eksik.");
                return false;
            }
            if (flyLayer == null || flyLayer.parent != canvasRect)
            {
                ReportPoolBindingError(
                    "Flight Layer eksik veya doğrudan authored Menu Canvas altında değil.");
                return false;
            }
            if (coinRects == null || coinRects.Length != CoinPoolSize
                || coinImages == null || coinImages.Length != CoinPoolSize)
            {
                ReportPoolBindingError($"Tam olarak {CoinPoolSize} coin Rect/Image bağlanmalı.");
                return false;
            }
            for (int i = 0; i < CoinPoolSize; i++)
            {
                RectTransform rect = coinRects[i];
                Image image = coinImages[i];
                if (rect == null || image == null || image.rectTransform != rect
                    || rect.parent != flyLayer)
                {
                    ReportPoolBindingError(
                        $"Reward Coin {i + 1:00} Rect/Image eşleşmesi eksik veya yanlış parent altında.");
                    return false;
                }
            }
            return true;
        }

        private void ReportPoolBindingError(string reason)
        {
            if (poolBindingErrorReported) return;
            poolBindingErrorReported = true;
            Debug.LogError("Authored Home Reward coin pool binding error: " + reason, this);
        }

        private void ApplyCoinSprite()
        {
            if (coinImages == null) return;
            Sprite sprite = coinTarget != null ? coinTarget.sprite : null;
            Material material = coinTarget != null ? coinTarget.material : null;
            for (int i = 0; i < coinImages.Length; i++)
            {
                if (coinImages[i] == null) continue;
                coinImages[i].sprite = sprite;
                coinImages[i].material = material;
            }
        }

        private bool CanPresent()
        {
            return CanPresentCoins()
                && (!activeReward.IncludesLevelCelebration || CanPresentLevel());
        }

        private bool CanPresentCoins()
        {
            return ownerCanvas != null && ownerCanvas.isActiveAndEnabled && canvasRect != null
                && coinTarget != null && coinTarget.isActiveAndEnabled && coinTarget.sprite != null
                && coinTargetRect != null && balanceLabel != null && balanceLabel.isActiveAndEnabled
                && EnsurePool();
        }

        private bool CanPresentLevel() => levelButton != null && levelButton.isActiveAndEnabled
            && levelLabel != null && levelLabel.isActiveAndEnabled
            && levelButtonView != null && levelButtonView.CanPlayLevelAdvance();

        private bool IsCurrent(long revision) =>
            hasActiveReward && !settlingPresentation && revision == playbackRevision;

        private void WriteBalance(int value)
        {
            if (balanceLabel != null)
                balanceLabel.text = Mathf.Max(0, value)
                    .ToString(CultureInfo.InvariantCulture);
        }

        private void WriteLevel(int levelNumber)
        {
            int safeLevel = Mathf.Max(1, levelNumber);
            if (levelButtonView != null)
                levelButtonView.ApplyLevel(safeLevel);
            else if (levelLabel != null)
                levelLabel.text = "LEVEL "
                                  + safeLevel.ToString(CultureInfo.InvariantCulture);
        }

        private Vector2 ViewportToLocal(Vector2 viewportPoint)
        {
            Rect bounds = flyLayer != null ? flyLayer.rect : default;
            return new Vector2(
                Mathf.Lerp(bounds.xMin, bounds.xMax, Mathf.Clamp01(viewportPoint.x)),
                Mathf.Lerp(bounds.yMin, bounds.yMax, Mathf.Clamp01(viewportPoint.y)));
        }

        private bool TryGetTargetPosition(out Vector2 localPoint)
        {
            localPoint = default;
            if (flyLayer == null || coinTargetRect == null || ownerCanvas == null)
                return false;

            Camera eventCamera = ownerCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : ownerCanvas.worldCamera;
            Vector3 worldCentre = coinTargetRect.TransformPoint(coinTargetRect.rect.center);
            Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(
                eventCamera, worldCentre);
            return RectTransformUtility.ScreenPointToLocalPointInRectangle(
                flyLayer, screenPoint, eventCamera, out localPoint);
        }

        private float ResolveReferenceScale()
        {
            if (flyLayer == null) return 1f;
            Rect bounds = flyLayer.rect;
            if (bounds.width <= 1f || bounds.height <= 1f) return 1f;
            return Mathf.Clamp(
                Mathf.Min(bounds.width / ReferenceWidth, bounds.height / ReferenceHeight),
                0.72f,
                1.35f);
        }

        private void HideCoins()
        {
            if (coinImages != null)
            {
                for (int i = 0; i < coinImages.Length; i++)
                {
                    if (coinImages[i] != null) coinImages[i].enabled = false;
                    if (coinRects != null && i < coinRects.Length && coinRects[i] != null)
                        coinRects[i].localScale = Vector3.zero;
                }
            }
            if (flyLayer != null && flyLayer.gameObject.activeSelf)
                flyLayer.gameObject.SetActive(false);
        }

        private void OnDisable() => CancelAndSnap();

        private void OnDestroy()
        {
            CancelAndSnap();
            StopOwnedMotion();
            StopTargetPunch(false);
            LevelAdvanceReady = null;
            PresentationCompleted = null;
            PresentationInterrupted = null;
        }
    }
}
