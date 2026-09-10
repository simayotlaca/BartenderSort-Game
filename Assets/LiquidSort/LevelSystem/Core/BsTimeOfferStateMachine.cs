using System;

namespace BartenderSort.Core
{
    /// <summary>Unique increasing ID for one time offer. The default value is invalid.</summary>
    public readonly struct BsTimeOfferId : IEquatable<BsTimeOfferId>
    {
        public long Value { get; }
        public bool IsValid => Value > 0L;

        public BsTimeOfferId(long value)
        {
            Value = value;
        }

        public bool Equals(BsTimeOfferId other) => Value == other.Value;
        public override bool Equals(object obj) =>
            obj is BsTimeOfferId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public static bool operator ==(BsTimeOfferId left, BsTimeOfferId right) =>
            left.Equals(right);
        public static bool operator !=(BsTimeOfferId left, BsTimeOfferId right) =>
            !left.Equals(right);
        public override string ToString() => IsValid ? $"TimeOffer {Value}" : "No TimeOffer";
    }

    /// <summary>Immutable payload used both for first presentation and rehydration.</summary>
    public readonly struct BsTimeOfferSnapshot
    {
        public BsTimeOfferId Id { get; }
        public int SlotIndex { get; }
        public int CoinCost { get; }
        public float AddedSeconds { get; }

        internal BsTimeOfferSnapshot(
            BsTimeOfferId id, int slotIndex, int coinCost, float addedSeconds)
        {
            Id = id;
            SlotIndex = slotIndex;
            CoinCost = coinCost;
            AddedSeconds = addedSeconds;
        }
    }

    public enum BsTimeOfferAcceptStatus
    {
        Rejected = 0,
        PurchaseRejected = 1,
        Accepted = 2,
    }

    public readonly struct BsTimeOfferAcceptResult
    {
        public BsTimeOfferAcceptStatus Status { get; }
        public BsTimeOfferId OfferId { get; }
        public string RejectionReason { get; }
        public bool Accepted => Status == BsTimeOfferAcceptStatus.Accepted;
        public bool OfferRemainsOpen =>
            Status == BsTimeOfferAcceptStatus.PurchaseRejected;

        private BsTimeOfferAcceptResult(
            BsTimeOfferAcceptStatus status,
            BsTimeOfferId offerId,
            string rejectionReason)
        {
            Status = status;
            OfferId = offerId;
            RejectionReason = rejectionReason;
        }

        internal static BsTimeOfferAcceptResult Reject(
            BsTimeOfferId offerId, string reason) =>
            new BsTimeOfferAcceptResult(
                BsTimeOfferAcceptStatus.Rejected, offerId, reason);

        internal static BsTimeOfferAcceptResult RejectPurchase(
            BsTimeOfferId offerId, string reason) =>
            new BsTimeOfferAcceptResult(
                BsTimeOfferAcceptStatus.PurchaseRejected, offerId, reason);

        internal static BsTimeOfferAcceptResult Accept(BsTimeOfferId offerId) =>
            new BsTimeOfferAcceptResult(
                BsTimeOfferAcceptStatus.Accepted, offerId, null);
    }

    public enum BsTimeOfferDeclineStatus
    {
        Rejected = 0,
        DecisionAcceptedSettlementPending = 1,
        DecisionAcceptedTerminalCommitted = 2,
    }

    public readonly struct BsTimeOfferDeclineResult
    {
        public BsTimeOfferDeclineStatus Status { get; }
        public BsTimeOfferId OfferId { get; }
        public string RejectionReason { get; }
        public bool DecisionAccepted =>
            Status == BsTimeOfferDeclineStatus.DecisionAcceptedSettlementPending
            || Status == BsTimeOfferDeclineStatus.DecisionAcceptedTerminalCommitted;
        public bool TerminalCommitted =>
            Status == BsTimeOfferDeclineStatus.DecisionAcceptedTerminalCommitted;

        private BsTimeOfferDeclineResult(
            BsTimeOfferDeclineStatus status,
            BsTimeOfferId offerId,
            string rejectionReason)
        {
            Status = status;
            OfferId = offerId;
            RejectionReason = rejectionReason;
        }

        internal static BsTimeOfferDeclineResult Reject(
            BsTimeOfferId offerId, string reason) =>
            new BsTimeOfferDeclineResult(
                BsTimeOfferDeclineStatus.Rejected, offerId, reason);

        internal static BsTimeOfferDeclineResult Defer(
            BsTimeOfferId offerId, string reason) =>
            new BsTimeOfferDeclineResult(
                BsTimeOfferDeclineStatus.DecisionAcceptedSettlementPending,
                offerId,
                reason);

        internal static BsTimeOfferDeclineResult Commit(BsTimeOfferId offerId) =>
            new BsTimeOfferDeclineResult(
                BsTimeOfferDeclineStatus.DecisionAcceptedTerminalCommitted,
                offerId,
                null);
    }

    /// <summary>
    /// Saves a declined offer's navigation intent with its outbox so view lifecycles cannot change the
    /// destination.
    /// </summary>
    public enum BsTimeOfferDeclineDisposition
    {
        PresentFailure = 0,
        ReturnToMainMenu = 1,
    }

    public enum BsTimeOfferSettlementStatus
    {
        Invalid = 0,
        SettlementPending = 1,
        TerminalCommitted = 2,
    }

    /// <summary>
    /// Keeps the consumed decline receipt visible through save retries and after commit, until the next
    /// round.
    /// </summary>
    public readonly struct BsTimeOfferSettlementSnapshot
    {
        public BsTimeOfferId OfferId { get; }
        public BsTimeOfferDeclineDisposition Disposition { get; }
        public BsTimeOfferSettlementStatus Status { get; }
        public bool IsValid => OfferId.IsValid
            && (Status == BsTimeOfferSettlementStatus.SettlementPending
                || Status == BsTimeOfferSettlementStatus.TerminalCommitted);
        public bool IsPending => Status == BsTimeOfferSettlementStatus.SettlementPending;
        public bool TerminalCommitted =>
            Status == BsTimeOfferSettlementStatus.TerminalCommitted;

        internal BsTimeOfferSettlementSnapshot(
            BsTimeOfferId offerId,
            BsTimeOfferDeclineDisposition disposition,
            BsTimeOfferSettlementStatus status)
        {
            OfferId = offerId;
            Disposition = disposition;
            Status = status;
        }

        internal BsTimeOfferSettlementSnapshot WithStatus(
            BsTimeOfferSettlementStatus status) =>
            new BsTimeOfferSettlementSnapshot(OfferId, Disposition, status);
    }

    public enum BsTimeOfferSettlementRetryStatus
    {
        Rejected = 0,
        StillPending = 1,
        TerminalCommitted = 2,
        AlreadyCommitted = 3,
    }

    public readonly struct BsTimeOfferSettlementRetryResult
    {
        public BsTimeOfferSettlementRetryStatus Status { get; }
        public BsTimeOfferId OfferId { get; }
        public string RejectionReason { get; }
        public bool Accepted =>
            Status == BsTimeOfferSettlementRetryStatus.StillPending
            || Status == BsTimeOfferSettlementRetryStatus.TerminalCommitted
            || Status == BsTimeOfferSettlementRetryStatus.AlreadyCommitted;
        public bool TerminalCommitted =>
            Status == BsTimeOfferSettlementRetryStatus.TerminalCommitted
            || Status == BsTimeOfferSettlementRetryStatus.AlreadyCommitted;

        private BsTimeOfferSettlementRetryResult(
            BsTimeOfferSettlementRetryStatus status,
            BsTimeOfferId offerId,
            string rejectionReason)
        {
            Status = status;
            OfferId = offerId;
            RejectionReason = rejectionReason;
        }

        internal static BsTimeOfferSettlementRetryResult Reject(
            BsTimeOfferId offerId, string reason) =>
            new BsTimeOfferSettlementRetryResult(
                BsTimeOfferSettlementRetryStatus.Rejected, offerId, reason);

        internal static BsTimeOfferSettlementRetryResult Pending(
            BsTimeOfferId offerId) =>
            new BsTimeOfferSettlementRetryResult(
                BsTimeOfferSettlementRetryStatus.StillPending, offerId, null);

        internal static BsTimeOfferSettlementRetryResult Commit(
            BsTimeOfferId offerId) =>
            new BsTimeOfferSettlementRetryResult(
                BsTimeOfferSettlementRetryStatus.TerminalCommitted, offerId, null);

        internal static BsTimeOfferSettlementRetryResult AlreadyCommitted(
            BsTimeOfferId offerId) =>
            new BsTimeOfferSettlementRetryResult(
                BsTimeOfferSettlementRetryStatus.AlreadyCommitted, offerId, null);
    }

    internal enum BsTimeOfferState
    {
        Closed = 0,
        Open = 1,
        Accepting = 2,
        DeclineOutboxCommitting = 3,
        DeclineCommitting = 4,
        DeclineSettlementPending = 5,
    }

    internal enum BsTimeOfferTrigger
    {
        Open = 0,
        BeginAccept = 1,
        AcceptCommitted = 2,
        AcceptRejected = 3,
        BeginDecline = 4,
        DeclineRejected = 5,
        DeclineOutboxCommitted = 6,
        SettlementCommitted = 7,
        SettlementDeferred = 8,
        RetryDue = 9,
        Reset = 10,
    }

    /// <summary>
    /// Owns offer state without Unity or saving code. Run external work after accepting transitions so
    /// callbacks see a complete state.
    /// </summary>
    internal sealed class BsTimeOfferStateMachine
    {
        private long lastIssuedId;

        public BsTimeOfferState State { get; private set; } = BsTimeOfferState.Closed;
        public BsTimeOfferId CurrentId { get; private set; }
        public bool IsActive => State != BsTimeOfferState.Closed;
        /// <summary>
        /// Only an idle offer can be shown again. Accepting keeps its lease but blocks the view from
        /// reopening during save callbacks.
        /// </summary>
        public bool IsPresentable => State == BsTimeOfferState.Open;
        public bool HoldsPresentationLease =>
            State == BsTimeOfferState.Open
            || State == BsTimeOfferState.Accepting
            || State == BsTimeOfferState.DeclineOutboxCommitting;

        public bool TryOpen(out BsTimeOfferId offerId)
        {
            offerId = default;
            if (!TryResolve(State, BsTimeOfferTrigger.Open, out BsTimeOfferState next))
                return false;

            if (lastIssuedId == long.MaxValue)
                throw new InvalidOperationException("Time-offer id space was exhausted.");

            offerId = new BsTimeOfferId(++lastIssuedId);
            CurrentId = offerId;
            State = next;
            return true;
        }

        public bool Dispatch(BsTimeOfferTrigger trigger, BsTimeOfferId expectedOfferId)
        {
            if (trigger == BsTimeOfferTrigger.Open
                || trigger == BsTimeOfferTrigger.Reset
                || !expectedOfferId.IsValid
                || expectedOfferId != CurrentId
                || !TryResolve(State, trigger, out BsTimeOfferState next))
                return false;

            State = next;
            if (next == BsTimeOfferState.Closed) CurrentId = default;
            return true;
        }

        public void Reset()
        {
            State = BsTimeOfferState.Closed;
            CurrentId = default;
        }

        /// <summary>Restored receipts are no longer active, but their IDs must still invalidate older callbacks.</summary>
        public void InvalidateThrough(BsTimeOfferId observedId)
        {
            if (observedId.IsValid && observedId.Value > lastIssuedId)
                lastIssuedId = observedId.Value;
        }

        internal static bool TryResolve(
            BsTimeOfferState from,
            BsTimeOfferTrigger trigger,
            out BsTimeOfferState next)
        {
            next = from;
            int stateValue = (int)from;
            int triggerValue = (int)trigger;
            if (stateValue < (int)BsTimeOfferState.Closed
                || stateValue > (int)BsTimeOfferState.DeclineSettlementPending
                || triggerValue < (int)BsTimeOfferTrigger.Open
                || triggerValue > (int)BsTimeOfferTrigger.Reset)
                return false;
            if (trigger == BsTimeOfferTrigger.Reset)
            {
                next = BsTimeOfferState.Closed;
                return true;
            }

            switch (from)
            {
                case BsTimeOfferState.Closed:
                    if (trigger != BsTimeOfferTrigger.Open) return false;
                    next = BsTimeOfferState.Open;
                    return true;

                case BsTimeOfferState.Open:
                    if (trigger == BsTimeOfferTrigger.BeginAccept)
                    {
                        next = BsTimeOfferState.Accepting;
                        return true;
                    }
                    if (trigger == BsTimeOfferTrigger.BeginDecline)
                    {
                        next = BsTimeOfferState.DeclineOutboxCommitting;
                        return true;
                    }
                    return false;

                case BsTimeOfferState.Accepting:
                    if (trigger == BsTimeOfferTrigger.AcceptCommitted)
                    {
                        next = BsTimeOfferState.Closed;
                        return true;
                    }
                    if (trigger == BsTimeOfferTrigger.AcceptRejected)
                    {
                        next = BsTimeOfferState.Open;
                        return true;
                    }
                    return false;

                case BsTimeOfferState.DeclineOutboxCommitting:
                    if (trigger == BsTimeOfferTrigger.DeclineRejected)
                    {
                        next = BsTimeOfferState.Open;
                        return true;
                    }
                    if (trigger == BsTimeOfferTrigger.DeclineOutboxCommitted)
                    {
                        next = BsTimeOfferState.DeclineCommitting;
                        return true;
                    }
                    return false;

                case BsTimeOfferState.DeclineCommitting:
                    if (trigger == BsTimeOfferTrigger.SettlementCommitted)
                    {
                        next = BsTimeOfferState.Closed;
                        return true;
                    }
                    if (trigger == BsTimeOfferTrigger.SettlementDeferred)
                    {
                        next = BsTimeOfferState.DeclineSettlementPending;
                        return true;
                    }
                    return false;

                case BsTimeOfferState.DeclineSettlementPending:
                    if (trigger != BsTimeOfferTrigger.RetryDue) return false;
                    next = BsTimeOfferState.DeclineCommitting;
                    return true;

                default:
                    return false;
            }
        }
    }
}
