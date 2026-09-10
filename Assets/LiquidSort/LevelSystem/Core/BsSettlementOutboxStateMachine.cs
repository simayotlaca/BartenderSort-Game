using System;

namespace BartenderSort.Core
{
    internal enum BsSettlementOutboxState
    {
        Empty = 0,
        // Keep these numeric values stable for v4 saves. Values 1-2 belonged to the old arm flow.
        Pending = 3,
        Committing = 4,
        CommitRetryScheduled = 5,
        Committed = 6,
        Acknowledged = 7,
    }

    internal enum BsSettlementRestoreEvidence
    {
        None = 0,
        MatchingActiveAttempt = 1,
        MatchingCommittedRecord = 2,
    }

    /// <summary>
    /// Wraps the coordinator's exact request with campaign accounting and optional time-offer identity. No
    /// persistence details live here.
    /// </summary>
    internal readonly struct BsSettlementDraft : IEquatable<BsSettlementDraft>
    {
        public BsSettlementRequest Request { get; }
        public int CampaignSlot { get; }
        public int NextUnlockedOnWin { get; }
        public BsTimeOfferId TimeOfferId { get; }

        public BsAttemptId AttemptId => Request.AttemptId;
        public BsOperationId OperationId => Request.OperationId;
        public long Revision => Request.Revision;
        public int BoardRevision => Request.BoardRevision;
        public BsRoundCompletion Completion => Request.Completion;
        public BsRoundTransitionCause Cause => Request.Cause;
        public bool IsTimeOfferDecline =>
            Cause == BsRoundTransitionCause.TimeOfferDeclinedPresentFailure
            || Cause == BsRoundTransitionCause.TimeOfferDeclinedReturnToMenu;
        public BsTimeOfferDeclineDisposition TimeOfferDisposition =>
            Cause == BsRoundTransitionCause.TimeOfferDeclinedReturnToMenu
                ? BsTimeOfferDeclineDisposition.ReturnToMainMenu
                : BsTimeOfferDeclineDisposition.PresentFailure;

        public bool IsValid
        {
            get
            {
                if (!Request.IsValid || CampaignSlot < 0) return false;
                if (!CompletionCauseIsValid(Completion, Cause)) return false;
                if (Completion == BsRoundCompletion.Won)
                {
                    if (CampaignSlot == int.MaxValue
                        || NextUnlockedOnWin != CampaignSlot + 1)
                        return false;
                }
                else if (NextUnlockedOnWin != -1)
                {
                    return false;
                }

                return IsTimeOfferDecline
                    ? Completion == BsRoundCompletion.Failed && TimeOfferId.IsValid
                    : !TimeOfferId.IsValid;
            }
        }

        private static bool CompletionCauseIsValid(
            BsRoundCompletion completion,
            BsRoundTransitionCause cause)
        {
            if (completion == BsRoundCompletion.Quit)
                return cause == BsRoundTransitionCause.PauseMenuQuit;
            if (completion == BsRoundCompletion.Won)
                return cause == BsRoundTransitionCause.PlayerPour
                       || cause == BsRoundTransitionCause.PlayerDelivery;
            return completion == BsRoundCompletion.Failed
                   && (cause == BsRoundTransitionCause.PlayerPour
                       || cause == BsRoundTransitionCause.PlayerDelivery
                       || cause == BsRoundTransitionCause.TimedOrderExpired
                       || cause == BsRoundTransitionCause.DeadEndDetected
                       || cause == BsRoundTransitionCause.TimeOfferDeclinedPresentFailure
                       || cause == BsRoundTransitionCause.TimeOfferDeclinedReturnToMenu);
        }

        internal BsSettlementDraft(
            BsSettlementRequest request,
            int campaignSlot,
            int nextUnlockedOnWin,
            BsTimeOfferId timeOfferId = default)
        {
            Request = request;
            CampaignSlot = campaignSlot;
            NextUnlockedOnWin = nextUnlockedOnWin;
            TimeOfferId = timeOfferId;
        }

        public bool Equals(BsSettlementDraft other) =>
            Request == other.Request
            && CampaignSlot == other.CampaignSlot
            && NextUnlockedOnWin == other.NextUnlockedOnWin
            && TimeOfferId == other.TimeOfferId;

        public override bool Equals(object obj) =>
            obj is BsSettlementDraft other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Request.GetHashCode();
                hash = (hash * 397) ^ CampaignSlot;
                hash = (hash * 397) ^ NextUnlockedOnWin;
                return (hash * 397) ^ TimeOfferId.GetHashCode();
            }
        }

        public static bool operator ==(
            BsSettlementDraft left, BsSettlementDraft right) => left.Equals(right);

        public static bool operator !=(
            BsSettlementDraft left, BsSettlementDraft right) => !left.Equals(right);
    }

    internal readonly struct BsSettlementOutboxSnapshot
    {
        public BsSettlementOutboxState State { get; }
        public BsSettlementDraft Draft { get; }
        public BsSettlementReceipt Receipt { get; }
        public long LastObservedOperationId { get; }
        public string ActiveRoundJson { get; }

        public bool IsValid
        {
            get
            {
                if (LastObservedOperationId < 0L) return false;
                if (State == BsSettlementOutboxState.Empty)
                    return !Draft.IsValid && !Receipt.IsValid
                           && string.IsNullOrEmpty(ActiveRoundJson);
                if (!Draft.IsValid
                    || LastObservedOperationId < Draft.OperationId.Value)
                    return false;

                return State >= BsSettlementOutboxState.Pending
                       && State <= BsSettlementOutboxState.Acknowledged
                       && Receipt.IsValid
                       && Receipt.IsDurable
                       && Receipt.Request == Draft.Request
                       && !string.IsNullOrWhiteSpace(ActiveRoundJson);
            }
        }

        internal BsSettlementOutboxSnapshot(
            BsSettlementOutboxState state,
            BsSettlementDraft draft,
            BsSettlementReceipt receipt,
            long lastObservedOperationId,
            string activeRoundJson = null)
        {
            State = state;
            Draft = draft;
            Receipt = receipt;
            LastObservedOperationId = lastObservedOperationId;
            ActiveRoundJson = activeRoundJson ?? string.Empty;
        }
    }

    /// <summary>
    /// Owns settlement state without Unity or storage. The host saves between accepted transitions and returns
    /// the exact receipt.
    /// </summary>
    internal sealed class BsSettlementOutboxStateMachine
    {
        private BsSettlementOutboxState state;
        private BsSettlementDraft draft;
        private BsSettlementReceipt receipt;
        private long lastObservedOperationId;
        private string activeRoundJson = string.Empty;

        public BsSettlementOutboxState State => state;
        public BsSettlementDraft Draft => draft;
        public BsSettlementReceipt Receipt => receipt;
        public long LastObservedOperationId => lastObservedOperationId;
        public string ActiveRoundJson => activeRoundJson;
        public bool IsIdle => state == BsSettlementOutboxState.Empty
                              || state == BsSettlementOutboxState.Acknowledged;
        public bool IsPending => state == BsSettlementOutboxState.Pending
                                 || state == BsSettlementOutboxState.CommitRetryScheduled;
        public bool IsCommitted => state == BsSettlementOutboxState.Committed
                                   || state == BsSettlementOutboxState.Acknowledged;
        public bool BlocksGameplay => !IsIdle;

        public BsSettlementOutboxSnapshot Capture() =>
            new BsSettlementOutboxSnapshot(
                state, draft, receipt, lastObservedOperationId, activeRoundJson);

        public bool TryStage(
            BsSettlementDraft requested,
            BsSettlementReceipt expected,
            string persistedActiveRoundJson)
        {
            if (!requested.IsValid
                || !expected.IsValid
                || !expected.IsDurable
                || expected.Request != requested.Request
                || string.IsNullOrWhiteSpace(persistedActiveRoundJson))
                return false;
            if (!IsIdle)
                return draft == requested
                       && receipt == expected
                       && string.Equals(activeRoundJson,
                           persistedActiveRoundJson, StringComparison.Ordinal);
            if (requested.OperationId.Value <= lastObservedOperationId) return false;

            lastObservedOperationId = requested.OperationId.Value;
            draft = requested;
            receipt = expected;
            activeRoundJson = persistedActiveRoundJson;
            state = BsSettlementOutboxState.Pending;
            return true;
        }

        public bool TryBeginCommit(BsSettlementReceipt expected)
        {
            if (!MatchesReceipt(expected)
                || (state != BsSettlementOutboxState.Pending
                    && state != BsSettlementOutboxState.CommitRetryScheduled))
                return false;
            state = BsSettlementOutboxState.Committing;
            return true;
        }

        public bool TryDeferCommit(BsSettlementReceipt expected)
        {
            if (!MatchesReceipt(expected)) return false;
            if (state == BsSettlementOutboxState.CommitRetryScheduled) return true;
            if (state != BsSettlementOutboxState.Committing) return false;
            state = BsSettlementOutboxState.CommitRetryScheduled;
            return true;
        }

        public bool TryConfirmCommitted(BsSettlementReceipt expected)
        {
            if (!MatchesReceipt(expected)) return false;
            if (state == BsSettlementOutboxState.Committed
                || state == BsSettlementOutboxState.Acknowledged)
                return true;
            if (state != BsSettlementOutboxState.Committing) return false;
            state = BsSettlementOutboxState.Committed;
            return true;
        }

        public bool TryAcknowledge(BsSettlementReceipt expected)
        {
            if (!MatchesReceipt(expected)) return false;
            if (state == BsSettlementOutboxState.Acknowledged) return true;
            if (state != BsSettlementOutboxState.Committed) return false;
            state = BsSettlementOutboxState.Acknowledged;
            return true;
        }

        public bool TryRestore(
            BsSettlementOutboxSnapshot snapshot,
            BsSettlementRestoreEvidence evidence)
        {
            if (!IsIdle || !snapshot.IsValid) return false;

            if (snapshot.State == BsSettlementOutboxState.Empty)
            {
                if (evidence != BsSettlementRestoreEvidence.None) return false;
                state = BsSettlementOutboxState.Empty;
                draft = default;
                receipt = default;
                activeRoundJson = string.Empty;
                lastObservedOperationId = Math.Max(
                    lastObservedOperationId, snapshot.LastObservedOperationId);
                return true;
            }

            BsSettlementOutboxState restoredState;
            switch (evidence)
            {
                case BsSettlementRestoreEvidence.MatchingActiveAttempt:
                    if (snapshot.State == BsSettlementOutboxState.Committed
                        || snapshot.State == BsSettlementOutboxState.Acknowledged
                        || !snapshot.Receipt.IsValid)
                        return false;
                    // Pending and RetryScheduled are already saved checkpoints. Only Committing is uncertain
                    // after restart, so retry that state with the same receipt.
                    restoredState = snapshot.State == BsSettlementOutboxState.Committing
                        ? BsSettlementOutboxState.CommitRetryScheduled
                        : snapshot.State;
                    break;

                case BsSettlementRestoreEvidence.MatchingCommittedRecord:
                    if (!snapshot.Receipt.IsValid) return false;
                    restoredState = BsSettlementOutboxState.Committed;
                    break;

                default:
                    return false;
            }

            draft = snapshot.Draft;
            receipt = snapshot.Receipt;
            activeRoundJson = snapshot.ActiveRoundJson;
            lastObservedOperationId = Math.Max(
                lastObservedOperationId, snapshot.LastObservedOperationId);
            state = restoredState;
            return true;
        }

        private bool MatchesReceipt(BsSettlementReceipt expected) =>
            expected.IsValid && receipt.IsValid && expected == receipt;
    }
}
