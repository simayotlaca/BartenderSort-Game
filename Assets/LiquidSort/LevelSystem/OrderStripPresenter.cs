using System;
using System.Collections.Generic;
using BartenderSort.Core;
using DG.Tweening;
using UnityEngine;

namespace LiquidSort.Levels
{
    [DefaultExecutionOrder(300)]
    [DisallowMultipleComponent]
    public sealed class OrderStripPresenter : MonoBehaviour
    {
        private const float DealStagger = 0.045f;

        private const float OrderBellVolume = 0.78f;
        private const float OrderExpiredVolume = 0.85f;
        private const float DeliveryGlowHold = 0.10f;
        private const float DeliveryExitDuration = 0.20f;
        private const float QueueShiftDuration = 0.23f;
        private const float QueueShiftStagger = 0.025f;
        private const float QueueWatchdogGrace = 0.75f;

        [Header("Rig references")]
        [Tooltip("Uses a component on this object if empty.")]
        [SerializeField] private BartenderLevelController controller;
        [Tooltip("Looks for the controller on this object if empty.")]
        [SerializeField] private BartenderShelfLevelView shelfView;

        [Header("Card slots")]
        [Tooltip("Slots from left to right. Uses the level's OrderSlots count.")]
        [SerializeField] private OrderCardView[] cards = new OrderCardView[0];
        [Tooltip("Slide cards in from the right. Disable to place them instantly.")]
        [SerializeField] private bool animateInitialDeal;

        [Header("Timer warning")]
        [Tooltip("Start ticking below this time. Match the card's critical colour threshold.")]
        [SerializeField, Min(0f)] private float tickWindowSeconds = 5f;
        [Tooltip("Disable ticking while keeping the red timer warning.")]
        [SerializeField] private bool playCountdownTicks = true;
        [Tooltip("Controls the time-boost flight layer and spark pool.")]
        [SerializeField] private TimeBoostFlightPresenter timeBoostFlight;

        private const int TickPanicSeconds = 3;
        private static readonly float[] TickPitchRamp = { 1.00f, 1.04f, 1.09f, 1.15f };
        private static readonly float[] TickVolumeRamp = { 0.62f, 0.72f, 0.85f, 0.98f };

        private int lastTickedSecond = -1;
        private int lastTickedOrderIndex = -1;

        private BartenderLevelController subscribedController;
        private BartenderLevelController presentationBarrierController;
        private int controllerSubscriptionGeneration;
        private Action<BsLevel> levelLoadedSubscription;
        private Action controllerOrdersSubscription;
        private Action<BartenderBoardChange> controllerBoardSubscription;
        private Action<BartenderLevelState> levelStateSubscription;
        private Action<float> timeBoostedSubscription;
        private OrderStripSignalQueue signalQueue;
        private readonly BsOrderStripStateMachine presentationState =
            new BsOrderStripStateMachine();
        private int presentationEpoch;

        private int deferredAtFrame = -1;
        private float deferredAtUnscaledTime = -1f;
        private int pendingDeliveredSlot = -1;
        private BartenderDeliveryReceipt pendingDeliveryReceipt;
        private readonly DeliveryPoofPresentation deliveryPoof = new DeliveryPoofPresentation();
        private bool deliveryPoofWaiting;
        private int deliveryPoofEpoch;
        private bool snapshotDirty;
        private float dealCompletionAtUnscaledTime = -1f;
        private int dealCompletionEpoch = -1;

        private Vector2[] slotPositions = new Vector2[0];
        private bool slotPositionsCaptured;
        private bool hasPresentedLiveLevel;
        private int transitionSlotCount;
        private bool[] transitionRemoved = new bool[0];
        private readonly HashSet<OrderCardView> shiftingCards = new HashSet<OrderCardView>();
        private readonly List<OrderCardView> cardPartitionScratch = new List<OrderCardView>(4);
        private Sequence queueTransition;
        private float queueWatchdogAtUnscaledTime = -1f;
        private BsLevel lastCapacityFaultLevel;
        private int lastCapacityFaultCardCount = -1;

        // A delivered card never holds input. Deliveries that land while a card is stamped leave with it in one
        // batch; later ones wait for the next batch. Each batch keeps the board slots right after its last delivery.
        private const float StampHoldWatchdogSeconds = 1.5f;

        private sealed class DeliveryBatch
        {
            public readonly List<BartenderDeliveryReceipt> Receipts = new List<BartenderDeliveryReceipt>(3);
            public readonly List<int> OrderIndices = new List<int>(3);
            public OrderDef[] SlotsAfter;

            public void Add(BartenderDeliveryReceipt receipt, int orderIndex, OrderDef[] slotsAfter)
            {
                Receipts.Add(receipt);
                OrderIndices.Add(orderIndex);
                SlotsAfter = slotsAfter;
            }
        }

        private sealed class JoinedDelivery
        {
            public BartenderDeliveryReceipt Receipt;
            public OrderCardView Card;
            public int OrderIndex;
            public bool Finished;
        }

        private DeliveryBatch activeBatch;
        private readonly List<DeliveryBatch> pendingBatches = new List<DeliveryBatch>(2);
        private readonly List<JoinedDelivery> joinedDeliveries = new List<JoinedDelivery>(3);
        private float stampHoldWatchdogAtUnscaledTime = -1f;
        private bool stampHoldForced;

        public IReadOnlyList<OrderCardView> Cards => cards;
        public bool TransitionPlaying => presentationState.TransitionPlaying;
        public bool PresentationActive =>
            TransitionPlaying || activeBatch != null || pendingBatches.Count > 0;

        private void Awake()
        {
            EnsureSignalQueue();
            ResolveDependencies();
            CaptureSlotPositions();
        }

        private void OnEnable()
        {
            EnsureSignalQueue();
            ResolveDependencies();
            CaptureSlotPositions();
            Subscribe();
            signalQueue.Enqueue(OrderStripSignal.Activate());
        }

        private void OnDisable()
        {
            Unsubscribe();
            EnsureSignalQueue();
            signalQueue.Enqueue(OrderStripSignal.Deactivate());
        }

        private void OnDestroy()
        {
            try { deliveryPoof.Dispose(); }
            finally { ReleaseControllerPresentationBarrier(); }
        }

        public bool ValidateBindings(out string reason)
        {
            if (controller == null)
            {
                reason = "Level controller missing.";
                return false;
            }
            if (cards == null || cards.Length == 0)
            {
                reason = "Order cards missing.";
                return false;
            }
            if (timeBoostFlight == null)
            {
                reason = "Time boost flight missing.";
                return false;
            }
            if (shelfView != null && shelfView.Controller == null)
            {
                reason = "Shelf view controller missing.";
                return false;
            }
            if (shelfView != null
                && !ReferenceEquals(shelfView.Controller, controller))
            {
                reason = "Order strip and shelf use different controllers.";
                return false;
            }
            for (int i = 0; i < cards.Length; i++)
            {
                if (cards[i] == null)
                {
                    reason = $"Order card {i} missing.";
                    return false;
                }
                if (!cards[i].IsReady())
                {
                    reason = $"Order card {i} is incomplete.";
                    return false;
                }
                for (int previous = 0; previous < i; previous++)
                {
                    if (!ReferenceEquals(cards[previous], cards[i])) continue;
                    reason = $"Order card {i} is duplicated.";
                    return false;
                }
            }

            BsLevel capacityLevel = controller.CurrentLevel;
            int requiredCapacity = capacityLevel != null
                ? Mathf.Max(1, capacityLevel.OrderSlots)
                : 0;
            BsLevel[] campaign = Resources.LoadAll<BsLevel>("Levels");
            for (int i = 0; i < campaign.Length; i++)
            {
                BsLevel level = campaign[i];
                if (level == null || Mathf.Max(1, level.OrderSlots) <= requiredCapacity)
                    continue;
                requiredCapacity = Mathf.Max(1, level.OrderSlots);
                capacityLevel = level;
            }
            if (cards.Length < requiredCapacity)
            {
                string levelLabel = capacityLevel != null
                    ? $"Level {capacityLevel.Index}"
                    : "Campaign";
                reason = $"{levelLabel} needs {requiredCapacity} cards.";
                return false;
            }
            reason = null;
            return true;
        }

        [ContextMenu("Validate Order Strip Bindings")]
        private void ValidateFromContextMenu()
        {
            if (!ValidateBindings(out string reason))
                Debug.LogError("Order strip binding error: " + reason, this);
        }

        private void LateUpdate()
        {
            StepDeliveryPoof();
            switch (presentationState.State)
            {
                case BsOrderStripState.Dealing:
                    if (dealCompletionEpoch == presentationEpoch
                        && Time.unscaledTime >= dealCompletionAtUnscaledTime)
                        EnqueueDealAnimationFinished(presentationEpoch);
                    break;

                case BsOrderStripState.StampHold:
                    StepStampHold();
                    break;

                case BsOrderStripState.QueueAnimating:
                    if (queueWatchdogAtUnscaledTime >= 0f
                        && Time.unscaledTime >= queueWatchdogAtUnscaledTime)
                        EnqueueQueueAnimationAborted(
                            presentationEpoch, pendingDeliveryReceipt);
                    break;

                case BsOrderStripState.Faulted:
                case BsOrderStripState.Detached:
                case BsOrderStripState.Hidden:
                    return;

                case BsOrderStripState.Ready:
                    break;
            }

            // The order clock keeps running through deals and deliveries, so live cards follow it in every
            // state that shows them. A signal above may already have hidden the strip.
            switch (presentationState.State)
            {
                case BsOrderStripState.Ready:
                case BsOrderStripState.Dealing:
                case BsOrderStripState.StampHold:
                case BsOrderStripState.QueueAnimating:
                    TickTimers();
                    return;
            }
        }

        public void Refresh()
        {
            EnsureSignalQueue();
            signalQueue.Enqueue(OrderStripSignal.SnapshotDirty());
        }

        private float ApplySnapshot(bool forceDeal, bool suppressEntrances, OrderDef[] slotSource = null)
        {
            BsLevel level = controller != null ? controller.CurrentLevel : null;
            BsPalette palette = controller != null ? controller.Palette : null;
            bool live = level != null && controller.State != BartenderLevelState.Unloaded
                        && controller.State != BartenderLevelState.CampaignComplete;
            if (live && !EnsureCardCapacity(level)) return 0f;
            if (cards == null) return 0f;
            CaptureSlotPositions();

            int slots = live ? Mathf.Max(1, level.OrderSlots) : 0;
            bool timed = live && level.AllowTimedOrders;
            bool initialLivePresentation = live && !hasPresentedLiveLevel;
            bool suppressInitialMotion = initialLivePresentation && !animateInitialDeal;
            bool dealLiveLevel = !suppressInitialMotion
                                 && (forceDeal || initialLivePresentation);
            int visibleCardCount = VisibleOrderCount(slots, slotSource);
            // Highlights follow the order identity on the live board, whichever slot it sits in there.
            BsBoard liveBoard = live ? controller.Board : null;

            int visibleOrdinal = 0;
            int dealIndex = 0;
            float longestDealDuration = 0f;

            for (int i = 0; i < cards.Length; i++)
            {
                OrderCardView card = cards[i];
                if (card == null) continue;

                card.Initialize(palette);

                bool inUse = i < slots;
                OrderDef order = inUse ? SlotOrder(i, slotSource) : null;
                bool changed = !SameOrder(card.Model, order);
                bool wasEmpty = card.Model == null;
                bool visible = inUse && order != null;

                Vector2 restingPosition = slotPositions[i];
                if (visible)
                {
                    restingPosition.x = CenteredSlotX(
                        slotPositions, visibleCardCount, visibleOrdinal);
                    visibleOrdinal++;
                }
                card.SetRestingPosition(restingPosition, true);

                card.SetOrder(order, timed);
                bool deal = visible && !suppressEntrances && !suppressInitialMotion
                            && (dealLiveLevel || wasEmpty || changed);
                card.SetVisible(visible,
                    !deal && !suppressEntrances && !suppressInitialMotion);
                card.SetHighlighted(order != null && HasMatchingGlass(liveBoard, order));
                if (deal)
                {
                    Tween tween = card.PlayDealIn(dealIndex * DealStagger);
                    if (tween != null)
                        longestDealDuration = Mathf.Max(
                            longestDealDuration, tween.Duration(false));
                    dealIndex++;
                }
                if (visible && timed) PrimeCardTimer(card, i);
            }

            hasPresentedLiveLevel = live;
            return longestDealDuration;
        }

        private OrderDef SlotOrder(int slot, OrderDef[] slotSource)
        {
            if (slotSource == null) return controller != null ? controller.OrderAtSlot(slot) : null;
            return slot >= 0 && slot < slotSource.Length ? slotSource[slot]?.Clone() : null;
        }

        internal static float CenteredSlotX(IReadOnlyList<Vector2> authoredPositions,
                                            int visibleCount,
                                            int visibleOrdinal)
        {
            if (authoredPositions == null || authoredPositions.Count == 0) return 0f;

            int authoredCount = authoredPositions.Count;
            int count = Mathf.Clamp(visibleCount, 1, authoredCount);
            int ordinal = Mathf.Clamp(visibleOrdinal, 0, count - 1);
            float left = authoredPositions[0].x;
            float right = authoredPositions[authoredCount - 1].x;
            float center = (left + right) * 0.5f;
            float spacing = authoredCount > 1
                ? (right - left) / (authoredCount - 1)
                : 0f;
            return center + (ordinal - (count - 1) * 0.5f) * spacing;
        }

        private bool EnsureCardCapacity(BsLevel level)
        {
            int available = cards != null ? cards.Length : 0;
            int required = level != null ? Mathf.Max(1, level.OrderSlots) : 0;
            if (available >= required)
            {
                lastCapacityFaultLevel = null;
                lastCapacityFaultCardCount = -1;
                return true;
            }

            presentationState.Dispatch(BsOrderStripTrigger.BindingRejected);
            HideCardsImmediate(true);
            if (!ReferenceEquals(lastCapacityFaultLevel, level)
                || lastCapacityFaultCardCount != available)
            {
                lastCapacityFaultLevel = level;
                lastCapacityFaultCardCount = available;
                Debug.LogError($"Order strip needs {required} cards; {available} bound.", this);
            }
            return false;
        }

        private void BeginQueueTransition()
        {
            if (presentationState.State != BsOrderStripState.QueueAnimating) return;
            CaptureSlotPositions();
            CompletePendingDeals();
            dealCompletionAtUnscaledTime = -1f;
            dealCompletionEpoch = -1;

            int slotCount = ActiveSlotCount();
            DeliveryBatch batch = activeBatch;
            BartenderDeliveryReceipt deliveryReceipt = pendingDeliveryReceipt;
            if (cards == null || slotCount <= 0 || slotCount > cards.Length
                || batch == null || deliveryReceipt == null)
            {
                FailClosedQueueTransition();
                return;
            }

            // Every card of the batch leaves, found by order identity; survivors keep their order.
            if (transitionRemoved.Length != cards.Length) transitionRemoved = new bool[cards.Length];
            Array.Clear(transitionRemoved, 0, transitionRemoved.Length);
            int firstRemoved = -1;
            for (int i = 0; i < slotCount; i++)
            {
                OrderCardView card = cards[i];
                if (card == null || card.Model == null
                    || !batch.OrderIndices.Contains(card.Model.RuntimeOrderIndex)) continue;
                transitionRemoved[i] = true;
                if (firstRemoved < 0) firstRemoved = i;
            }
            if (firstRemoved < 0)
            {
                FailClosedQueueTransition();
                return;
            }

            transitionSlotCount = slotCount;
            shiftingCards.Clear();

            int postDeliveryVisibleCount = VisibleOrderCount(slotCount, batch.SlotsAfter);
            float transitionDuration = DeliveryExitDuration;
            int survivorOrdinal = 0;
            int survivorsAfterFirstRemoved = 0;
            for (int i = 0; i < slotCount; i++)
            {
                OrderCardView card = cards[i];
                if (transitionRemoved[i])
                {
                    card.PlayQueueExit(DeliveryExitDuration);
                    continue;
                }
                if (card == null || card.Model == null) continue;

                // Move surviving cards straight to their final centred positions to avoid a jump after the
                // commit.
                int destinationSlot = survivorOrdinal++;
                float delay = i > firstRemoved
                    ? survivorsAfterFirstRemoved++ * QueueShiftStagger
                    : 0f;
                if (destinationSlot >= postDeliveryVisibleCount) continue;
                Vector2 destination = slotPositions[destinationSlot];
                destination.x = CenteredSlotX(
                    slotPositions, postDeliveryVisibleCount, destinationSlot);
                RectTransform cardRt = card.Rt != null
                    ? card.Rt
                    : card.transform as RectTransform;
                if (cardRt != null
                    && (cardRt.anchoredPosition - destination).sqrMagnitude
                    <= 0.000001f) continue;

                card.PlayQueueShift(destination, QueueShiftDuration, delay);
                shiftingCards.Add(card);
                transitionDuration = Mathf.Max(
                    transitionDuration, delay + QueueShiftDuration);
            }

            int epoch = presentationEpoch;
            Sequence transition = DOTween.Sequence()
                .SetTarget(this).SetUpdate(true).SetRecyclable(true)
                .AppendInterval(transitionDuration)
                .AppendCallback(() => EnqueueQueueAnimationFinished(
                    epoch, deliveryReceipt));
            queueTransition = transition;
            queueWatchdogAtUnscaledTime = Time.unscaledTime
                                        + transitionDuration + QueueWatchdogGrace;
            transition.OnKill(() =>
            {
                if (epoch != presentationEpoch
                    || !object.ReferenceEquals(queueTransition, transition)) return;
                EnqueueQueueAnimationAborted(epoch, deliveryReceipt);
            });
        }

        private void CommitQueueTransition()
        {
            BartenderDeliveryReceipt committedReceipt = pendingDeliveryReceipt;
            DeliveryBatch batch = activeBatch;
            int slotCount = transitionSlotCount;
            queueTransition = null;
            queueWatchdogAtUnscaledTime = -1f;
            transitionSlotCount = 0;
            shiftingCards.Clear();

            int removedCount = PartitionCardViews(slotCount);
            ClearDeliveryLatch();
            snapshotDirty = false;
            ApplySnapshot(false, true,
                pendingBatches.Count > 0 && batch != null ? batch.SlotsAfter : null);

            // Reuse the exiting views at the back of the queue and deal each one that got a new order from the
            // right, one after another. The first landing rings the bell.
            float longestDeal = 0f;
            int dealt = 0;
            for (int i = Mathf.Max(0, slotCount - removedCount); i < slotCount && i < cards.Length; i++)
            {
                OrderCardView card = cards[i];
                if (card == null || card.Model == null) continue;
                Tween deal = card.PlayDealIn(DealStagger * (dealt + 1),
                    dealt == 0 ? (Action)(() => PlayOrderBell(committedReceipt)) : null);
                PrimeCardTimer(card, i);
                if (deal != null) longestDeal = Mathf.Max(longestDeal, deal.Duration(false));
                dealt++;
            }
            if (longestDeal > 0f && StartDealBarrier(longestDeal)) return;
            StartNextPendingBatch();
        }

        private void PlayOrderBell(BartenderDeliveryReceipt receipt)
        {
            if (!IsCurrentDeliveryReceipt(receipt)
                || controller.State != BartenderLevelState.Playing) return;
            BsAudio.Instance?.Play(BsSfx.OrderBell, OrderBellVolume);
        }

        private int PartitionCardViews(int slotCount)
        {
            if (cards == null || slotCount <= 0 || slotCount > cards.Length
                || transitionRemoved.Length < slotCount)
                return 0;

            cardPartitionScratch.Clear();
            int removed = 0;
            for (int i = 0; i < slotCount; i++)
                if (!transitionRemoved[i]) cardPartitionScratch.Add(cards[i]);
            for (int i = 0; i < slotCount; i++)
            {
                if (!transitionRemoved[i]) continue;
                cardPartitionScratch.Add(cards[i]);
                removed++;
            }
            for (int i = 0; i < slotCount; i++)
                cards[i] = cardPartitionScratch[i];
            cardPartitionScratch.Clear();
            Array.Clear(transitionRemoved, 0, transitionRemoved.Length);
            return removed;
        }

        private void CancelQueueTransition()
        {
            Sequence oldTransition = queueTransition;
            queueTransition = null;
            queueWatchdogAtUnscaledTime = -1f;
            if (oldTransition != null && oldTransition.IsActive())
                oldTransition.Kill(false);
            Array.Clear(transitionRemoved, 0, transitionRemoved.Length);
            shiftingCards.Clear();
            transitionSlotCount = 0;

            if (cards == null) return;
            for (int i = 0; i < cards.Length; i++)
                if (cards[i] != null) cards[i].ResetPose();
        }

        private bool StartDealBarrier(float duration)
        {
            if (duration <= 0f) return false;
            if (presentationState.State != BsOrderStripState.Dealing
                && !presentationState.Dispatch(BsOrderStripTrigger.BeginDeal))
                return false;

            dealCompletionEpoch = presentationEpoch;
            dealCompletionAtUnscaledTime = Time.unscaledTime + duration;
            return true;
        }

        private void FailClosedQueueTransition()
        {
            DeliveryBatch batch = activeBatch;
            CancelQueueTransition();
            presentationState.Dispatch(BsOrderStripTrigger.QueueCompleted);
            ClearDeliveryLatch();
            snapshotDirty = false;
            float dealDuration = ApplySnapshot(false, false,
                pendingBatches.Count > 0 && batch != null ? batch.SlotsAfter : null);
            if (dealDuration > 0f && StartDealBarrier(dealDuration)) return;
            StartNextPendingBatch();
        }

        private void ClearDeliveryLatch()
        {
            deliveryPoofWaiting = false;
            deliveryPoof.Cancel();
            deferredAtFrame = -1;
            deferredAtUnscaledTime = -1f;
            pendingDeliveredSlot = -1;
            pendingDeliveryReceipt = null;
            activeBatch = null;
            joinedDeliveries.Clear();
            stampHoldWatchdogAtUnscaledTime = -1f;
            stampHoldForced = false;
        }

        private void ResetPresentationBoundary(bool clearModels)
        {
            ResetCountdownTicks();
            timeBoostFlight?.CancelAll();
            presentationEpoch++;
            CancelQueueTransition();
            dealCompletionAtUnscaledTime = -1f;
            dealCompletionEpoch = -1;
            ClearDeliveryLatch();
            pendingBatches.Clear();
            snapshotDirty = false;
            hasPresentedLiveLevel = false;
            HideCardsImmediate(clearModels);
        }

        private void HideCardsImmediate(bool clearModels)
        {
            if (cards == null) return;
            BsPalette palette = controller != null ? controller.Palette : null;
            for (int i = 0; i < cards.Length; i++)
            {
                OrderCardView card = cards[i];
                if (card == null) continue;
                card.Initialize(palette);
                if (clearModels) card.SetOrder(null, false);
                card.SetVisible(false, false);
                card.ResetPose();
            }
        }

        private void SynchronizeCurrentLevel(bool forceDeal)
        {
            BsLevel level = controller != null ? controller.CurrentLevel : null;
            bool live = level != null
                        && controller.State != BartenderLevelState.Unloaded
                        && controller.State != BartenderLevelState.CampaignComplete;
            if (!live)
            {
                HideCardsImmediate(true);
                presentationState.Dispatch(BsOrderStripTrigger.LevelDeactivated);
                return;
            }

            float dealDuration = ApplySnapshot(forceDeal, false);
            snapshotDirty = false;
            if (dealDuration > 0f)
                StartDealBarrier(dealDuration);
            else if (presentationState.State == BsOrderStripState.Hidden)
                presentationState.Dispatch(BsOrderStripTrigger.ActivateLiveLevel);
        }

        private int ActiveSlotCount()
        {
            if (cards == null || controller == null || controller.CurrentLevel == null)
                return 0;
            return Mathf.Min(cards.Length,
                Mathf.Max(1, controller.CurrentLevel.OrderSlots));
        }

        private int VisibleOrderCount(int slotCount, OrderDef[] slotSource = null)
        {
            if (cards == null || controller == null) return 0;
            int count = 0;
            int limit = Mathf.Min(cards.Length, Mathf.Max(0, slotCount));
            for (int i = 0; i < limit; i++)
            {
                bool occupied = slotSource != null
                    ? i < slotSource.Length && slotSource[i] != null
                    : controller.OrderAtSlot(i) != null;
                if (occupied) count++;
            }
            return count;
        }

        private int FindCardIndexForOrder(int orderIndex, int slotCount)
        {
            if (cards == null || orderIndex < 0) return -1;
            int limit = Mathf.Min(cards.Length, Mathf.Max(0, slotCount));
            for (int i = 0; i < limit; i++)
            {
                OrderCardView card = cards[i];
                if (card == null || card.Model == null
                    || card.Model.RuntimeOrderIndex != orderIndex
                    || IsCardLeaving(i)) continue;
                return i;
            }
            return -1;
        }

        private bool IsCardLeaving(int cardIndex)
        {
            if (cards == null || cardIndex < 0 || cardIndex >= cards.Length) return false;
            if (presentationState.State == BsOrderStripState.QueueAnimating
                && cardIndex < transitionRemoved.Length && transitionRemoved[cardIndex])
                return true;
            return false;
        }

        private void CaptureSlotPositions()
        {
            if (slotPositionsCaptured && cards != null
                && slotPositions.Length == cards.Length)
                return;

            int count = cards != null ? cards.Length : 0;
            slotPositions = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                OrderCardView card = cards[i];
                if (card == null) continue;
                RectTransform cardRt = card.Rt != null
                    ? card.Rt
                    : card.transform as RectTransform;
                if (cardRt == null) continue;
                slotPositions[i] = cardRt.anchoredPosition;
                card.SetRestingPosition(slotPositions[i], false);
            }
            slotPositionsCaptured = true;
        }

        private static bool SameOrder(OrderDef a, OrderDef b)
        {
            if (object.ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Kind != b.Kind || a.Glass != b.Glass
                || !Mathf.Approximately(a.TimeLimit, b.TimeLimit))
                return false;

            int count = a.Contents != null ? a.Contents.Count : 0;
            if (count != (b.Contents != null ? b.Contents.Count : 0)) return false;
            for (int i = 0; i < count; i++)
                if (a.Contents[i] != b.Contents[i]) return false;
            return true;
        }

        private static bool HasMatchingGlass(BsBoard board, OrderDef order)
        {
            if (board?.Slots == null || board.Glasses == null || order == null) return false;
            for (int i = 0; i < board.Glasses.Count; i++)
            {
                int slot = board.MatchedSlot(board.Glasses[i]);
                if (slot >= 0 && slot < board.Slots.Length && board.Slots[slot] != null
                    && board.Slots[slot].RuntimeOrderIndex == order.RuntimeOrderIndex)
                    return true;
            }
            return false;
        }

        public Tween PlayTimedOrderExpiredFeedback(int slotIndex)
        {
            BsOrderStripState state = presentationState.State;
            if (!isActiveAndEnabled || controller == null || cards == null
                || state == BsOrderStripState.Hidden || state == BsOrderStripState.Detached
                || state == BsOrderStripState.Faulted)
                return null;

            // TIME'S UP can open while cards move, so the card is found by order identity, not by slot.
            OrderDef order = controller.OrderAtSlot(slotIndex);
            int cardIndex = order != null
                ? FindCardIndexForOrder(order.RuntimeOrderIndex, ActiveSlotCount())
                : -1;
            OrderCardView card = cardIndex >= 0 ? cards[cardIndex] : null;
            if (card == null || card.Model == null
                || !controller.TryGetOrderTimeRemainingForOrder(
                    order.RuntimeOrderIndex, out float remaining, out _)
                || remaining > 0f)
                return null;

            ResetCountdownTicks();
            BsAudio.Instance?.Play(BsSfx.OrderExpired, OrderExpiredVolume);
            return card.PlayTimerExpiredFeedback();
        }

        private void TickTimers()
        {
            if (cards == null || controller == null)
            {
                ResetCountdownTicks();
                return;
            }
            BsLevel level = controller.CurrentLevel;
            if (level == null || !level.AllowTimedOrders)
            {
                ResetCountdownTicks();
                return;
            }

            // Numbers and ticks follow the real-time order clock. Cards are matched by order identity because a
            // delivery moves the controller's slots before the queue animation rotates these views. The urgent
            // pulse waits until the cards stop moving.
            bool clockRunning = controller.OrderClockRunning;
            bool pulseAllowed = clockRunning
                                && presentationState.State == BsOrderStripState.Ready;

            float mostUrgent = float.PositiveInfinity;
            int mostUrgentOrderIndex = -1;
            for (int i = 0; i < cards.Length; i++)
            {
                OrderCardView card = cards[i];
                if (card == null || card.Model == null) continue;
                int orderIndex = card.Model.RuntimeOrderIndex;
                if (!controller.TryGetOrderTimeRemainingForOrder(
                        orderIndex, out float remaining, out float duration))
                    continue;

                card.SetTimer(remaining, duration, pulseAllowed);
                if (remaining > 0f && remaining < mostUrgent)
                {
                    mostUrgent = remaining;
                    mostUrgentOrderIndex = orderIndex;
                }
            }

            DriveCountdownTicks(mostUrgent, mostUrgentOrderIndex, clockRunning);
        }

        private void PrimeCardTimer(OrderCardView card, int slot)
        {
            // Views and controller slots differ while delivered cards wait to leave, so the order identity decides.
            if (card == null || card.Model == null || controller == null
                || !controller.TryGetOrderTimeRemainingForOrder(card.Model.RuntimeOrderIndex,
                    out float remaining, out float duration))
                return;

            bool pulseAllowed = controller.OrderClockRunning
                                && presentationState.State == BsOrderStripState.Ready;
            card.SetTimer(remaining, duration, pulseAllowed);
        }

        private void DriveCountdownTicks(float mostUrgentRemaining,
                                         int mostUrgentOrderIndex,
                                         bool motionAllowed)
        {
            if (!playCountdownTicks || !motionAllowed
                || float.IsInfinity(mostUrgentRemaining)
                || mostUrgentOrderIndex < 0
                || mostUrgentRemaining > tickWindowSeconds)
            {
                ResetCountdownTicks();
                return;
            }

            int second = Mathf.CeilToInt(mostUrgentRemaining);
            if (mostUrgentOrderIndex != lastTickedOrderIndex)
            {
                // A new or reused card starts a fresh rhythm without inheriting the last order's second.
                lastTickedOrderIndex = mostUrgentOrderIndex;
                lastTickedSecond = second;
                return;
            }
            if (second == lastTickedSecond) return;

            bool firstFrameInWindow = lastTickedSecond < 0;
            lastTickedSecond = second;
            if (firstFrameInWindow) return;

            BsSfx clip = (second & 1) == 1 ? BsSfx.TimerTick : BsSfx.TimerTock;

            int stepsIntoPanic = Mathf.Clamp(TickPanicSeconds - second + 1, 0, 3);
            float pitch = TickPitchRamp[stepsIntoPanic];
            float volume = TickVolumeRamp[stepsIntoPanic];
            BsAudio.Instance?.Play(clip, volume, pitch);
        }

        private void ResetCountdownTicks()
        {
            lastTickedSecond = -1;
            lastTickedOrderIndex = -1;
        }

        private void EnsureSignalQueue()
        {
            if (signalQueue == null)
                signalQueue = new OrderStripSignalQueue(HandleSignal);
        }

        private void HandleSignal(OrderStripSignal signal)
        {
            try
            {
                switch (signal.Kind)
                {
                    case OrderStripSignalKind.Activate:
                        ActivatePresentation();
                        return;

                    case OrderStripSignalKind.Deactivate:
                        DeactivatePresentation();
                        return;

                    case OrderStripSignalKind.LevelLoaded:
                        if (!IsCurrentControllerSignal(signal)) return;
                        HandleLevelLoadedSignal(signal.Level);
                        return;

                    case OrderStripSignalKind.SnapshotDirty:
                        if (!IsCurrentControllerSignal(signal)) return;
                        HandleSnapshotDirtySignal();
                        return;

                    case OrderStripSignalKind.LevelStateChanged:
                        if (!IsCurrentControllerSignal(signal)) return;
                        HandleLevelStateSignal(signal.LevelState);
                        return;

                    case OrderStripSignalKind.BoardCommitted:
                        if (!IsCurrentControllerSignal(signal)) return;
                        HandleBoardCommittedSignal(signal.BoardChange);
                        return;

                    case OrderStripSignalKind.TimeBoosted:
                        if (!IsCurrentControllerSignal(signal)) return;
                        HandleTimeBoostedSignal(signal.TimeBoostSeconds);
                        return;

                    case OrderStripSignalKind.StampHoldElapsed:
                        HandleStampHoldElapsed(
                            signal.PresentationEpoch, signal.Receipt);
                        return;

                    case OrderStripSignalKind.QueueAnimationFinished:
                        HandleQueueAnimationFinished(
                            signal.PresentationEpoch, signal.Receipt);
                        return;

                    case OrderStripSignalKind.QueueAnimationAborted:
                        HandleQueueAnimationAborted(
                            signal.PresentationEpoch, signal.Receipt);
                        return;

                    case OrderStripSignalKind.DealAnimationFinished:
                        HandleDealAnimationFinished(signal.PresentationEpoch);
                        return;

                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(signal), signal.Kind,
                            "Unknown order-strip signal.");
                }
            }
            finally
            {
                SynchronizeControllerPresentationBarrier();
            }
        }

        private void ActivatePresentation()
        {
            if (presentationState.State == BsOrderStripState.Detached)
                presentationState.Dispatch(BsOrderStripTrigger.Attach);
            if (presentationState.State == BsOrderStripState.Hidden)
                SynchronizeCurrentLevel(false);
        }

        private void DeactivatePresentation()
        {
            ResetPresentationBoundary(true);
            presentationState.Dispatch(BsOrderStripTrigger.Detach);
        }

        private void HandleLevelLoadedSignal(BsLevel level)
        {
            if (presentationState.State == BsOrderStripState.Detached
                || controller == null
                || !ReferenceEquals(controller.CurrentLevel, level)) return;
            ResetPresentationBoundary(true);
            presentationState.Dispatch(BsOrderStripTrigger.LevelLoaded);
            SynchronizeCurrentLevel(true);
        }

        private void HandleSnapshotDirtySignal()
        {
            if (presentationState.State == BsOrderStripState.Ready)
            {
                snapshotDirty = false;
                float dealDuration = ApplySnapshot(false, false);
                if (dealDuration > 0f) StartDealBarrier(dealDuration);
            }
            else if (presentationState.State == BsOrderStripState.Hidden)
            {
                SynchronizeCurrentLevel(false);
            }
            else
            {
                snapshotDirty = true;
            }
        }

        private void EnqueueStampHoldElapsed(
            int epoch, BartenderDeliveryReceipt receipt) =>
            signalQueue.Enqueue(
                OrderStripSignal.StampHoldElapsed(epoch, receipt));

        private void HandleStampHoldElapsed(
            int epoch, BartenderDeliveryReceipt receipt)
        {
            bool matches = stampHoldForced
                ? receipt != null && epoch == presentationEpoch
                  && ReferenceEquals(receipt, pendingDeliveryReceipt)
                : MatchesPendingDelivery(epoch, receipt);
            if (!matches
                || !presentationState.Dispatch(
                    BsOrderStripTrigger.StampHoldElapsed)) return;
            stampHoldForced = false;
            try
            {
                BeginQueueTransition();
            }
            catch (Exception exception)
            {
                // Tween setup failure must not trap delivery in QueueAnimating. Garnish effects own no queue
                // or barrier lease.
                Debug.LogException(exception, this);
                try
                {
                    FailClosedQueueTransition();
                }
                catch (Exception cleanupException)
                {
                    Debug.LogException(cleanupException, this);
                    try
                    {
                        presentationState.Dispatch(BsOrderStripTrigger.QueueCompleted);
                    }
                    catch (Exception stateCleanupException)
                    {
                        Debug.LogException(stateCleanupException, this);
                    }
                    try
                    {
                        CancelQueueTransition();
                    }
                    catch (Exception tweenCleanupException)
                    {
                        Debug.LogException(tweenCleanupException, this);
                    }
                    finally
                    {
                        queueWatchdogAtUnscaledTime = -1f;
                        ClearDeliveryLatch();
                        // Deliveries waiting behind this batch must still leave, or the board would read as settling
                        // forever. If they cannot start either, drop them and show the latest board instead.
                        try
                        {
                            StartNextPendingBatch();
                        }
                        catch (Exception batchException)
                        {
                            Debug.LogException(batchException, this);
                            pendingBatches.Clear();
                            snapshotDirty = false;
                            signalQueue.Enqueue(OrderStripSignal.SnapshotDirty());
                        }
                    }
                }
            }
        }

        private void EnqueueQueueAnimationFinished(
            int epoch, BartenderDeliveryReceipt receipt) =>
            signalQueue.Enqueue(
                OrderStripSignal.QueueAnimationFinished(epoch, receipt));

        private void HandleQueueAnimationFinished(
            int epoch, BartenderDeliveryReceipt receipt)
        {
            if (!MatchesPendingDelivery(epoch, receipt)
                || !presentationState.Dispatch(
                    BsOrderStripTrigger.QueueCompleted)) return;
            CommitQueueTransition();
        }

        private void EnqueueQueueAnimationAborted(
            int epoch, BartenderDeliveryReceipt receipt) =>
            signalQueue.Enqueue(
                OrderStripSignal.QueueAnimationAborted(epoch, receipt));

        private void HandleQueueAnimationAborted(
            int epoch, BartenderDeliveryReceipt receipt)
        {
            // Recovery only needs the same presentation run; a receipt that stopped reading as current must not
            // leave the strip stuck in QueueAnimating.
            if (receipt == null || epoch != presentationEpoch
                || !ReferenceEquals(receipt, pendingDeliveryReceipt)
                || presentationState.State
                   != BsOrderStripState.QueueAnimating) return;
            FailClosedQueueTransition();
        }

        private void EnqueueDealAnimationFinished(int epoch) =>
            signalQueue.Enqueue(OrderStripSignal.DealAnimationFinished(epoch));

        private void HandleDealAnimationFinished(int epoch)
        {
            if (epoch != presentationEpoch) return;
            bool transitioned = false;
            try
            {
                CompletePendingDeals();
            }
            finally
            {
                transitioned = presentationState.Dispatch(
                    BsOrderStripTrigger.DealCompleted);
                dealCompletionAtUnscaledTime = -1f;
                dealCompletionEpoch = -1;
            }
            if (!transitioned) return;
            if (pendingBatches.Count > 0)
            {
                StartNextPendingBatch();
                return;
            }
            if (!snapshotDirty) return;

            snapshotDirty = false;
            float dealDuration = ApplySnapshot(false, false);
            if (dealDuration > 0f) StartDealBarrier(dealDuration);
        }

        private void CompletePendingDeals()
        {
            if (cards == null) return;
            for (int i = 0; i < cards.Length; i++)
                if (cards[i] != null) cards[i].CompletePendingDeal();
        }

        private void SynchronizeControllerPresentationBarrier()
        {
            if (presentationBarrierController != null
                && !presentationBarrierController.IsPresentationBarrierOwnedBy(this))
                presentationBarrierController = null;

            // Deals, the delivery stamp and queue movement never hold gameplay or the order clock. Only a faulted
            // strip does: it holds the round until its binding is fixed, like a modal.
            bool shouldBlock = Application.isPlaying && isActiveAndEnabled
                               && controller != null
                               && presentationState.State == BsOrderStripState.Faulted;
            if (presentationBarrierController != null
                && (!shouldBlock
                    || !ReferenceEquals(presentationBarrierController, controller)))
                ReleaseControllerPresentationBarrier();

            if (!shouldBlock) return;
            if (presentationBarrierController != null) return;
            if (!controller.AcquirePresentationBarrier(this)) return;
            presentationBarrierController = controller;
        }

        private void ReleaseControllerPresentationBarrier()
        {
            BartenderLevelController owner = presentationBarrierController;
            presentationBarrierController = null;
            if (owner != null) owner.ReleasePresentationBarrier(this);
        }

        private void HandleLevelStateSignal(BartenderLevelState state)
        {
            if (presentationState.State == BsOrderStripState.Detached) return;
            if (state != BartenderLevelState.Playing)
            {
                ResetCountdownTicks();
                timeBoostFlight?.CancelAll();
            }
            if (state == BartenderLevelState.Unloaded
                || state == BartenderLevelState.CampaignComplete)
            {
                ResetPresentationBoundary(true);
                presentationState.Dispatch(BsOrderStripTrigger.LevelDeactivated);
                return;
            }

            if (state == BartenderLevelState.Paused && cards != null)
            {
                for (int i = 0; i < cards.Length; i++)
                    if (cards[i] != null) cards[i].SuspendTimerEmphasis();
            }

            // Pause/resume must not clear the delivery stamp. Show the final snapshot when Ready; otherwise
            // keep the old card until commit.
            if ((state == BartenderLevelState.Won
                 || state == BartenderLevelState.Failed)
                && presentationState.State == BsOrderStripState.Ready)
                signalQueue.Enqueue(OrderStripSignal.SnapshotDirty());
        }

        private void HandleBoardCommittedSignal(BartenderBoardChange change)
        {
            if (!IsCurrentBoardChange(change)) return;
            if (change.DeliveryReceipt == null)
            {
                HandleSnapshotDirtySignal();
                return;
            }
            if (!DeliveryMatchesBoardChange(change.DeliveryReceipt, change)) return;
            HandleDeliveredSignal(change);
        }

        private void HandleDeliveredSignal(BartenderBoardChange change)
        {
            BartenderDeliveryReceipt receipt = change.DeliveryReceipt;
            ResetCountdownTicks();
            bool currentReceipt = IsCurrentDeliveryReceipt(receipt)
                                  && receipt.DeliveredGlass != null
                                  && receipt.DeliveredOrder != null;
            int deliveredOrderIndex = currentReceipt
                ? receipt.DeliveredOrder.RuntimeOrderIndex
                : -1;
            if (deliveredOrderIndex >= 0)
                timeBoostFlight?.CancelForOrder(deliveredOrderIndex);
            else
                timeBoostFlight?.CancelAll();

            int slotCount = ActiveSlotCount();
            if (!currentReceipt || cards == null || slotCount <= 0)
            {
                signalQueue.Enqueue(OrderStripSignal.SnapshotDirty());
                return;
            }

            // This runs inside the delivery's publish, so the live slots are exactly this delivery's board. The card
            // is found by order identity: two cards can show the same recipe.
            BsBoard deliveredBoard = controller.Board;
            OrderDef[] slotsAfter = deliveredBoard != null ? deliveredBoard.Slots : null;
            int cardIndex = FindCardIndexForOrder(deliveredOrderIndex, slotCount);
            BsOrderStripState state = presentationState.State;
            bool queueIdle = activeBatch == null && pendingBatches.Count == 0;

            if (queueIdle && (state == BsOrderStripState.Ready || state == BsOrderStripState.Dealing))
            {
                if (cardIndex < 0)
                {
                    signalQueue.Enqueue(OrderStripSignal.SnapshotDirty());
                    return;
                }
                var batch = new DeliveryBatch();
                batch.Add(receipt, deliveredOrderIndex, slotsAfter);
                if (!StartDeliveryBatch(batch, true))
                    signalQueue.Enqueue(OrderStripSignal.SnapshotDirty());
                return;
            }

            if (state == BsOrderStripState.StampHold && activeBatch != null
                && pendingBatches.Count == 0 && cardIndex >= 0)
            {
                OrderCardView joinedCard = cards[cardIndex];
                activeBatch.Add(receipt, deliveredOrderIndex, slotsAfter);
                joinedCard.SuspendTimerEmphasis();
                joinedCard.BeginSynchronizedDelivery();
                joinedDeliveries.Add(new JoinedDelivery
                {
                    Receipt = receipt,
                    Card = joinedCard,
                    OrderIndex = deliveredOrderIndex,
                });
                stampHoldWatchdogAtUnscaledTime = Mathf.Max(stampHoldWatchdogAtUnscaledTime,
                    Time.unscaledTime + StampHoldWatchdogSeconds);
                return;
            }

            if (activeBatch != null || pendingBatches.Count > 0
                || state == BsOrderStripState.StampHold || state == BsOrderStripState.QueueAnimating)
            {
                DeliveryBatch next = pendingBatches.Count > 0 ? pendingBatches[pendingBatches.Count - 1] : null;
                if (next == null)
                {
                    next = new DeliveryBatch();
                    pendingBatches.Add(next);
                }
                next.Add(receipt, deliveredOrderIndex, slotsAfter);
                if (cardIndex >= 0 && !shiftingCards.Contains(cards[cardIndex]))
                    cards[cardIndex].ShowDelivered();
                return;
            }

            signalQueue.Enqueue(OrderStripSignal.SnapshotDirty());
        }

        private bool StartDeliveryBatch(DeliveryBatch batch, bool withPoof)
        {
            if (batch == null || cards == null) return false;
            int slotCount = ActiveSlotCount();
            int headEntry = -1;
            int headCardIndex = -1;
            for (int entry = 0; entry < batch.Receipts.Count && headEntry < 0; entry++)
            {
                int index = FindCardIndexForOrder(batch.OrderIndices[entry], slotCount);
                if (index < 0) continue;
                headEntry = entry;
                headCardIndex = index;
            }
            if (headEntry < 0
                || !presentationState.Dispatch(BsOrderStripTrigger.DeliveryCommitted))
                return false;

            activeBatch = batch;
            joinedDeliveries.Clear();
            deferredAtFrame = Time.frameCount;
            deferredAtUnscaledTime = Time.unscaledTime;
            pendingDeliveredSlot = headCardIndex;
            pendingDeliveryReceipt = batch.Receipts[headEntry];
            stampHoldWatchdogAtUnscaledTime = Time.unscaledTime + StampHoldWatchdogSeconds;
            stampHoldForced = false;

            for (int i = 0; i < cards.Length; i++)
                if (cards[i] != null) cards[i].SuspendTimerEmphasis();

            OrderCardView headCard = cards[headCardIndex];
            headCard.CompletePendingDeal();
            deliveryPoofEpoch = presentationEpoch;
            deliveryPoofWaiting = false;
            if (withPoof)
            {
                try
                {
                    deliveryPoofWaiting = deliveryPoof.TryBegin(shelfView, pendingDeliveryReceipt, headCard);
                }
                catch (Exception exception)
                {
                    deliveryPoofWaiting = false;
                    deliveryPoof.Cancel();
                    Debug.LogException(exception, this);
                }
                if (!deliveryPoofWaiting) headCard.ShowDelivered();
                return true;
            }

            for (int entry = 0; entry < batch.Receipts.Count; entry++)
            {
                int index = entry == headEntry
                    ? headCardIndex
                    : FindCardIndexForOrder(batch.OrderIndices[entry], slotCount);
                // An order delivered before its card was ever dealt has nothing to show.
                if (index < 0) continue;
                OrderCardView card = cards[index];
                BartenderDeliveryReceipt entryReceipt = batch.Receipts[entry];
                if (shelfView != null
                    && shelfView.TryGetDeliverySample(entryReceipt, out _, out _, out bool finished)
                    && !finished)
                {
                    card.BeginSynchronizedDelivery();
                    joinedDeliveries.Add(new JoinedDelivery
                    {
                        Receipt = entryReceipt,
                        Card = card,
                        OrderIndex = batch.OrderIndices[entry],
                    });
                }
                else card.ShowDelivered();
            }
            return true;
        }

        private void StartNextPendingBatch()
        {
            while (pendingBatches.Count > 0
                   && presentationState.State == BsOrderStripState.Ready)
            {
                DeliveryBatch batch = pendingBatches[0];
                pendingBatches.RemoveAt(0);
                if (StartDeliveryBatch(batch, false)) return;
                snapshotDirty = false;
                float dealDuration = ApplySnapshot(false, false,
                    pendingBatches.Count > 0 ? batch.SlotsAfter : null);
                if (dealDuration > 0f && StartDealBarrier(dealDuration)) return;
            }
        }

        private void StepStampHold()
        {
            if (dealCompletionEpoch == presentationEpoch && dealCompletionAtUnscaledTime >= 0f
                && Time.unscaledTime >= dealCompletionAtUnscaledTime)
            {
                CompletePendingDeals();
                dealCompletionAtUnscaledTime = -1f;
                dealCompletionEpoch = -1;
            }

            bool joinedFinished = StepJoinedDeliveries();
            if (stampHoldWatchdogAtUnscaledTime >= 0f
                && Time.unscaledTime >= stampHoldWatchdogAtUnscaledTime)
            {
                stampHoldWatchdogAtUnscaledTime = -1f;
                Debug.LogWarning("Order strip StampHold watchdog: delivered cards waited too long for their "
                    + "glasses; moving the queue on.", this);
                deliveryPoofWaiting = false;
                stampHoldForced = true;
                EnqueueStampHoldElapsed(presentationEpoch, pendingDeliveryReceipt);
                return;
            }
            if (!deliveryPoofWaiting && joinedFinished && Time.frameCount > deferredAtFrame
                && Time.unscaledTime >= deferredAtUnscaledTime + DeliveryGlowHold)
                EnqueueStampHoldElapsed(presentationEpoch, pendingDeliveryReceipt);
        }

        private bool StepJoinedDeliveries()
        {
            bool allFinished = true;
            for (int i = 0; i < joinedDeliveries.Count; i++)
            {
                JoinedDelivery joined = joinedDeliveries[i];
                if (joined.Finished) continue;
                float opacity = 0f;
                bool finished = true;
                if (shelfView == null
                    || !shelfView.TryGetDeliverySample(joined.Receipt, out _, out opacity, out finished))
                {
                    opacity = 0f;
                    finished = true;
                }
                OrderCardView card = joined.Card;
                if (card != null && card.Model != null && card.Model.RuntimeOrderIndex == joined.OrderIndex)
                    card.SampleSynchronizedDelivery(opacity, finished);
                if (!finished)
                {
                    allFinished = false;
                    continue;
                }
                joined.Finished = true;
                deferredAtFrame = Time.frameCount;
                deferredAtUnscaledTime = Mathf.Max(deferredAtUnscaledTime,
                    Time.unscaledTime - DeliveryGlowHold);
            }
            return allFinished;
        }

        private void StepDeliveryPoof()
        {
            if (!MatchesPendingDelivery(deliveryPoofEpoch, pendingDeliveryReceipt))
            {
                deliveryPoof.Cancel();
                deliveryPoofWaiting = false;
                return;
            }
            DeliveryPoofPresentation.StepResult result;
            try { result = deliveryPoof.Step(); }
            catch (Exception exception)
            {
                deliveryPoof.Cancel();
                result = DeliveryPoofPresentation.StepResult.Aborted;
                Debug.LogException(exception, this);
            }
            bool disappeared = result == DeliveryPoofPresentation.StepResult.Disappeared;
            if (!deliveryPoofWaiting || (!disappeared
                && result != DeliveryPoofPresentation.StepResult.Aborted)) return;
            deliveryPoofWaiting = false;
            deferredAtFrame = Time.frameCount;
            deferredAtUnscaledTime = Time.unscaledTime - (disappeared ? DeliveryGlowHold : 0f);
            if (!disappeared && pendingDeliveredSlot >= 0 && pendingDeliveredSlot < cards.Length
                && cards[pendingDeliveredSlot] != null)
                cards[pendingDeliveredSlot].ShowDelivered();
        }

        private bool MatchesPendingDelivery(
            int epoch, BartenderDeliveryReceipt receipt) =>
            pendingDeliveryReceipt != null && receipt != null
            && epoch == presentationEpoch
            && ReferenceEquals(receipt, pendingDeliveryReceipt)
            && IsCurrentDeliveryReceipt(receipt);

        private bool IsCurrentDeliveryReceipt(BartenderDeliveryReceipt receipt)
        {
            if (controller == null || receipt == null
                || !receipt.AttemptId.IsValid
                || !receipt.OperationId.IsValid
                || receipt.DomainRevision <= 0L
                || receipt.BoardRevision < 0
                || receipt.Cause != BsRoundTransitionCause.PlayerDelivery)
                return false;
            BsRoundCommandStamp current = controller.CurrentRoundStamp;
            return current.IsValid
                   && current.AttemptId == receipt.AttemptId
                   && current.Revision >= receipt.DomainRevision
                   && current.BoardRevision >= receipt.BoardRevision;
        }

        private bool IsCurrentBoardChange(BartenderBoardChange change)
        {
            return controller != null && change != null
                   && change.AttemptId.IsValid
                   && change.OperationId.IsValid
                   && change.DomainRevision > 0L
                   && change.BoardRevision >= 0
                   && change.Cause != BsRoundTransitionCause.None
                   && controller.CurrentRoundStamp == new BsRoundCommandStamp(
                       change.AttemptId,
                       change.Token,
                       change.DomainRevision,
                       change.BoardRevision);
        }

        private static bool DeliveryMatchesBoardChange(
            BartenderDeliveryReceipt receipt,
            BartenderBoardChange change)
        {
            return receipt != null && change != null
                   && receipt.Cause == BsRoundTransitionCause.PlayerDelivery
                   && receipt.AttemptId == change.AttemptId
                   && receipt.OperationId == change.OperationId
                   && receipt.DomainRevision == change.DomainRevision
                   && receipt.BoardRevision == change.BoardRevision
                   && receipt.Cause == change.Cause
                   && receipt.Token == change.Token
                   && Nullable.Equals(
                       receipt.SettlementReceipt,
                       change.SettlementReceipt);
        }

        private void ResolveDependencies()
        {
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            if (controller == null && shelfView != null) controller = shelfView.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
        }

        private void Subscribe()
        {
            if (subscribedController != controller)
            {
                UnsubscribeController();
                subscribedController = controller;
                if (subscribedController != null)
                {
                    int generation = ++controllerSubscriptionGeneration;
                    BartenderLevelController source = subscribedController;
                    levelLoadedSubscription = level =>
                    {
                        if (IsCurrentControllerSubscription(source, generation))
                            HandleLevelLoaded(level, generation);
                    };
                    controllerOrdersSubscription = () =>
                    {
                        if (IsCurrentControllerSubscription(source, generation))
                            HandleOrdersChanged(generation);
                    };
                    controllerBoardSubscription = change =>
                    {
                        if (IsCurrentControllerSubscription(source, generation))
                            HandleBoardCommitted(change, generation);
                    };
                    levelStateSubscription = state =>
                    {
                        if (IsCurrentControllerSubscription(source, generation))
                            HandleStateChanged(state, generation);
                    };
                    timeBoostedSubscription = seconds =>
                    {
                        if (IsCurrentControllerSubscription(source, generation))
                            HandleTimeBoosted(seconds, generation);
                    };
                    subscribedController.LevelLoaded += levelLoadedSubscription;
                    subscribedController.OrdersChanged += controllerOrdersSubscription;
                    subscribedController.BoardCommitted += controllerBoardSubscription;
                    subscribedController.StateChanged += levelStateSubscription;
                    subscribedController.TimeBoosted += timeBoostedSubscription;
                }
            }
        }

        private void Unsubscribe()
        {
            UnsubscribeController();
        }

        private void UnsubscribeController()
        {
            controllerSubscriptionGeneration++;
            if (subscribedController != null)
            {
                subscribedController.LevelLoaded -= levelLoadedSubscription;
                subscribedController.OrdersChanged -= controllerOrdersSubscription;
                subscribedController.BoardCommitted -= controllerBoardSubscription;
                subscribedController.StateChanged -= levelStateSubscription;
                subscribedController.TimeBoosted -= timeBoostedSubscription;
            }
            subscribedController = null;
            levelLoadedSubscription = null;
            controllerOrdersSubscription = null;
            controllerBoardSubscription = null;
            levelStateSubscription = null;
            timeBoostedSubscription = null;
        }

        private bool IsCurrentControllerSubscription(
            BartenderLevelController source, int generation) =>
            isActiveAndEnabled && generation == controllerSubscriptionGeneration
            && ReferenceEquals(source, subscribedController)
            && ReferenceEquals(source, controller);

        private bool IsCurrentControllerSignal(OrderStripSignal signal) =>
            signal.SubscriptionGeneration < 0
            || signal.SubscriptionGeneration == controllerSubscriptionGeneration;

        internal void PrepareTimeBoostSource(Transform source, float launchDelay) =>
            timeBoostFlight?.SetNextBoostSource(source, launchDelay);

        internal void ClearTimeBoostSource() => timeBoostFlight?.ClearNextBoostSource();

        private void HandleLevelLoaded(BsLevel level, int generation)
        {
            signalQueue.Enqueue(OrderStripSignal.LevelLoaded(level, generation));
        }

        private void HandleOrdersChanged(int generation)
        {
            signalQueue.Enqueue(
                OrderStripSignal.ControllerSnapshotDirty(generation));
        }

        private void HandleStateChanged(
            BartenderLevelState state, int generation)
        {
            signalQueue.Enqueue(
                OrderStripSignal.LevelStateChanged(state, generation));
        }

        private void HandleBoardCommitted(
            BartenderBoardChange change, int generation)
        {
            signalQueue.Enqueue(
                OrderStripSignal.BoardCommitted(change, generation));
        }

        private void HandleTimeBoosted(float seconds, int generation)
        {
            signalQueue.Enqueue(
                OrderStripSignal.TimeBoosted(seconds, generation));
        }

        private void HandleTimeBoostedSignal(float seconds)
        {
            BsOrderStripState stripState = presentationState.State;
            if (controller == null || cards == null
                || stripState == BsOrderStripState.Faulted
                || stripState == BsOrderStripState.Hidden
                || stripState == BsOrderStripState.Detached
                || controller.State != BartenderLevelState.Playing
                || controller.CurrentLevel == null
                || !controller.CurrentLevel.AllowTimedOrders
                || float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0f)
                return;

            TimeBoostFlightPresenter flight = timeBoostFlight;
            if (flight != null) flight.BeginBoost();

            // Cards may be shifting, so each card's live slot is resolved from its order identity.
            int slotCount = ActiveSlotCount();
            for (int cardIndex = 0; cardIndex < slotCount; cardIndex++)
            {
                OrderCardView card = cards[cardIndex];
                if (card == null || card.Model == null || card.Model.TimeLimit <= 0f
                    || IsCardLeaving(cardIndex)
                    || !TryResolveLiveOrderSlot(card.Model.RuntimeOrderIndex, cardIndex, out int targetSlot)
                    || !controller.TryGetOrderTimeRemaining(targetSlot, out _, out _))
                    continue;

                int orderIndex = card.Model.RuntimeOrderIndex;
                int holdToken = 0;
                bool launched = false;
                if (flight != null)
                {
                    holdToken = card.HoldTimeBoostPresentation(seconds);
                    if (holdToken > 0)
                    {
                        int targetHoldToken = holdToken;
                        launched = flight.LaunchTo(card, orderIndex, seconds, impacted =>
                            CompleteTimeBoostFlight(card, targetSlot, orderIndex,
                                targetHoldToken, seconds, impacted));
                    }
                }

                if (launched) continue;
                card.ReleaseTimeBoostPresentation(holdToken);
                ProjectTimeBoostImpact(card, targetSlot, orderIndex, seconds, true);
            }
        }

        private void CompleteTimeBoostFlight(OrderCardView card, int slot,
                                             int orderIndex, int holdToken,
                                             float seconds, bool impacted)
        {
            if (card == null) return;
            card.ReleaseTimeBoostPresentation(holdToken);
            ProjectTimeBoostImpact(card, slot, orderIndex, seconds, impacted);
        }

        private void ProjectTimeBoostImpact(OrderCardView card, int slot,
                                            int orderIndex, float seconds,
                                            bool playImpact)
        {
            if (card == null || controller == null || card.Model == null
                || card.Model.RuntimeOrderIndex != orderIndex
                || !TryResolveLiveOrderSlot(orderIndex, slot, out int liveSlot)
                || !controller.TryGetOrderTimeRemaining(
                    liveSlot, out float remaining, out float duration))
                return;

            bool pulseAllowed = controller.OrderClockRunning
                                && presentationState.State == BsOrderStripState.Ready;
            card.SetTimer(remaining, duration, pulseAllowed);
            if (playImpact && controller.State == BartenderLevelState.Playing)
                card.PlayTimeBoostFeedback(seconds);
        }

        private bool TryResolveLiveOrderSlot(int orderIndex, int preferredSlot,
                                             out int resolvedSlot)
        {
            resolvedSlot = -1;
            if (controller == null || orderIndex < 0) return false;

            int slotCount = ActiveSlotCount();
            if (preferredSlot >= 0 && preferredSlot < slotCount
                && controller.OrderAtSlot(preferredSlot)?.RuntimeOrderIndex == orderIndex)
            {
                resolvedSlot = preferredSlot;
                return true;
            }

            for (int slot = 0; slot < slotCount; slot++)
            {
                if (slot == preferredSlot) continue;
                if (controller.OrderAtSlot(slot)?.RuntimeOrderIndex != orderIndex) continue;
                resolvedSlot = slot;
                return true;
            }
            return false;
        }
    }
}
