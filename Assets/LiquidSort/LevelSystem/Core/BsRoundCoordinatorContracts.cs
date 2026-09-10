using System;

namespace BartenderSort.Core
{
    /// <summary>
    /// Owns the round lifecycle. Campaign completion and scene navigation stay outside this domain state.
    /// </summary>
    public enum BsRoundState
    {
        Empty = 0,
        Preparing = 1,
        Playing = 2,
        Paused = 3,
        Completed = 4,
    }

    public enum BsRoundCompletion
    {
        None = 0,
        Won = 1,
        Failed = 2,
        Quit = 3,
    }

    public enum BsRoundAttemptKind
    {
        Durable = 0,
        Standalone = 1,
    }

    /// <summary>
    /// The accepted change's explicit cause, so UI never has to guess from old state or temporary flags.
    /// </summary>
    public enum BsRoundTransitionCause
    {
        None = 0,
        CampaignRoundPrepared = 1,
        SavedRoundRestored = 2,
        StandaloneRoundPrepared = 3,
        PreparationActivated = 4,
        PreparationCancelled = 5,
        PlayerPause = 6,
        ApplicationPause = 7,
        PlayerResume = 8,
        ApplicationResume = 9,
        PlayerPour = 10,
        PlayerDelivery = 11,
        PlayerUndo = 12,
        ExtraGlass = 13,
        Shuffle = 14,
        Restart = 15,
        TimedOrderExpired = 16,
        DeadEndDetected = 17,
        TimeOfferDeclinedPresentFailure = 18,
        TimeOfferDeclinedReturnToMenu = 19,
        PauseMenuQuit = 20,
        ReturnToMenu = 21,
        StandaloneAbort = 22,
        LoadFailed = 23,
    }

    public enum BsBoardMutationKind
    {
        Prepared = 0,
        Pour = 1,
        Delivery = 2,
        ExtraGlass = 3,
        Shuffle = 4,
        RestoreCheckpoint = 5,
        Replace = 6,
    }

    /// <summary>Unique identity for a saved or standalone round.</summary>
    public readonly struct BsAttemptId : IEquatable<BsAttemptId>
    {
        public string Value { get; }
        public bool IsValid => !string.IsNullOrWhiteSpace(Value);

        public BsAttemptId(string value)
        {
            Value = value ?? string.Empty;
        }

        public bool Equals(BsAttemptId other) =>
            string.Equals(Value, other.Value, StringComparison.Ordinal);

        public override bool Equals(object obj) =>
            obj is BsAttemptId other && Equals(other);

        public override int GetHashCode() =>
            StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);

        public static bool operator ==(BsAttemptId left, BsAttemptId right) =>
            left.Equals(right);

        public static bool operator !=(BsAttemptId left, BsAttemptId right) =>
            !left.Equals(right);

        public override string ToString() => IsValid ? Value : "No Attempt";
    }

    public readonly struct BsOperationId : IEquatable<BsOperationId>
    {
        public long Value { get; }
        public bool IsValid => Value > 0L;

        public BsOperationId(long value)
        {
            Value = value;
        }

        public bool Equals(BsOperationId other) => Value == other.Value;
        public override bool Equals(object obj) =>
            obj is BsOperationId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public static bool operator ==(BsOperationId left, BsOperationId right) =>
            left.Equals(right);
        public static bool operator !=(BsOperationId left, BsOperationId right) =>
            !left.Equals(right);
        public override string ToString() => IsValid ? $"Operation {Value}" : "No Operation";
    }

    /// <summary>
    /// A command-start stamp checking attempt, gameplay token, domain revision and board revision together.
    /// </summary>
    public readonly struct BsRoundCommandStamp : IEquatable<BsRoundCommandStamp>
    {
        public BsAttemptId AttemptId { get; }
        public BsRoundToken Token { get; }
        public long Revision { get; }
        public int BoardRevision { get; }
        public bool IsValid => AttemptId.IsValid && Revision >= 0L && BoardRevision >= 0;

        public BsRoundCommandStamp(
            BsAttemptId attemptId,
            BsRoundToken token,
            long revision,
            int boardRevision)
        {
            AttemptId = attemptId;
            Token = token;
            Revision = revision;
            BoardRevision = boardRevision;
        }

        public bool Equals(BsRoundCommandStamp other) =>
            AttemptId == other.AttemptId
            && Token == other.Token
            && Revision == other.Revision
            && BoardRevision == other.BoardRevision;

        public override bool Equals(object obj) =>
            obj is BsRoundCommandStamp other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = AttemptId.GetHashCode();
                hash = (hash * 397) ^ Token.GetHashCode();
                hash = (hash * 397) ^ Revision.GetHashCode();
                return (hash * 397) ^ BoardRevision;
            }
        }

        public static bool operator ==(
            BsRoundCommandStamp left,
            BsRoundCommandStamp right) => left.Equals(right);

        public static bool operator !=(
            BsRoundCommandStamp left,
            BsRoundCommandStamp right) => !left.Equals(right);
    }

    /// <summary>
    /// Exact persistence request before a board or terminal commit. Its successful receipt must match all five
    /// identity fields.
    /// </summary>
    public readonly struct BsSettlementRequest : IEquatable<BsSettlementRequest>
    {
        public BsAttemptId AttemptId { get; }
        public BsOperationId OperationId { get; }
        public long Revision { get; }
        public int BoardRevision { get; }
        public BsRoundCompletion Completion { get; }
        public BsRoundTransitionCause Cause { get; }
        public bool IsValid => AttemptId.IsValid && OperationId.IsValid
            && Revision > 0L && BoardRevision >= 0
            && Completion != BsRoundCompletion.None
            && Cause != BsRoundTransitionCause.None;

        internal BsSettlementRequest(
            BsAttemptId attemptId,
            BsOperationId operationId,
            long revision,
            int boardRevision,
            BsRoundCompletion completion,
            BsRoundTransitionCause cause)
        {
            AttemptId = attemptId;
            OperationId = operationId;
            Revision = revision;
            BoardRevision = boardRevision;
            Completion = completion;
            Cause = cause;
        }

        public bool Equals(BsSettlementRequest other) =>
            AttemptId == other.AttemptId
            && OperationId == other.OperationId
            && Revision == other.Revision
            && BoardRevision == other.BoardRevision
            && Completion == other.Completion
            && Cause == other.Cause;

        public override bool Equals(object obj) =>
            obj is BsSettlementRequest other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = AttemptId.GetHashCode();
                hash = (hash * 397) ^ OperationId.GetHashCode();
                hash = (hash * 397) ^ Revision.GetHashCode();
                hash = (hash * 397) ^ BoardRevision;
                hash = (hash * 397) ^ (int)Completion;
                return (hash * 397) ^ (int)Cause;
            }
        }

        public static bool operator ==(
            BsSettlementRequest left,
            BsSettlementRequest right) => left.Equals(right);

        public static bool operator !=(
            BsSettlementRequest left,
            BsSettlementRequest right) => !left.Equals(right);
    }

    /// <summary>The repository's repeat-safe receipt for an exact settlement request.</summary>
    public readonly struct BsSettlementReceipt : IEquatable<BsSettlementReceipt>
    {
        public BsSettlementRequest Request { get; }
        public string PersistenceReceiptId { get; }
        public bool IsDurable { get; }
        public bool IsValid => Request.IsValid
            && (!IsDurable || !string.IsNullOrWhiteSpace(PersistenceReceiptId));

        private BsSettlementReceipt(
            BsSettlementRequest request,
            string persistenceReceiptId,
            bool isDurable)
        {
            Request = request;
            PersistenceReceiptId = persistenceReceiptId ?? string.Empty;
            IsDurable = isDurable;
        }

        public static BsSettlementReceipt Durable(
            BsSettlementRequest request,
            string persistenceReceiptId) =>
            new BsSettlementReceipt(request, persistenceReceiptId, true);

        internal static BsSettlementReceipt Standalone(BsSettlementRequest request) =>
            new BsSettlementReceipt(request, string.Empty, false);

        public bool Equals(BsSettlementReceipt other) =>
            Request == other.Request
            && IsDurable == other.IsDurable
            && string.Equals(PersistenceReceiptId, other.PersistenceReceiptId,
                StringComparison.Ordinal);

        public override bool Equals(object obj) =>
            obj is BsSettlementReceipt other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Request.GetHashCode();
                hash = (hash * 397) ^ (IsDurable ? 1 : 0);
                return (hash * 397) ^ StringComparer.Ordinal.GetHashCode(
                    PersistenceReceiptId ?? string.Empty);
            }
        }

        public static bool operator ==(
            BsSettlementReceipt left,
            BsSettlementReceipt right) => left.Equals(right);

        public static bool operator !=(
            BsSettlementReceipt left,
            BsSettlementReceipt right) => !left.Equals(right);
    }

    public sealed class BsRoundTransition
    {
        public BsAttemptId AttemptId { get; }
        public BsOperationId OperationId { get; }
        public long Revision { get; }
        public int BoardRevision { get; }
        public BsRoundState From { get; }
        public BsRoundState To { get; }
        public BsRoundTransitionCause Cause { get; }
        public BsRoundCompletion Completion { get; }
        public BsRoundToken Token { get; }
        public BsSettlementReceipt? SettlementReceipt { get; }

        internal BsRoundTransition(
            BsAttemptId attemptId,
            BsOperationId operationId,
            long revision,
            int boardRevision,
            BsRoundState from,
            BsRoundState to,
            BsRoundTransitionCause cause,
            BsRoundCompletion completion,
            BsRoundToken token,
            BsSettlementReceipt? settlementReceipt)
        {
            AttemptId = attemptId;
            OperationId = operationId;
            Revision = revision;
            BoardRevision = boardRevision;
            From = from;
            To = to;
            Cause = cause;
            Completion = completion;
            Token = token;
            SettlementReceipt = settlementReceipt;
        }

        public BsRoundCommandStamp Stamp => new BsRoundCommandStamp(
            AttemptId, Token, Revision, BoardRevision);
    }

    public sealed class BsPourCommitEvidence
    {
        private readonly RtGlass sourceBefore;
        private readonly RtGlass sourceAfter;
        private readonly RtGlass targetBefore;
        private readonly RtGlass targetAfter;

        public int SourceGlassId => sourceBefore.Id;
        public int TargetGlassId => targetBefore.Id;
        public int Amount { get; }

        public RtGlass SourceBefore => sourceBefore.Clone();
        public RtGlass SourceAfter => sourceAfter.Clone();
        public RtGlass TargetBefore => targetBefore.Clone();
        public RtGlass TargetAfter => targetAfter.Clone();

        internal BsPourCommitEvidence(
            RtGlass sourceBefore,
            RtGlass sourceAfter,
            RtGlass targetBefore,
            RtGlass targetAfter,
            int amount)
        {
            this.sourceBefore = sourceBefore.Clone();
            this.sourceAfter = sourceAfter.Clone();
            this.targetBefore = targetBefore.Clone();
            this.targetAfter = targetAfter.Clone();
            Amount = amount;
        }
    }

    public sealed class BsDeliveryCommitEvidence
    {
        private readonly RtGlass deliveredGlass;
        private readonly OrderDef deliveredOrder;

        public int GlassId => deliveredGlass.Id;
        public int SlotIndex { get; }
        public RtGlass DeliveredGlass => deliveredGlass.Clone();
        public OrderDef DeliveredOrder => deliveredOrder?.Clone();

        internal BsDeliveryCommitEvidence(
            RtGlass deliveredGlass,
            OrderDef deliveredOrder,
            int slotIndex)
        {
            this.deliveredGlass = deliveredGlass.Clone();
            this.deliveredOrder = deliveredOrder?.Clone();
            SlotIndex = slotIndex;
        }
    }

    public sealed class BsGlassCommitEvidence
    {
        private readonly RtGlass before;
        private readonly RtGlass after;

        public int GlassId { get; }
        public RtGlass Before => before?.Clone();
        public RtGlass After => after?.Clone();

        internal BsGlassCommitEvidence(int glassId, RtGlass before, RtGlass after)
        {
            GlassId = glassId;
            this.before = before?.Clone();
            this.after = after?.Clone();
        }
    }

    public sealed class BsBoardCommit
    {
        private readonly BsBoard board;

        public BsAttemptId AttemptId { get; }
        public BsOperationId OperationId { get; }
        public long Revision { get; }
        public int BoardRevision { get; }
        public BsRoundToken Token { get; }
        public BsRoundTransitionCause Cause { get; }
        public BsBoardMutationKind Kind { get; }
        public BsRoundCompletion Completion { get; }
        public BsSettlementReceipt? SettlementReceipt { get; }
        public BsPourCommitEvidence Pour { get; }
        public BsDeliveryCommitEvidence Delivery { get; }
        public BsGlassCommitEvidence Glass { get; }

        internal BsBoardCommit(
            BsAttemptId attemptId,
            BsOperationId operationId,
            long revision,
            int boardRevision,
            BsRoundToken token,
            BsRoundTransitionCause cause,
            BsBoardMutationKind kind,
            BsRoundCompletion completion,
            BsSettlementReceipt? settlementReceipt,
            BsBoard board,
            BsPourCommitEvidence pour,
            BsDeliveryCommitEvidence delivery,
            BsGlassCommitEvidence glass)
        {
            AttemptId = attemptId;
            OperationId = operationId;
            Revision = revision;
            BoardRevision = boardRevision;
            Token = token;
            Cause = cause;
            Kind = kind;
            Completion = completion;
            SettlementReceipt = settlementReceipt;
            this.board = board.Clone();
            Pour = pour;
            Delivery = delivery;
            Glass = glass;
        }

        public BsRoundCommandStamp Stamp => new BsRoundCommandStamp(
            AttemptId, Token, Revision, BoardRevision);

        public BsBoard CaptureBoard() => board.Clone();
    }

    /// <summary>
    /// Facts from one atomic domain change share operation, revision and cause. The adapter publishes them only
    /// after the save succeeds.
    /// </summary>
    public sealed class BsRoundCommit
    {
        public BsOperationId OperationId { get; }
        public long Revision { get; }
        public BsRoundTransitionCause Cause { get; }
        public BsBoardCommit BoardCommit { get; }
        public BsRoundTransition Transition { get; }

        internal BsRoundCommit(
            BsOperationId operationId,
            long revision,
            BsRoundTransitionCause cause,
            BsBoardCommit boardCommit,
            BsRoundTransition transition)
        {
            OperationId = operationId;
            Revision = revision;
            Cause = cause;
            BoardCommit = boardCommit;
            Transition = transition;
        }
    }

    /// <summary>Detached restore/checkpoint image; every board read returns a clone.</summary>
    public sealed class BsRoundSnapshot
    {
        private readonly BsBoard board;

        public BsRoundState State { get; }
        public BsRoundCompletion Completion { get; }
        public BsRoundAttemptKind AttemptKind { get; }
        public BsAttemptId AttemptId { get; }
        public BsRoundToken Token { get; }
        public long Revision { get; }
        public BsOperationId LastOperationId { get; }
        public int BoardRevision { get; }
        public bool HasBoard => board != null;
        public BsRoundState CompletionFrom { get; }
        public BsOperationId CompletionOperationId { get; }
        public BsRoundTransitionCause CompletionCause { get; }
        public BsSettlementReceipt? SettlementReceipt { get; }
        public BsSettlementRequest? SettlementRequest =>
            State != BsRoundState.Completed
                ? (BsSettlementRequest?)null
                : new BsSettlementRequest(
                    AttemptId,
                    CompletionOperationId,
                    Revision,
                    BoardRevision,
                    Completion,
                    CompletionCause);

        internal BsRoundSnapshot(
            BsRoundState state,
            BsRoundCompletion completion,
            BsRoundAttemptKind attemptKind,
            BsAttemptId attemptId,
            BsRoundToken token,
            long revision,
            BsOperationId lastOperationId,
            int boardRevision,
            BsBoard board,
            BsRoundTransition completionTransition)
        {
            State = state;
            Completion = completion;
            AttemptKind = attemptKind;
            AttemptId = attemptId;
            Token = token;
            Revision = revision;
            LastOperationId = lastOperationId;
            BoardRevision = boardRevision;
            this.board = board?.Clone();
            CompletionFrom = completionTransition?.From ?? BsRoundState.Empty;
            CompletionOperationId = completionTransition?.OperationId ?? default;
            CompletionCause = completionTransition?.Cause
                              ?? BsRoundTransitionCause.None;
            SettlementReceipt = completionTransition?.SettlementReceipt;
        }

        public BsBoard CaptureBoard() => board?.Clone();
    }
}
