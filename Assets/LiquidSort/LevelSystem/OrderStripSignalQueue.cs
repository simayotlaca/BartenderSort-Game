using System;
using System.Collections.Generic;
using System.Threading;
using BartenderSort.Core;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Typed signals accepted by the order strip. Each call can create only the valid data shape for its own
    /// signal.
    /// </summary>
    internal enum OrderStripSignalKind
    {
        Invalid = 0,
        Activate,
        Deactivate,
        LevelLoaded,
        SnapshotDirty,
        LevelStateChanged,
        BoardCommitted,
        TimeBoosted,
        StampHoldElapsed,
        QueueAnimationFinished,
        QueueAnimationAborted,
        DealAnimationFinished,
    }

    internal readonly struct OrderStripSignal
    {
        private const int NoEpoch = -1;
        private const int NoSubscriptionGeneration = -1;

        public OrderStripSignalKind Kind { get; }
        public BsLevel Level { get; }
        public BartenderLevelState LevelState { get; }
        public BartenderBoardChange BoardChange { get; }
        public BartenderDeliveryReceipt Receipt { get; }
        public int PresentationEpoch { get; }
        public int SubscriptionGeneration { get; }
        public float TimeBoostSeconds { get; }

        private OrderStripSignal(
            OrderStripSignalKind kind,
            BsLevel level = null,
            BartenderLevelState levelState = default,
            BartenderBoardChange boardChange = null,
            BartenderDeliveryReceipt receipt = null,
            int presentationEpoch = NoEpoch,
            int subscriptionGeneration = NoSubscriptionGeneration,
            float timeBoostSeconds = 0f)
        {
            Kind = kind;
            Level = level;
            LevelState = levelState;
            BoardChange = boardChange;
            Receipt = receipt;
            PresentationEpoch = presentationEpoch;
            SubscriptionGeneration = subscriptionGeneration;
            TimeBoostSeconds = timeBoostSeconds;
        }

        public static OrderStripSignal Activate() =>
            new OrderStripSignal(OrderStripSignalKind.Activate);

        public static OrderStripSignal Deactivate() =>
            new OrderStripSignal(OrderStripSignalKind.Deactivate);

        public static OrderStripSignal LevelLoaded(
            BsLevel level, int subscriptionGeneration)
        {
            if (level == null) throw new ArgumentNullException(nameof(level));
            ValidateSubscriptionGeneration(subscriptionGeneration);
            return new OrderStripSignal(
                OrderStripSignalKind.LevelLoaded, level,
                subscriptionGeneration: subscriptionGeneration);
        }

        public static OrderStripSignal SnapshotDirty() =>
            new OrderStripSignal(OrderStripSignalKind.SnapshotDirty);

        public static OrderStripSignal ControllerSnapshotDirty(
            int subscriptionGeneration)
        {
            ValidateSubscriptionGeneration(subscriptionGeneration);
            return new OrderStripSignal(
                OrderStripSignalKind.SnapshotDirty,
                subscriptionGeneration: subscriptionGeneration);
        }

        public static OrderStripSignal LevelStateChanged(
            BartenderLevelState state, int subscriptionGeneration)
        {
            if (!IsKnownLevelState(state))
                throw new ArgumentOutOfRangeException(nameof(state), state,
                    "Bilinmeyen level state sinyali kuyruğa alınamaz.");
            ValidateSubscriptionGeneration(subscriptionGeneration);
            return new OrderStripSignal(
                OrderStripSignalKind.LevelStateChanged, levelState: state,
                subscriptionGeneration: subscriptionGeneration);
        }

        public static OrderStripSignal BoardCommitted(
            BartenderBoardChange change, int subscriptionGeneration)
        {
            if (change == null) throw new ArgumentNullException(nameof(change));
            ValidateSubscriptionGeneration(subscriptionGeneration);
            return new OrderStripSignal(
                OrderStripSignalKind.BoardCommitted, boardChange: change,
                subscriptionGeneration: subscriptionGeneration);
        }

        public static OrderStripSignal TimeBoosted(
            float seconds, int subscriptionGeneration)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0f)
                throw new ArgumentOutOfRangeException(nameof(seconds), seconds,
                    "Time boost pozitif ve sonlu olmalıdır.");
            ValidateSubscriptionGeneration(subscriptionGeneration);
            return new OrderStripSignal(
                OrderStripSignalKind.TimeBoosted,
                subscriptionGeneration: subscriptionGeneration,
                timeBoostSeconds: seconds);
        }

        public static OrderStripSignal StampHoldElapsed(
            int epoch, BartenderDeliveryReceipt receipt) =>
            PresentationSignal(
                OrderStripSignalKind.StampHoldElapsed, epoch, receipt);

        public static OrderStripSignal QueueAnimationFinished(
            int epoch, BartenderDeliveryReceipt receipt) =>
            PresentationSignal(
                OrderStripSignalKind.QueueAnimationFinished, epoch, receipt);

        public static OrderStripSignal QueueAnimationAborted(
            int epoch, BartenderDeliveryReceipt receipt) =>
            PresentationSignal(
                OrderStripSignalKind.QueueAnimationAborted, epoch, receipt);

        public static OrderStripSignal DealAnimationFinished(int epoch)
        {
            ValidateEpoch(epoch);
            return new OrderStripSignal(
                OrderStripSignalKind.DealAnimationFinished,
                presentationEpoch: epoch);
        }

        internal bool IsValid()
        {
            switch (Kind)
            {
                case OrderStripSignalKind.Activate:
                case OrderStripSignalKind.Deactivate:
                    return HasNoPayload();

                case OrderStripSignalKind.SnapshotDirty:
                    return Level == null && BoardChange == null && Receipt == null
                           && PresentationEpoch == NoEpoch
                           && SubscriptionGeneration >= NoSubscriptionGeneration
                           && HasNoTimeBoost();

                case OrderStripSignalKind.LevelLoaded:
                    return Level != null && BoardChange == null && Receipt == null
                           && PresentationEpoch == NoEpoch
                           && SubscriptionGeneration >= 0 && HasNoTimeBoost();

                case OrderStripSignalKind.LevelStateChanged:
                    return IsKnownLevelState(LevelState) && Level == null
                           && BoardChange == null && Receipt == null
                           && PresentationEpoch == NoEpoch
                           && SubscriptionGeneration >= 0 && HasNoTimeBoost();

                case OrderStripSignalKind.BoardCommitted:
                    return BoardChange != null && Receipt == null && Level == null
                           && PresentationEpoch == NoEpoch
                           && SubscriptionGeneration >= 0 && HasNoTimeBoost();

                case OrderStripSignalKind.TimeBoosted:
                    return TimeBoostSeconds > 0f
                           && !float.IsNaN(TimeBoostSeconds)
                           && !float.IsInfinity(TimeBoostSeconds)
                           && Level == null && BoardChange == null && Receipt == null
                           && PresentationEpoch == NoEpoch
                           && SubscriptionGeneration >= 0;

                case OrderStripSignalKind.StampHoldElapsed:
                case OrderStripSignalKind.QueueAnimationFinished:
                case OrderStripSignalKind.QueueAnimationAborted:
                    return Receipt != null && PresentationEpoch >= 0
                           && Level == null && BoardChange == null
                           && SubscriptionGeneration == NoSubscriptionGeneration
                           && HasNoTimeBoost();

                case OrderStripSignalKind.DealAnimationFinished:
                    return PresentationEpoch >= 0 && Level == null
                           && BoardChange == null && Receipt == null
                           && SubscriptionGeneration == NoSubscriptionGeneration
                           && HasNoTimeBoost();

                default:
                    return false;
            }
        }

        private bool HasNoPayload() =>
            Level == null && BoardChange == null && Receipt == null
            && PresentationEpoch == NoEpoch
            && SubscriptionGeneration == NoSubscriptionGeneration
            && HasNoTimeBoost();

        private bool HasNoTimeBoost() => TimeBoostSeconds == 0f;

        private static OrderStripSignal PresentationSignal(
            OrderStripSignalKind kind,
            int epoch,
            BartenderDeliveryReceipt receipt)
        {
            ValidateEpoch(epoch);
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            return new OrderStripSignal(
                kind, receipt: receipt, presentationEpoch: epoch);
        }

        private static void ValidateEpoch(int epoch)
        {
            if (epoch < 0)
                throw new ArgumentOutOfRangeException(nameof(epoch), epoch,
                    "Presentation epoch negatif olamaz.");
        }

        private static void ValidateSubscriptionGeneration(int generation)
        {
            if (generation < 0)
                throw new ArgumentOutOfRangeException(nameof(generation), generation,
                    "Subscription generation negatif olamaz.");
        }

        private static bool IsKnownLevelState(BartenderLevelState state)
        {
            switch (state)
            {
                case BartenderLevelState.Unloaded:
                case BartenderLevelState.Playing:
                case BartenderLevelState.Paused:
                case BartenderLevelState.Won:
                case BartenderLevelState.Failed:
                case BartenderLevelState.CampaignComplete:
                    return true;

                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// One synchronous FIFO queue with a fixed handler. Nested signals wait their turn; errors clear nested
    /// work so the next independent signal starts cleanly.
    /// </summary>
    internal sealed class OrderStripSignalQueue
    {
        private readonly Queue<OrderStripSignal> pending =
            new Queue<OrderStripSignal>();
        private readonly Action<OrderStripSignal> handler;
        private readonly int ownerThreadId;
        private bool draining;

        public OrderStripSignalQueue(Action<OrderStripSignal> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (handler.GetInvocationList().Length != 1)
                throw new ArgumentException(
                    "Order strip kuyruğu tam olarak bir owner handler kabul eder.",
                    nameof(handler));

            this.handler = handler;
            ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public void Enqueue(OrderStripSignal signal)
        {
            EnsureOwnerThread();
            if (!signal.IsValid())
                throw new ArgumentException(
                    "Default, bilinmeyen veya payload şekli bozuk sinyal kuyruğa alınamaz.",
                    nameof(signal));

            pending.Enqueue(signal);
            if (draining) return;

            draining = true;
            try
            {
                while (pending.Count > 0)
                    handler(pending.Dequeue());
            }
            catch
            {
                // Discard work queued by the failed callback so it cannot replay on the next signal.
                pending.Clear();
                throw;
            }
            finally
            {
                draining = false;
            }
        }

        private void EnsureOwnerThread()
        {
            if (Thread.CurrentThread.ManagedThreadId == ownerThreadId) return;
            throw new InvalidOperationException(
                "Order strip sinyal kuyruğuna yalnız owner thread erişebilir.");
        }
    }
}
