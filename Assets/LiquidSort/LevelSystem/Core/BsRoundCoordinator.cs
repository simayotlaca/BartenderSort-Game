using System;
using System.Collections.Generic;

namespace BartenderSort.Core
{
    /// <summary>A checkpoint belongs to the coordinator that made it. It never exposes the live board.</summary>
    public sealed class BsBoardCheckpoint
    {
        private readonly BsBoard board;

        internal Guid OwnerId { get; }
        public BsAttemptId AttemptId { get; }
        public BsRoundToken Token { get; }
        public long Revision { get; }
        public int BoardRevision { get; }

        internal BsBoardCheckpoint(
            Guid ownerId,
            BsAttemptId attemptId,
            BsRoundToken token,
            long revision,
            int boardRevision,
            BsBoard board)
        {
            OwnerId = ownerId;
            AttemptId = attemptId;
            Token = token;
            Revision = revision;
            BoardRevision = boardRevision;
            this.board = board.Clone();
        }

        internal BsBoard CaptureOwnedBoard() => board.Clone();
    }

    /// <summary>
    /// A board candidate not yet applied. Its reserved operation ID lets saving and settlement refer to it
    /// before commit.
    /// </summary>
    public sealed class BsStagedBoardMutation
    {
        private readonly BsBoard candidateBoard;

        internal Guid OwnerId { get; }
        internal long StageId { get; }
        internal BsRoundCommandStamp BaseStamp { get; }
        internal BsPourCommitEvidence PourEvidence { get; }
        internal BsDeliveryCommitEvidence DeliveryEvidence { get; }
        internal BsGlassCommitEvidence GlassEvidence { get; }

        public BsAttemptId AttemptId => BaseStamp.AttemptId;
        public BsOperationId OperationId { get; }
        public long Revision { get; }
        public int BoardRevision { get; }
        public BsRoundToken Token => BaseStamp.Token;
        public BsRoundTransitionCause Cause { get; }
        public BsBoardMutationKind Kind { get; }
        public BsRoundCompletion DetectedCompletion { get; }
        public bool ResumesRound { get; }
        public bool RequiresSettlement => DetectedCompletion != BsRoundCompletion.None;
        public BsSettlementRequest? SettlementRequest { get; }

        internal BsStagedBoardMutation(
            Guid ownerId,
            long stageId,
            BsRoundCommandStamp baseStamp,
            BsOperationId operationId,
            long revision,
            int boardRevision,
            BsRoundTransitionCause cause,
            BsBoardMutationKind kind,
            BsRoundCompletion detectedCompletion,
            bool resumesRound,
            BsBoard candidateBoard,
            BsPourCommitEvidence pourEvidence,
            BsDeliveryCommitEvidence deliveryEvidence,
            BsGlassCommitEvidence glassEvidence)
        {
            OwnerId = ownerId;
            StageId = stageId;
            BaseStamp = baseStamp;
            OperationId = operationId;
            Revision = revision;
            BoardRevision = boardRevision;
            Cause = cause;
            Kind = kind;
            DetectedCompletion = detectedCompletion;
            ResumesRound = resumesRound;
            this.candidateBoard = candidateBoard;
            PourEvidence = pourEvidence;
            DeliveryEvidence = deliveryEvidence;
            GlassEvidence = glassEvidence;
            SettlementRequest = detectedCompletion == BsRoundCompletion.None
                ? (BsSettlementRequest?)null
                : new BsSettlementRequest(
                    baseStamp.AttemptId,
                    operationId,
                    revision,
                    boardRevision,
                    detectedCompletion,
                    cause);
        }

        public BsBoard CaptureCandidateBoard() => candidateBoard.Clone();
        internal BsBoard TakeCandidateBoard() => candidateBoard;
    }

    /// <summary>
    /// Drafts timer, dead-end or quit results without changing the board. Commit only after saving settles
    /// this exact request.
    /// </summary>
    public sealed class BsStagedRoundCompletion
    {
        internal Guid OwnerId { get; }
        internal long StageId { get; }
        internal BsRoundCommandStamp BaseStamp { get; }

        public BsAttemptId AttemptId => BaseStamp.AttemptId;
        public BsOperationId OperationId { get; }
        public long Revision { get; }
        public int BoardRevision => BaseStamp.BoardRevision;
        public BsRoundToken Token => BaseStamp.Token;
        public BsRoundCompletion Completion { get; }
        public BsRoundTransitionCause Cause { get; }
        public BsSettlementRequest SettlementRequest { get; }

        internal BsStagedRoundCompletion(
            Guid ownerId,
            long stageId,
            BsRoundCommandStamp baseStamp,
            BsOperationId operationId,
            long revision,
            BsRoundCompletion completion,
            BsRoundTransitionCause cause)
        {
            OwnerId = ownerId;
            StageId = stageId;
            BaseStamp = baseStamp;
            OperationId = operationId;
            Revision = revision;
            Completion = completion;
            Cause = cause;
            SettlementRequest = new BsSettlementRequest(
                baseStamp.AttemptId,
                operationId,
                revision,
                baseStamp.BoardRevision,
                completion,
                cause);
        }
    }

    /// <summary>
    /// Owns round rules without Unity or view dependencies. It commits a staged board only after save
    /// approval, then returns one immutable result for the adapter to publish.
    /// </summary>
    public sealed class BsRoundCoordinator
    {
        private readonly Guid ownerId = Guid.NewGuid();

        private BsRoundState state = BsRoundState.Empty;
        private BsRoundCompletion completion = BsRoundCompletion.None;
        private BsRoundAttemptKind attemptKind;
        private BsAttemptId attemptId;
        private BsBoard board;
        private int roundId;
        private int gameplayEpoch;
        private int boardRevision;
        private long revision;
        private long lastOperationId;
        private long lastStageId;
        private BsStagedBoardMutation activeBoardMutation;
        private BsStagedRoundCompletion activeCompletion;
        private BsRoundTransition completionTransition;

        public BsRoundCoordinator(long operationFloor = 0L)
        {
            if (operationFloor < 0L)
                throw new ArgumentOutOfRangeException(
                    nameof(operationFloor), "The operation floor cannot be negative");
            lastOperationId = operationFloor;
        }

        public BsRoundState State => state;
        public BsRoundCompletion Completion => completion;
        public BsRoundAttemptKind AttemptKind => attemptKind;
        public BsAttemptId AttemptId => attemptId;
        public BsRoundToken CurrentToken => new BsRoundToken(roundId, gameplayEpoch);
        public int BoardRevision => boardRevision;
        public long Revision => revision;
        public BsOperationId LastOperationId => new BsOperationId(lastOperationId);
        public bool HasBoard => board != null;
        public bool HasStagedOperation => activeBoardMutation != null
                                          || activeCompletion != null;
        public bool AcceptsInput => state == BsRoundState.Playing
                                    && !HasStagedOperation;

        public BsRoundCommandStamp CurrentStamp => new BsRoundCommandStamp(
            attemptId, CurrentToken, revision, boardRevision);

        public BsBoard CaptureBoard() => board?.Clone();

        public BsRoundSnapshot CaptureSnapshot() => new BsRoundSnapshot(
            state,
            completion,
            attemptKind,
            attemptId,
            CurrentToken,
            revision,
            LastOperationId,
            boardRevision,
            board,
            completionTransition);

        /// <summary>The last exact commit lets Session restore the result without guessing.</summary>
        public bool TryGetCompletedTransition(out BsRoundTransition transition)
        {
            transition = state == BsRoundState.Completed
                ? completionTransition
                : null;
            return transition != null;
        }

        public BsBoardCheckpoint CaptureBoardCheckpoint()
        {
            if (board == null || HasStagedOperation
                || (state != BsRoundState.Playing && state != BsRoundState.Paused))
                return null;

            return new BsBoardCheckpoint(
                ownerId,
                attemptId,
                CurrentToken,
                revision,
                boardRevision,
                board);
        }

        /// <summary>
        /// Imports trusted saved undo data as this coordinator's checkpoint. Rejects old attempts or
        /// generations and copies the board.
        /// </summary>
        public bool TryImportBoardCheckpoint(
            BsRoundCommandStamp checkpointStamp,
            BsBoard persistedBoard,
            out BsBoardCheckpoint checkpoint,
            out string rejectionReason)
        {
            checkpoint = null;
            rejectionReason = null;
            if (HasStagedOperation
                || (state != BsRoundState.Playing && state != BsRoundState.Paused))
            {
                rejectionReason = "The round cannot import an undo checkpoint now";
                return false;
            }
            if (!checkpointStamp.IsValid
                || checkpointStamp.AttemptId != attemptId
                || checkpointStamp.Token != CurrentToken
                || checkpointStamp.Revision > revision
                || checkpointStamp.BoardRevision > boardRevision)
            {
                rejectionReason = "The persisted undo checkpoint is stale";
                return false;
            }
            if (!TryCloneValidBoard(
                    persistedBoard, out BsBoard ownedBoard, out rejectionReason))
                return false;
            if (DetectCompletion(ownedBoard) != BsRoundCompletion.None)
            {
                rejectionReason = "An undo checkpoint must contain a playable board";
                return false;
            }

            checkpoint = new BsBoardCheckpoint(
                ownerId,
                attemptId,
                CurrentToken,
                checkpointStamp.Revision,
                checkpointStamp.BoardRevision,
                ownedBoard);
            return true;
        }

        /// <summary>
        /// Builds the saved snapshot for a staged board command without changing live state. Terminal
        /// restore also needs its outbox receipt.
        /// </summary>
        public bool TryCaptureProspectiveSnapshot(
            BsStagedBoardMutation staged,
            out BsRoundSnapshot snapshot,
            out string rejectionReason)
        {
            snapshot = null;
            rejectionReason = null;
            if (!MatchesActiveStage(staged) || !IsCurrent(staged.BaseStamp)
                || staged.Revision != revision + 1L
                || staged.BoardRevision != boardRevision + 1)
            {
                rejectionReason = "The staged board command is stale or foreign";
                return false;
            }

            bool changesGeneration = staged.RequiresSettlement || staged.ResumesRound;
            if (changesGeneration && gameplayEpoch == int.MaxValue)
            {
                rejectionReason = "The gameplay token space was exhausted";
                return false;
            }
            BsRoundToken prospectiveToken = changesGeneration
                ? new BsRoundToken(roundId, gameplayEpoch + 1)
                : CurrentToken;
            BsRoundState prospectiveState = staged.RequiresSettlement
                ? BsRoundState.Completed
                : staged.ResumesRound
                    ? BsRoundState.Playing
                    : state;
            BsRoundCompletion prospectiveCompletion = staged.RequiresSettlement
                ? staged.DetectedCompletion
                : BsRoundCompletion.None;
            BsRoundTransition prospectiveTransition = null;
            if (staged.RequiresSettlement || staged.ResumesRound)
            {
                BsSettlementReceipt? receipt = staged.RequiresSettlement
                    && attemptKind == BsRoundAttemptKind.Standalone
                        ? BsSettlementReceipt.Standalone(
                            staged.SettlementRequest.Value)
                        : (BsSettlementReceipt?)null;
                prospectiveTransition = new BsRoundTransition(
                    attemptId,
                    staged.OperationId,
                    staged.Revision,
                    staged.BoardRevision,
                    state,
                    prospectiveState,
                    staged.Cause,
                    prospectiveCompletion,
                    prospectiveToken,
                    receipt);
            }

            snapshot = new BsRoundSnapshot(
                prospectiveState,
                prospectiveCompletion,
                attemptKind,
                attemptId,
                prospectiveToken,
                staged.Revision,
                staged.OperationId,
                staged.BoardRevision,
                staged.CaptureCandidateBoard(),
                prospectiveTransition);
            return true;
        }

        /// <summary>
        /// Builds a terminal snapshot with the same attempt, operation, revisions, token, cause and
        /// settlement request.
        /// </summary>
        public bool TryCaptureProspectiveSnapshot(
            BsStagedRoundCompletion staged,
            out BsRoundSnapshot snapshot,
            out string rejectionReason)
        {
            snapshot = null;
            rejectionReason = null;
            if (!MatchesActiveStage(staged) || !IsCurrent(staged.BaseStamp)
                || staged.Revision != revision + 1L)
            {
                rejectionReason = "The staged completion is stale or foreign";
                return false;
            }
            if (gameplayEpoch == int.MaxValue)
            {
                rejectionReason = "The gameplay token space was exhausted";
                return false;
            }

            var prospectiveToken = new BsRoundToken(roundId, gameplayEpoch + 1);
            BsSettlementReceipt? receipt =
                attemptKind == BsRoundAttemptKind.Standalone
                    ? BsSettlementReceipt.Standalone(staged.SettlementRequest)
                    : (BsSettlementReceipt?)null;
            var prospectiveTransition = new BsRoundTransition(
                attemptId,
                staged.OperationId,
                staged.Revision,
                boardRevision,
                state,
                BsRoundState.Completed,
                staged.Cause,
                staged.Completion,
                prospectiveToken,
                receipt);
            snapshot = new BsRoundSnapshot(
                BsRoundState.Completed,
                staged.Completion,
                attemptKind,
                attemptId,
                prospectiveToken,
                staged.Revision,
                staged.OperationId,
                boardRevision,
                board,
                prospectiveTransition);
            return true;
        }

        public bool IsCurrent(BsRoundCommandStamp stamp) =>
            stamp.IsValid
            && attemptId == stamp.AttemptId
            && CurrentToken == stamp.Token
            && revision == stamp.Revision
            && boardRevision == stamp.BoardRevision;

        public bool IsCurrent(
            BsAttemptId expectedAttemptId,
            BsRoundToken expectedToken,
            long expectedRevision) =>
            attemptId == expectedAttemptId
            && CurrentToken == expectedToken
            && revision == expectedRevision;

        /// <summary>Restores a new coordinator with a copied board and continuing operation/revision counters.</summary>
        public static bool TryRestore(
            BsRoundSnapshot snapshot,
            out BsRoundCoordinator coordinator,
            out string rejectionReason) =>
            TryRestore(
                snapshot, 0L, null, out coordinator, out rejectionReason);

        /// <summary>
        /// Restore needs the outbox receipt for this snapshot's exact operation. Missing or mismatched save
        /// evidence rejects it.
        /// </summary>
        public static bool TryRestore(
            BsRoundSnapshot snapshot,
            BsSettlementReceipt? recoveredSettlementReceipt,
            out BsRoundCoordinator coordinator,
            out string rejectionReason) =>
            TryRestore(
                snapshot,
                0L,
                recoveredSettlementReceipt,
                out coordinator,
                out rejectionReason);

        /// <summary>The next operation ID uses the greater of the saved counter and the global settlement floor.</summary>
        public static bool TryRestore(
            BsRoundSnapshot snapshot,
            long operationFloor,
            BsSettlementReceipt? recoveredSettlementReceipt,
            out BsRoundCoordinator coordinator,
            out string rejectionReason)
        {
            coordinator = null;
            rejectionReason = null;
            if (operationFloor < 0L)
            {
                rejectionReason = "The operation floor cannot be negative";
                return false;
            }
            if (snapshot == null)
            {
                rejectionReason = "The round snapshot is missing";
                return false;
            }

            BsBoard restoredBoard = snapshot.CaptureBoard();
            if (!SnapshotShapeIsValid(snapshot, restoredBoard, out rejectionReason))
                return false;
            if (!TryResolveSnapshotCompletion(
                    snapshot,
                    recoveredSettlementReceipt,
                    out BsRoundTransition restoredCompletion,
                    out rejectionReason))
                return false;

            coordinator = new BsRoundCoordinator(operationFloor)
            {
                state = snapshot.State,
                completion = snapshot.Completion,
                attemptKind = snapshot.AttemptKind,
                attemptId = snapshot.AttemptId,
                board = restoredBoard,
                roundId = snapshot.Token.RoundId,
                gameplayEpoch = snapshot.Token.GameplayEpoch,
                boardRevision = snapshot.BoardRevision,
                revision = snapshot.Revision,
                lastOperationId = Math.Max(
                    snapshot.LastOperationId.Value, operationFloor),
                completionTransition = restoredCompletion,
            };
            return true;
        }

        public bool TryPrepareRound(
            BsAttemptId newAttemptId,
            BsRoundAttemptKind newAttemptKind,
            BsBoard initialBoard,
            BsRoundTransitionCause cause,
            out BsRoundCommit commit,
            out string rejectionReason)
        {
            commit = null;
            rejectionReason = null;
            if (!CanRunImmediateCommand(out rejectionReason)) return false;
            if (state != BsRoundState.Empty)
            {
                rejectionReason = "A round can only be prepared from Empty";
                return false;
            }
            if (!newAttemptId.IsValid)
            {
                rejectionReason = "The attempt id is invalid";
                return false;
            }
            if (!IsKnownAttemptKind(newAttemptKind))
            {
                rejectionReason = "The attempt kind is invalid";
                return false;
            }
            if (cause != BsRoundTransitionCause.CampaignRoundPrepared
                && cause != BsRoundTransitionCause.SavedRoundRestored
                && cause != BsRoundTransitionCause.StandaloneRoundPrepared)
            {
                rejectionReason = "The preparation cause is invalid";
                return false;
            }
            if (!TryCloneValidBoard(initialBoard, out BsBoard ownedBoard,
                    out rejectionReason))
                return false;
            if (DetectCompletion(ownedBoard) != BsRoundCompletion.None)
            {
                rejectionReason = "A prepared board must start in a playable state";
                return false;
            }
            if (roundId == int.MaxValue || gameplayEpoch == int.MaxValue)
            {
                rejectionReason = "The round token space was exhausted";
                return false;
            }
            if (!TryReserveImmediateIdentity(
                    out BsOperationId operationId,
                    out long nextRevision,
                    out rejectionReason))
                return false;

            BsRoundState from = state;
            roundId++;
            gameplayEpoch++;
            attemptId = newAttemptId;
            attemptKind = newAttemptKind;
            board = ownedBoard;
            boardRevision = 0;
            completion = BsRoundCompletion.None;
            completionTransition = null;
            state = BsRoundState.Preparing;
            revision = nextRevision;

            BsRoundToken token = CurrentToken;
            var boardCommit = new BsBoardCommit(
                attemptId, operationId, revision, boardRevision, token, cause,
                BsBoardMutationKind.Prepared, BsRoundCompletion.None, null,
                board, null, null, null);
            var transition = new BsRoundTransition(
                attemptId, operationId, revision, boardRevision,
                from, state, cause, BsRoundCompletion.None, token, null);
            commit = new BsRoundCommit(
                operationId, revision, cause, boardCommit, transition);
            return true;
        }

        public bool TryActivatePreparedRound(
            BsRoundCommandStamp expected,
            out BsRoundCommit commit,
            out string rejectionReason) =>
            TryTransition(
                expected,
                BsRoundState.Preparing,
                BsRoundState.Playing,
                BsRoundTransitionCause.PreparationActivated,
                BsRoundCompletion.None,
                false,
                out commit,
                out rejectionReason);

        public bool TryPause(
            BsRoundCommandStamp expected,
            BsRoundTransitionCause cause,
            out BsRoundCommit commit,
            out string rejectionReason)
        {
            if (cause != BsRoundTransitionCause.PlayerPause
                && cause != BsRoundTransitionCause.ApplicationPause)
            {
                commit = null;
                rejectionReason = "The pause cause is invalid";
                return false;
            }
            return TryTransition(
                expected, BsRoundState.Playing, BsRoundState.Paused,
                cause, BsRoundCompletion.None, false,
                out commit, out rejectionReason);
        }

        public bool TryResume(
            BsRoundCommandStamp expected,
            BsRoundTransitionCause cause,
            out BsRoundCommit commit,
            out string rejectionReason)
        {
            if (cause != BsRoundTransitionCause.PlayerResume
                && cause != BsRoundTransitionCause.ApplicationResume
                && cause != BsRoundTransitionCause.Restart)
            {
                commit = null;
                rejectionReason = "The resume cause is invalid";
                return false;
            }
            return TryTransition(
                expected, BsRoundState.Paused, BsRoundState.Playing,
                cause, BsRoundCompletion.None, false,
                out commit, out rejectionReason);
        }

        public bool TryClear(
            BsRoundCommandStamp expected,
            BsRoundTransitionCause cause,
            out BsRoundCommit commit,
            out string rejectionReason)
        {
            commit = null;
            rejectionReason = null;
            if (!CanRunImmediateCommand(out rejectionReason)) return false;
            if (!IsCurrent(expected))
            {
                rejectionReason = "The round command is stale";
                return false;
            }
            if (state != BsRoundState.Completed && state != BsRoundState.Preparing)
            {
                rejectionReason = "Only a prepared or completed round can be cleared";
                return false;
            }
            if (cause != BsRoundTransitionCause.ReturnToMenu
                && cause != BsRoundTransitionCause.PreparationCancelled
                && cause != BsRoundTransitionCause.StandaloneAbort
                && cause != BsRoundTransitionCause.LoadFailed)
            {
                rejectionReason = "The clear cause is invalid";
                return false;
            }
            if (gameplayEpoch == int.MaxValue)
            {
                rejectionReason = "The gameplay token space was exhausted";
                return false;
            }
            if (!TryReserveImmediateIdentity(
                    out BsOperationId operationId,
                    out long nextRevision,
                    out rejectionReason))
                return false;

            BsRoundState from = state;
            BsAttemptId completedAttempt = attemptId;
            gameplayEpoch++;
            state = BsRoundState.Empty;
            completion = BsRoundCompletion.None;
            completionTransition = null;
            board = null;
            boardRevision = 0;
            revision = nextRevision;
            var transition = new BsRoundTransition(
                completedAttempt, operationId, revision, 0,
                from, state, cause, BsRoundCompletion.None,
                CurrentToken, null);
            attemptId = default;
            attemptKind = default;
            commit = new BsRoundCommit(
                operationId, revision, cause, null, transition);
            return true;
        }

        /// <summary>
        /// Clears standalone rounds such as First Shift. Saved Playing or Paused rounds cannot skip
        /// settlement here.
        /// </summary>
        public bool TryAbortStandalone(
            BsRoundCommandStamp expected,
            out BsRoundCommit commit,
            out string rejectionReason)
        {
            commit = null;
            rejectionReason = null;
            if (!CanRunImmediateCommand(out rejectionReason)) return false;
            if (!IsCurrent(expected))
            {
                rejectionReason = "The round command is stale";
                return false;
            }
            if (attemptKind != BsRoundAttemptKind.Standalone
                || (state != BsRoundState.Playing && state != BsRoundState.Paused))
            {
                rejectionReason = "Only an active standalone round can be aborted";
                return false;
            }
            if (gameplayEpoch == int.MaxValue)
            {
                rejectionReason = "The gameplay token space was exhausted";
                return false;
            }
            if (!TryReserveImmediateIdentity(
                    out BsOperationId operationId,
                    out long nextRevision,
                    out rejectionReason))
                return false;

            BsRoundState from = state;
            BsAttemptId abortedAttempt = attemptId;
            gameplayEpoch++;
            state = BsRoundState.Empty;
            completion = BsRoundCompletion.None;
            completionTransition = null;
            board = null;
            boardRevision = 0;
            revision = nextRevision;
            var transition = new BsRoundTransition(
                abortedAttempt,
                operationId,
                revision,
                0,
                from,
                state,
                BsRoundTransitionCause.StandaloneAbort,
                BsRoundCompletion.None,
                CurrentToken,
                null);
            attemptId = default;
            attemptKind = default;
            commit = new BsRoundCommit(
                operationId,
                revision,
                BsRoundTransitionCause.StandaloneAbort,
                null,
                transition);
            return true;
        }

        public PourResult CanPour(int sourceGlassId, int targetGlassId)
        {
            if (board == null) return PourResult.Fail("No round board is loaded");
            return board.CanPour(
                board.GlassById(sourceGlassId),
                board.GlassById(targetGlassId));
        }

        public bool TryStagePour(
            BsRoundCommandStamp expected,
            int sourceGlassId,
            int targetGlassId,
            out BsStagedBoardMutation staged,
            out string rejectionReason)
        {
            staged = null;
            if (!CanStageBoardCommand(expected, out rejectionReason)) return false;

            BsBoard candidate = board.Clone();
            RtGlass source = candidate.GlassById(sourceGlassId);
            RtGlass target = candidate.GlassById(targetGlassId);
            PourResult rule = candidate.CanPour(source, target);
            if (!rule.Success)
            {
                rejectionReason = rule.Reason;
                return false;
            }

            RtGlass sourceBefore = source.Clone();
            RtGlass targetBefore = target.Clone();
            PourResult result = candidate.Pour(source, target);
            if (!result.Success)
            {
                rejectionReason = result.Reason;
                return false;
            }

            var evidence = new BsPourCommitEvidence(
                sourceBefore, source, targetBefore, target, result.Amount);
            return InstallBoardStage(
                expected, candidate, BsBoardMutationKind.Pour,
                BsRoundTransitionCause.PlayerPour,
                evidence, null, null,
                false,
                out staged, out rejectionReason);
        }

        public bool TryStageDelivery(
            BsRoundCommandStamp expected,
            int glassId,
            out BsStagedBoardMutation staged,
            out string rejectionReason)
        {
            staged = null;
            if (!CanStageBoardCommand(expected, out rejectionReason)) return false;

            BsBoard candidate = board.Clone();
            RtGlass glass = candidate.GlassById(glassId);
            if (glass == null)
            {
                rejectionReason = "The glass is not in this round";
                return false;
            }
            int expectedSlot = candidate.MatchedSlot(glass);
            if (expectedSlot < 0)
            {
                rejectionReason = "The glass does not match an open order";
                return false;
            }

            RtGlass deliveredGlass = glass.Clone();
            OrderDef deliveredOrder = candidate.Slots[expectedSlot]?.Clone();
            if (!candidate.Deliver(glass, out int committedSlot)
                || committedSlot != expectedSlot)
            {
                rejectionReason = "The delivery rule rejected the action";
                return false;
            }

            var evidence = new BsDeliveryCommitEvidence(
                deliveredGlass, deliveredOrder, committedSlot);
            return InstallBoardStage(
                expected, candidate, BsBoardMutationKind.Delivery,
                BsRoundTransitionCause.PlayerDelivery,
                null, evidence, null,
                false,
                out staged, out rejectionReason);
        }

        public bool TryStageAddEmptyGlass(
            BsRoundCommandStamp expected,
            GlassType type,
            int maximumGlassCount,
            out BsStagedBoardMutation staged,
            out string rejectionReason)
        {
            staged = null;
            if (!CanStageBoardCommand(expected, out rejectionReason)) return false;
            if (!IsKnownGlassType(type))
            {
                rejectionReason = "The glass type is invalid";
                return false;
            }
            if (maximumGlassCount <= 0 || board.Glasses.Count >= maximumGlassCount)
            {
                rejectionReason = "The round cannot accept another glass";
                return false;
            }

            BsBoard candidate = board.Clone();
            RtGlass added = candidate.AddEmptyGlass(type);
            if (added == null)
            {
                rejectionReason = "The extra glass could not be added";
                return false;
            }

            var evidence = new BsGlassCommitEvidence(added.Id, null, added);
            return InstallBoardStage(
                expected, candidate, BsBoardMutationKind.ExtraGlass,
                BsRoundTransitionCause.ExtraGlass,
                null, null, evidence,
                false,
                out staged, out rejectionReason);
        }

        public bool TryStageShuffle(
            BsRoundCommandStamp expected,
            int glassId,
            int selectionKey,
            out BsStagedBoardMutation staged,
            out string rejectionReason)
        {
            staged = null;
            if (!CanStageBoardCommand(expected, out rejectionReason)) return false;

            BsBoard candidate = board.Clone();
            RtGlass glass = candidate.GlassById(glassId);
            if (!candidate.IsShuffleTarget(glass))
            {
                rejectionReason = "The selected glass cannot be shuffled";
                return false;
            }

            RtGlass before = glass.Clone();
            if (!TryApplyDeterministicShuffle(candidate, glass, selectionKey))
            {
                rejectionReason = "No legal shuffle result could be found";
                return false;
            }

            var evidence = new BsGlassCommitEvidence(glass.Id, before, glass);
            return InstallBoardStage(
                expected, candidate, BsBoardMutationKind.Shuffle,
                BsRoundTransitionCause.Shuffle,
                null, null, evidence,
                false,
                out staged, out rejectionReason);
        }

        public bool TryStageCheckpointRestore(
            BsRoundCommandStamp expected,
            BsBoardCheckpoint checkpoint,
            out BsStagedBoardMutation staged,
            out string rejectionReason)
        {
            staged = null;
            if (!CanStageBoardCommand(expected, out rejectionReason)) return false;
            if (checkpoint == null
                || checkpoint.OwnerId != ownerId
                || checkpoint.AttemptId != attemptId
                || checkpoint.Token != CurrentToken
                || checkpoint.Revision > revision
                || checkpoint.BoardRevision > boardRevision)
            {
                rejectionReason = "The board checkpoint is stale or belongs to another round";
                return false;
            }

            BsBoard candidate = checkpoint.CaptureOwnedBoard();
            if (!TryCloneValidBoard(
                    candidate, out BsBoard ownedCandidate, out rejectionReason))
                return false;
            return InstallBoardStage(
                expected, ownedCandidate, BsBoardMutationKind.RestoreCheckpoint,
                BsRoundTransitionCause.PlayerUndo,
                null, null, null,
                false,
                out staged, out rejectionReason);
        }

        /// <summary>
        /// Pause restart copies the new board, keeps the attempt and refreshes its generation. Board
        /// replacement and Resume(Restart) commit together.
        /// </summary>
        public bool TryStagePausedBoardReplacement(
            BsRoundCommandStamp expected,
            BsBoard replacement,
            out BsStagedBoardMutation staged,
            out string rejectionReason)
        {
            staged = null;
            if (!CanStageBoardCommand(
                    expected, BsRoundState.Paused, out rejectionReason))
                return false;
            if (!TryCloneValidBoard(replacement, out BsBoard candidate,
                    out rejectionReason))
                return false;
            if (DetectCompletion(candidate) != BsRoundCompletion.None)
            {
                rejectionReason =
                    "A restarted board must become playable before it can complete";
                return false;
            }
            return InstallBoardStage(
                expected, candidate, BsBoardMutationKind.Replace,
                BsRoundTransitionCause.Restart,
                null, null, null,
                true,
                out staged, out rejectionReason);
        }

        /// <summary>
        /// Call after the candidate save is accepted. A mismatched terminal receipt leaves live state
        /// unchanged and the candidate available for retry.
        /// </summary>
        public bool TryCommitBoardMutation(
            BsStagedBoardMutation staged,
            BsSettlementReceipt? settlementReceipt,
            out BsRoundCommit commit,
            out string rejectionReason)
        {
            commit = null;
            rejectionReason = null;
            if (!MatchesActiveStage(staged))
            {
                rejectionReason = "The staged board command is stale or foreign";
                return false;
            }
            if (!IsCurrent(staged.BaseStamp))
            {
                rejectionReason = "The round changed before the board command committed";
                return false;
            }
            if (staged.Revision != revision + 1L
                || staged.BoardRevision != boardRevision + 1)
            {
                rejectionReason = "The staged board revision is stale";
                return false;
            }

            BsSettlementReceipt? acceptedSettlement = null;
            if (staged.RequiresSettlement)
            {
                BsSettlementRequest request = staged.SettlementRequest.Value;
                if (!TryValidateSettlement(
                        request, settlementReceipt, out BsSettlementReceipt validated,
                        out rejectionReason))
                    return false;
                acceptedSettlement = validated;
            }
            else if (settlementReceipt.HasValue)
            {
                rejectionReason = "A non-terminal board command cannot carry settlement";
                return false;
            }

            if ((staged.DetectedCompletion != BsRoundCompletion.None
                    || staged.ResumesRound)
                && gameplayEpoch == int.MaxValue)
            {
                rejectionReason = "The gameplay token space was exhausted";
                return false;
            }

            BsRoundState from = state;
            board = staged.TakeCandidateBoard();
            boardRevision = staged.BoardRevision;
            revision = staged.Revision;
            activeBoardMutation = null;

            BsRoundTransition transition = null;
            if (staged.DetectedCompletion != BsRoundCompletion.None)
            {
                state = BsRoundState.Completed;
                completion = staged.DetectedCompletion;
                gameplayEpoch++;
                transition = new BsRoundTransition(
                    attemptId,
                    staged.OperationId,
                    revision,
                    boardRevision,
                    from,
                    state,
                    staged.Cause,
                    completion,
                    CurrentToken,
                    acceptedSettlement);
                completionTransition = transition;
            }
            else if (staged.ResumesRound)
            {
                state = BsRoundState.Playing;
                completion = BsRoundCompletion.None;
                completionTransition = null;
                gameplayEpoch++;
                transition = new BsRoundTransition(
                    attemptId,
                    staged.OperationId,
                    revision,
                    boardRevision,
                    from,
                    state,
                    staged.Cause,
                    BsRoundCompletion.None,
                    CurrentToken,
                    null);
            }

            var boardCommit = new BsBoardCommit(
                attemptId,
                staged.OperationId,
                revision,
                boardRevision,
                CurrentToken,
                staged.Cause,
                staged.Kind,
                staged.DetectedCompletion,
                acceptedSettlement,
                board,
                staged.PourEvidence,
                staged.DeliveryEvidence,
                staged.GlassEvidence);
            commit = new BsRoundCommit(
                staged.OperationId,
                revision,
                staged.Cause,
                boardCommit,
                transition);
            return true;
        }

        public bool TryDiscard(BsStagedBoardMutation staged)
        {
            if (!MatchesActiveStage(staged)) return false;
            activeBoardMutation = null;
            return true;
        }

        public bool TryStageCompletion(
            BsRoundCommandStamp expected,
            BsRoundCompletion requestedCompletion,
            BsRoundTransitionCause cause,
            out BsStagedRoundCompletion staged,
            out string rejectionReason)
        {
            staged = null;
            rejectionReason = null;
            if (!CanRunImmediateCommand(out rejectionReason)) return false;
            if (!IsCurrent(expected))
            {
                rejectionReason = "The round command is stale";
                return false;
            }
            bool validState = requestedCompletion == BsRoundCompletion.Quit
                ? state == BsRoundState.Paused
                : state == BsRoundState.Playing;
            if (!validState
                || requestedCompletion == BsRoundCompletion.None
                || !IsKnownCompletion(requestedCompletion))
            {
                rejectionReason = "The completion is invalid for the current state";
                return false;
            }
            if (!CompletionCauseIsValid(requestedCompletion, cause))
            {
                rejectionReason = "The completion cause is invalid";
                return false;
            }
            if (gameplayEpoch == int.MaxValue)
            {
                rejectionReason = "The gameplay token space was exhausted";
                return false;
            }
            if (!TryReserveStageIdentity(
                    out BsOperationId operationId,
                    out long nextRevision,
                    out long stageId,
                    out rejectionReason))
                return false;

            staged = new BsStagedRoundCompletion(
                ownerId,
                stageId,
                expected,
                operationId,
                nextRevision,
                requestedCompletion,
                cause);
            activeCompletion = staged;
            return true;
        }

        public bool TryCommitCompletion(
            BsStagedRoundCompletion staged,
            BsSettlementReceipt? settlementReceipt,
            out BsRoundCommit commit,
            out string rejectionReason)
        {
            commit = null;
            rejectionReason = null;
            if (!MatchesActiveStage(staged))
            {
                rejectionReason = "The staged completion is stale or foreign";
                return false;
            }
            if (!IsCurrent(staged.BaseStamp) || staged.Revision != revision + 1L)
            {
                rejectionReason = "The round changed before completion committed";
                return false;
            }
            if (!TryValidateSettlement(
                    staged.SettlementRequest,
                    settlementReceipt,
                    out BsSettlementReceipt acceptedSettlement,
                    out rejectionReason))
                return false;
            if (gameplayEpoch == int.MaxValue)
            {
                rejectionReason = "The gameplay token space was exhausted";
                return false;
            }

            BsRoundState from = state;
            activeCompletion = null;
            revision = staged.Revision;
            state = BsRoundState.Completed;
            completion = staged.Completion;
            gameplayEpoch++;
            var transition = new BsRoundTransition(
                attemptId,
                staged.OperationId,
                revision,
                boardRevision,
                from,
                state,
                staged.Cause,
                completion,
                CurrentToken,
                acceptedSettlement);
            completionTransition = transition;
            commit = new BsRoundCommit(
                staged.OperationId,
                revision,
                staged.Cause,
                null,
                transition);
            return true;
        }

        public bool TryDiscard(BsStagedRoundCompletion staged)
        {
            if (!MatchesActiveStage(staged)) return false;
            activeCompletion = null;
            return true;
        }

        private bool TryTransition(
            BsRoundCommandStamp expected,
            BsRoundState required,
            BsRoundState next,
            BsRoundTransitionCause cause,
            BsRoundCompletion nextCompletion,
            bool invalidateGameplay,
            out BsRoundCommit commit,
            out string rejectionReason)
        {
            commit = null;
            rejectionReason = null;
            if (!CanRunImmediateCommand(out rejectionReason)) return false;
            if (!IsCurrent(expected))
            {
                rejectionReason = "The round command is stale";
                return false;
            }
            if (state != required)
            {
                rejectionReason = $"The round must be {required}";
                return false;
            }
            if (!IsKnownCause(cause) || cause == BsRoundTransitionCause.None)
            {
                rejectionReason = "The transition cause is invalid";
                return false;
            }
            if (invalidateGameplay && gameplayEpoch == int.MaxValue)
            {
                rejectionReason = "The gameplay token space was exhausted";
                return false;
            }
            if (!TryReserveImmediateIdentity(
                    out BsOperationId operationId,
                    out long nextRevision,
                    out rejectionReason))
                return false;

            BsRoundState from = state;
            state = next;
            completion = nextCompletion;
            if (invalidateGameplay) gameplayEpoch++;
            revision = nextRevision;
            var transition = new BsRoundTransition(
                attemptId,
                operationId,
                revision,
                boardRevision,
                from,
                state,
                cause,
                nextCompletion,
                CurrentToken,
                null);
            commit = new BsRoundCommit(
                operationId, revision, cause, null, transition);
            return true;
        }

        private bool InstallBoardStage(
            BsRoundCommandStamp expected,
            BsBoard candidate,
            BsBoardMutationKind kind,
            BsRoundTransitionCause cause,
            BsPourCommitEvidence pourEvidence,
            BsDeliveryCommitEvidence deliveryEvidence,
            BsGlassCommitEvidence glassEvidence,
            bool resumesRound,
            out BsStagedBoardMutation staged,
            out string rejectionReason)
        {
            staged = null;
            if (boardRevision == int.MaxValue)
            {
                rejectionReason = "The board revision space was exhausted";
                return false;
            }

            BsRoundCompletion detected = DetectCompletion(candidate);
            if (detected != BsRoundCompletion.None
                && !CompletionCauseIsValid(detected, cause))
            {
                rejectionReason =
                    "The board result is incompatible with the mutation cause";
                return false;
            }
            if (detected != BsRoundCompletion.None
                || resumesRound)
            {
                if (gameplayEpoch == int.MaxValue)
                {
                    rejectionReason = "The gameplay token space was exhausted";
                    return false;
                }
            }
            if (!TryReserveStageIdentity(
                    out BsOperationId operationId,
                    out long nextRevision,
                    out long stageId,
                    out rejectionReason))
                return false;

            staged = new BsStagedBoardMutation(
                ownerId,
                stageId,
                expected,
                operationId,
                nextRevision,
                boardRevision + 1,
                cause,
                kind,
                detected,
                resumesRound,
                candidate,
                pourEvidence,
                deliveryEvidence,
                glassEvidence);
            activeBoardMutation = staged;
            return true;
        }

        private bool CanStageBoardCommand(
            BsRoundCommandStamp expected,
            out string rejectionReason) =>
            CanStageBoardCommand(
                expected, BsRoundState.Playing, out rejectionReason);

        private bool CanStageBoardCommand(
            BsRoundCommandStamp expected,
            BsRoundState requiredState,
            out string rejectionReason)
        {
            if (!CanRunImmediateCommand(out rejectionReason)) return false;
            if (!IsCurrent(expected))
            {
                rejectionReason = "The round command is stale";
                return false;
            }
            if (state != requiredState || board == null)
            {
                rejectionReason = "The round board is not available for this command";
                return false;
            }
            return true;
        }

        private bool CanRunImmediateCommand(out string rejectionReason)
        {
            if (HasStagedOperation)
            {
                rejectionReason = "A staged round operation is already pending";
                return false;
            }
            rejectionReason = null;
            return true;
        }

        private bool TryReserveImmediateIdentity(
            out BsOperationId operationId,
            out long nextRevision,
            out string rejectionReason)
        {
            operationId = default;
            nextRevision = revision;
            rejectionReason = null;
            if (lastOperationId == long.MaxValue || revision == long.MaxValue)
            {
                rejectionReason = "The round identity space was exhausted";
                return false;
            }
            operationId = new BsOperationId(++lastOperationId);
            nextRevision = revision + 1L;
            return true;
        }

        private bool TryReserveStageIdentity(
            out BsOperationId operationId,
            out long nextRevision,
            out long stageId,
            out string rejectionReason)
        {
            stageId = 0L;
            if (lastStageId == long.MaxValue)
            {
                operationId = default;
                nextRevision = revision;
                rejectionReason = "The staged-operation identity space was exhausted";
                return false;
            }
            if (!TryReserveImmediateIdentity(
                    out operationId, out nextRevision, out rejectionReason))
                return false;
            stageId = ++lastStageId;
            return true;
        }

        private bool TryValidateSettlement(
            BsSettlementRequest request,
            BsSettlementReceipt? offered,
            out BsSettlementReceipt accepted,
            out string rejectionReason)
        {
            accepted = default;
            rejectionReason = null;
            if (attemptKind == BsRoundAttemptKind.Standalone)
            {
                if (offered.HasValue
                    && (!offered.Value.IsValid
                        || offered.Value.Request != request))
                {
                    rejectionReason = "The settlement receipt does not match the operation";
                    return false;
                }
                accepted = offered ?? BsSettlementReceipt.Standalone(request);
                if (accepted.IsValid) return true;
                rejectionReason = "The standalone settlement receipt is invalid";
                return false;
            }

            if (!offered.HasValue
                || !offered.Value.IsValid
                || !offered.Value.IsDurable
                || offered.Value.Request != request)
            {
                rejectionReason = "A matching durable settlement receipt is required";
                return false;
            }
            accepted = offered.Value;
            return true;
        }

        private bool MatchesActiveStage(BsStagedBoardMutation staged) =>
            staged != null
            && ReferenceEquals(activeBoardMutation, staged)
            && staged.OwnerId == ownerId
            && staged.StageId > 0L;

        private bool MatchesActiveStage(BsStagedRoundCompletion staged) =>
            staged != null
            && ReferenceEquals(activeCompletion, staged)
            && staged.OwnerId == ownerId
            && staged.StageId > 0L;

        private static BsRoundCompletion DetectCompletion(BsBoard candidate)
        {
            if (candidate.IsWin()) return BsRoundCompletion.Won;
            if (candidate.IsFail()) return BsRoundCompletion.Failed;
            return BsRoundCompletion.None;
        }

        private static bool TryCloneValidBoard(
            BsBoard source,
            out BsBoard clone,
            out string rejectionReason)
        {
            clone = null;
            if (source == null || source.Glasses == null || source.Slots == null)
            {
                rejectionReason = "The round board is invalid";
                return false;
            }

            var ids = new HashSet<int>();
            int greatestGlassId = -1;
            for (int i = 0; i < source.Glasses.Count; i++)
            {
                RtGlass glass = source.Glasses[i];
                if (glass == null || glass.Layers == null || glass.Id < 0
                    || !ids.Add(glass.Id) || !IsKnownGlassType(glass.Type)
                    || glass.UnlockAfter < 0
                    || glass.Layers.Count > BsRules.Capacity(glass.Type))
                {
                    rejectionReason = "The round board contains an invalid glass";
                    return false;
                }
                for (int layerIndex = 0;
                     layerIndex < glass.Layers.Count;
                     layerIndex++)
                {
                    Layer layer = glass.Layers[layerIndex];
                    if (layer.Color >= 0 && layer.LockUntil >= 0) continue;
                    rejectionReason = "The round board contains an invalid layer";
                    return false;
                }
                greatestGlassId = Math.Max(greatestGlassId, glass.Id);
            }

            BsBoardSnapshot snapshot;
            try { snapshot = source.CaptureSnapshot(); }
            catch (Exception exception)
            {
                rejectionReason = "The round board could not be inspected: "
                                + exception.Message;
                return false;
            }
            if (snapshot.TotalOrders < 0
                || snapshot.DeckIndex < 0
                || snapshot.DeckIndex > snapshot.TotalOrders
                || snapshot.Delivered < 0
                || snapshot.Delivered > snapshot.DeckIndex
                || snapshot.NextGlassId <= greatestGlassId
                || snapshot.NextGlassId == int.MaxValue)
            {
                rejectionReason = "The round board counters are inconsistent";
                return false;
            }

            var usedOrders = new HashSet<int>();
            int occupiedSlots = 0;
            for (int slotIndex = 0;
                 slotIndex < snapshot.SlotOrderIndices.Length;
                 slotIndex++)
            {
                int orderIndex = snapshot.SlotOrderIndices[slotIndex];
                if (orderIndex == -1) continue;
                if (orderIndex < 0
                    || orderIndex >= snapshot.DeckIndex
                    || !usedOrders.Add(orderIndex))
                {
                    rejectionReason = "The round board order slots are inconsistent";
                    return false;
                }
                occupiedSlots++;
            }
            if (snapshot.Delivered != snapshot.DeckIndex - occupiedSlots)
            {
                rejectionReason = "The round board delivery count is inconsistent";
                return false;
            }

            try { clone = source.Clone(); }
            catch (Exception exception)
            {
                rejectionReason = "The round board could not be cloned: "
                                + exception.Message;
                return false;
            }
            rejectionReason = null;
            return true;
        }

        private static bool SnapshotShapeIsValid(
            BsRoundSnapshot snapshot,
            BsBoard restoredBoard,
            out string rejectionReason)
        {
            int stateValue = (int)snapshot.State;
            int completionValue = (int)snapshot.Completion;
            if (stateValue < (int)BsRoundState.Empty
                || stateValue > (int)BsRoundState.Completed
                || completionValue < (int)BsRoundCompletion.None
                || completionValue > (int)BsRoundCompletion.Quit
                || snapshot.Revision < 0L
                || snapshot.LastOperationId.Value < 0L
                || snapshot.BoardRevision < 0
                || snapshot.Token.RoundId < 0
                || snapshot.Token.GameplayEpoch < 0)
            {
                rejectionReason = "The round snapshot counters are invalid";
                return false;
            }

            if (snapshot.State == BsRoundState.Empty)
            {
                if (snapshot.AttemptId.IsValid || restoredBoard != null
                    || snapshot.Completion != BsRoundCompletion.None
                    || snapshot.CompletionOperationId.IsValid
                    || snapshot.CompletionCause != BsRoundTransitionCause.None
                    || snapshot.SettlementReceipt.HasValue)
                {
                    rejectionReason = "An Empty snapshot cannot own a round";
                    return false;
                }
                rejectionReason = null;
                return true;
            }

            if (!snapshot.AttemptId.IsValid || restoredBoard == null
                || !IsKnownAttemptKind(snapshot.AttemptKind)
                || snapshot.Revision <= 0L
                || snapshot.Token.RoundId <= 0
                || snapshot.Token.GameplayEpoch <= 0
                || snapshot.LastOperationId.Value < snapshot.Revision)
            {
                rejectionReason = "The round snapshot has no valid attempt or board";
                return false;
            }
            bool completed = snapshot.State == BsRoundState.Completed;
            if (completed != (snapshot.Completion != BsRoundCompletion.None))
            {
                rejectionReason = "The round snapshot completion is inconsistent";
                return false;
            }
            if (!completed)
            {
                if (snapshot.CompletionOperationId.IsValid
                    || snapshot.CompletionCause != BsRoundTransitionCause.None
                    || snapshot.SettlementReceipt.HasValue)
                {
                    rejectionReason =
                        "A live round snapshot cannot contain terminal evidence";
                    return false;
                }
            }
            else
            {
                bool fromIsValid = snapshot.Completion == BsRoundCompletion.Quit
                    ? snapshot.CompletionFrom == BsRoundState.Paused
                    : snapshot.CompletionFrom == BsRoundState.Playing;
                if (!fromIsValid
                    || snapshot.CompletionOperationId.Value
                        > snapshot.LastOperationId.Value
                    || !CompletionCauseIsValid(
                        snapshot.Completion, snapshot.CompletionCause)
                    || !snapshot.SettlementRequest.HasValue
                    || !snapshot.SettlementRequest.Value.IsValid)
                {
                    rejectionReason =
                        "The round snapshot terminal evidence is inconsistent";
                    return false;
                }
                if (snapshot.SettlementReceipt.HasValue
                    && (!snapshot.SettlementReceipt.Value.IsValid
                        || snapshot.SettlementReceipt.Value.Request
                            != snapshot.SettlementRequest.Value))
                {
                    rejectionReason =
                        "The round snapshot settlement receipt is inconsistent";
                    return false;
                }
            }
            return TryCloneValidBoard(
                restoredBoard, out _, out rejectionReason);
        }

        private static bool TryResolveSnapshotCompletion(
            BsRoundSnapshot snapshot,
            BsSettlementReceipt? recovered,
            out BsRoundTransition transition,
            out string rejectionReason)
        {
            transition = null;
            rejectionReason = null;
            if (snapshot.State != BsRoundState.Completed)
            {
                if (!recovered.HasValue) return true;
                rejectionReason =
                    "A live round snapshot cannot accept a settlement receipt";
                return false;
            }

            BsSettlementRequest request = snapshot.SettlementRequest.Value;
            if (snapshot.SettlementReceipt.HasValue && recovered.HasValue
                && snapshot.SettlementReceipt.Value != recovered.Value)
            {
                rejectionReason =
                    "The recovered settlement receipt conflicts with the snapshot";
                return false;
            }
            BsSettlementReceipt? offered = recovered ?? snapshot.SettlementReceipt;
            BsSettlementReceipt accepted;
            if (snapshot.AttemptKind == BsRoundAttemptKind.Durable)
            {
                if (!offered.HasValue
                    || !offered.Value.IsValid
                    || !offered.Value.IsDurable
                    || offered.Value.Request != request)
                {
                    rejectionReason =
                        "A matching durable settlement receipt is required to restore completion";
                    return false;
                }
                accepted = offered.Value;
            }
            else
            {
                if (offered.HasValue
                    && (!offered.Value.IsValid
                        || offered.Value.Request != request))
                {
                    rejectionReason =
                        "The recovered standalone settlement receipt is invalid";
                    return false;
                }
                accepted = offered ?? BsSettlementReceipt.Standalone(request);
            }

            transition = new BsRoundTransition(
                snapshot.AttemptId,
                snapshot.CompletionOperationId,
                snapshot.Revision,
                snapshot.BoardRevision,
                snapshot.CompletionFrom,
                BsRoundState.Completed,
                snapshot.CompletionCause,
                snapshot.Completion,
                snapshot.Token,
                accepted);
            return true;
        }

        private static bool CompletionCauseIsValid(
            BsRoundCompletion requestedCompletion,
            BsRoundTransitionCause cause)
        {
            if (!IsKnownCause(cause) || cause == BsRoundTransitionCause.None)
                return false;
            if (requestedCompletion == BsRoundCompletion.Quit)
                return cause == BsRoundTransitionCause.PauseMenuQuit;
            if (requestedCompletion == BsRoundCompletion.Won)
                return cause == BsRoundTransitionCause.PlayerPour
                    || cause == BsRoundTransitionCause.PlayerDelivery;
            return requestedCompletion == BsRoundCompletion.Failed
                && (cause == BsRoundTransitionCause.PlayerPour
                    || cause == BsRoundTransitionCause.PlayerDelivery
                    || cause == BsRoundTransitionCause.TimedOrderExpired
                    || cause == BsRoundTransitionCause.DeadEndDetected
                    || cause == BsRoundTransitionCause.TimeOfferDeclinedPresentFailure
                    || cause == BsRoundTransitionCause.TimeOfferDeclinedReturnToMenu);
        }

        private static bool IsKnownAttemptKind(BsRoundAttemptKind kind) =>
            kind == BsRoundAttemptKind.Durable
            || kind == BsRoundAttemptKind.Standalone;

        private static bool IsKnownCompletion(BsRoundCompletion value) =>
            value == BsRoundCompletion.Won
            || value == BsRoundCompletion.Failed
            || value == BsRoundCompletion.Quit;

        private static bool IsKnownCause(BsRoundTransitionCause cause) =>
            (int)cause >= (int)BsRoundTransitionCause.None
            && (int)cause <= (int)BsRoundTransitionCause.LoadFailed;

        private static bool IsKnownGlassType(GlassType type)
        {
            int value = (int)type;
            return value >= 0 && value < BsRules.CapacityTable.Length;
        }

        private static bool TryApplyDeterministicShuffle(
            BsBoard candidate,
            RtGlass target,
            int selectionKey)
        {
            Layer[] before = target.Layers.ToArray();
            Layer[] working = (Layer[])before.Clone();
            var results = new List<Layer[]>(120);
            try
            {
                CollectShuffleCandidates(
                    candidate, target, before, working, 0, results);
            }
            finally
            {
                WriteLayers(target, before);
            }
            if (results.Count == 0) return false;

            long nonNegativeKey = selectionKey;
            if (nonNegativeKey < 0L) nonNegativeKey = -nonNegativeKey;
            int index = (int)(nonNegativeKey % results.Count);
            WriteLayers(target, results[index]);
            return true;
        }

        private static void CollectShuffleCandidates(
            BsBoard board,
            RtGlass target,
            Layer[] before,
            Layer[] working,
            int index,
            List<Layer[]> destination)
        {
            if (index >= working.Length)
            {
                if (LayerColorsEqual(before, working)) return;
                WriteLayers(target, working);
                if (!board.IsFail()) destination.Add((Layer[])working.Clone());
                return;
            }

            for (int candidate = index; candidate < working.Length; candidate++)
            {
                bool duplicate = false;
                for (int seen = index; seen < candidate; seen++)
                {
                    if (working[seen].Color != working[candidate].Color) continue;
                    duplicate = true;
                    break;
                }
                if (duplicate) continue;

                Swap(working, index, candidate);
                CollectShuffleCandidates(
                    board, target, before, working, index + 1, destination);
                Swap(working, index, candidate);
            }
        }

        private static bool LayerColorsEqual(Layer[] left, Layer[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i].Color != right[i].Color) return false;
            return true;
        }

        private static void WriteLayers(RtGlass glass, Layer[] layers)
        {
            for (int i = 0; i < layers.Length; i++) glass.Layers[i] = layers[i];
        }

        private static void Swap(Layer[] layers, int left, int right)
        {
            if (left == right) return;
            Layer value = layers[left];
            layers[left] = layers[right];
            layers[right] = value;
        }
    }
}
