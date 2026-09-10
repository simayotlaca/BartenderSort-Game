using System;
using System.Collections.Generic;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Commits moves through the controller, then animates the old shelf view. Completion and cancellation
    /// restore the committed snapshot; views cannot roll back rules.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderPourInteraction : MonoBehaviour
    {
        internal const double PresentationTransactionTimeoutSeconds = 8d;
        internal const double CompletionTailTimeoutSeconds = 6d;

        internal sealed class PresentationTransactionContext
        {
            public readonly long RunId;
            public readonly object Owner;
            public readonly BartenderLevelController Controller;
            public readonly BartenderShelfLevelView View;
            public readonly BsShelfSynchronizationLease SynchronizationLease;
            public readonly PourAnimator Animator;
            public readonly BartenderSession Session;
            public readonly BsRoundCommandStamp StartStamp;
            public readonly double Deadline;

            public BartenderPourReceipt Receipt;
            public bool IsUndo;
            public BartenderBoardChange UndoChange;
            public Action<int, PourOutcome> PourFinishedHandler;
            public int AnimationOperationId;
            public int CompletionGlassId = -1;
            public int LockedRevision = -1;
            public BsOperationId OperationId => Receipt != null
                ? Receipt.OperationId
                : UndoChange != null ? UndoChange.OperationId : default;
            public BsRoundToken RoundToken => Receipt != null
                ? Receipt.Token
                : UndoChange != null ? UndoChange.Token : StartStamp.Token;
            public long DomainRevision => Receipt != null
                ? Receipt.DomainRevision
                : UndoChange != null ? UndoChange.DomainRevision : StartStamp.Revision;
            public int BoardRevision => Receipt != null
                ? Receipt.BoardRevision
                : UndoChange != null ? UndoChange.BoardRevision : StartStamp.BoardRevision;

            public PresentationTransactionContext(
                long runId,
                object owner,
                BartenderLevelController controller,
                BartenderShelfLevelView view,
                BsShelfSynchronizationLease synchronizationLease,
                PourAnimator animator,
                BartenderSession session,
                BsRoundCommandStamp startStamp,
                double deadline)
            {
                RunId = runId;
                Owner = owner;
                Controller = controller;
                View = view;
                SynchronizationLease = synchronizationLease;
                Animator = animator;
                Session = session;
                StartStamp = startStamp;
                Deadline = deadline;
            }
        }

        internal sealed class CompletionTailContext
        {
            public readonly long RunId;
            public readonly BartenderLevelController Controller;
            public readonly BartenderShelfLevelView View;
            public readonly BartenderPourReceipt Receipt;
            public readonly int GlassId;
            public readonly LiquidBottle Bottle;
            public readonly BottleShell Shell;
            public readonly BottleCompletionEffect Effect;
            public readonly DeliveryBadgePresenter Badges;
            public readonly double Deadline;
            public readonly BartenderGlassSeatPose? SeatPose;

            public CompletionTailContext(
                long runId,
                BartenderLevelController controller,
                BartenderShelfLevelView view,
                BartenderPourReceipt receipt,
                int glassId,
                LiquidBottle bottle,
                BottleShell shell,
                BottleCompletionEffect effect,
                DeliveryBadgePresenter badges,
                double deadline,
                BartenderGlassSeatPose? seatPose = null)
            {
                RunId = runId;
                Controller = controller;
                View = view;
                Receipt = receipt;
                GlassId = glassId;
                Bottle = bottle;
                Shell = shell;
                Effect = effect;
                Badges = badges;
                Deadline = deadline;
                SeatPose = seatPose;
            }
        }

        [Header("Required rig references")]
        [SerializeField] private BartenderLevelController controller;
        [SerializeField] private BartenderShelfLevelView shelfView;
        [SerializeField] private PourAnimator pourAnimator;
        [Tooltip("Round-token owner. Empty resolves the BartenderSession on this rig.")]
        [SerializeField] private BartenderSession session;
        [Tooltip("Optional. Empty resolves the OrderStripPresenter on this rig.")]
        [SerializeField] private OrderStripPresenter orderStrip;

        [Header("Completion effect")]
        [Tooltip("Authored template cloned into a small reusable pool so different glasses can finish independently.")]
        [SerializeField] private BottleCompletionEffect glassCompletionEffect;

        [Header("Host scene")]
        [Tooltip("Optional. A portable prefab resolves Camera.main when this is empty.")]
        [SerializeField] private Camera inputCamera;

        [Header("Pointer feel")]
        [SerializeField, Min(0f)] private float pickPadding = 0.22f;
        [SerializeField, Min(0f)] private float selectionLift = 0.38f;
        [SerializeField, Range(1f, 1.08f)] private float selectionScale = 1.05f;
        [SerializeField, Min(0.01f)] private float selectionSpeed = 18f;

        [Header("Rejected move feel")]
        [SerializeField, Min(0.08f)] private float rejectionDuration = 0.28f;
        [SerializeField, Range(0f, 12f)] private float sourceRejectionWobble = 3.5f;
        [SerializeField, Range(0f, 12f)] private float targetRejectionWobble = 6f;
        [SerializeField, Range(0f, 1f)] private float rejectionHighlightAlpha = 0.58f;
        [SerializeField] private Color rejectedSourceColor =
            new Color(1f, 0.63f, 0.16f, 1f);
        [SerializeField] private Color rejectedTargetColor =
            new Color(1f, 0.20f, 0.18f, 1f);

        private BartenderLevelController subscribedController;
        private BartenderShelfLevelView subscribedView;
        private IBartenderInputPolicy inputPolicy;

        private LiquidBottle selectedBottle;
        private Transform selectedMotionRoot;
        private int selectedGlassId = -1;
        private Vector3 selectedHomePosition;
        private Quaternion selectedHomeRotation = Quaternion.identity;
        private Vector3 selectedHomeScale = Vector3.one;
        private float selectedRoyalRelativeScale = 1f;

        private PresentationTransactionContext activeTransaction;
        private long nextPresentationRunId;
        private long nextCompletionTailRunId;
        private readonly Dictionary<int, CompletionTailContext> completionTails =
            new Dictionary<int, CompletionTailContext>();
        private readonly Stack<BottleCompletionEffect> completionEffectPool =
            new Stack<BottleCompletionEffect>();
        private readonly HashSet<BottleCompletionEffect> ownedCompletionEffects =
            new HashSet<BottleCompletionEffect>();
        private readonly List<CompletionTailContext> completionTailScratch =
            new List<CompletionTailContext>(8);

        private int ActiveAnimationOperationId => activeTransaction == null
            ? 0
            : activeTransaction.AnimationOperationId;

        public bool Busy => activeTransaction != null
                         || (pourAnimator != null && pourAnimator.Busy)
                         || (orderStrip != null && orderStrip.TransitionPlaying);
        internal int ActiveCompletionTailCount => completionTails.Count;
        internal PresentationTransactionContext ActiveTransaction => activeTransaction;
        public string LastRejection { get; private set; }
        public BartenderLevelController Controller => controller;
        public BartenderShelfLevelView ShelfView => shelfView;
        public PourAnimator Animator => pourAnimator;
        public BartenderSession Session => session;
        public Camera InputCamera => ResolveCamera();

        /// <summary>Raised only when the selected domain glass id actually changes.</summary>
        public event Action<int> SelectionChanged;

        /// <summary>
        /// Acquires shared modal input without taking it from another tutorial, accessibility or demo flow.
        /// </summary>
        public bool TrySetInputPolicy(IBartenderInputPolicy policy)
        {
            if (policy == null) return false;
            DropDestroyedInputPolicy();
            if (inputPolicy != null && !ReferenceEquals(inputPolicy, policy)) return false;
            inputPolicy = policy;
            return true;
        }

        public bool ClearInputPolicy(IBartenderInputPolicy policy)
        {
            if (policy == null || !ReferenceEquals(inputPolicy, policy)) return false;
            inputPolicy = null;
            return true;
        }

        /// <summary>
        /// Clears pointer state for a modal without changing the board. Still publishes the selection
        /// change.
        /// </summary>
        public void ClearSelectionForModal() => ClearSelection(true);

        private void Awake() => ResolveDependencies();

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
        }

        private void OnDisable()
        {
            PresentationTransactionContext transaction = activeTransaction;
            Exception cleanupFailure = ExecuteLifecycleCleanup(
                () =>
                {
                    bool refresh = false;
                    try
                    {
                        refresh = CanReconcileTransactionBoard(transaction);
                    }
                    finally
                    {
                        // Try releasing this transaction's deferral and lock even if the round check throws
                        // during disable.
                        TrySettlePresentationTransaction(transaction, refresh, true);
                    }
                },
                Unsubscribe,
                () => CancelAllCompletionTails(true),
                () => CancelRejectionFeedbacks(true),
                () => ClearSelection(true),
                () => inputPolicy = null);
            if (cleanupFailure != null) Debug.LogException(cleanupFailure, this);
        }

        private void OnDestroy()
        {
            CancelAllCompletionTails(false);
            foreach (BottleCompletionEffect effect in ownedCompletionEffects)
            {
                if (effect != null) Destroy(effect.gameObject);
            }
            ownedCompletionEffects.Clear();
            completionEffectPool.Clear();
        }

        private void OnValidate()
        {
            pickPadding = Mathf.Max(0f, pickPadding);
            selectionLift = Mathf.Max(0f, selectionLift);
            selectionScale = Mathf.Clamp(selectionScale, 1f, 1.08f);
            selectionSpeed = Mathf.Max(0.01f, selectionSpeed);
            rejectionDuration = Mathf.Max(0.08f, rejectionDuration);
            sourceRejectionWobble = Mathf.Clamp(sourceRejectionWobble, 0f, 12f);
            targetRejectionWobble = Mathf.Clamp(targetRejectionWobble, 0f, 12f);
            rejectionHighlightAlpha = Mathf.Clamp01(rejectionHighlightAlpha);
        }

        private void Update()
        {
            TrySettleExpiredPresentations(Time.realtimeSinceStartupAsDouble);
            AnimateSelection();
            if (TryHandleTerminalPointer()) return;
            if (!CanReadPointer()
                || !TryReadPointerDown(out Vector2 screenPoint, out int pointerId)) return;
            BoosterTrayInput boosterTray = BoosterTrayInput.Current;
            if (boosterTray != null
                && boosterTray.gameObject.scene == gameObject.scene
                && boosterTray.OwnsScreenPoint(ResolveCamera(), screenPoint))
                return;
            if (BartenderUiPointerGuard.IsPointerOverUi(screenPoint, pointerId)) return;
            HandlePointerDown(screenPoint);
        }

        /// <summary>
        /// Pays for one undo, then pours the restored amount back while the shelf still shows the old
        /// board. Delivery undo uses the shelf's existing glass-return and reseating animation.
        /// </summary>
        public bool TryPurchaseAndAnimateUndo(out string rejectionReason)
        {
            rejectionReason = null;
            LastRejection = null;
            ResolveDependencies();
            if (!isActiveAndEnabled || controller == null || shelfView == null
                || pourAnimator == null || session == null
                || !ReferenceEquals(session.Controller, controller))
                return Reject("Geri alma sunum bağlantısı eksik.", out rejectionReason);
            if (!session.AcceptsInput || !shelfView.Ready || shelfView.SeatAnimationPlaying
                || shelfView.SynchronizationDeferred || Busy || activeTransaction != null
                || controller.PresentationLocked)
                return Reject("Sahne başka bir sunum animasyonuyla meşgul.", out rejectionReason);
            if (!controller.CanPurchaseUndo(out rejectionReason)) return false;

            // Return selected/lifted glasses to their real seats before reusing the pour animator.
            CancelAllCompletionTails(true);
            CancelRejectionFeedbacks(true);
            ClearSelection(true);
            BsBoard before = controller.Board;
            object owner = new object();
            if (!shelfView.TryBeginSynchronizationDeferral(
                    owner, out BsShelfSynchronizationLease lease))
                return Reject("Bardak görünümü başka bir senkronizasyonu bekliyor.",
                    out rejectionReason);
            PresentationTransactionContext transaction = BeginPresentationTransaction(
                controller, shelfView, pourAnimator, lease, owner);
            transaction.IsUndo = true;

            bool committed;
            try
            {
                committed = transaction.Controller.TryPurchaseUndo(out rejectionReason);
            }
            catch
            {
                TrySettlePresentationTransaction(
                    transaction, IsTransactionRoundCurrent(transaction), true);
                throw;
            }
            if (!committed)
            {
                TrySettlePresentationTransaction(transaction, false, false);
                return false;
            }

            bool animationStarted = false;
            try
            {
                // Commit callbacks can disable this component or replace the round. Never animate an old
                // purchase onto the new board, and never charge again if the visual cannot start.
                if (transaction.UndoChange == null
                    || !CanContinueTransaction(transaction, controller, shelfView, pourAnimator))
                    return true;
                BsBoard restored = transaction.Controller.Board;
                if (!TryResolveUndoPour(before, restored,
                        out int sourceId, out int targetId, out int amount))
                    return true;
                if (!transaction.View.TryGetBottle(sourceId, out LiquidBottle source)
                    || !transaction.View.TryGetBottle(targetId, out LiquidBottle target)
                    || !transaction.View.TryGetSeatPose(sourceId, out BartenderGlassSeatPose home)
                    || source.UnitCount != before.GlassById(sourceId).Layers.Count
                    || target.UnitCount != before.GlassById(targetId).Layers.Count
                    || source.TopRunLength < amount || target.FreeSpace < amount)
                    return true;
                if (!transaction.Controller.TryAcquirePresentationLock(
                        transaction.Owner, transaction.BoardRevision))
                    return true;
                transaction.LockedRevision = transaction.BoardRevision;
                // Undo may restore liquid over a different colour or a previously hidden layer. The exact
                // committed checkpoint, including hidden/locked flags, is reapplied after the motion.
                if (!transaction.Animator.TryStartPour(source, target, amount, home.MotionRoot,
                        home.Position, home.Rotation, home.LocalScale, false))
                    return true;
                int operationId = transaction.Animator.ActiveOperationId;
                animationStarted = TryBindPourFinished(
                    transaction, transaction.Animator, operationId);
                if (!animationStarted)
                    transaction.AnimationOperationId = operationId;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
            finally
            {
                if (!animationStarted)
                    TrySettlePresentationTransaction(
                        transaction, IsTransactionRoundCurrent(transaction), true);
            }
            return true;
        }

        /// <summary>Uses saved board snapshots, so undo presentation also works after resuming a round.</summary>
        private static bool TryResolveUndoPour(BsBoard before, BsBoard restored,
            out int sourceId, out int targetId, out int amount)
        {
            sourceId = targetId = -1;
            amount = 0;
            if (before == null || restored == null || before.Delivered != restored.Delivered
                || before.Glasses.Count != restored.Glasses.Count) return false;
            int received = 0;
            for (int i = 0; i < before.Glasses.Count; i++)
            {
                RtGlass previous = before.Glasses[i];
                RtGlass next = restored.GlassById(previous.Id);
                if (next == null || previous.Type != next.Type) return false;
                int delta = previous.Layers.Count - next.Layers.Count;
                if (delta > 0)
                {
                    if (sourceId >= 0) return false;
                    sourceId = previous.Id;
                    amount = delta;
                }
                else if (delta < 0)
                {
                    if (targetId >= 0) return false;
                    targetId = previous.Id;
                    received = -delta;
                }
            }
            return sourceId >= 0 && targetId >= 0 && amount > 0 && amount == received;
        }

        /// <summary>
        /// Returns true once the move commits. If animation cannot start, snap to that result and still
        /// return true.
        /// </summary>
        public bool TryCommitAndAnimatePour(int sourceGlassId, int targetGlassId,
                                            out string rejectionReason)
        {
            rejectionReason = null;
            LastRejection = null;
            ResolveDependencies();

            if (!isActiveAndEnabled)
                return Reject("Gameplay sunumu şu anda etkin değil.", out rejectionReason);

            if (!CheckInputPolicy(
                    BartenderInputRequest.Pour(sourceGlassId, targetGlassId),
                    out rejectionReason))
                return false;

            if (controller == null || shelfView == null || pourAnimator == null
                || session == null)
                return Reject("Gameplay rig controller/view/animator/session bağlantısı eksik.",
                              out rejectionReason);
            if (!ReferenceEquals(session.Controller, controller))
                return Reject("Tur FSM'i farklı bir level controller'a bağlı.",
                              out rejectionReason);
            if (!session.AcceptsInput)
                return Reject("Tur FSM'i şu anda gameplay komutu kabul etmiyor.",
                              out rejectionReason);
            if (!shelfView.Ready || shelfView.SeatAnimationPlaying
                || shelfView.SynchronizationDeferred || Busy
                || controller.PresentationLocked)
                return Reject("Sahne başka bir sunum animasyonuyla meşgul.",
                              out rejectionReason);
            if (!shelfView.TryGetBottle(sourceGlassId, out LiquidBottle source)
                || !shelfView.TryGetBottle(targetGlassId, out LiquidBottle target))
                return Reject("Bardakların aktif sahne bağlantısı bulunamadı.",
                              out rejectionReason);
            if (HasCompletionTail(sourceGlassId)
                || HasCompletionTail(targetGlassId))
                return Reject("Bu bardaktaki tamamlanma sunumu henüz bitmedi.",
                              out rejectionReason);

            PourResult rule = controller.CanPour(sourceGlassId, targetGlassId);
            if (!rule.Success)
            {
                BsAudio.Instance?.Play(BsSfx.Invalid);
                PlayRejectedPourFeedback(source, target);
                return Reject(rule.Reason, out rejectionReason);
            }

            // Stop only our feedback tweens and restore rotations before PourAnimator takes these roots. Do
            // not use transform.DOKill.
            CancelRejectionFeedback(source, true);
            CancelRejectionFeedback(target, true);

            if (!shelfView.TryGetSeatPose(sourceGlassId,
                    out BartenderGlassSeatPose home))
                return Reject("Kaynak bardağın raf oturma pozu bulunamadı.",
                              out rejectionReason);

            object transactionOwner = new object();
            if (!shelfView.TryBeginSynchronizationDeferral(
                    transactionOwner,
                    out BsShelfSynchronizationLease synchronizationLease))
                return Reject("Bardak görünümü başka bir senkronizasyonu bekliyor.",
                              out rejectionReason);

            BartenderLevelController committedController = controller;
            BartenderShelfLevelView deferredView = shelfView;
            PourAnimator selectedAnimator = pourAnimator;
            PresentationTransactionContext transaction = BeginPresentationTransaction(
                committedController, deferredView, selectedAnimator,
                synchronizationLease, transactionOwner);

            BartenderPourReceipt receipt;
            string domainRejection;
            bool committed;
            try
            {
                committed = committedController.TryPour(
                    sourceGlassId, targetGlassId, out receipt, out domainRejection);
            }
            catch
            {
                TrySettlePresentationTransaction(transaction, true, false);
                throw;
            }
            if (!committed)
            {
                TrySettlePresentationTransaction(transaction, false, false);
                BsAudio.Instance?.Play(BsSfx.Invalid);
                PlayRejectedPourFeedback(source, target);
                return Reject(domainRejection, out rejectionReason);
            }

            CaptureTransactionPourReceipt(transaction, receipt);

            // TryPour callbacks may disable this bridge and clean up the committed move. Recheck before
            // creating another lock.
            if (!CanContinueTransaction(transaction, committedController,
                                        deferredView, selectedAnimator))
            {
                bool stillOwnsTransaction =
                    ReferenceEquals(activeTransaction, transaction);
                if (stillOwnsTransaction)
                {
                    TrySettlePresentationTransaction(
                        transaction, IsTransactionRoundCurrent(transaction), false);
                    ClearSelection(true);
                }
                LastRejection = "Dökme kaydedildi; sahne değiştiği için sonuç anında gösterildi.";
                rejectionReason = LastRejection;
                return true;
            }

            // A pour removes liquid from its source, so only the receiving glass can newly match a full
            // order.
            int completionGlassId = BecameMatched(
                committedController, receipt.TargetBefore, receipt.TargetAfter)
                ? targetGlassId
                : -1;

            if (!committedController.TryAcquirePresentationLock(
                    transaction.Owner, receipt.Revision))
            {
                HoldCompletionBadge(deferredView, completionGlassId);
                TrySettlePresentationTransaction(transaction, true, false);
                if (CanStartCompletionPresentation(
                        committedController, deferredView, completionGlassId, receipt))
                    TryStartCompletionTail(deferredView, completionGlassId, receipt);
                else if (IsCurrentPourReceipt(committedController, receipt))
                    ReconcileCompletionBadge(deferredView, completionGlassId);
                LastRejection = "Dökme kaydedildi; sunum kilidi alınamadığı için anında gösterildi.";
                rejectionReason = LastRejection;
                ClearSelection(true);
                return true;
            }

            transaction.LockedRevision = receipt.Revision;
            transaction.CompletionGlassId = completionGlassId;
            // Different colours may stack under BsBoard.CanPour, so this must pass false.
            bool animationStarted;
            try
            {
                animationStarted = selectedAnimator.TryStartPour(
                    source, target, receipt.Amount, home.MotionRoot,
                    home.Position, home.Rotation, home.LocalScale, false);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                animationStarted = false;
            }
            if (!animationStarted)
            {
                int pendingGlassId = transaction.CompletionGlassId;
                HoldCompletionBadge(deferredView, pendingGlassId);
                TrySettlePresentationTransaction(transaction, true, false);
                if (CanStartCompletionPresentation(
                        committedController, deferredView, pendingGlassId, receipt))
                    TryStartCompletionTail(deferredView, pendingGlassId, receipt);
                else if (IsCurrentPourReceipt(committedController, receipt))
                    ReconcileCompletionBadge(deferredView, pendingGlassId);
                LastRejection = "Dökme kaydedildi; animasyon başlayamadığı için sonuç gösterildi.";
                rejectionReason = LastRejection;
                ClearSelection(true);
                return true;
            }

            int startedOperationId = selectedAnimator.ActiveOperationId;
            if (!TryBindPourFinished(
                    transaction,
                    selectedAnimator,
                    startedOperationId))
            {
                // A failed subscription still owns the animation it just started. Retain its exact ID so
                // cancellation cannot leave its reservation/tween alive after releasing the shelf hold.
                transaction.AnimationOperationId = startedOperationId;
                int pendingGlassId = transaction.CompletionGlassId;
                HoldCompletionBadge(deferredView, pendingGlassId);
                TrySettlePresentationTransaction(transaction, true, true);
                if (CanStartCompletionPresentation(
                        committedController, deferredView, pendingGlassId, receipt))
                    TryStartCompletionTail(deferredView, pendingGlassId, receipt);
                else if (IsCurrentPourReceipt(committedController, receipt))
                    ReconcileCompletionBadge(deferredView, pendingGlassId);
                LastRejection = "Dökme kaydedildi; animasyon callback'i kurulamadığı için sonuç gösterildi.";
                rejectionReason = LastRejection;
                ClearSelection(true);
                return true;
            }
            ClearSelection(false);
            return true;
        }

        /// <summary>
        /// Commits delivery after the badge is ready. The synchronous shelf refresh removes the glass and
        /// badge together.
        /// </summary>
        public bool TryCommitDelivery(int glassId, out string rejectionReason)
        {
            rejectionReason = null;
            LastRejection = null;
            ResolveDependencies();

            if (!isActiveAndEnabled)
                return Reject("Gameplay sunumu şu anda etkin değil.", out rejectionReason);

            if (!CheckInputPolicy(BartenderInputRequest.Delivery(glassId),
                                  out rejectionReason))
                return false;

            if (controller == null || shelfView == null || session == null)
                return Reject("Gameplay rig controller/view/session bağlantısı eksik.",
                              out rejectionReason);
            if (!ReferenceEquals(session.Controller, controller))
                return Reject("Tur FSM'i farklı bir level controller'a bağlı.",
                              out rejectionReason);
            if (!session.AcceptsInput)
                return Reject("Tur FSM'i şu anda gameplay komutu kabul etmiyor.",
                              out rejectionReason);
            if (!shelfView.Ready || shelfView.SeatAnimationPlaying
                || shelfView.SynchronizationDeferred || Busy
                || controller.PresentationLocked)
                return Reject("Sahne başka bir sunum animasyonuyla meşgul.",
                              out rejectionReason);
            if (!shelfView.TryGetBottle(glassId, out LiquidBottle deliveryBottle))
                return Reject("Bardağın aktif sahne bağlantısı bulunamadı.",
                              out rejectionReason);
            if (HasCompletionTail(glassId))
                return Reject("Tamamlanma onayı henüz hazır değil.",
                              out rejectionReason);
            if (controller.MatchedOrderSlot(glassId) < 0)
            {
                BsAudio.Instance?.Play(BsSfx.Invalid);
                PlayRejectionFeedback(deliveryBottle, rejectedTargetColor,
                    targetRejectionWobble);
                return Reject("Bardak açık bir siparişi karşılamıyor.",
                              out rejectionReason);
            }
            DeliveryBadgePresenter deliveryBadges =
                ResolveDeliveryBadges(shelfView);
            if (deliveryBadges != null
                && !deliveryBadges.IsReadyForDelivery(deliveryBottle))
                return Reject("Tamamlanma onayı henüz hazır değil.",
                              out rejectionReason);

            CancelRejectionFeedback(deliveryBottle, true);
            ClearSelection(true);
            bool committed = controller.TryDeliver(
                glassId, out _, out string domainRejection);
            if (!committed)
            {
                BsAudio.Instance?.Play(BsSfx.Invalid);
                PlayRejectionFeedback(deliveryBottle, rejectedTargetColor,
                    targetRejectionWobble);
                return Reject(domainRejection, out rejectionReason);
            }
            return true;
        }

        private bool CanReadPointer()
        {
            return controller != null && shelfView != null && pourAnimator != null
                && session != null
                && ReferenceEquals(session.Controller, controller)
                && controller.State == BartenderLevelState.Playing
                && session.AcceptsInput
                && CanReadThroughOwnedModalBarrier()
                && shelfView.Ready
                && !shelfView.SeatAnimationPlaying
                && !shelfView.SynchronizationDeferred
                && !Busy;
        }

        private bool CanReadThroughOwnedModalBarrier()
        {
            if (controller == null || !controller.PresentationLocked) return true;
            DropDestroyedInputPolicy();
            return inputPolicy != null
                && controller.IsPresentationBarrierExclusivelyOwnedBy(inputPolicy);
        }

        /// <summary>
        /// World-tap fallback for scenes without result UI. Require a new tap after the result animation and
        /// check frame/token to prevent duplicate navigation.
        /// </summary>
        private bool TryHandleTerminalPointer()
        {
            if (controller == null || shelfView == null || session == null
                || !ReferenceEquals(session.Controller, controller))
                return false;

            bool continueAfterWin = session.CanContinueAfterWin;
            bool retryAfterFailure = session.CanRetryAfterFailure;
            if (!continueAfterWin && !retryAfterFailure) return false;
            if (Busy || controller.PresentationLocked || !shelfView.Ready
                || shelfView.SeatAnimationPlaying || shelfView.SynchronizationDeferred)
                return false;
            if (!TryReadPointerDown(out Vector2 screenPoint, out int pointerId))
                return false;

            // Let result buttons own their clicks; the world fallback must not reuse them.
            if (BartenderUiPointerGuard.IsPointerOverUi(screenPoint, pointerId)) return true;
            BoosterTrayInput boosterTray = BoosterTrayInput.Current;
            if (boosterTray != null
                && boosterTray.gameObject.scene == gameObject.scene
                && boosterTray.OwnsScreenPoint(ResolveCamera(), screenPoint))
                return true;
            if (!CheckInputPolicy(
                    BartenderInputRequest.Background(selectedGlassId), out _))
                return true;

            bool accepted = continueAfterWin
                ? session.RequestContinueAfterWin()
                : session.RequestRetryAfterFailure();
            if (!accepted)
                LastRejection = "Terminal geçiş niyeti artık güncel değil.";
            return true;
        }

        private static bool TryReadPointerDown(out Vector2 screenPoint,
                                               out int pointerId)
        {
            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch touch = Input.GetTouch(i);
                if (touch.phase == TouchPhase.Began)
                {
                    screenPoint = touch.position;
                    pointerId = touch.fingerId;
                    return true;
                }
            }

            if (Input.touchCount > 0)
            {
                screenPoint = default;
                pointerId = -1;
                return false;
            }

            if (Input.GetMouseButtonDown(0))
            {
                screenPoint = Input.mousePosition;
                pointerId = -1;
                return true;
            }

            screenPoint = default;
            pointerId = -1;
            return false;
        }

        private void HandlePointerDown(Vector2 screenPoint)
        {
            Camera camera = ResolveCamera();
            if (camera == null
                || !shelfView.TryPickBottle(camera, screenPoint, pickPadding,
                                            out LiquidBottle hit, out int hitId))
            {
                BartenderInputRequest background =
                    BartenderInputRequest.Background(selectedGlassId);
                if (!CheckInputPolicy(background, out _))
                    return;
                if (TryConsumeAcceptedInput(background)) return;
                ClearSelectionWithSound(true);
                return;
            }

            BartenderInputRequest bottleTap =
                BartenderInputRequest.Bottle(hitId, selectedGlassId);
            if (!CheckInputPolicy(bottleTap, out _))
                return;
            if (TryConsumeAcceptedInput(bottleTap)) return;

            if (controller != null && controller.MatchedOrderSlot(hitId) >= 0)
            {
                DeliveryBadgePresenter badges = ResolveDeliveryBadges(shelfView);
                if (badges != null && !badges.IsReadyForDelivery(hit)) return;
                ClearSelectionWithSound(true);
                TryCommitDelivery(hitId, out _);
                return;
            }

            if (selectedBottle == null)
            {
                SelectIfUsable(hit, hitId);
                return;
            }

            if (hit == selectedBottle)
            {
                ClearSelectionWithSound(true);
                return;
            }

            int sourceId = selectedGlassId;
            if (TryCommitAndAnimatePour(sourceId, hitId, out _)) return;
            // An invalid target clears selection and returns the source to its shelf seat while rejection
            // feedback plays.
            ClearSelection(true);
            return;
        }

        private void SelectIfUsable(LiquidBottle bottle, int glassId)
        {
            if (bottle == null || controller == null
                || !controller.CanSelectAsPourSource(glassId))
            {
                ClearSelection(true);
                return;
            }

            ClearSelection(true);
            if (!shelfView.TryGetSeatPose(glassId,
                    out BartenderGlassSeatPose home))
                return;
            selectedBottle = bottle;
            selectedMotionRoot = home.MotionRoot != null
                ? home.MotionRoot
                : bottle.transform;
            selectedGlassId = glassId;
            selectedHomePosition = home.Position;
            selectedHomeRotation = home.Rotation;
            selectedHomeScale = home.LocalScale;
            selectedRoyalRelativeScale = VesselPresentationMath.RelativeToRoyalReference(
                bottle.transform, bottle.profile);
            BsAudio.Instance?.Play(BsSfx.GlassPickup);
            NotifySelectionChanged(glassId);
        }

        private void AnimateSelection()
        {
            if (selectedBottle == null || Busy) return;

            // Safe-area fitting can move the shelf after selection. I use its current layout pose instead
            // of the old captured one.
            if (shelfView != null && selectedGlassId >= 0
                && shelfView.TryGetSeatPose(selectedGlassId,
                    out BartenderGlassSeatPose liveHome))
            {
                selectedHomePosition = liveHome.Position;
                selectedHomeRotation = liveHome.Rotation;
                selectedHomeScale = liveHome.LocalScale;
                selectedMotionRoot = liveHome.MotionRoot != null
                    ? liveHome.MotionRoot
                    : selectedBottle.transform;
                selectedRoyalRelativeScale = VesselPresentationMath.RelativeToRoyalReference(
                    selectedBottle.transform, selectedBottle.profile);
            }

            Transform motionRoot = selectedMotionRoot != null
                ? selectedMotionRoot
                : selectedBottle.transform;

            float follow = 1f - Mathf.Exp(-selectionSpeed * Time.unscaledDeltaTime);
            float scaledLift = VesselPresentationMath.ReferenceDistance(
                selectionLift, selectedRoyalRelativeScale);
            Vector3 liftDirection = shelfView != null
                ? shelfView.LayoutUpWorld
                : Vector3.up;
            Vector3 wanted = selectedHomePosition + liftDirection * scaledLift;
            motionRoot.position = Vector3.Lerp(
                motionRoot.position, wanted, follow);
            BartenderInvalidMoveFeedback rejection =
                selectedBottle.GetComponent<BartenderInvalidMoveFeedback>();
            if (rejection == null || !rejection.Playing)
            {
                motionRoot.rotation = Quaternion.Slerp(
                    motionRoot.rotation, selectedHomeRotation, follow);
            }
            motionRoot.localScale = Vector3.Lerp(
                motionRoot.localScale, selectedHomeScale * selectionScale, follow);

            BottleShell shell = selectedBottle.GetComponent<BottleShell>();
            if (shell != null)
                shell.highlight = Mathf.Lerp(shell.highlight, 1f, follow);
        }

        private void ClearSelection(bool restorePose)
        {
            int previousGlassId = selectedGlassId;
            LiquidBottle bottle = selectedBottle;
            if (bottle != null)
            {
                if (restorePose && (pourAnimator == null || !pourAnimator.Busy))
                {
                    if (shelfView != null && previousGlassId >= 0
                        && shelfView.TryGetSeatPose(previousGlassId,
                            out BartenderGlassSeatPose liveHome))
                    {
                        selectedHomePosition = liveHome.Position;
                        selectedHomeRotation = liveHome.Rotation;
                        selectedHomeScale = liveHome.LocalScale;
                        selectedMotionRoot = liveHome.MotionRoot != null
                            ? liveHome.MotionRoot
                            : bottle.transform;
                    }
                    Transform motionRoot = selectedMotionRoot != null
                        ? selectedMotionRoot
                        : bottle.transform;
                    motionRoot.SetPositionAndRotation(
                        selectedHomePosition, selectedHomeRotation);
                    motionRoot.localScale = selectedHomeScale;
                }
                BottleShell shell = bottle.GetComponent<BottleShell>();
                if (shell != null) shell.highlight = 0f;
            }
            selectedBottle = null;
            selectedMotionRoot = null;
            selectedGlassId = -1;
            selectedHomePosition = default;
            selectedHomeRotation = Quaternion.identity;
            selectedHomeScale = Vector3.one;
            selectedRoyalRelativeScale = 1f;
            if (previousGlassId >= 0) NotifySelectionChanged(-1);
        }

        private void ClearSelectionWithSound(bool restorePose)
        {
            bool hadSelection = selectedBottle != null;
            ClearSelection(restorePose);
            if (hadSelection) BsAudio.Instance?.Play(BsSfx.GlassSet);
        }

        private void HandlePourFinished(
            PresentationTransactionContext transaction,
            PourAnimator expectedAnimator,
            int expectedOperationId,
            Action<int, PourOutcome> expectedHandler,
            int operationId,
            PourOutcome outcome)
        {
            if (transaction == null
                || expectedAnimator == null
                || expectedOperationId == 0
                || operationId != expectedOperationId
                || !ReferenceEquals(activeTransaction, transaction)
                || !ReferenceEquals(transaction.Animator, expectedAnimator)
                || transaction.AnimationOperationId != expectedOperationId
                || !ReferenceEquals(
                    transaction.PourFinishedHandler, expectedHandler)) return;
            int glassId = transaction.CompletionGlassId;
            BartenderShelfLevelView pulseView = transaction.View != null
                ? transaction.View
                : shelfView;
            BartenderLevelController pulseController = transaction.Controller != null
                ? transaction.Controller
                : controller;
            BartenderPourReceipt pourReceipt = transaction.Receipt;
            bool roundCurrent = IsTransactionRoundCurrent(transaction);
            bool willPlayCompletion = !transaction.IsUndo
                && outcome == PourOutcome.Completed && roundCurrent;
            if (willPlayCompletion)
                HoldCompletionBadge(pulseView, glassId);
            // Release gameplay ownership after the pour and shelf refresh. The glass flourish is cosmetic
            // and holds no Busy flag or controller barrier.
            TrySettlePresentationTransaction(transaction, roundCurrent, false);
            if (!willPlayCompletion) return;
            if (CanStartCompletionPresentation(
                    pulseController, pulseView, glassId, pourReceipt))
                TryStartCompletionTail(pulseView, glassId, pourReceipt);
            else if (IsCurrentPourReceipt(pulseController, pourReceipt))
                ReconcileCompletionBadge(pulseView, glassId);
        }

        private static bool BecameMatched(BartenderLevelController owner,
                                          RtGlass before, RtGlass after) =>
            owner != null && after != null
            && owner.MatchedOrderSlot(after) >= 0
            && (before == null || owner.MatchedOrderSlot(before) < 0);

        private bool TryStartCompletionTail(
            BartenderShelfLevelView view,
            int glassId,
            BartenderPourReceipt receipt)
        {
            BartenderLevelController ownerController = view != null
                ? view.Controller
                : null;
            if (glassId < 0 || view == null
                || !CanStartCompletionPresentation(
                    ownerController, view, glassId, receipt)
                || !view.TryGetBottle(glassId, out LiquidBottle bottle)
                || bottle == null) return false;

            DeliveryBadgePresenter badges = ResolveDeliveryBadges(view);
            BottleShell shell = bottle.GetComponent<BottleShell>();
            BottleCompletionEffect effect = AcquireCompletionEffect();
            if (shell != null && effect != null
                && view.TryGetSeatPose(glassId, out BartenderGlassSeatPose seat)
                && seat.MotionRoot != null)
            {
                nextCompletionTailRunId = AdvanceRunId(nextCompletionTailRunId);
                var tail = new CompletionTailContext(
                    nextCompletionTailRunId, ownerController, view, receipt,
                    glassId, bottle, shell, effect, badges,
                    Time.realtimeSinceStartupAsDouble
                        + CompletionTailTimeoutSeconds,
                    seat);
                if (!TryTrackCompletionTail(tail))
                {
                    ReleaseCompletionEffect(effect);
                    return false;
                }
                Action revealBadge = badges != null
                    ? () =>
                    {
                        if (OwnsCompletionTail(tail) && TailRoundIsCurrent(tail))
                            badges.RevealFromCompletionCue(bottle);
                    }
                    : null;
                Action confirm = () =>
                {
                    if (!OwnsCompletionTail(tail) || !TailRoundIsCurrent(tail)) return;
                    if (badges != null) badges.MarkCompletionBadgeReady(bottle);
                    BsAudio.Instance?.Play(BsSfx.Check);
                };
                Action presentationFinished = () =>
                    TrySettleCompletionTail(tail, true);

                bool started = false;
                try
                {
                    started = shell.PlayCompletionPresentation(
                        effect, seat.MotionRoot,
                        seat.Position, seat.Rotation,
                        seat.LocalScale, view.LayoutUpWorld,
                        revealBadge, confirm, presentationFinished);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                    TrySettleCompletionTail(tail, true);
                }
                if (started) return true;
                TrySettleCompletionTail(tail, true);
                return false;
            }

            ReleaseCompletionEffect(effect);

            // A missing badge visual must not leave a matched glass impossible to deliver.
            if (TailReceiptCanAffectGlass(ownerController, view, glassId, bottle, receipt))
            {
                badges?.RevealFromCompletionCue(bottle);
                badges?.MarkCompletionBadgeReady(bottle);
                BsAudio.Instance?.Play(BsSfx.Check);
            }
            return false;
        }

        private static void HoldCompletionBadge(BartenderShelfLevelView view,
                                                int glassId)
        {
            DeliveryBadgePresenter badges = ResolveDeliveryBadges(view);
            if (badges == null || view == null) return;
            if (glassId >= 0
                && view.TryGetBottle(glassId, out LiquidBottle bottle))
                badges.HoldForCompletionCue(bottle);
        }

        private static void ReconcileCompletionBadge(BartenderShelfLevelView view,
                                                      int glassId)
        {
            DeliveryBadgePresenter badges = ResolveDeliveryBadges(view);
            if (badges == null || view == null) return;
            if (glassId >= 0
                && view.TryGetBottle(glassId, out LiquidBottle bottle))
                badges.ReconcileCancelledCompletion(bottle);
        }

        private bool CanStartCompletionPresentation(
            BartenderLevelController ownerController,
            BartenderShelfLevelView ownerView,
            int glassId,
            BartenderPourReceipt receipt)
        {
            return isActiveAndEnabled && glassId >= 0
                && !completionTails.ContainsKey(glassId)
                && ownerController != null && ownerView != null
                && ReferenceEquals(controller, ownerController)
                && ReferenceEquals(shelfView, ownerView)
                && ReferenceEquals(ownerView.Controller, ownerController)
                && ownerView.Ready && !ownerView.SynchronizationDeferred
                && !ownerController.PresentationLocked
                && ownerController.State == BartenderLevelState.Playing
                && IsCurrentPourReceipt(ownerController, receipt);
        }

        private static DeliveryBadgePresenter ResolveDeliveryBadges(
            BartenderShelfLevelView view)
        {
            if (view == null) return null;
            DeliveryBadgePresenter presenter =
                view.GetComponent<DeliveryBadgePresenter>();
            return presenter != null && presenter.isActiveAndEnabled
                ? presenter
                : null;
        }

        internal bool TrySettlePresentationTransaction(
            PresentationTransactionContext expected,
            bool refresh,
            bool cancelAnimator)
        {
            if (expected == null || expected.RunId <= 0L
                || !ReferenceEquals(activeTransaction, expected)) return false;

            // Clear ownership before cancelling; PourFinished may fire immediately and must not settle a
            // newer transaction.
            activeTransaction = null;
            int animationOperationId = expected.AnimationOperationId;
            expected.AnimationOperationId = 0;
            Exception firstCleanupFailure = null;
            Action<int, PourOutcome> pourFinishedHandler =
                expected.PourFinishedHandler;
            expected.PourFinishedHandler = null;
            int lockedRevision = expected.LockedRevision;
            expected.LockedRevision = -1;

            try
            {
                if (pourFinishedHandler != null && expected.Animator != null)
                    expected.Animator.PourFinished -= pourFinishedHandler;
            }
            catch (Exception exception)
            {
                firstCleanupFailure = exception;
            }

            try
            {
                if (cancelAnimator && animationOperationId != 0
                    && expected.Animator != null
                    && expected.Animator.ActiveOperationId == animationOperationId)
                    expected.Animator.CancelActivePour();
            }
            catch (Exception exception)
            {
                if (firstCleanupFailure == null)
                    firstCleanupFailure = exception;
            }

            try
            {
                if (expected.View != null
                    && expected.View.IsSynchronizationDeferredBy(
                        expected.Owner, expected.SynchronizationLease))
                {
                    if (refresh)
                        expected.View.EndSynchronizationDeferralAndRefresh(
                            expected.Owner, expected.SynchronizationLease, true);
                    else
                        expected.View.DropSynchronizationDeferral(
                            expected.Owner, expected.SynchronizationLease);
                }
            }
            catch (Exception exception)
            {
                if (firstCleanupFailure == null)
                    firstCleanupFailure = exception;
            }

            try
            {
                if (expected.Controller != null && lockedRevision >= 0)
                    expected.Controller.ReleasePresentationLock(
                        expected.Owner, lockedRevision);
            }
            catch (Exception exception)
            {
                if (firstCleanupFailure == null)
                    firstCleanupFailure = exception;
            }

            // Try every owned release even if one throws. Report afterward without throwing into lifecycle
            // cleanup.
            if (firstCleanupFailure != null)
                Debug.LogException(firstCleanupFailure, this);
            return true;
        }

        /// <summary>
        /// Settle the exact transaction first, then try each local cleanup separately. Return the first
        /// error instead of throwing from OnDisable.
        /// </summary>
        internal static Exception ExecuteLifecycleCleanup(params Action[] cleanupSteps)
        {
            Exception firstFailure = null;
            if (cleanupSteps == null) return null;
            for (int i = 0; i < cleanupSteps.Length; i++)
            {
                Action cleanup = cleanupSteps[i];
                if (cleanup == null) continue;
                try
                {
                    cleanup();
                }
                catch (Exception exception)
                {
                    if (firstFailure == null) firstFailure = exception;
                }
            }
            return firstFailure;
        }

        internal PresentationTransactionContext BeginPresentationTransaction(
            BartenderLevelController ownerController,
            BartenderShelfLevelView ownerView,
            PourAnimator ownerAnimator,
            BsShelfSynchronizationLease synchronizationLease,
            object transactionOwner)
        {
            if (transactionOwner == null)
                throw new ArgumentNullException(nameof(transactionOwner));
            if (activeTransaction != null)
                TrySettlePresentationTransaction(
                    activeTransaction, IsTransactionRoundCurrent(activeTransaction), true);
            nextPresentationRunId = AdvanceRunId(nextPresentationRunId);
            var transaction = new PresentationTransactionContext(
                nextPresentationRunId,
                transactionOwner,
                ownerController,
                ownerView,
                synchronizationLease,
                ownerAnimator,
                session,
                ownerController != null
                    ? ownerController.CurrentRoundStamp
                    : default,
                Time.realtimeSinceStartupAsDouble
                    + PresentationTransactionTimeoutSeconds);
            activeTransaction = transaction;
            return transaction;
        }

        /// <summary>
        /// Capture context, animator and operation ID together so an old animator reusing an ID cannot
        /// finish a newer pour.
        /// </summary>
        internal bool TryBindPourFinished(
            PresentationTransactionContext transaction,
            PourAnimator ownerAnimator,
            int operationId)
        {
            if (transaction == null || ownerAnimator == null || operationId == 0
                || !ReferenceEquals(activeTransaction, transaction)
                || !ReferenceEquals(transaction.Animator, ownerAnimator)
                || ownerAnimator.ActiveOperationId != operationId
                || transaction.AnimationOperationId != 0
                || transaction.PourFinishedHandler != null)
                return false;

            Action<int, PourOutcome> handler = null;
            handler = (completedOperationId, outcome) =>
                HandlePourFinished(
                    transaction,
                    ownerAnimator,
                    operationId,
                    handler,
                    completedOperationId,
                    outcome);
            transaction.AnimationOperationId = operationId;
            transaction.PourFinishedHandler = handler;
            ownerAnimator.PourFinished += handler;
            return true;
        }

        private bool CanContinueTransaction(
            PresentationTransactionContext transaction,
            BartenderLevelController ownerController,
            BartenderShelfLevelView ownerView,
            PourAnimator ownerAnimator)
        {
            return transaction != null
            && ReferenceEquals(activeTransaction, transaction)
            && isActiveAndEnabled
            && transaction.Owner != null
            && ReferenceEquals(transaction.Controller, ownerController)
            && ReferenceEquals(transaction.View, ownerView)
            && ReferenceEquals(transaction.Animator, ownerAnimator)
            && ReferenceEquals(transaction.Session, session)
            && ReferenceEquals(controller, ownerController)
            && ReferenceEquals(shelfView, ownerView)
            && ReferenceEquals(pourAnimator, ownerAnimator)
            && IsTransactionRoundCurrent(transaction)
            && ownerView != null && ownerView.IsSynchronizationDeferredBy(
                transaction.Owner, transaction.SynchronizationLease);
        }

        private void CaptureTransactionPourReceipt(
            PresentationTransactionContext transaction,
            BartenderPourReceipt receipt)
        {
            if (transaction == null
                || !ReferenceEquals(activeTransaction, transaction)
                || transaction.Controller == null || receipt == null) return;
            transaction.Receipt = receipt;
        }

        private bool IsTransactionRoundCurrent(
            PresentationTransactionContext transaction)
        {
            if (transaction == null) return false;
            if (transaction.Controller == null || transaction.Session == null
                || !ReferenceEquals(session, transaction.Session)) return false;
            if (transaction.IsUndo)
            {
                if (transaction.UndoChange != null)
                    return transaction.UndoChange.Cause == BsRoundTransitionCause.PlayerUndo
                        && IsCurrentBoardChange(transaction.Controller, transaction.UndoChange);
                // An earlier commit listener may disable us before BoardCommitted reaches this listener.
                // Reconcile that round's authoritative board even when the undo receipt was not captured.
                BsRoundCommandStamp current = transaction.Controller.CurrentRoundStamp;
                return transaction.StartStamp.IsValid && current.IsValid
                    && current.AttemptId == transaction.StartStamp.AttemptId
                    && current.Token == transaction.StartStamp.Token;
            }
            return transaction.Receipt != null
                ? IsCurrentPourReceipt(
                    transaction.Controller, transaction.Receipt)
                : transaction.StartStamp.IsValid
                  && transaction.Controller.CurrentRoundStamp
                      == transaction.StartStamp;
        }

        /// <summary>
        /// Cancellation reconciles the authoritative board for the same attempt, even when a synchronous
        /// commit disabled this bridge before its receipt was returned or pause advanced the domain stamp.
        /// This does not authorize an animation or its success cues; those still require the exact receipt.
        /// </summary>
        private bool CanReconcileTransactionBoard(PresentationTransactionContext transaction)
        {
            if (transaction == null || transaction.Controller == null
                || transaction.View == null || transaction.Session == null
                || !ReferenceEquals(controller, transaction.Controller)
                || !ReferenceEquals(shelfView, transaction.View)
                || !ReferenceEquals(session, transaction.Session)
                || !ReferenceEquals(transaction.View.Controller, transaction.Controller)
                || !ReferenceEquals(transaction.Session.Controller, transaction.Controller))
                return false;
            BsRoundCommandStamp current = transaction.Controller.CurrentRoundStamp;
            return transaction.StartStamp.IsValid && current.IsValid
                && current.AttemptId == transaction.StartStamp.AttemptId
                && current.Token == transaction.StartStamp.Token
                && current.BoardRevision >= transaction.StartStamp.BoardRevision;
        }

        private static bool IsCurrentPourReceipt(
            BartenderLevelController owner,
            BartenderPourReceipt receipt)
        {
            return owner != null && receipt != null
                && receipt.AttemptId.IsValid
                && receipt.OperationId.IsValid
                && receipt.DomainRevision > 0L
                && receipt.BoardRevision >= 0
                && receipt.Cause == BsRoundTransitionCause.PlayerPour
                && owner.CurrentRoundStamp == new BsRoundCommandStamp(
                    receipt.AttemptId,
                    receipt.Token,
                    receipt.DomainRevision,
                    receipt.BoardRevision);
        }

        private void HandleLevelLoaded(BsLevel _)
        {
            TrySettlePresentationTransaction(activeTransaction, false, true);
            CancelAllCompletionTails(false);
            CancelRejectionFeedbacks(true);
            ClearSelection(true);
        }

        internal bool HasCompletionTail(int glassId) =>
            completionTails.TryGetValue(glassId, out CompletionTailContext tail)
            && tail != null;

        internal bool TryTrackCompletionTail(CompletionTailContext tail)
        {
            if (tail == null || tail.RunId <= 0L || tail.GlassId < 0
                || completionTails.ContainsKey(tail.GlassId)) return false;
            completionTails.Add(tail.GlassId, tail);
            return true;
        }

        private bool OwnsCompletionTail(CompletionTailContext expected) =>
            expected != null
            && completionTails.TryGetValue(
                expected.GlassId, out CompletionTailContext current)
            && ReferenceEquals(current, expected);

        private bool TailRoundIsCurrent(CompletionTailContext tail) =>
            tail != null && isActiveAndEnabled
            && TailReceiptCanAffectGlass(
                tail.Controller, tail.View, tail.GlassId,
                tail.Bottle, tail.Receipt)
            && CompletionTailSeatIsCurrent(tail)
            && (tail.Badges == null
                || ReferenceEquals(
                    ResolveDeliveryBadges(tail.View), tail.Badges));

        private static bool TailReceiptCanAffectGlass(
            BartenderLevelController owner,
            BartenderShelfLevelView view,
            int glassId,
            LiquidBottle bottle,
            BartenderPourReceipt receipt,
            bool allowPaused = false)
        {
            if (owner == null || view == null || bottle == null || receipt == null
                || glassId < 0 || !receipt.AttemptId.IsValid
                || !receipt.OperationId.IsValid
                || receipt.Cause != BsRoundTransitionCause.PlayerPour
                || !ReferenceEquals(view.Controller, owner)
                || (owner.State != BartenderLevelState.Playing
                    && !(allowPaused && owner.State == BartenderLevelState.Paused)))
                return false;
            BsRoundCommandStamp current = owner.CurrentRoundStamp;
            if (current.AttemptId != receipt.AttemptId
                || current.Token != receipt.Token
                || !view.TryGetBottle(glassId, out LiquidBottle currentBottle)
                || !ReferenceEquals(currentBottle, bottle)) return false;
            return owner.MatchedOrderSlot(glassId) >= 0;
        }

        private static bool CompletionTailSeatIsCurrent(CompletionTailContext tail)
        {
            if (tail == null || !tail.SeatPose.HasValue || tail.View == null
                || !tail.View.TryGetSeatPose(tail.GlassId,
                    out BartenderGlassSeatPose current)) return false;
            BartenderGlassSeatPose captured = tail.SeatPose.Value;
            return ReferenceEquals(current.MotionRoot, captured.MotionRoot)
                && (current.Position - captured.Position).sqrMagnitude <= 1e-8f
                && Quaternion.Angle(current.Rotation, captured.Rotation) <= 0.01f
                && (current.LocalScale - captured.LocalScale).sqrMagnitude <= 1e-8f;
        }

        internal bool TrySettleCompletionTail(
            CompletionTailContext expected,
            bool reconcileBadge)
        {
            if (!OwnsCompletionTail(expected)) return false;
            completionTails.Remove(expected.GlassId);
            bool effectStopped = expected.Effect == null;
            Exception cleanupFailure = ExecuteLifecycleCleanup(
                () =>
                {
                    // Stop the captured effect itself: the pooled shell may now own another glass/run.
                    if (expected.Effect != null) expected.Effect.Stop(false);
                    effectStopped = true;
                },
                () =>
                {
                    // The shelf may move during this effect. Restore its current pose, never the old seat.
                    if (!TailReceiptCanAffectGlass(expected.Controller, expected.View,
                            expected.GlassId, expected.Bottle, expected.Receipt, true)
                        || !expected.SeatPose.HasValue || expected.View.SeatAnimationPlaying
                        || !expected.View.TryGetSeatPose(expected.GlassId,
                            out BartenderGlassSeatPose currentSeat)
                        || currentSeat.MotionRoot == null
                        || !ReferenceEquals(currentSeat.MotionRoot,
                            expected.SeatPose.Value.MotionRoot)) return;
                    currentSeat.MotionRoot.SetPositionAndRotation(
                        currentSeat.Position, currentSeat.Rotation);
                    currentSeat.MotionRoot.localScale = currentSeat.LocalScale;
                },
                () =>
                {
                    if (reconcileBadge && expected.Badges != null
                        && TailReceiptCanAffectGlass(expected.Controller, expected.View,
                            expected.GlassId, expected.Bottle, expected.Receipt, true)
                        && ReferenceEquals(ResolveDeliveryBadges(expected.View), expected.Badges))
                        expected.Badges.ReconcileCancelledCompletion(expected.Bottle);
                },
                () =>
                {
                    // Keep a failed teardown out of the reusable pool.
                    if (effectStopped) ReleaseCompletionEffect(expected.Effect);
                });
            if (cleanupFailure != null) Debug.LogException(cleanupFailure, this);
            return true;
        }

        internal void CancelAllCompletionTails(bool reconcileBadges)
        {
            if (completionTails.Count == 0) return;
            completionTailScratch.Clear();
            foreach (CompletionTailContext tail in completionTails.Values)
                completionTailScratch.Add(tail);
            for (int i = 0; i < completionTailScratch.Count; i++)
                TrySettleCompletionTail(completionTailScratch[i], reconcileBadges);
            completionTailScratch.Clear();
        }

        private BottleCompletionEffect AcquireCompletionEffect()
        {
            while (completionEffectPool.Count > 0)
            {
                BottleCompletionEffect pooled = completionEffectPool.Pop();
                if (pooled != null) return pooled;
            }
            if (glassCompletionEffect == null) return null;
            BottleCompletionEffect created = Instantiate(
                glassCompletionEffect, glassCompletionEffect.transform.parent);
            created.name = glassCompletionEffect.name + " (Pooled Tail)";
            ownedCompletionEffects.Add(created);
            return created;
        }

        private void ReleaseCompletionEffect(BottleCompletionEffect effect)
        {
            if (effect == null || !effect.CanReuse || !ownedCompletionEffects.Contains(effect)) return;
            completionEffectPool.Push(effect);
        }

        private static long AdvanceRunId(long current) =>
            current == long.MaxValue ? 1L : current + 1L;

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state != BartenderLevelState.Playing)
            {
                // Pause retains the committed board for both pours and undo. It must be shown before the
                // old view's synchronization hold is released.
                PresentationTransactionContext transaction = activeTransaction;
                TrySettlePresentationTransaction(transaction,
                    state == BartenderLevelState.Paused
                        && CanReconcileTransactionBoard(transaction), true);
                // Pause keeps this board. Settle the badge now because stopping its effect also removes the
                // ready callback.
                CancelAllCompletionTails(state == BartenderLevelState.Paused);
                CancelRejectionFeedbacks(true);
                ClearSelectionWithSound(true);
            }
        }

        private void HandleBoardCommitted(BartenderBoardChange change)
        {
            if (!IsCurrentBoardChange(controller, change)) return;
            PresentationTransactionContext transaction = activeTransaction;
            if (transaction != null && transaction.IsUndo)
            {
                if (transaction.UndoChange == null
                    && change.Cause == BsRoundTransitionCause.PlayerUndo
                    && change.AttemptId == transaction.StartStamp.AttemptId
                    && change.Token == transaction.StartStamp.Token)
                    transaction.UndoChange = change;
                else if (transaction.UndoChange != null
                    && change.BoardRevision != transaction.BoardRevision)
                    TrySettlePresentationTransaction(transaction, true, true);
            }
            if (completionTails.Count == 0) return;

            // Copy entries before settling removes them. Board changes may invalidate only one glass's
            // effect.
            completionTailScratch.Clear();
            foreach (CompletionTailContext tail in completionTails.Values)
            {
                if (!TailReceiptCanAffectGlass(
                        tail.Controller, tail.View, tail.GlassId,
                        tail.Bottle, tail.Receipt))
                    completionTailScratch.Add(tail);
            }
            for (int i = 0; i < completionTailScratch.Count; i++)
                TrySettleCompletionTail(completionTailScratch[i], false);
            completionTailScratch.Clear();
        }

        /// <summary>
        /// Uses real time to clean up a stalled animation or lost lock. Only the captured run can be
        /// settled.
        /// </summary>
        internal int TrySettleExpiredPresentations(double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now)) return 0;
            int settled = 0;
            PresentationTransactionContext transaction = activeTransaction;
            if (transaction != null
                && (now >= transaction.Deadline
                    || transaction.View == null
                    || !transaction.View.IsSynchronizationDeferredBy(
                        transaction.Owner, transaction.SynchronizationLease)))
            {
                if (TrySettlePresentationTransaction(
                        transaction, CanReconcileTransactionBoard(transaction), true))
                    settled++;
            }

            if (completionTails.Count == 0) return settled;
            completionTailScratch.Clear();
            foreach (CompletionTailContext tail in completionTails.Values)
            {
                if (now >= tail.Deadline || !TailRoundIsCurrent(tail))
                    completionTailScratch.Add(tail);
            }
            for (int i = 0; i < completionTailScratch.Count; i++)
            {
                if (TrySettleCompletionTail(completionTailScratch[i], true))
                    settled++;
            }
            completionTailScratch.Clear();
            return settled;
        }

        private static bool IsCurrentBoardChange(
            BartenderLevelController owner,
            BartenderBoardChange change) =>
            owner != null && change != null
            && change.AttemptId.IsValid
            && change.OperationId.IsValid
            && change.DomainRevision > 0L
            && change.BoardRevision >= 0
            && owner.CurrentRoundStamp == new BsRoundCommandStamp(
                change.AttemptId,
                change.Token,
                change.DomainRevision,
                change.BoardRevision);

        private void HandlePresentationChanged()
        {
            // Stop effects whose seats changed before another tween tick writes their old poses.
            completionTailScratch.Clear();
            foreach (CompletionTailContext tail in completionTails.Values)
                if (!CompletionTailSeatIsCurrent(tail))
                    completionTailScratch.Add(tail);
            for (int i = 0; i < completionTailScratch.Count; i++)
                TrySettleCompletionTail(completionTailScratch[i], true);
            completionTailScratch.Clear();

            if (ActiveAnimationOperationId != 0) return;

            // The shelf already has new poses. Stop feedback without restoring an outdated rotation.
            CancelRejectionFeedbacks(false);
            ClearSelection(false);
        }

        private void PlayRejectedPourFeedback(LiquidBottle source, LiquidBottle target)
        {
            float direction = source != null && target != null
                && source.transform.position.x > target.transform.position.x
                ? -1f
                : 1f;
            PlayRejectionFeedback(source, rejectedSourceColor,
                -direction * sourceRejectionWobble);
            PlayRejectionFeedback(target, rejectedTargetColor,
                direction * targetRejectionWobble);
        }

        private void PlayRejectionFeedback(LiquidBottle bottle, Color color,
                                           float wobbleDegrees)
        {
            if (bottle == null || !bottle.gameObject.activeInHierarchy) return;
            BartenderInvalidMoveFeedback feedback =
                bottle.GetComponent<BartenderInvalidMoveFeedback>();
            if (feedback == null)
            {
                Debug.LogError(
                    $"{bottle.name}: authored invalid-move feedback is missing.",
                    bottle);
                return;
            }
            Transform motionRoot = bottle.transform;
            if (shelfView != null
                && shelfView.TryGetMotionRoot(bottle, out Transform resolvedRoot))
                motionRoot = resolvedRoot;
            feedback.Play(color, rejectionHighlightAlpha,
                wobbleDegrees, rejectionDuration, motionRoot);
        }

        private static void CancelRejectionFeedback(LiquidBottle bottle,
                                                     bool restoreRotation)
        {
            if (bottle == null) return;
            BartenderInvalidMoveFeedback feedback =
                bottle.GetComponent<BartenderInvalidMoveFeedback>();
            if (feedback != null) feedback.Cancel(restoreRotation);
        }

        private void CancelRejectionFeedbacks(bool restoreRotation)
        {
            if (shelfView == null) return;
            BartenderInvalidMoveFeedback[] feedbacks =
                shelfView.GetComponentsInChildren<BartenderInvalidMoveFeedback>(true);
            for (int i = 0; i < feedbacks.Length; i++)
            {
                BartenderInvalidMoveFeedback feedback = feedbacks[i];
                if (feedback != null) feedback.Cancel(restoreRotation);
            }
        }

        private Camera ResolveCamera()
        {
            if (inputCamera == null) inputCamera = Camera.main;
            return inputCamera;
        }

        private void ResolveDependencies()
        {
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            if (controller == null && shelfView != null) controller = shelfView.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
            if (pourAnimator == null) pourAnimator = GetComponent<PourAnimator>();
            if (session == null) session = GetComponent<BartenderSession>();
            if (orderStrip == null) orderStrip = GetComponent<OrderStripPresenter>();
        }

        private void Subscribe()
        {
            if (subscribedController != controller)
            {
                if (subscribedController != null)
                {
                    subscribedController.LevelLoaded -= HandleLevelLoaded;
                    subscribedController.StateChanged -= HandleStateChanged;
                    subscribedController.BoardCommitted -= HandleBoardCommitted;
                }
                subscribedController = controller;
                if (subscribedController != null)
                {
                    subscribedController.LevelLoaded += HandleLevelLoaded;
                    subscribedController.StateChanged += HandleStateChanged;
                    subscribedController.BoardCommitted += HandleBoardCommitted;
                }
            }

            if (subscribedView != shelfView)
            {
                if (subscribedView != null)
                {
                    subscribedView.PresentationChanged -= HandlePresentationChanged;
                }
                subscribedView = shelfView;
                if (subscribedView != null)
                {
                    subscribedView.PresentationChanged += HandlePresentationChanged;
                }
            }
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.StateChanged -= HandleStateChanged;
                subscribedController.BoardCommitted -= HandleBoardCommitted;
            }
            if (subscribedView != null)
            {
                subscribedView.PresentationChanged -= HandlePresentationChanged;
            }
            subscribedController = null;
            subscribedView = null;
        }

        private bool CheckInputPolicy(BartenderInputRequest request,
                                      out string rejectionReason)
        {
            rejectionReason = null;
            DropDestroyedInputPolicy();
            IBartenderInputPolicy policy = inputPolicy;
            if (policy == null) return true;

            bool allowed;
            try
            {
                allowed = policy.Allows(request, out rejectionReason);
            }
            catch (Exception exception)
            {
                // Drop a broken presentation lease so it cannot block gameplay.
                Debug.LogException(exception, this);
                if (ReferenceEquals(inputPolicy, policy)) inputPolicy = null;
                rejectionReason = null;
                return true;
            }

            if (allowed) return true;
            if (string.IsNullOrEmpty(rejectionReason))
                rejectionReason = "Tap the glowing target for this step.";
            LastRejection = rejectionReason;
            try
            {
                policy.HandleRejected(request, rejectionReason);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                if (ReferenceEquals(inputPolicy, policy)) inputPolicy = null;
            }
            return false;
        }

        private bool TryConsumeAcceptedInput(BartenderInputRequest request)
        {
            DropDestroyedInputPolicy();
            if (!(inputPolicy is IBartenderAcceptedInputConsumer consumer))
                return false;
            try
            {
                return consumer.TryConsume(request);
            }
            catch (Exception exception)
            {
                // Consume a failed modal tap so it cannot fall through into a pour or delivery.
                Debug.LogException(exception, this);
                inputPolicy = null;
                return true;
            }
        }

        private void DropDestroyedInputPolicy()
        {
            if (inputPolicy is UnityEngine.Object unityOwner && unityOwner == null)
                inputPolicy = null;
        }

        private void NotifySelectionChanged(int glassId)
        {
            Action<int> handlers = SelectionChanged;
            if (handlers == null) return;
            Delegate[] invocationList = handlers.GetInvocationList();
            for (int i = 0; i < invocationList.Length; i++)
            {
                try
                {
                    ((Action<int>)invocationList[i]).Invoke(glassId);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                }
            }
        }

        private bool Reject(string reason, out string rejectionReason)
        {
            LastRejection = string.IsNullOrEmpty(reason) ? "The pour was rejected." : reason;
            rejectionReason = LastRejection;
            return false;
        }
    }
}
