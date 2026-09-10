using System;
using System.Collections.Generic;
using BartenderSort.Core;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Identifies one result presentation. Keep the whole receipt so delayed callbacks cannot act on another
    /// attempt reusing its gameplay token.
    /// </summary>
    public readonly struct BartenderTerminalPresentationReceipt
    {
        public BsAttemptId AttemptId { get; }
        public BsOperationId RoundOperationId { get; }
        public long Revision { get; }
        public int BoardRevision { get; }
        public BsRoundTransitionCause Cause { get; }
        public BsRoundOutcome Outcome { get; }
        public BsRoundToken Token { get; }

        internal BartenderTerminalPresentationReceipt(
            BsRoundTransition transition,
            BsRoundOutcome outcome)
        {
            if (transition == null)
                throw new ArgumentNullException(nameof(transition));
            AttemptId = transition.AttemptId;
            RoundOperationId = transition.OperationId;
            Revision = transition.Revision;
            BoardRevision = transition.BoardRevision;
            Cause = transition.Cause;
            Outcome = outcome;
            Token = transition.Token;
        }

        public bool IsValid => AttemptId.IsValid && RoundOperationId.IsValid
            && Revision > 0L;
    }

    /// <summary>
    /// Identifies an accepted terminal command. Reserve OperationId when queued so completion can be matched
    /// before execution starts.
    /// </summary>
    public readonly struct BartenderTerminalCommandReceipt :
        IEquatable<BartenderTerminalCommandReceipt>
    {
        public long OperationId { get; }
        public BsAttemptId AttemptId { get; }
        public BsOperationId RoundOperationId { get; }
        public long Revision { get; }
        public int BoardRevision { get; }
        public BsRoundTransitionCause Cause { get; }
        public BsRoundOutcome Outcome { get; }
        public BsRoundToken Token { get; }
        public bool IsValid => OperationId > 0L && AttemptId.IsValid
            && RoundOperationId.IsValid && Revision > 0L;

        internal BartenderTerminalCommandReceipt(
            long operationId,
            BartenderTerminalPresentationReceipt presentation)
        {
            OperationId = operationId;
            AttemptId = presentation.AttemptId;
            RoundOperationId = presentation.RoundOperationId;
            Revision = presentation.Revision;
            BoardRevision = presentation.BoardRevision;
            Cause = presentation.Cause;
            Outcome = presentation.Outcome;
            Token = presentation.Token;
        }

        public bool Equals(BartenderTerminalCommandReceipt other) =>
            OperationId == other.OperationId
            && AttemptId == other.AttemptId
            && RoundOperationId == other.RoundOperationId
            && Revision == other.Revision
            && BoardRevision == other.BoardRevision
            && Cause == other.Cause
            && Outcome == other.Outcome
            && Token == other.Token;

        public override bool Equals(object obj) =>
            obj is BartenderTerminalCommandReceipt other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)(OperationId ^ (OperationId >> 32));
                hash = (hash * 397) ^ AttemptId.GetHashCode();
                hash = (hash * 397) ^ RoundOperationId.GetHashCode();
                hash = (hash * 397) ^ Revision.GetHashCode();
                hash = (hash * 397) ^ BoardRevision;
                hash = (hash * 397) ^ (int)Cause;
                hash = (hash * 397) ^ (int)Outcome;
                return (hash * 397) ^ Token.GetHashCode();
            }
        }

        public static bool operator ==(
            BartenderTerminalCommandReceipt left,
            BartenderTerminalCommandReceipt right) => left.Equals(right);

        public static bool operator !=(
            BartenderTerminalCommandReceipt left,
            BartenderTerminalCommandReceipt right) => !left.Equals(right);
    }

    /// <summary>Completion of one exact accepted terminal command.</summary>
    public readonly struct BartenderTerminalCommandCompletion
    {
        public BartenderTerminalCommandReceipt Receipt { get; }
        public long OperationId => Receipt.OperationId;
        public BsAttemptId AttemptId => Receipt.AttemptId;
        public BsOperationId RoundOperationId => Receipt.RoundOperationId;
        public long Revision => Receipt.Revision;
        public int BoardRevision => Receipt.BoardRevision;
        public BsRoundTransitionCause Cause => Receipt.Cause;
        public BsRoundOutcome Outcome => Receipt.Outcome;
        public BsRoundToken Token => Receipt.Token;
        public bool Succeeded { get; }

        internal BartenderTerminalCommandCompletion(
            BartenderTerminalCommandReceipt receipt,
            bool succeeded)
        {
            Receipt = receipt;
            Succeeded = succeeded;
        }
    }

    /// <summary>
    /// Tracks one retryable trip to the main menu after the terminal command commits. Gameplay state is owned
    /// elsewhere.
    /// </summary>
    internal enum BartenderTerminalNavigationState
    {
        Idle = 0,
        Pending = 1,
        Loading = 2,
    }

    internal sealed class BartenderTerminalNavigationStateMachine
    {
        internal const int RetryDelayFrames = 30;

        private BartenderTerminalNavigationState state;
        private BartenderTerminalCommandReceipt receipt;
        private readonly HashSet<BartenderTerminalCommandReceipt>
            acceptedReceipts = new HashSet<BartenderTerminalCommandReceipt>();
        private int earliestAttemptFrame = -1;

        public BartenderTerminalNavigationState State => state;
        public BartenderTerminalCommandReceipt Receipt => receipt;
        public int EarliestAttemptFrame => earliestAttemptFrame;
        public bool Active => state != BartenderTerminalNavigationState.Idle;

        /// <summary>
        /// Only accepted domain results can reserve a scene load. Replaying the same receipt is safe.
        /// </summary>
        public bool TrySchedule(
            BartenderTerminalCommandReceipt expectedReceipt,
            bool domainCommandAccepted,
            int currentFrame)
        {
            if (!domainCommandAccepted || !expectedReceipt.IsValid)
                return false;

            // After command success, keep its navigation work tracked. Clamp an invalid frame value instead of
            // dropping ownership.
            int safeCurrentFrame = Math.Max(0, currentFrame);

            if (state != BartenderTerminalNavigationState.Idle)
            {
                // A repeated accepted command must not start another menu load, but still retain its exact
                // receipt.
                acceptedReceipts.Add(expectedReceipt);
                return true;
            }

            receipt = expectedReceipt;
            acceptedReceipts.Clear();
            acceptedReceipts.Add(expectedReceipt);
            earliestAttemptFrame = safeCurrentFrame;
            state = BartenderTerminalNavigationState.Pending;
            return true;
        }

        public bool CanAttempt(int currentFrame) =>
            state == BartenderTerminalNavigationState.Pending
            && currentFrame >= 0
            && currentFrame >= earliestAttemptFrame;

        public bool TryRecordAttemptFailed(
            BartenderTerminalCommandReceipt expectedReceipt,
            int currentFrame)
        {
            if (!OwnsPending(expectedReceipt) || !CanAttempt(currentFrame))
                return false;

            earliestAttemptFrame = currentFrame > int.MaxValue - RetryDelayFrames
                ? int.MaxValue
                : currentFrame + RetryDelayFrames;
            return true;
        }

        public bool TryRecordLoadStarted(
            BartenderTerminalCommandReceipt expectedReceipt)
        {
            if (!OwnsPending(expectedReceipt)) return false;
            state = BartenderTerminalNavigationState.Loading;
            earliestAttemptFrame = -1;
            return true;
        }

        public bool Owns(BartenderTerminalCommandReceipt expectedReceipt) =>
            Active && expectedReceipt.IsValid
            && acceptedReceipts.Contains(expectedReceipt);

        public void Reset()
        {
            state = BartenderTerminalNavigationState.Idle;
            receipt = default;
            acceptedReceipts.Clear();
            earliestAttemptFrame = -1;
        }

        private bool OwnsPending(
            BartenderTerminalCommandReceipt expectedReceipt) =>
            state == BartenderTerminalNavigationState.Pending
            && expectedReceipt.IsValid
            && receipt == expectedReceipt;
    }
}
