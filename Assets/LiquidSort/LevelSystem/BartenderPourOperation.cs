using System;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    internal sealed class BartenderPourOperation
    {
        internal BsPourOperationStateMachine Lifecycle { get; }
        public long RunId => Lifecycle.RunId;
        public bool IsUndo => Lifecycle.IsUndo;
        public int SourceGlassId => Lifecycle.SourceGlassId;
        public int TargetGlassId => Lifecycle.TargetGlassId;
        public BsPourOperationState State => Lifecycle.State;
        public double Deadline => Lifecycle.Deadline;
        public bool PersistencePending => Lifecycle.IsPersistencePending;
        public int AnimationOperationId => Lifecycle.AnimationOperationId;
        public object Owner { get; }
        public BartenderLevelController Controller { get; }
        public BartenderShelfLevelView View { get; }
        public BsShelfSynchronizationLease SynchronizationLease { get; }
        public PourAnimator Animator { get; }
        public BartenderSession Session { get; }
        public BsRoundCommandStamp StartStamp { get; }
        public BartenderPourReceipt Receipt { get; private set; }
        public BartenderBoardChange UndoChange { get; private set; }
        public Action<int, PourOutcome> PourFinishedHandler { get; private set; }
        public int CompletionGlassId { get; private set; } = -1;
        public int LockedRevision { get; private set; } = -1;
        public BsOperationId OperationId => Receipt != null ? Receipt.OperationId
            : UndoChange != null ? UndoChange.OperationId : default;
        public BsRoundToken RoundToken => Receipt != null ? Receipt.Token
            : UndoChange != null ? UndoChange.Token : StartStamp.Token;
        public long DomainRevision => Receipt != null ? Receipt.DomainRevision
            : UndoChange != null ? UndoChange.DomainRevision : StartStamp.Revision;
        public int BoardRevision => Receipt != null ? Receipt.BoardRevision
            : UndoChange != null ? UndoChange.BoardRevision : StartStamp.BoardRevision;

        public BartenderPourOperation(long runId, object owner, BartenderLevelController controller,
            BartenderShelfLevelView view, BsShelfSynchronizationLease lease, PourAnimator animator,
            BartenderSession session, BsRoundCommandStamp startStamp, int sourceId, int targetId,
            bool isUndo, double now, double timeout)
        {
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Controller = controller;
            View = view;
            SynchronizationLease = lease;
            Animator = animator;
            Session = session;
            StartStamp = startStamp;
            Lifecycle = new BsPourOperationStateMachine(runId, sourceId, targetId, isUndo, now, timeout);
            if (view == null || !view.BindSynchronizationLifecycle(owner, lease, Lifecycle))
                throw new InvalidOperationException("The pour operation does not own its shelf lease.");
        }

        public bool TryBeginSaving() => Lifecycle.TryBeginSaving();

        public bool TryCommitPour(BartenderPourReceipt receipt, int completionGlassId, double now)
        {
            if (IsUndo || receipt == null || receipt.SourceAfter == null || receipt.TargetAfter == null
                || receipt.SourceAfter.Id != SourceGlassId || receipt.TargetAfter.Id != TargetGlassId
                || receipt.Cause != BsRoundTransitionCause.PlayerPour
                || receipt.AttemptId != StartStamp.AttemptId || receipt.Token != StartStamp.Token
                || !StartStamp.IsValid || !receipt.OperationId.IsValid || receipt.Amount <= 0
                || receipt.DomainRevision <= StartStamp.Revision
                || receipt.BoardRevision <= StartStamp.BoardRevision
                || !Lifecycle.TryCommit(now)) return false;
            Receipt = receipt;
            CompletionGlassId = completionGlassId;
            return true;
        }

        public bool TryCommitUndo(BartenderBoardChange change, double now)
        {
            if (!IsUndo || change == null || change.Cause != BsRoundTransitionCause.PlayerUndo
                || change.AttemptId != StartStamp.AttemptId || change.Token != StartStamp.Token
                || !StartStamp.IsValid || !change.OperationId.IsValid
                || change.DomainRevision <= StartStamp.Revision
                || change.BoardRevision <= StartStamp.BoardRevision
                || !Lifecycle.TryCommit(now)) return false;
            UndoChange = change;
            return true;
        }

        public bool TryAcquirePresentationLock()
        {
            if (State != BsPourOperationState.Committed || LockedRevision >= 0 || Controller == null)
                return false;
            bool acquired = IsUndo
                ? Controller.TryAcquireAnimationPresentationLock(Owner, BoardRevision)
                : Controller.TryAcquirePourPresentationLock(Owner, BoardRevision, SourceGlassId, TargetGlassId);
            if (acquired) LockedRevision = BoardRevision;
            return acquired;
        }

        public bool TryStartAnimation(LiquidBottle source, LiquidBottle target, int amount,
            BartenderGlassSeatPose home, Action<BartenderPourOperation, PourOutcome> finished)
        {
            if (State != BsPourOperationState.Committed || LockedRevision < 0
                || Animator == null || finished == null) return false;
            if (!Animator.TryStartPour(source, target, amount, home.MotionRoot,
                    home.Position, home.Rotation, home.LocalScale, false)) return false;

            int operationId = Animator.ActiveOperationId;
            if (!Lifecycle.TryBeginAnimation(operationId))
            {
                // Starting an animator can invoke external code. A cancelled operation must not revive.
                if (operationId != 0 && Animator.ActiveOperationId == operationId)
                    Animator.CancelActivePour();
                return false;
            }
            Action<int, PourOutcome> handler = null;
            handler = (completedId, outcome) =>
            {
                if (State != BsPourOperationState.Animating || completedId != operationId
                    || AnimationOperationId != operationId
                    || !ReferenceEquals(PourFinishedHandler, handler)) return;
                finished(this, outcome);
            };
            PourFinishedHandler = handler;
            Animator.PourFinished += handler;
            return true;
        }

        public bool HasExpired(double now) => Lifecycle.HasExpired(now);

        public bool TrySettle(BsPourSettlementReason reason, bool refresh, bool cancelAnimator,
            Action<BartenderPourOperation> onSettling)
        {
            if (!Lifecycle.TryBeginSettlement(reason)) return false;
            Action<int, PourOutcome> handler = PourFinishedHandler;
            PourFinishedHandler = null;
            int lockedRevision = LockedRevision;
            LockedRevision = -1;
            Exception firstFailure = null;
            void Release(Action action)
            {
                try { action(); }
                catch (Exception exception) { if (firstFailure == null) firstFailure = exception; }
            }
            try
            {
                Release(() => onSettling?.Invoke(this));
                Release(() => { if (handler != null && Animator != null) Animator.PourFinished -= handler; });
                Release(() =>
                {
                    if (cancelAnimator && AnimationOperationId != 0 && Animator != null
                        && Animator.ActiveOperationId == AnimationOperationId) Animator.CancelActivePour();
                });
                Release(() =>
                {
                    if (View == null || !View.IsSynchronizationDeferredBy(Owner, SynchronizationLease)) return;
                    if (refresh) View.EndSynchronizationDeferralAndRefresh(Owner, SynchronizationLease, true);
                    else View.DropSynchronizationDeferral(Owner, SynchronizationLease);
                });
                Release(() =>
                {
                    if (Controller != null && lockedRevision >= 0)
                        Controller.ReleasePresentationLock(Owner, lockedRevision);
                });
            }
            finally
            {
                Lifecycle.CompleteSettlement();
            }
            if (firstFailure != null) Debug.LogException(firstFailure, Controller);
            return true;
        }
    }
}
