using System;
using System.Collections;
using BartenderSort.Core;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    public sealed class BartenderTimesUpOfferPresenter : MonoBehaviour
    {
        private const float EntrySeconds = 0.26f;

        private const float CloseAfterPurchaseSeconds = 0.14f;

        private const float TokenLaunchDelaySeconds = 0.12f;

        private const string NotEnoughCoinsMessage = "NOT ENOUGH COINS";
        private const string SavingMessage = "SAVING... TAP X TO RETRY";
        private const string SaveFailedMessage = "SAVE FAILED - TAP X TO RETRY";

        private readonly BsPurchaseOverlayStateMachine machine =
            new BsPurchaseOverlayStateMachine();

        [Header("Authored Times-Up UI")]
        [SerializeField] private GameObject canvasRoot = null;
        [SerializeField] private BartenderMoreLivesPopupView view = null;
        [SerializeField] private BartenderLevelController controller = null;
        [SerializeField] private BartenderSession session = null;

        private OrderStripPresenter orderStrip;

        private bool viewPrepared;
        private BartenderLevelController subscribedController;
        private BsTimeOfferSnapshot? activeOffer;
        private BsTimeOfferSettlementSnapshot? activeSettlement;
        private Tween entryTween;
        private Tween closeTween;
        private Tween shakeTween;
        private Tween expiryTween;
        private Coroutine expiryRoutine;
        private bool expiryCuePending;
        private Vector2 shakeRestPosition;
        private int presentationRevision;

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
            RehydrateOpenOffer();
        }

        private void OnDisable()
        {
            Unsubscribe();
            ResetPresentationOnDisable();
        }

        private void ResolveDependencies()
        {
            if (session == null) session = GetComponent<BartenderSession>();
            if (controller == null) controller = GetComponent<BartenderLevelController>();
            if (orderStrip == null) orderStrip = GetComponent<OrderStripPresenter>();
        }

        private void Subscribe()
        {
            if (subscribedController == controller) return;
            Unsubscribe();
            if (controller == null) return;
            controller.TimeOfferRequested += HandleTimeOfferRequested;
            controller.TimeOfferSettlementChanged += HandleTimeOfferSettlementChanged;
            controller.TimeOfferDecisionResolved += HandleTimeOfferDecisionResolved;
            subscribedController = controller;
        }

        private void Unsubscribe()
        {
            BartenderLevelController source = subscribedController;
            subscribedController = null;
            if (source == null) return;
            source.TimeOfferRequested -= HandleTimeOfferRequested;
            source.TimeOfferSettlementChanged -= HandleTimeOfferSettlementChanged;
            source.TimeOfferDecisionResolved -= HandleTimeOfferDecisionResolved;
        }

        private void RehydrateOpenOffer()
        {
            if (controller != null && controller.TimeOfferDecisionPending) return;
            BsTimeOfferSettlementSnapshot? settlement = controller != null
                ? controller.CurrentTimeOfferSettlement
                : null;
            if (settlement.HasValue && settlement.Value.IsPending)
            {
                ShowPendingSettlement(settlement.Value, false);
                return;
            }

            BsTimeOfferSnapshot? current = controller != null
                ? controller.CurrentTimeOffer
                : null;
            if (current.HasValue) ShowTimeOffer(current.Value, animateExpiry: false);
        }

        private void HandleTimeOfferDecisionResolved()
        {
            if (isActiveAndEnabled && machine.State == BsPurchaseOverlayState.Hidden)
                RehydrateOpenOffer();
        }

        private void HandleTimeOfferRequested(BsTimeOfferSnapshot requestedOffer) =>
            ShowTimeOffer(requestedOffer, animateExpiry: true);

        private void ShowTimeOffer(BsTimeOfferSnapshot requestedOffer, bool animateExpiry)
        {
            if (!isActiveAndEnabled) return;
            BsTimeOfferSnapshot? current = controller != null
                ? controller.CurrentTimeOffer
                : null;
            if (!requestedOffer.Id.IsValid || !current.HasValue
                || current.Value.Id != requestedOffer.Id)
                return;

            if (machine.State != BsPurchaseOverlayState.Hidden)
            {
                // Repeated events for this offer are safe. A different offer cannot replace a visible card.
                if (activeOffer.HasValue
                    && activeOffer.Value.Id == requestedOffer.Id)
                    return;
                Debug.LogError("A second time offer tried to replace an open card.", this);
                return;
            }

            if (!EnsureView())
            {
                return;
            }
            if (!machine.Dispatch(BsPurchaseOverlayTrigger.Show)) return;
            CancelClose();
            presentationRevision++;
            activeOffer = current.Value;
            activeSettlement = null;

            if (animateExpiry && orderStrip != null)
            {
                view.SetButtonsInteractable(false, false);
                view.SetVisible(false);
                expiryTween = orderStrip.PlayTimedOrderExpiredFeedback(requestedOffer.SlotIndex);
                if (expiryTween != null)
                {
                    expiryCuePending = true;
                    expiryRoutine = StartCoroutine(RevealAfterExpiryCue(
                        presentationRevision, requestedOffer.Id,
                        controller.CurrentRoundStamp, expiryTween.Duration(false)));
                    return;
                }
            }

            ShowOfferCard();
        }

        private IEnumerator RevealAfterExpiryCue(
            int revision, BsTimeOfferId offerId,
            BsRoundCommandStamp roundStamp, float duration)
        {
            yield return new WaitForSecondsRealtime(duration);
            if (!OwnsPresentation(revision, offerId)) yield break;

            expiryRoutine = null;
            CancelExpiryCue();
            BsTimeOfferSnapshot? current = controller != null
                ? controller.CurrentTimeOffer : null;
            BsRoundCommandStamp currentStamp = controller != null
                ? controller.CurrentRoundStamp : default;
            if (!current.HasValue || current.Value.Id != offerId
                || !roundStamp.IsValid
                || currentStamp.AttemptId != roundStamp.AttemptId
                || currentStamp.Token != roundStamp.Token)
            {
                Hide(false);
                yield break;
            }

            ShowOfferCard();
        }

        private void ShowOfferCard()
        {
            canvasRoot.SetActive(true);
            view.SetFeedback(string.Empty);
            view.SetButtonsInteractable(true, true);
            view.SetVisible(true);
            BsAudio.Instance?.Play(BsSfx.PopupOpen, 0.85f);
            PlayEntry();
        }

        private void HandleTimeOfferSettlementChanged(
            BsTimeOfferSettlementSnapshot settlement)
        {
            // An earlier listener may finish settlement inside this callback. Do not let the older event
            // reopen its UI.
            BsTimeOfferSettlementSnapshot? current = controller != null
                ? controller.CurrentTimeOfferSettlement
                : null;
            if (!SettlementMatches(current, settlement)) return;

            if (settlement.IsPending)
            {
                ShowPendingSettlement(settlement, false);
                return;
            }

            if (!settlement.TerminalCommitted
                || !OwnsSettlement(settlement.OfferId))
                return;

            if (machine.State == BsPurchaseOverlayState.PurchasePending)
                machine.Dispatch(BsPurchaseOverlayTrigger.PurchaseSucceeded);
            else if (machine.State == BsPurchaseOverlayState.Visible)
                machine.Dispatch(BsPurchaseOverlayTrigger.Dismiss);
            Hide();
        }

        private void ShowPendingSettlement(
            BsTimeOfferSettlementSnapshot settlement,
            bool retryFailed)
        {
            if (!settlement.OfferId.IsValid || !settlement.IsPending
                || !EnsureView())
                return;

            if (machine.State != BsPurchaseOverlayState.Hidden
                && !OwnsSettlement(settlement.OfferId))
            {
                Debug.LogError(
                    "A different time-offer settlement tried to replace this card.",
                    this);
                return;
            }

            CancelExpiryCue();
            CancelClose();
            bool wasHidden = machine.State == BsPurchaseOverlayState.Hidden;
            bool wasVisible = view.gameObject.activeSelf && canvasRoot.activeSelf;
            if (wasHidden
                && !machine.Dispatch(BsPurchaseOverlayTrigger.Show))
                return;
            if (machine.State == BsPurchaseOverlayState.Visible
                && !machine.Dispatch(BsPurchaseOverlayTrigger.BeginPurchase))
                return;
            if (machine.State != BsPurchaseOverlayState.PurchasePending)
                return;

            if (wasHidden) presentationRevision++;
            activeOffer = null;
            activeSettlement = settlement;
            canvasRoot.SetActive(true);
            view.SetFeedback(retryFailed ? SaveFailedMessage : SavingMessage);
            view.SetButtonsInteractable(true, false);
            view.SetVisible(true);
            if (wasVisible) return;
            BsAudio.Instance?.Play(BsSfx.PopupOpen, 0.85f);
            PlayEntry();
        }

        private async void HandleAddTimePressed()
        {
            if (machine.State != BsPurchaseOverlayState.Visible || controller == null
                || !activeOffer.HasValue || expiryCuePending)
                return;
            if (!machine.Dispatch(BsPurchaseOverlayTrigger.BeginPurchase)) return;

            view.SetButtonsInteractable(false, false);
            view.SetFeedback(string.Empty);

            BsTimeOfferSnapshot offer = activeOffer.Value;
            int cost = offer.CoinCost;
            if (!BartenderProgressService.CanAfford(cost))
            {
                machine.Dispatch(BsPurchaseOverlayTrigger.PurchaseRejected);
                BsAudio.NotEnoughCoins();
                BartenderHaptics.Light();
                view.SetButtonsInteractable(true, true);
                view.SetFeedback(NotEnoughCoinsMessage);

                if (!BartenderShopPresenter.TryOpenCurrentScene()) ShakeCard();
                return;
            }

            int revision = presentationRevision;
            BartenderLevelController source = controller;
            orderStrip?.PrepareTimeBoostSource(
                view.RefillButton != null ? view.RefillButton.transform : null,
                TokenLaunchDelaySeconds);
            BsTimeOfferAcceptResult result;
            try
            {
                result = await source.AcceptTimeOfferAsync(offer.Id);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                result = BsTimeOfferAcceptResult.RejectPurchase(
                    offer.Id, "The time offer purchase could not be completed");
            }
            if (!result.Accepted) orderStrip?.ClearTimeBoostSource();
            if (!CanCompleteDecision(source, revision, offer.Id)) return;

            if (result.Accepted)
            {
                machine.Dispatch(BsPurchaseOverlayTrigger.PurchaseSucceeded);
                BsAudio.Instance?.Play(BsSfx.Check);
                CloseAfterPurchase();
                return;
            }

            BsTimeOfferSnapshot? current = controller.CurrentTimeOffer;
            if (!current.HasValue || current.Value.Id != offer.Id)
            {
                Hide(false);
                return;
            }

            machine.Dispatch(BsPurchaseOverlayTrigger.PurchaseRejected);
            view.SetButtonsInteractable(true, true);
            view.SetFeedback(string.IsNullOrEmpty(result.RejectionReason)
                ? "TRY AGAIN"
                : result.RejectionReason);
            BsAudio.Instance?.Play(BsSfx.Invalid, 0.5f);
            BartenderHaptics.Light();
            ShakeCard();
        }

        private async void HandleClosePressed()
        {
            if (controller == null || expiryCuePending) return;

            if (machine.State == BsPurchaseOverlayState.PurchasePending
                && activeSettlement.HasValue
                && activeSettlement.Value.IsPending)
            {
                RetryPendingSettlement(activeSettlement.Value);
                return;
            }

            if (machine.State != BsPurchaseOverlayState.Visible
                || !activeOffer.HasValue)
                return;
            if (!machine.Dispatch(BsPurchaseOverlayTrigger.BeginPurchase)) return;

            view.SetButtonsInteractable(false, false);
            BsTimeOfferId offerId = activeOffer.Value.Id;
            int revision = presentationRevision;
            BartenderLevelController source = controller;
            bool decisionAccepted;
            if (session != null)
            {
                try
                {
                    decisionAccepted = await session
                        .RequestDeclineTimeOfferAndPresentFailureAsync(offerId);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                    decisionAccepted = false;
                }
            }
            else
            {
                Debug.LogError(
                    "Times-Up close requires its authored BartenderSession reference.",
                    this);
                decisionAccepted = false;
            }

            if (!CanCompleteDecision(source, revision, offerId)) return;
            if (!decisionAccepted)
            {
                BsTimeOfferSnapshot? current = controller.CurrentTimeOffer;
                if (!current.HasValue || current.Value.Id != offerId)
                {
                    Hide(false);
                    return;
                }

                machine.Dispatch(BsPurchaseOverlayTrigger.PurchaseRejected);
                view.SetButtonsInteractable(true, true);
                view.SetFeedback("TRY AGAIN");
                BsAudio.Instance?.Play(BsSfx.Invalid, 0.5f);
                BartenderHaptics.Light();
                ShakeCard();
                return;
            }

            if (!isActiveAndEnabled) return;

            BsTimeOfferSettlementSnapshot? settlement =
                controller.CurrentTimeOfferSettlement;
            if (settlement.HasValue && settlement.Value.OfferId == offerId)
            {
                if (settlement.Value.IsPending)
                {
                    ShowPendingSettlement(settlement.Value, false);
                    return;
                }

                if (settlement.Value.TerminalCommitted)
                {
                    if (machine.State == BsPurchaseOverlayState.PurchasePending)
                        machine.Dispatch(BsPurchaseOverlayTrigger.PurchaseSucceeded);
                    else if (machine.State == BsPurchaseOverlayState.Visible)
                        machine.Dispatch(BsPurchaseOverlayTrigger.Dismiss);
                    Hide();
                    return;
                }
            }

            // Never reopen an accepted decision. If another listener resolved it, only update the view.
            Hide(false);
        }

        private void RetryPendingSettlement(
            BsTimeOfferSettlementSnapshot expectedSettlement)
        {
            BsTimeOfferSettlementSnapshot? current =
                controller.CurrentTimeOfferSettlement;
            if (!SettlementMatches(current, expectedSettlement)
                || !expectedSettlement.IsPending)
            {
                Hide(false);
                return;
            }

            view.SetButtonsInteractable(false, false);
            view.SetFeedback(SavingMessage);

            int revision = presentationRevision;
            bool retryAccepted;
            if (expectedSettlement.Disposition
                    == BsTimeOfferDeclineDisposition.ReturnToMainMenu
                && session != null)
            {
                retryAccepted =
                    session.RequestRetryDeclinedTimeOfferAndReturnToMainMenu(
                        expectedSettlement.OfferId);
            }
            else
            {
                retryAccepted = controller.RetryTimeOfferSettlement(
                    expectedSettlement.OfferId).Accepted;
            }

            // Retry may finish settlement or disable this view immediately. Read the controller snapshot
            // after those callbacks return.
            if (!OwnsPresentation(revision, expectedSettlement.OfferId)) return;
            current = controller.CurrentTimeOfferSettlement;
            if (!current.HasValue
                || current.Value.OfferId != expectedSettlement.OfferId
                || current.Value.TerminalCommitted)
            {
                Hide();
                return;
            }

            ShowPendingSettlement(current.Value, !retryAccepted);
            if (retryAccepted) return;
            BsAudio.Instance?.Play(BsSfx.Invalid, 0.5f);
            BartenderHaptics.Light();
            ShakeCard();
        }

        private bool OwnsSettlement(BsTimeOfferId offerId) =>
            (activeSettlement.HasValue
             && activeSettlement.Value.OfferId == offerId)
            || (activeOffer.HasValue && activeOffer.Value.Id == offerId);

        private bool OwnsPresentation(int revision, BsTimeOfferId offerId) =>
            this != null && isActiveAndEnabled && revision == presentationRevision
            && OwnsSettlement(offerId);

        private bool CanCompleteDecision(
            BartenderLevelController source, int revision, BsTimeOfferId offerId)
        {
            // A disabled or newer presentation owns its own cleanup. An old callback must not hide it.
            if (!OwnsPresentation(revision, offerId)) return false;
            if (controller == source) return true;

            Hide(false);
            Subscribe();
            RehydrateOpenOffer();
            return false;
        }

        internal static bool SettlementMatches(
            BsTimeOfferSettlementSnapshot? current,
            BsTimeOfferSettlementSnapshot expected) =>
            current.HasValue
            && current.Value.OfferId == expected.OfferId
            && current.Value.Disposition == expected.Disposition
            && current.Value.Status == expected.Status;

        private void ResetPresentationOnDisable()
        {
            Hide(false);
        }

        private void Hide(bool playCloseSound = true)
        {
            presentationRevision++;
            CancelExpiryCue();
            CancelEntry();
            CancelShake();
            CancelClose();
            bool wasVisible = playCloseSound && view != null
                && view.gameObject.activeSelf
                && (canvasRoot == null || canvasRoot.activeSelf);

            // Reset before SetActive(false), which may call OnDisable immediately. That callback must not
            // cancel an already-resolved offer.
            machine.Dispatch(BsPurchaseOverlayTrigger.Reset);
            activeOffer = null;
            activeSettlement = null;
            if (view != null) view.SetVisible(false);
            if (canvasRoot != null && canvasRoot.activeSelf)
                canvasRoot.SetActive(false);
            if (wasVisible) BsAudio.Instance?.Play(BsSfx.PopupClose, 0.8f);
        }

        private void CancelExpiryCue()
        {
            expiryCuePending = false;
            Coroutine routine = expiryRoutine;
            expiryRoutine = null;
            if (routine != null) StopCoroutine(routine);
            Tween tween = expiryTween;
            expiryTween = null;
            if (tween != null && tween.IsActive()) tween.Kill(false);
        }

        private void PlayEntry()
        {
            CancelEntry();
            CancelShake();
            RectTransform pivot = view.CardPivot;
            if (pivot == null) return;
            pivot.localScale = Vector3.one * 0.78f;
            Tween entry = pivot.DOScale(Vector3.one, EntrySeconds)
                .SetEase(Ease.OutBack).SetUpdate(true).SetRecyclable(true);
            entryTween = entry;
            entry.OnKill(() =>
            {
                if (!ReferenceEquals(entryTween, entry)) return;
                entryTween = null;
                if (pivot != null) pivot.localScale = Vector3.one;
            });
        }

        private void CloseAfterPurchase()
        {
            RectTransform pivot = view != null ? view.CardPivot : null;
            CanvasGroup overlay = view != null ? view.OverlayGroup : null;
            if (!isActiveAndEnabled || pivot == null || overlay == null
                || canvasRoot == null || !canvasRoot.activeSelf)
            {
                Hide();
                return;
            }

            presentationRevision++;
            CancelExpiryCue();
            CancelEntry();
            CancelShake();
            CancelClose();
            machine.Dispatch(BsPurchaseOverlayTrigger.Reset);
            activeOffer = null;
            activeSettlement = null;
            view.SetButtonsInteractable(false, false);
            overlay.interactable = false;
            overlay.blocksRaycasts = false;
            BsAudio.Instance?.Play(BsSfx.PopupClose, 0.8f);

            Sequence close = DOTween.Sequence().SetUpdate(true).SetRecyclable(true);
            close.Join(pivot.DOScale(0.85f, CloseAfterPurchaseSeconds).SetEase(Ease.InQuad));
            close.Join(overlay.DOFade(0f, CloseAfterPurchaseSeconds).SetEase(Ease.InQuad));
            closeTween = close;
            close.OnKill(() =>
            {
                if (!ReferenceEquals(closeTween, close)) return;
                closeTween = null;
                FinishClose();
            });
        }

        private void CancelClose()
        {
            Tween close = closeTween;
            if (close == null) return;
            closeTween = null;
            if (close.IsActive()) close.Kill(false);
            FinishClose();
        }

        private void FinishClose()
        {
            if (view != null)
            {
                if (view.CardPivot != null) view.CardPivot.localScale = Vector3.one;
                view.SetVisible(false);
            }
            if (canvasRoot != null && canvasRoot.activeSelf) canvasRoot.SetActive(false);
        }

        private void ShakeCard()
        {
            if (!isActiveAndEnabled || machine.State == BsPurchaseOverlayState.Hidden) return;
            RectTransform pivot = view != null ? view.CardPivot : null;
            if (pivot == null) return;
            CancelShake();
            shakeRestPosition = pivot.anchoredPosition;
            Tween shake = pivot.DOShakeAnchorPos(0.3f, new Vector2(18f, 0f), 12, 0f, false, true)
                .SetUpdate(true).SetRecyclable(true);
            shakeTween = shake;
            shake.OnKill(() =>
            {
                if (!ReferenceEquals(shakeTween, shake)) return;
                shakeTween = null;
                if (pivot != null) pivot.anchoredPosition = shakeRestPosition;
            });
        }

        private void CancelEntry()
        {
            Tween entry = entryTween;
            entryTween = null;
            if (entry != null && entry.IsActive()) entry.Kill(false);
            if (view != null && view.CardPivot != null)
                view.CardPivot.localScale = Vector3.one;
        }

        private void CancelShake()
        {
            Tween shake = shakeTween;
            shakeTween = null;
            if (shake == null) return;
            if (shake.IsActive()) shake.Kill(false);
            if (view != null && view.CardPivot != null)
                view.CardPivot.anchoredPosition = shakeRestPosition;
        }

        private bool EnsureView()
        {
            if (canvasRoot == null || view == null)
            {
                Debug.LogError("Time offer view missing.",
                    this);
                return false;
            }

            if (!view.IsReady)
            {
                Debug.LogError("Time offer bindings missing.",
                    view);
                return false;
            }

            if (viewPrepared) return true;
            viewPrepared = true;

            RebindButton(view.RefillButton, HandleAddTimePressed);
            RebindButton(view.CloseButton, HandleClosePressed);

            view.CoinBalance.SetMoment(
                BartenderCoinBalanceView.BalanceMoment.Current);

            view.SetVisible(false);
            canvasRoot.SetActive(false);
            return true;
        }

        private static void RebindButton(Button button, UnityEngine.Events.UnityAction action)
        {
            if (button == null) return;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(action);
        }
    }
}
