using System.Collections;
using BartenderSort.Core;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>Shows the controller's time offer. Low coins opens the shop; X returns home.</summary>
    [DisallowMultipleComponent]
    public sealed class BartenderTimesUpOfferPresenter : MonoBehaviour
    {
        private const float EntrySeconds = 0.26f;

        // Card feedback makes X's exit role clear.
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
        private bool subscribed;
        private BsTimeOfferSnapshot? activeOffer;
        private BsTimeOfferSettlementSnapshot? activeSettlement;
        private Tween entryTween;
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
            if (subscribed || controller == null) return;
            controller.TimeOfferRequested += HandleTimeOfferRequested;
            controller.TimeOfferSettlementChanged += HandleTimeOfferSettlementChanged;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed || controller == null) return;
            controller.TimeOfferRequested -= HandleTimeOfferRequested;
            controller.TimeOfferSettlementChanged -= HandleTimeOfferSettlementChanged;
            subscribed = false;
        }

        // Offer flow.

        private void RehydrateOpenOffer()
        {
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
                // Disabling a view is not a decline. The controller keeps the offer so the same card can
                // resume.
                return;
            }
            if (!machine.Dispatch(BsPurchaseOverlayTrigger.Show)) return;
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
            // The offer already owns the gameplay barrier. Only its visual entrance is delayed.
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
            // Play a cue when the time offer appears.
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
            // The decision is consumed. Add Time stays closed; X only retries this exact pending
            // settlement.
            view.SetButtonsInteractable(true, false);
            view.SetVisible(true);
            if (wasVisible) return;
            BsAudio.Instance?.Play(BsSfx.PopupOpen, 0.85f);
            PlayEntry();
        }

        private void HandleAddTimePressed()
        {
            if (machine.State != BsPurchaseOverlayState.Visible || controller == null
                || !activeOffer.HasValue || expiryCuePending)
                return;
            if (!machine.Dispatch(BsPurchaseOverlayTrigger.BeginPurchase)) return;

            view.SetButtonsInteractable(false, false);
            // Clear the old balance message; add it back below only if still needed.
            view.SetFeedback(string.Empty);

            // Too few coins opens the shop above this card without ending the round. Both keep their own
            // barriers, and X remains the exit.
            BsTimeOfferSnapshot offer = activeOffer.Value;
            int cost = offer.CoinCost;
            if (!BartenderProgressService.CanAfford(cost))
            {
                machine.Dispatch(BsPurchaseOverlayTrigger.PurchaseRejected);
                BsAudio.Instance?.Play(BsSfx.Invalid, 0.5f);
                BartenderHaptics.Light();
                view.SetButtonsInteractable(true, true);
                view.SetFeedback(NotEnoughCoinsMessage);

                // Shake the card if no shop can open so the tap still gets feedback.
                if (!BartenderShopPresenter.TryOpenCurrentScene()) ShakeCard();
                return;
            }

            int revision = presentationRevision;
            BsTimeOfferAcceptResult result = controller.TryAcceptTimeOffer(offer.Id);
            // Controller callbacks may disable this presenter. OnDisable already cleans up its view in that
            // case.
            if (!OwnsPresentation(revision, offer.Id)) return;

            if (result.Accepted)
            {
                machine.Dispatch(BsPurchaseOverlayTrigger.PurchaseSucceeded);
                BsAudio.Instance?.Play(BsSfx.Check);
                Hide();
                return;
            }

            BsTimeOfferSnapshot? current = controller.CurrentTimeOffer;
            if (!current.HasValue || current.Value.Id != offer.Id)
            {
                Hide(false);
                return;
            }

            // A temporary rule can reject an affordable purchase. Keep the card open for another try.
            machine.Dispatch(BsPurchaseOverlayTrigger.PurchaseRejected);
            view.SetButtonsInteractable(true, true);
            view.SetFeedback(string.IsNullOrEmpty(result.RejectionReason)
                ? "TRY AGAIN"
                : result.RejectionReason);
            BsAudio.Instance?.Play(BsSfx.Invalid, 0.5f);
            BartenderHaptics.Light();
            ShakeCard();
        }

        private void HandleClosePressed()
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

            view.SetButtonsInteractable(false, false);
            BsTimeOfferId offerId = activeOffer.Value.Id;
            int revision = presentationRevision;
            bool decisionAccepted;
            if (session != null)
            {
                decisionAccepted =
                    session.RequestDeclineTimeOfferAndReturnToMainMenu(offerId);
            }
            else
            {
                // Session owns the return-home intent. If its wiring fails, leave the offer retryable
                // instead of using the controller's default failure-card route.
                Debug.LogError(
                    "Times-Up close requires its authored BartenderSession reference.",
                    this);
                decisionAccepted = false;
            }

            if (!OwnsPresentation(revision, offerId)) return;
            if (!decisionAccepted)
            {
                BsTimeOfferSnapshot? current = controller.CurrentTimeOffer;
                if (!current.HasValue || current.Value.Id != offerId)
                {
                    Hide(false);
                    return;
                }

                view.SetButtonsInteractable(true, true);
                view.SetFeedback("TRY AGAIN");
                BsAudio.Instance?.Play(BsSfx.Invalid, 0.5f);
                BartenderHaptics.Light();
                ShakeCard();
                return;
            }

            // Callbacks may disable this view and reset its state before returning.
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

            ShowPendingSettlement(current.Value, true);
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
            isActiveAndEnabled && revision == presentationRevision
            && OwnsSettlement(offerId);

        internal static bool SettlementMatches(
            BsTimeOfferSettlementSnapshot? current,
            BsTimeOfferSettlementSnapshot expected) =>
            current.HasValue
            && current.Value.OfferId == expected.OfferId
            && current.Value.Disposition == expected.Disposition
            && current.Value.Status == expected.Status;

        /// <summary>
        /// Disable clears only the view state. The controller keeps the offer barrier and settlement so
        /// enabling can restore the same card.
        /// </summary>
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

        // Card visuals.

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

        // Setup.

        private bool EnsureView()
        {
            if (canvasRoot == null || view == null)
            {
                Debug.LogError("Süre teklifi Canvas/View referansları Hierarchy'de bağlı değil.",
                    this);
                return false;
            }

            if (!view.IsReady)
            {
                Debug.LogError("Süre teklifi kartının zorunlu UI referansları eksik.",
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
