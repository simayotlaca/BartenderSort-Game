using System;
using System.Collections.Generic;
using BartenderSort.Core;
using DG.Tweening;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Shows board slots with existing scene cards. After a committed delivery, keep the stamp visible for at
    /// least 0.30 seconds before moving the queue.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OrderStripPresenter : MonoBehaviour
    {
        private const float DealStagger = 0.045f;

        // Keep the bell below the delivery sound so both stay clear in the mix.
        private const float OrderBellVolume = 0.78f;
        private const float DeliveryStampMinimumHold = 0.30f;
        private const float DeliveryExitDuration = 0.18f;
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

        // Raises tension during the last three seconds; index 0 is normal and 3 is the last second.
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
        private bool snapshotDirty;
        private float dealCompletionAtUnscaledTime = -1f;
        private int dealCompletionEpoch = -1;

        private Vector2[] slotPositions = new Vector2[0];
        private bool slotPositionsCaptured;
        private bool hasPresentedLiveLevel;
        private int transitionDeliveredSlot = -1;
        private int transitionSlotCount;
        private Sequence queueTransition;
        private float queueWatchdogAtUnscaledTime = -1f;
        private BsLevel lastCapacityFaultLevel;
        private int lastCapacityFaultCardCount = -1;

        public IReadOnlyList<OrderCardView> Cards => cards;
        /// <summary>
        /// True during dealing, the delivery stamp or queue movement. Blocks new gameplay commands until cards
        /// settle.
        /// </summary>
        public bool TransitionPlaying => presentationState.TransitionPlaying;

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
            ReleaseControllerPresentationBarrier();
        }

        /// <summary>Strict binding check with a message an artist can act on.</summary>
        public bool ValidateBindings(out string reason)
        {
            if (controller == null)
            {
                reason = "BartenderLevelController Inspector referansı eksik.";
                return false;
            }
            if (cards == null || cards.Length == 0)
            {
                reason = "Sipariş kartı bağlanmamış.";
                return false;
            }
            if (timeBoostFlight == null)
            {
                reason = "TimeBoostFlightPresenter Inspector referansı eksik.";
                return false;
            }
            if (shelfView != null && shelfView.Controller == null)
            {
                reason = "Bağlı shelf view üzerinde BartenderLevelController eksik.";
                return false;
            }
            if (shelfView != null
                && !ReferenceEquals(shelfView.Controller, controller))
            {
                reason = "Order strip ve shelf view farklı controller'lara bağlı.";
                return false;
            }
            for (int i = 0; i < cards.Length; i++)
            {
                if (cards[i] == null)
                {
                    reason = $"Sipariş kartı [{i}] boş.";
                    return false;
                }
                if (!cards[i].IsReady())
                {
                    reason = $"Sipariş kartı [{i}] ({cards[i].name}) eksik parça taşıyor.";
                    return false;
                }
                for (int previous = 0; previous < i; previous++)
                {
                    if (!ReferenceEquals(cards[previous], cards[i])) continue;
                    reason = $"Sipariş kartı [{i}], [{previous}] ile aynı view nesnesi.";
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
                reason = $"{levelLabel}, {requiredCapacity} order slot istiyor; "
                       + $"yalnız {cards.Length} kart bağlı.";
                return false;
            }
            reason = null;
            return true;
        }

        [ContextMenu("Validate Order Strip Bindings")]
        private void ValidateFromContextMenu()
        {
            if (ValidateBindings(out string reason))
                Debug.Log($"Sipariş şeridi: {cards.Length} kart geçerli.", this);
            else
                Debug.LogError("Order strip binding error: " + reason, this);
        }

        private void LateUpdate()
        {
            switch (presentationState.State)
            {
                case BsOrderStripState.Dealing:
                    if (dealCompletionEpoch == presentationEpoch
                        && Time.unscaledTime >= dealCompletionAtUnscaledTime)
                        EnqueueDealAnimationFinished(presentationEpoch);
                    return;

                case BsOrderStripState.StampHold:
                    if (Time.frameCount > deferredAtFrame
                        && Time.unscaledTime >= deferredAtUnscaledTime
                           + DeliveryStampMinimumHold)
                        EnqueueStampHoldElapsed(
                            presentationEpoch, pendingDeliveryReceipt);
                    return;

                case BsOrderStripState.QueueAnimating:
                    if (queueWatchdogAtUnscaledTime >= 0f
                        && Time.unscaledTime >= queueWatchdogAtUnscaledTime)
                        EnqueueQueueAnimationAborted(
                            presentationEpoch, pendingDeliveryReceipt);
                    return;

                case BsOrderStripState.Faulted:
                case BsOrderStripState.Detached:
                case BsOrderStripState.Hidden:
                    return;

                case BsOrderStripState.Ready:
                    TickTimers();
                    return;
            }
        }

        /// <summary>Refreshes cards from the controller's open slots.</summary>
        public void Refresh()
        {
            EnsureSignalQueue();
            signalQueue.Enqueue(OrderStripSignal.SnapshotDirty());
        }

        /// <summary>
        /// Applies the controller snapshot after delivery movement ends so moving cards keep their old artwork.
        /// </summary>
        private float ApplySnapshot(bool forceDeal, bool suppressEntrances)
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
            int visibleCardCount = VisibleOrderCount(slots);

            int visibleOrdinal = 0;
            int dealIndex = 0;
            float longestDealDuration = 0f;

            for (int i = 0; i < cards.Length; i++)
            {
                OrderCardView card = cards[i];
                if (card == null) continue;

                // Send the current palette before SetOrder on every snapshot; startup may load it from
                // Resources after the card enables.
                card.Initialize(palette);

                // Unused extra scene cards stay hidden; three wired cards are valid for a two-slot level.
                bool inUse = i < slots;
                OrderDef order = inUse ? controller.OrderAtSlot(i) : null;
                bool changed = !SameOrder(card.Model, order);
                bool wasEmpty = card.Model == null;
                bool visible = inUse && order != null;

                // Centre visible cards without changing model slots: three use [-S,0,+S], two [-S/2,+S/2], one
                // [0].
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
                card.SetHighlighted(order != null && HasMatchingGlass(i));
                if (deal)
                {
                    Tween tween = card.PlayDealIn(dealIndex * DealStagger);
                    if (tween != null)
                        longestDealDuration = Mathf.Max(
                            longestDealDuration, tween.Duration(false));
                    dealIndex++;
                }
            }

            hasPresentedLiveLevel = live;
            return longestDealDuration;
        }

        /// <summary>
        /// Centres the visible cards horizontally while keeping their authored spacing and Y positions.
        /// </summary>
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
                string levelLabel = level != null ? $"Level {level.Index}" : "Aktif level";
                Debug.LogError($"Order strip capacity error: {levelLabel}, {required} slot "
                             + $"istiyor; yalnız {available} kart bağlı. Gameplay güvenli "
                             + "olarak sunum bariyerinde tutuldu.", this);
            }
            return false;
        }

        /// <summary>
        /// After the stamp hold, move the old cards before applying the snapshot so their contents cannot
        /// change mid-slide.
        /// </summary>
        private void BeginQueueTransition()
        {
            if (presentationState.State != BsOrderStripState.QueueAnimating) return;
            CaptureSlotPositions();

            int slotCount = ActiveSlotCount();
            int deliveredSlot = pendingDeliveredSlot;
            BartenderDeliveryReceipt deliveryReceipt = pendingDeliveryReceipt;
            if (cards == null || slotCount <= 0 || deliveredSlot < 0
                || deliveredSlot >= slotCount || cards[deliveredSlot] == null
                || deliveryReceipt == null)
            {
                FailClosedQueueTransition();
                return;
            }

            transitionDeliveredSlot = deliveredSlot;
            transitionSlotCount = slotCount;

            cards[deliveredSlot].PlayQueueExit(DeliveryExitDuration);
            int postDeliveryVisibleCount = VisibleOrderCount(slotCount);
            float transitionDuration = DeliveryExitDuration;
            for (int i = 0; i < slotCount; i++)
            {
                if (i == deliveredSlot) continue;
                OrderCardView card = cards[i];
                if (card == null || card.Model == null) continue;

                // Move surviving cards straight to their final centred positions to avoid a jump after the
                // commit.
                int destinationSlot = i < deliveredSlot ? i : i - 1;
                if (destinationSlot < 0
                    || destinationSlot >= postDeliveryVisibleCount) continue;
                Vector2 destination = slotPositions[destinationSlot];
                destination.x = CenteredSlotX(
                    slotPositions, postDeliveryVisibleCount, destinationSlot);
                RectTransform cardRt = card.Rt != null
                    ? card.Rt
                    : card.transform as RectTransform;
                if (cardRt != null
                    && (cardRt.anchoredPosition - destination).sqrMagnitude
                    <= 0.000001f) continue;

                float delay = i > deliveredSlot
                    ? (i - deliveredSlot - 1) * QueueShiftStagger
                    : 0f;
                card.PlayQueueShift(destination, QueueShiftDuration, delay);
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
                // A live handle here means the sequence was killed externally. Queue the abort so the FIFO
                // handler owns the state change.
                if (epoch != presentationEpoch
                    || !object.ReferenceEquals(queueTransition, transition)) return;
                EnqueueQueueAnimationAborted(epoch, deliveryReceipt);
            });
        }

        private void CommitQueueTransition()
        {
            BartenderDeliveryReceipt committedReceipt = pendingDeliveryReceipt;
            int deliveredSlot = transitionDeliveredSlot;
            int slotCount = transitionSlotCount;
            queueTransition = null;
            queueWatchdogAtUnscaledTime = -1f;
            transitionDeliveredSlot = -1;
            transitionSlotCount = 0;

            RotateCardViewsLeft(deliveredSlot, slotCount);
            ClearDeliveryLatch();
            snapshotDirty = false;
            ApplySnapshot(false, true);

            // Reuse the exiting view at the back of the queue and deal it from the right if a new order
            // exists.
            int replacementSlot = slotCount - 1;
            Tween replacementDeal = null;
            if (replacementSlot >= 0 && replacementSlot < cards.Length
                && cards[replacementSlot] != null
                && cards[replacementSlot].Model != null)
            {
                replacementDeal = cards[replacementSlot].PlayDealIn(
                    DealStagger, () => PlayOrderBell(committedReceipt));
            }
            if (replacementDeal != null)
                StartDealBarrier(replacementDeal.Duration(false));
        }

        /// <summary>
        /// Rings only when a single replacement card lands during Playing. Initial deals, refreshes and paused
        /// rounds stay silent.
        /// </summary>
        private void PlayOrderBell(BartenderDeliveryReceipt receipt)
        {
            if (!IsCurrentDeliveryReceipt(receipt)
                || controller.State != BartenderLevelState.Playing) return;
            BsAudio.Instance?.Play(BsSfx.OrderBell, OrderBellVolume);
        }

        private void RotateCardViewsLeft(int deliveredSlot, int slotCount)
        {
            if (cards == null || deliveredSlot < 0 || deliveredSlot >= slotCount
                || slotCount > cards.Length)
                return;

            OrderCardView departing = cards[deliveredSlot];
            for (int i = deliveredSlot; i < slotCount - 1; i++)
                cards[i] = cards[i + 1];
            cards[slotCount - 1] = departing;
        }

        private void CancelQueueTransition()
        {
            Sequence oldTransition = queueTransition;
            queueTransition = null;
            queueWatchdogAtUnscaledTime = -1f;
            if (oldTransition != null && oldTransition.IsActive())
                oldTransition.Kill(false);
            transitionDeliveredSlot = -1;
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
            CancelQueueTransition();
            presentationState.Dispatch(BsOrderStripTrigger.QueueCompleted);
            ClearDeliveryLatch();
            snapshotDirty = false;
            float dealDuration = ApplySnapshot(false, false);
            if (dealDuration > 0f) StartDealBarrier(dealDuration);
        }

        private void ClearDeliveryLatch()
        {
            deferredAtFrame = -1;
            deferredAtUnscaledTime = -1f;
            pendingDeliveredSlot = -1;
            pendingDeliveryReceipt = null;
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

        private int VisibleOrderCount(int slotCount)
        {
            if (cards == null || controller == null) return 0;
            int count = 0;
            int limit = Mathf.Min(cards.Length, Mathf.Max(0, slotCount));
            for (int i = 0; i < limit; i++)
                if (controller.OrderAtSlot(i) != null) count++;
            return count;
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

        /// <summary>Checks whether a scene glass matches this slot.</summary>
        private bool HasMatchingGlass(int slotIndex)
        {
            if (controller == null) return false;
            BsBoard snapshot = controller.Board;
            if (snapshot == null) return false;
            for (int i = 0; i < snapshot.Glasses.Count; i++)
                if (snapshot.MatchedSlot(snapshot.Glasses[i]) == slotIndex) return true;
            return false;
        }

        /// <summary>Finds the expired order's current view before emphasizing its zero timer.</summary>
        public Tween PlayTimedOrderExpiredFeedback(int slotIndex)
        {
            if (!isActiveAndEnabled || controller == null || cards == null
                || presentationState.State != BsOrderStripState.Ready
                || slotIndex < 0 || slotIndex >= cards.Length)
                return null;

            OrderCardView card = cards[slotIndex];
            OrderDef order = controller.OrderAtSlot(slotIndex);
            if (card == null || card.Model == null || order == null
                || card.Model.RuntimeOrderIndex != order.RuntimeOrderIndex
                || !controller.TryGetOrderTimeRemaining(slotIndex, out float remaining, out _)
                || remaining > 0f)
                return null;

            ResetCountdownTicks();
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

            bool motionAllowed = controller.State == BartenderLevelState.Playing
                                 && !controller.PresentationLocked;

            float mostUrgent = float.PositiveInfinity;
            int mostUrgentOrderIndex = -1;
            for (int i = 0; i < cards.Length; i++)
            {
                if (cards[i] == null) continue;
                if (controller.TryGetOrderTimeRemaining(i, out float remaining,
                        out float duration))
                {
                    cards[i].SetTimer(remaining, duration, motionAllowed);
                    if (remaining > 0f && remaining < mostUrgent)
                    {
                        mostUrgent = remaining;
                        mostUrgentOrderIndex = cards[i].Model != null
                            ? cards[i].Model.RuntimeOrderIndex
                            : -1;
                    }
                }
            }

            DriveCountdownTicks(mostUrgent, mostUrgentOrderIndex, motionAllowed);
        }

        /// <summary>
        /// Only the order with the least time drives the last-second ticking, so multiple cards cannot stack
        /// beats.
        /// </summary>
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

            // Start the clock on entering the critical window without adding an extra beat.
            bool firstFrameInWindow = lastTickedSecond < 0;
            lastTickedSecond = second;
            if (firstFrameInWindow) return;

            // Use odd/even seconds for tick/tock so switching cards keeps the rhythm consistent.
            BsSfx clip = (second & 1) == 1 ? BsSfx.TimerTick : BsSfx.TimerTock;

            // Raise pitch and volume in the last three seconds, keeping tempo fixed and volume capped at 0.98.
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

        // ---- Wiring -----------------------------------------------------------------

        private void EnsureSignalQueue()
        {
            if (signalQueue == null)
                signalQueue = new OrderStripSignalQueue(HandleSignal);
        }

        /// <summary>
        /// Handles queued signals in one place. Always sync barriers; the queue catches errors and clears
        /// nested work.
        /// </summary>
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
                            "Bilinmeyen order strip sinyali işlendi.");
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
                // Mark changes dirty during deals and queue movement. Read the latest snapshot once when they
                // finish.
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
            if (!MatchesPendingDelivery(epoch, receipt)
                || !presentationState.Dispatch(
                    BsOrderStripTrigger.StampHoldElapsed)) return;
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
            if (!MatchesPendingDelivery(epoch, receipt)
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

            bool shouldBlock = Application.isPlaying && isActiveAndEnabled
                               && controller != null
                               && presentationState.TransitionPlaying;
            if (presentationBarrierController != null
                && (!shouldBlock
                    || !ReferenceEquals(presentationBarrierController, controller)))
                ReleaseControllerPresentationBarrier();

            if (!shouldBlock || presentationBarrierController != null) return;
            if (controller.AcquirePresentationBarrier(this))
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

            int slot = receipt != null ? receipt.SlotIndex : -1;
            int slotCount = ActiveSlotCount();
            if (!currentReceipt || slot < 0 || slot >= slotCount || cards == null
                || slot >= cards.Length || cards[slot] == null
                || !SameOrder(cards[slot].Model, receipt.DeliveredOrder))
            {
                signalQueue.Enqueue(OrderStripSignal.SnapshotDirty());
                return;
            }

            if (!presentationState.Dispatch(BsOrderStripTrigger.DeliveryCommitted))
                return;

            dealCompletionAtUnscaledTime = -1f;
            dealCompletionEpoch = -1;
            deferredAtFrame = Time.frameCount;
            deferredAtUnscaledTime = Time.unscaledTime;
            pendingDeliveredSlot = slot;
            pendingDeliveryReceipt = receipt;

            for (int i = 0; i < cards.Length; i++)
                if (cards[i] != null) cards[i].SuspendTimerEmphasis();
            cards[slot].ShowDelivered();
        }

        private bool MatchesPendingDelivery(
            int epoch, BartenderDeliveryReceipt receipt) =>
            pendingDeliveryReceipt != null && receipt != null
            && epoch == presentationEpoch
            && ReferenceEquals(receipt, pendingDeliveryReceipt)
            && IsCurrentDeliveryReceipt(receipt);

        private bool IsCurrentDeliveryReceipt(BartenderDeliveryReceipt receipt)
        {
            return controller != null && receipt != null
                   && receipt.AttemptId.IsValid
                   && receipt.OperationId.IsValid
                   && receipt.DomainRevision > 0L
                   && receipt.BoardRevision >= 0
                   && receipt.Cause == BsRoundTransitionCause.PlayerDelivery
                   && controller.CurrentRoundStamp == new BsRoundCommandStamp(
                       receipt.AttemptId,
                       receipt.Token,
                       receipt.DomainRevision,
                       receipt.BoardRevision);
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
            if (controller == null || cards == null
                || presentationState.State != BsOrderStripState.Ready
                || controller.State != BartenderLevelState.Playing
                || controller.CurrentLevel == null
                || !controller.CurrentLevel.AllowTimedOrders
                || float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0f)
                return;

            TimeBoostFlightPresenter flight = timeBoostFlight;
            if (flight != null) flight.BeginBoost();

            int slotCount = ActiveSlotCount();
            for (int slot = 0; slot < slotCount; slot++)
            {
                OrderCardView card = cards[slot];
                if (card == null || card.Model == null || card.Model.TimeLimit <= 0f
                    || !controller.TryGetOrderTimeRemaining(slot, out _, out _))
                    continue;

                int orderIndex = card.Model.RuntimeOrderIndex;
                int targetSlot = slot;
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

            bool motionAllowed = controller.State == BartenderLevelState.Playing
                                 && !controller.PresentationLocked;
            card.SetTimer(remaining, duration, motionAllowed);
            // A surviving card can receive its flying +time spark during delivery movement. The bonus is saved
            // already; OrderCardView checks its own lifecycle gate.
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
