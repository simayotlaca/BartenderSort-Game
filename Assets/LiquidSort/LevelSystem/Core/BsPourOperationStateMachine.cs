using System;

namespace BartenderSort.Core
{
    public enum BsPourOperationState
    {
        Reserved = 0,
        Saving = 1,
        Committed = 2,
        Animating = 3,
        Settling = 4,
        Settled = 5,
    }

    public enum BsPourSettlementReason
    {
        Completed = 0,
        Rejected = 1,
        Cancelled = 2,
        TimedOut = 3,
        Invalidated = 4,
        Faulted = 5,
        PresentedImmediately = 6,
    }

    public sealed class BsPourOperationStateMachine
    {
        private readonly double reservedAt;
        private readonly double timeoutSeconds;

        public long RunId { get; }
        public int SourceGlassId { get; }
        public int TargetGlassId { get; }
        public bool IsUndo { get; }
        public BsPourOperationState State { get; private set; }
        public double Deadline { get; private set; }

        public int AnimationOperationId { get; private set; }

        public BsPourSettlementReason? SettlementReason { get; private set; }
        public bool IsPersistencePending => State == BsPourOperationState.Saving;
        public bool IsActive => State != BsPourOperationState.Settling
            && State != BsPourOperationState.Settled;

        public BsPourOperationStateMachine(
            long runId,
            int sourceGlassId,
            int targetGlassId,
            bool isUndo,
            double now,
            double timeoutSeconds)
        {
            if (runId <= 0L)
                throw new ArgumentOutOfRangeException(nameof(runId));
            bool hasKnownPair = sourceGlassId >= 0 && targetGlassId >= 0
                && sourceGlassId != targetGlassId;
            bool hasUnknownUndoPair = isUndo && sourceGlassId == -1 && targetGlassId == -1;
            if (!hasKnownPair && !hasUnknownUndoPair)
                throw new ArgumentException("A pour requires distinct glass IDs; only undo may reserve an unknown pair.");
            if (!IsValidTime(now))
                throw new ArgumentOutOfRangeException(nameof(now));
            if (!TryGetDeadline(now, timeoutSeconds, out double deadline))
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

            RunId = runId;
            SourceGlassId = sourceGlassId;
            TargetGlassId = targetGlassId;
            IsUndo = isUndo;
            reservedAt = now;
            this.timeoutSeconds = timeoutSeconds;
            Deadline = deadline;
            State = BsPourOperationState.Reserved;
        }

        public bool TryBeginSaving()
        {
            if (State != BsPourOperationState.Reserved) return false;
            State = BsPourOperationState.Saving;
            return true;
        }

        public bool TryCommit(double now)
        {
            if ((State != BsPourOperationState.Reserved && State != BsPourOperationState.Saving)
                || !IsValidTime(now) || now < reservedAt
                || !TryGetDeadline(now, timeoutSeconds, out double deadline))
                return false;

            Deadline = deadline;
            State = BsPourOperationState.Committed;
            return true;
        }

        public bool TryBeginAnimation(int operationId)
        {
            if (State != BsPourOperationState.Committed || operationId == 0) return false;
            AnimationOperationId = operationId;
            State = BsPourOperationState.Animating;
            return true;
        }

        public bool TryBeginSettlement(BsPourSettlementReason reason)
        {
            if (!IsActive) return false;

            switch (reason)
            {
                case BsPourSettlementReason.Completed:
                    if (State != BsPourOperationState.Animating) return false;
                    break;
                case BsPourSettlementReason.PresentedImmediately:
                    if (State != BsPourOperationState.Committed) return false;
                    break;
                case BsPourSettlementReason.Rejected:
                    if (State != BsPourOperationState.Reserved
                        && State != BsPourOperationState.Saving) return false;
                    break;
                case BsPourSettlementReason.TimedOut:
                    if (IsPersistencePending) return false;
                    break;
                case BsPourSettlementReason.Cancelled:
                case BsPourSettlementReason.Invalidated:
                case BsPourSettlementReason.Faulted:
                    break;
                default:
                    return false;
            }

            SettlementReason = reason;
            State = BsPourOperationState.Settling;
            return true;
        }

        public bool CompleteSettlement()
        {
            if (State != BsPourOperationState.Settling) return false;
            State = BsPourOperationState.Settled;
            return true;
        }

        public bool HasExpired(double now) => IsActive && !IsPersistencePending
            && IsValidTime(now) && now >= Deadline;

        private static bool IsValidTime(double value) => value >= 0d
            && !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool TryGetDeadline(double now, double timeout, out double deadline)
        {
            deadline = now + timeout;
            return timeout > 0d && !double.IsNaN(timeout) && !double.IsInfinity(timeout)
                && !double.IsInfinity(deadline) && deadline > now;
        }
    }
}
