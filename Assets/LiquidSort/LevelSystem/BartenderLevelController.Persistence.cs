using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    public readonly struct BartenderCommandResult<T>
    {
        public readonly bool Succeeded;
        public readonly T Value;
        public readonly string RejectionReason;

        private BartenderCommandResult(bool succeeded, T value, string reason)
        {
            Succeeded = succeeded;
            Value = value;
            RejectionReason = reason;
        }

        public static BartenderCommandResult<T> Success(T value) =>
            new BartenderCommandResult<T>(true, value, null);
        public static BartenderCommandResult<T> Rejected(string reason) =>
            new BartenderCommandResult<T>(false, default, reason);
    }

    public sealed partial class BartenderLevelController
    {
        private enum SavedBoardCommand { Pour, Delivery, Undo, ExtraGlass, Shuffle }
        private bool boardWritePending;
        private bool preferAsyncSettlement = true;
        private Task<bool> settlementWrite;
        private Task<BartenderCommandResult<bool>> expiryWrite;
        private float expiryRetryAt;

        public bool PersistencePending => boardWritePending;

        public Task<BartenderCommandResult<BartenderPourReceipt>> PourAsync(int source, int target) =>
            ExecutePourAsync(source, target, true);

        internal Task<BartenderCommandResult<BartenderPourReceipt>> PourAsync(int source, int target,
            double? tapClock) =>
            ExecutePourAsync(source, target, true, tapClock);

        private async Task<BartenderCommandResult<BartenderPourReceipt>> ExecutePourAsync(
            int source, int target, bool asynchronous, double? tapClock = null)
        {
            if (IsGlassPresentationLocked(source) || IsGlassPresentationLocked(target))
                return BartenderCommandResult<BartenderPourReceipt>.Rejected("This glass is still pouring");
            if (!CanAcceptCommand(out string reason, allowConcurrentPours: true, tappedAtClock: tapClock)
                || !coordinator.TryStagePour(coordinator.CurrentStamp, source, target,
                    out BsStagedBoardMutation staged, out reason))
                return BartenderCommandResult<BartenderPourReceipt>.Rejected(reason);
            var history = new List<BoardMemento>(undoHistory);
            // A move that makes an order ready (its delivery tick appears) is an undo boundary, like a delivery.
            if (PourMakesOrderReady(staged.PourEvidence))
            {
                history.Clear();
            }
            else if (undoHistoryDepth > 0 && UndoRemaining > 0)
            {
                history.Add(CaptureCurrentMemento());
            }
            TrimUndoHistory(history, UndoRemaining);
            BartenderCommandResult<BsRoundCommit> result = await SaveBoardCommandAsync(
                staged, 0, SavedBoardCommand.Pour, history, asynchronous);
            return result.Succeeded
                ? BartenderCommandResult<BartenderPourReceipt>.Success(new BartenderPourReceipt(result.Value.BoardCommit))
                : BartenderCommandResult<BartenderPourReceipt>.Rejected(result.RejectionReason);
        }

        private bool PourMakesOrderReady(BsPourCommitEvidence evidence) =>
            evidence != null
            && (BecameOrderReady(evidence.SourceBefore, evidence.SourceAfter)
                || BecameOrderReady(evidence.TargetBefore, evidence.TargetAfter));

        private bool BecameOrderReady(RtGlass before, RtGlass after) =>
            MatchedOrderSlot(after) >= 0 && MatchedOrderSlot(before) < 0;

        public Task<BartenderCommandResult<BartenderDeliveryReceipt>> DeliverAsync(int glassId) =>
            ExecuteDeliveryAsync(glassId, true);

        internal Task<BartenderCommandResult<BartenderDeliveryReceipt>> DeliverAsync(int glassId,
            double? tapClock) =>
            ExecuteDeliveryAsync(glassId, true, tapClock);

        private async Task<BartenderCommandResult<BartenderDeliveryReceipt>> ExecuteDeliveryAsync(
            int glassId, bool asynchronous, double? tapClock = null)
        {
            if (IsGlassPresentationLocked(glassId))
                return BartenderCommandResult<BartenderDeliveryReceipt>.Rejected("This glass is still pouring");
            if (!CanAcceptCommand(out string reason, allowConcurrentPours: true, tappedAtClock: tapClock)
                || !coordinator.TryStageDelivery(coordinator.CurrentStamp, glassId,
                    out BsStagedBoardMutation staged, out reason))
                return BartenderCommandResult<BartenderDeliveryReceipt>.Rejected(reason);
            BartenderCommandResult<BsRoundCommit> result = await SaveBoardCommandAsync(
                staged, 0, SavedBoardCommand.Delivery, new List<BoardMemento>(), asynchronous);
            return result.Succeeded
                ? BartenderCommandResult<BartenderDeliveryReceipt>.Success(new BartenderDeliveryReceipt(result.Value.BoardCommit))
                : BartenderCommandResult<BartenderDeliveryReceipt>.Rejected(result.RejectionReason);
        }

        public Task<BartenderCommandResult<bool>> PurchaseUndoAsync() =>
            ExecuteUndoAsync(BartenderProgressTuning.UndoBoosterCoinCost, true);

        private async Task<BartenderCommandResult<bool>> ExecuteUndoAsync(int coinCost, bool asynchronous)
        {
            if (!CanPurchaseUndo(out string reason)) return BartenderCommandResult<bool>.Rejected(reason);
            BoardMemento memento = undoHistory[undoHistory.Count - 1];
            if (!coordinator.TryImportBoardCheckpoint(memento.Stamp, memento.Board,
                    out BsBoardCheckpoint checkpoint, out reason)
                || !coordinator.TryStageCheckpointRestore(coordinator.CurrentStamp, checkpoint,
                    out BsStagedBoardMutation staged, out reason))
            {
                ScheduleDeadEndProbe();
                return BartenderCommandResult<bool>.Rejected(reason);
            }
            var history = new List<BoardMemento>(undoHistory);
            history.RemoveAt(history.Count - 1);
            var result = await SaveBoardCommandAsync(staged, coinCost, SavedBoardCommand.Undo,
                history, asynchronous, memento.SlotDeadlines);
            return result.Succeeded ? BartenderCommandResult<bool>.Success(true)
                : BartenderCommandResult<bool>.Rejected(result.RejectionReason);
        }

        public Task<BartenderCommandResult<int>> PurchaseExtraGlassAsync(GlassType type) =>
            ExecuteExtraGlassAsync(type, BartenderProgressTuning.ExtraGlassBoosterCoinCost, true);

        private async Task<BartenderCommandResult<int>> ExecuteExtraGlassAsync(
            GlassType type, int coinCost, bool asynchronous)
        {
            if (!CanPurchaseExtraGlass(type, out string reason)
                || !coordinator.TryStageAddEmptyGlass(coordinator.CurrentStamp, type, MaxActiveGlasses,
                    out BsStagedBoardMutation staged, out reason))
                return BartenderCommandResult<int>.Rejected(reason);
            int glassId = staged.GlassEvidence.GlassId;
            var result = await SaveBoardCommandAsync(staged, coinCost, SavedBoardCommand.ExtraGlass,
                new List<BoardMemento>(), asynchronous);
            return result.Succeeded ? BartenderCommandResult<int>.Success(glassId)
                : BartenderCommandResult<int>.Rejected(result.RejectionReason);
        }

        public Task<BartenderCommandResult<bool>> PurchaseShuffleAsync(int glassId, int expectedRevision) =>
            ExecuteShuffleAsync(glassId, expectedRevision, BartenderProgressTuning.ShuffleBoosterCoinCost, true);

        private async Task<BartenderCommandResult<bool>> ExecuteShuffleAsync(
            int glassId, int expectedRevision, int coinCost, bool asynchronous)
        {
            if (!CanPurchaseShuffle(out string reason)) return BartenderCommandResult<bool>.Rejected(reason);
            if (BoardRevision != expectedRevision)
                return BartenderCommandResult<bool>.Rejected("The board changed before the bottle was selected");
            int selectionKey = UnityEngine.Random.Range(0, int.MaxValue);
            if (!coordinator.TryStageShuffle(coordinator.CurrentStamp, glassId, selectionKey,
                    out BsStagedBoardMutation staged, out reason))
                return BartenderCommandResult<bool>.Rejected(reason);
            var result = await SaveBoardCommandAsync(staged, coinCost, SavedBoardCommand.Shuffle,
                new List<BoardMemento>(), asynchronous);
            return result.Succeeded ? BartenderCommandResult<bool>.Success(true)
                : BartenderCommandResult<bool>.Rejected(result.RejectionReason);
        }

        private async Task<BartenderCommandResult<BsRoundCommit>> SaveBoardCommandAsync(
            BsStagedBoardMutation staged, int coinCost, SavedBoardCommand kind,
            List<BoardMemento> history, bool asynchronous, double?[] restoredDeadlines = null)
        {
            commandInProgress = true;
            boardWritePending = asynchronous;
            preferAsyncSettlement = asynchronous;
            BsRoundCoordinator reservedCoordinator = coordinator;
            BsRoundCommit committed = null;
            bool durable = false;
            try
            {
                if (!coordinator.TryCaptureProspectiveSnapshot(staged, out BsRoundSnapshot prospective,
                        out string reason))
                    return BartenderCommandResult<BsRoundCommit>.Rejected(reason);
                BsBoard candidate = staged.CaptureCandidateBoard();
                bool standalone = IsStandaloneRound;
                int undoStock = UndoRemaining - (kind == SavedBoardCommand.Undo ? 1 : 0);
                TrimUndoHistory(history, undoStock);
                ActiveRoundSnapshot snapshot = null;
                if (!standalone && !TryCaptureActiveRoundSnapshot(prospective, candidate, null,
                        out snapshot, out reason, history))
                    return BartenderCommandResult<BsRoundCommit>.Rejected(reason);
                int extraStock = ExtraGlassRemaining - (kind == SavedBoardCommand.ExtraGlass ? 1 : 0);
                int shuffleStock = ShuffleRemaining - (kind == SavedBoardCommand.Shuffle ? 1 : 0);
                if (snapshot != null)
                {
                    snapshot.UndoRemaining = undoStock;
                    snapshot.ExtraGlassRemaining = extraStock;
                    snapshot.ShuffleRemaining = shuffleStock;
                    if (restoredDeadlines != null) SetSnapshotDeadlines(snapshot, restoredDeadlines);
                }

                // Called by the storage owner on the main thread, after durability and before wallet events.
                // No provisional stock, history, timers or domain revision is visible while the worker runs.
                Action<BartenderSaveResult> adopt = saved =>
                {
                    if (!saved.Succeeded || committed != null) return;
                    durable = true;
                    BsSettlementReceipt? receipt = staged.RequiresSettlement && !standalone
                        ? saved.Receipt : (BsSettlementReceipt?)null;
                    if (!ReferenceEquals(coordinator, reservedCoordinator))
                        throw new InvalidOperationException("The reserved durable board belongs to a replaced round");
                    if (!coordinator.TryCommitBoardMutation(staged, receipt, out committed, out string rejected))
                    {
                        if (!TryRecoverSavedBoardCommand(reservedCoordinator, staged, prospective,
                                receipt, out committed, out string recoveryReason))
                            throw new InvalidOperationException("A reserved durable board could not commit: "
                                + rejected + "; recovery failed: " + recoveryReason);
                        Debug.LogWarning("Recovered a saved board command after its reservation was lost: "
                            + rejected, this);
                    }
                    SyncBoardProjection();
                    undoHistory.Clear();
                    undoHistory.AddRange(history);
                    UndoRemaining = undoStock;
                    ExtraGlassRemaining = extraStock;
                    ShuffleRemaining = shuffleStock;
                    if (restoredDeadlines != null) RestoreLiveDeadlines(restoredDeadlines);
                    if (kind == SavedBoardCommand.Delivery) RefreshOrderDeadlinesAfterDelivery();
                    if (receipt.HasValue)
                    {
                        retainedSettlementReceipt = receipt;
                        announcedSettlementReceipt = null;
                        retainedSettlementRetryAt = 0f;
                    }
                };

                BartenderSaveResult save;
                if (standalone)
                {
                    if (coinCost > 0 && !BartenderProgressService.TrySpendCoins(coinCost, out reason))
                        return BartenderCommandResult<BsRoundCommit>.Rejected(reason);
                    save = new BartenderSaveResult(true, null);
                    adopt(save);
                }
                else
                {
                    ActiveRoundSnapshot frozen = snapshot;
                    Func<string> serialize = () => SerializeFrozenRound(frozen);
                    BartenderDailyActivityReceipt activity = BartenderDailyActivityReceipt.From(staged);
                    Task<BartenderSaveResult> operation;
                    if (staged.RequiresSettlement)
                    {
                        BsSettlementRequest request = staged.SettlementRequest.Value;
                        var draft = new BsSettlementDraft(request, CurrentCampaignSlot,
                            request.Completion == BsRoundCompletion.Won ? CurrentCampaignSlot + 1 : -1,
                            TimeOfferIdForCause(request.Cause));
                        operation = BartenderProgressService.StageSettlementAsync(draft, serialize,
                            coinCost, activity, adopt);
                    }
                    else operation = BartenderProgressService.CommitActiveRoundAsync(CurrentAttemptValue,
                        CurrentCampaignSlot, serialize, coinCost, activity, adopt);
                    if (!asynchronous) BartenderProgressService.CompletePendingWriteForLifecycle();
                    save = await operation;
                }
                if (!save.Succeeded) return BartenderCommandResult<BsRoundCommit>.Rejected(save.RejectionReason);
                if (committed == null)
                    throw new InvalidOperationException("The durable board was not adopted by its reserved command");
                if (this != null)
                {
                    PublishCoordinatorCommit(committed,
                        kind == SavedBoardCommand.Pour ? new BartenderPourReceipt(committed.BoardCommit) : null,
                        kind == SavedBoardCommand.Delivery ? new BartenderDeliveryReceipt(committed.BoardCommit) : null);
                    if (kind == SavedBoardCommand.Delivery || kind == SavedBoardCommand.Undo)
                        InvokeSafely(OrdersChanged);
                    InvokeSafely(BoostersChanged);
                    if (committed.Transition != null) RetryRetainedSettlement();
                    else ScheduleDeadEndProbe();
                }
                return BartenderCommandResult<BsRoundCommit>.Success(committed);
            }
            finally
            {
                if (!durable) coordinator.TryDiscard(staged);
                boardWritePending = false;
                commandInProgress = false;
                if (this != null)
                {
                    FlushPendingStateChanged();
                    if (!durable) ScheduleDeadEndProbe();
                }
            }
        }

        private bool TryRecoverSavedBoardCommand(
            BsRoundCoordinator reservedCoordinator, BsStagedBoardMutation staged,
            BsRoundSnapshot savedRound, BsSettlementReceipt? settlementReceipt,
            out BsRoundCommit committed, out string rejectionReason)
        {
            committed = null;
            // Storage has already accepted this exact detached board. A lost reservation can be repaired
            // from it, but an old callback must never replace a newer round or command authority.
            if (!ReferenceEquals(coordinator, reservedCoordinator)
                || !reservedCoordinator.IsCurrent(staged.BaseStamp)
                || reservedCoordinator.LastOperationId != staged.OperationId)
            {
                rejectionReason = "The live round advanced beyond the saved command's reservation";
                return false;
            }

            long operationFloor = Math.Max(reservedCoordinator.LastOperationId.Value,
                BartenderProgressService.SettlementOperationFloor);
            if (!BsRoundCoordinator.TryRestore(savedRound, operationFloor, settlementReceipt,
                    out BsRoundCoordinator recovered, out rejectionReason))
                return false;

            BsRoundTransition transition = null;
            BsSettlementReceipt? acceptedReceipt = null;
            if (staged.RequiresSettlement)
            {
                if (!recovered.TryGetCompletedTransition(out transition))
                {
                    rejectionReason = "The saved terminal board has no matching settlement transition";
                    return false;
                }
                acceptedReceipt = transition.SettlementReceipt;
            }
            else if (staged.ResumesRound)
            {
                transition = new BsRoundTransition(savedRound.AttemptId, staged.OperationId,
                    savedRound.Revision, savedRound.BoardRevision, savedRound.CompletionFrom,
                    savedRound.State, staged.Cause, savedRound.Completion, savedRound.Token, null);
            }

            var boardCommit = new BsBoardCommit(savedRound.AttemptId, staged.OperationId,
                savedRound.Revision, savedRound.BoardRevision, savedRound.Token, staged.Cause,
                staged.Kind, staged.DetectedCompletion, acceptedReceipt, recovered.CaptureBoard(),
                staged.PourEvidence, staged.DeliveryEvidence, staged.GlassEvidence);
            var recoveredCommit = new BsRoundCommit(staged.OperationId, savedRound.Revision,
                staged.Cause, boardCommit, transition);

            coordinator = recovered;
            committed = recoveredCommit;
            return true;
        }

        public Task<BartenderCommandResult<bool>> PurchaseTimeBoostAsync(float seconds, int coinCost) =>
            ExecuteTimeBoostAsync(seconds, coinCost, false, true);

        private async Task<BartenderCommandResult<bool>> ExecuteTimeBoostAsync(
            float seconds, int coinCost, bool authorizedOfferPurchase, bool asynchronous)
        {
            if (!CanPurchaseTimeBoost(seconds, coinCost, true, false, authorizedOfferPurchase, out string reason))
                return BartenderCommandResult<bool>.Rejected(reason);
            int[] targetOrders = timeBoostOrderScratch.ToArray();
            commandInProgress = true;
            boardWritePending = asynchronous;
            try
            {
                TimeBoostMutationSnapshot candidate = CaptureTimeBoostMutation();
                var nextHistory = new List<BoardMemento>();
                if (!authorizedOfferPurchase)
                {
                    for (int i = 0; i < candidate.UndoHistory.Length; i++)
                    {
                        BoardMemento old = candidate.UndoHistory[i];
                        nextHistory.Add(old.WithDeadlines(candidate.UndoDeadlines[i]));
                    }
                }
                var keys = new List<OrderDef>(candidate.LiveDeadlines.Keys);
                foreach (int orderIndex in targetOrders)
                {
                    candidate.TimeBonusByOrderIndex[orderIndex] += seconds;
                    foreach (OrderDef order in keys)
                        if (order.RuntimeOrderIndex == orderIndex) candidate.LiveDeadlines[order] += seconds;
                    foreach (BoardMemento history in nextHistory)
                    {
                        for (int slot = 0; slot < history.SlotDeadlines.Length; slot++)
                            if (history.Board.Slots[slot]?.RuntimeOrderIndex == orderIndex
                                && history.SlotDeadlines[slot].HasValue)
                                history.SlotDeadlines[slot] += seconds;
                    }
                }
                candidate.TimeBoostRemaining--;
                candidate.UndoHistory = nextHistory.ToArray();
                candidate.UndoDeadlines = new double?[nextHistory.Count][];
                for (int i = 0; i < nextHistory.Count; i++)
                    candidate.UndoDeadlines[i] = nextHistory[i].SlotDeadlines;

                Action<BartenderSaveResult> adopt = saved =>
                {
                    if (saved.Succeeded) RestoreTimeBoostMutation(candidate);
                };
                if (IsStandaloneRound)
                {
                    if (!BartenderProgressService.TrySpendCoins(coinCost, out reason))
                        return BartenderCommandResult<bool>.Rejected(reason);
                    adopt(new BartenderSaveResult(true, null));
                }
                else
                {
                    if (!TryCaptureActiveRoundSnapshot(coordinator.CaptureSnapshot(), boardProjection,
                            null, out ActiveRoundSnapshot snapshot, out reason, nextHistory))
                        return BartenderCommandResult<bool>.Rejected(reason);
                    snapshot.TimeBonusByOrderIndex = candidate.TimeBonusByOrderIndex;
                    snapshot.TimeBoostRemaining = candidate.TimeBoostRemaining;
                    foreach (int orderIndex in targetOrders)
                        for (int slot = 0; slot < boardProjection.Slots.Length; slot++)
                            if (boardProjection.Slots[slot]?.RuntimeOrderIndex == orderIndex
                                && snapshot.HasSlotDeadline[slot]) snapshot.SlotDeadlines[slot] += seconds;
                    Task<BartenderSaveResult> operation = BartenderProgressService.CommitActiveRoundAsync(
                        CurrentAttemptValue, CurrentCampaignSlot, () => SerializeFrozenRound(snapshot),
                        coinCost, default, adopt);
                    if (!asynchronous) BartenderProgressService.CompletePendingWriteForLifecycle();
                    BartenderSaveResult saved = await operation;
                    if (!saved.Succeeded) return BartenderCommandResult<bool>.Rejected(saved.RejectionReason);
                }
                if (this != null)
                {
                    InvokeSafely(TimeBoosted, seconds);
                    InvokeSafely(OrdersChanged);
                    InvokeSafely(BoostersChanged);
                }
                return BartenderCommandResult<bool>.Success(true);
            }
            finally
            {
                boardWritePending = false;
                commandInProgress = false;
                if (this != null) FlushPendingStateChanged();
            }
        }

        private static string SerializeFrozenRound(ActiveRoundSnapshot snapshot)
        {
            if (TrySerializeActiveRound(snapshot, out string json, out string reason)) return json;
            throw new InvalidOperationException(reason);
        }

        private void TrimUndoHistory(List<BoardMemento> history, int remainingStock) =>
            TrimUndoHistory(history, remainingStock, undoHistoryDepth);

        private static void TrimUndoHistory(List<BoardMemento> history, int remainingStock,
            int maximumHistoryDepth)
        {
            // Stock can only decrease during an attempt. Older entries can never be purchased.
            int reachable = Math.Min(Math.Max(0, maximumHistoryDepth), Math.Max(0, remainingStock));
            if (history.Count > reachable) history.RemoveRange(0, history.Count - reachable);
        }

        private async Task<BartenderCommandResult<bool>> ExecuteCompletionAsync(
            BsRoundCompletion completion, BsRoundTransitionCause cause, bool asynchronous)
        {
            if (coordinator == null)
                return BartenderCommandResult<bool>.Rejected("No level is loaded");
            if (!coordinator.TryStageCompletion(coordinator.CurrentStamp, completion, cause,
                    out BsStagedRoundCompletion staged, out string reason))
                return BartenderCommandResult<bool>.Rejected(reason);

            bool ownsCommand = !commandInProgress;
            if (ownsCommand) commandInProgress = true;
            bool priorPending = boardWritePending;
            boardWritePending = asynchronous;
            preferAsyncSettlement = asynchronous;
            bool durable = false;
            BsRoundCommit committed = null;
            try
            {
                if (!coordinator.TryCaptureProspectiveSnapshot(staged,
                        out BsRoundSnapshot prospective, out reason))
                    return BartenderCommandResult<bool>.Rejected(reason);
                bool standalone = IsStandaloneRound;
                Action<BartenderSaveResult> adopt = saved =>
                {
                    if (!saved.Succeeded || committed != null) return;
                    durable = true;
                    BsSettlementReceipt? receipt = standalone ? (BsSettlementReceipt?)null : saved.Receipt;
                    if (!coordinator.TryCommitCompletion(staged, receipt, out committed, out string rejected))
                        throw new InvalidOperationException("A reserved durable completion could not commit: " + rejected);
                    if (receipt.HasValue)
                    {
                        retainedSettlementReceipt = receipt;
                        announcedSettlementReceipt = null;
                        retainedSettlementRetryAt = 0f;
                    }
                };
                if (standalone) adopt(new BartenderSaveResult(true, null));
                else
                {
                    if (!TryCaptureActiveRoundSnapshot(prospective, boardProjection, null,
                            out ActiveRoundSnapshot snapshot, out reason))
                        return BartenderCommandResult<bool>.Rejected(reason);
                    BsSettlementRequest request = staged.SettlementRequest;
                    var draft = new BsSettlementDraft(request, CurrentCampaignSlot,
                        request.Completion == BsRoundCompletion.Won ? CurrentCampaignSlot + 1 : -1,
                        TimeOfferIdForCause(request.Cause));
                    Task<BartenderSaveResult> operation = BartenderProgressService.StageSettlementAsync(
                        draft, () => SerializeFrozenRound(snapshot), 0, default, adopt);
                    if (!asynchronous) BartenderProgressService.CompletePendingWriteForLifecycle();
                    BartenderSaveResult saved = await operation;
                    if (!saved.Succeeded) return BartenderCommandResult<bool>.Rejected(saved.RejectionReason);
                }
                if (committed == null)
                    throw new InvalidOperationException("The durable completion was not adopted");
                if (this != null)
                {
                    PublishCoordinatorCommit(committed, publishBoard: false);
                    bool timeOfferCause = cause == BsRoundTransitionCause.TimeOfferDeclinedPresentFailure
                        || cause == BsRoundTransitionCause.TimeOfferDeclinedReturnToMenu;
                    if (!timeOfferCause) RetryRetainedSettlement();
                }
                return BartenderCommandResult<bool>.Success(true);
            }
            finally
            {
                if (!durable) coordinator.TryDiscard(staged);
                boardWritePending = priorPending;
                if (ownsCommand)
                {
                    commandInProgress = false;
                    if (this != null) FlushPendingStateChanged();
                }
            }
        }

        private OrderExpiryResult BeginExpiredRoundCompletion(out string reason)
        {
            reason = null;
            if (expiryWrite != null && !expiryWrite.IsCompleted) return OrderExpiryResult.Settled;
            if (Time.unscaledTime < expiryRetryAt)
            {
                reason = "The timed-out round is waiting to retry its save";
                return OrderExpiryResult.SettlementRejected;
            }
            expiryWrite = CompleteExpiredRoundAsync();
            if (!expiryWrite.IsCompleted) return OrderExpiryResult.Settled;
            var result = expiryWrite.GetAwaiter().GetResult();
            reason = result.RejectionReason;
            return result.Succeeded ? OrderExpiryResult.Settled : OrderExpiryResult.SettlementRejected;
        }

        private async Task<BartenderCommandResult<bool>> CompleteExpiredRoundAsync()
        {
            try
            {
                var result = await ExecuteCompletionAsync(BsRoundCompletion.Failed,
                    BsRoundTransitionCause.TimedOrderExpired, true);
                if (this != null) expiryRetryAt = result.Succeeded ? 0f : Time.unscaledTime + 1f;
                return result;
            }
            catch (Exception exception)
            {
                if (this != null)
                {
                    expiryRetryAt = Time.unscaledTime + 1f;
                    Debug.LogException(exception, this);
                }
                return BartenderCommandResult<bool>.Rejected("The timed-out round could not be saved");
            }
        }

        private static void SetSnapshotDeadlines(ActiveRoundSnapshot snapshot, double?[] deadlines)
        {
            snapshot.SlotDeadlines = new double[deadlines.Length];
            snapshot.HasSlotDeadline = new bool[deadlines.Length];
            for (int i = 0; i < deadlines.Length; i++)
            {
                snapshot.HasSlotDeadline[i] = deadlines[i].HasValue;
                snapshot.SlotDeadlines[i] = deadlines[i] ?? 0d;
            }
        }

        internal Task<bool> RetryTerminalSettlementAsync()
        {
            if (!HasPendingTerminalSettlement)
                return Task.FromResult(IsTerminalSettlementCommitted);
            if (settlementWrite == null || settlementWrite.IsCompleted)
                settlementWrite = SettleRetainedAsync(retainedSettlementReceipt.Value);
            return settlementWrite;
        }

        private bool RequestRetainedSettlement()
        {
            if (timeOfferMachine.State == BsTimeOfferState.DeclineOutboxCommitting) return false;
            if (!preferAsyncSettlement) return RetryRetainedSettlementImmediate();
            if (!retainedSettlementReceipt.HasValue) return true;
            if (IsTerminalSettlementCommitted) return RetryRetainedSettlementImmediate();
            if (settlementWrite != null && !settlementWrite.IsCompleted) return false;
            settlementWrite = SettleRetainedAsync(retainedSettlementReceipt.Value);
            return settlementWrite.IsCompleted && settlementWrite.GetAwaiter().GetResult();
        }

        private async Task<bool> SettleRetainedAsync(BsSettlementReceipt expected)
        {
            try
            {
                BartenderSaveResult saved = await BartenderProgressService.CommitSettlementAsync(expected);
                if (this == null || !retainedSettlementReceipt.HasValue
                    || retainedSettlementReceipt.Value != expected) return saved.Succeeded;
                if (saved.Succeeded) return RetryRetainedSettlementImmediate();
                retainedSettlementRetryAt = Time.unscaledTime + 1f;
                return false;
            }
            catch (Exception exception)
            {
                if (this != null)
                {
                    retainedSettlementRetryAt = Time.unscaledTime + 1f;
                    Debug.LogException(exception, this);
                }
                return false;
            }
        }
    }
}
