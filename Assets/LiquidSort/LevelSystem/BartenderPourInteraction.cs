using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    public sealed class BartenderPourInteraction : MonoBehaviour
    {
        internal const double PresentationTransactionTimeoutSeconds = 8d;
        internal const double CompletionTailTimeoutSeconds = 6d;
        internal const float UndoRefillRiseSeconds = 0.30f;
        internal const float UndoRefillExtraUnitSeconds = 0.10f;
        internal const float UndoRefillSettleSeconds = 0.22f;
        private const float UndoRefillTailStart = 0.45f;
        private const float UndoRefillEaseShare = 0.12f;
        private const float UndoRefillVolumeTolerance = 0.001f;
        private const float UndoRefillMaxStepSeconds = 1f / 30f;
        private const float UndoRefillWallClockGraceSeconds = 2f;

        private sealed class UndoRefillContext
        {
            public readonly BartenderShelfLevelView View;
            public readonly int GlassId;
            public readonly LiquidBottle Bottle;
            public readonly int ModelVersion;
            public readonly float From;
            public readonly float To;
            public readonly float RiseSeconds;
            public readonly float StartTime;
            public float Elapsed;
            public float LastVolume;

            public UndoRefillContext(BartenderShelfLevelView view, int glassId, LiquidBottle bottle,
                int modelVersion, float from, float to, float riseSeconds, float startTime)
            {
                View = view;
                GlassId = glassId;
                Bottle = bottle;
                ModelVersion = modelVersion;
                From = from;
                To = to;
                RiseSeconds = riseSeconds;
                StartTime = startTime;
                LastVolume = from;
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

        private readonly List<BartenderPourOperation> activeTransactions =
            new List<BartenderPourOperation>(4);
        private readonly List<PourAnimator> pooledAnimators = new List<PourAnimator>(4);
        private BartenderPourOperation ActiveUndoTransaction =>
            activeTransactions.Find(transaction => transaction.IsUndo);
        private long nextPresentationRunId;
        private int presentationGeneration;
        private long nextCompletionTailRunId;
        private readonly Dictionary<int, CompletionTailContext> completionTails =
            new Dictionary<int, CompletionTailContext>();
        private readonly Stack<BottleCompletionEffect> completionEffectPool =
            new Stack<BottleCompletionEffect>();
        private readonly HashSet<BottleCompletionEffect> ownedCompletionEffects =
            new HashSet<BottleCompletionEffect>();
        private readonly List<CompletionTailContext> completionTailScratch =
            new List<CompletionTailContext>(8);
        private UndoRefillContext undoRefill;
        private PourContactEffect undoRefillEffect;
        private readonly object selectionSeatLeaseOwner = new object();
        // Pour and delivery taps wait here, in tap order, while another command saves.
        private readonly BartenderCommandLane commandLane = new BartenderCommandLane();
        private readonly object commandLaneSeatLeaseOwner = new object();
        private readonly List<BartenderCommandIntent> droppedIntentScratch =
            new List<BartenderCommandIntent>(BartenderCommandLane.Capacity);
        private bool pumpingCommandLane;
        // A tap that cannot run for this long is dropped with the refusal cue and logged; it was never saved or shown.
        // The wait counts from the tap or from the last lane tap that started running, whichever is later, so a lane
        // that keeps moving never drops a tap and a stuck writer drops them all.
        private const double CommandLaneMaxWaitSeconds = 2d;
        private double commandLaneProgressRealtime = double.NegativeInfinity;
        private const string CommandLaneBusyRejection = "Saving previous tap.";
        private long refusalCueSerial;
        private const float SeatCatchUpMaxSeconds = 0.08f;

        private enum SelectionRelease
        {
            KeepPose,
            Glide,
            Snap,
        }

        private sealed class PutDownGlide
        {
            public readonly LiquidBottle Bottle;
            public readonly Transform MotionRoot;
            public readonly int GlassId;
            public readonly float LandDistance;
            public float Elapsed;

            public PutDownGlide(LiquidBottle bottle, Transform motionRoot, int glassId, float landDistance)
            {
                Bottle = bottle;
                MotionRoot = motionRoot;
                GlassId = glassId;
                LandDistance = landDistance;
            }
        }

        private readonly List<PutDownGlide> putDownGlides = new List<PutDownGlide>(4);
        private const float PutDownLandShare = 0.02f;
        // Safety net for a glide whose seat keeps moving away: it lands after this long, once no wobble plays.
        private const float PutDownMaxSeconds = 0.6f;

        // Order cards moving no longer make the board busy; see BoardPresentationSettling for idle-board checks.
        public bool Busy => activeTransactions.Count > 0
                         || (pourAnimator != null && pourAnimator.Busy);
        internal bool BoardPresentationSettling =>
            (orderStrip != null && orderStrip.PresentationActive)
            || (shelfView != null && (shelfView.DeparturesPlaying || shelfView.CompactionPlaying
                                      || shelfView.CompactionPending))
            || commandLane.HasPending;
        internal PourAnimator FlowAnimator
        {
            get
            {
                foreach (BartenderPourOperation transaction in activeTransactions)
                    if (transaction.Animator != null && transaction.Animator.Phase == PourPhase.Flow)
                        return transaction.Animator;
                return null;
            }
        }
        public string LastRejection { get; private set; }
        public BartenderLevelController Controller => controller;
        public BartenderShelfLevelView ShelfView => shelfView;
        public PourAnimator Animator => pourAnimator;
        public BartenderSession Session => session;
        public Camera InputCamera => ResolveCamera();

        private bool PourInputBlocked => activeTransactions.Exists(transaction => transaction.IsUndo)
            || (pourAnimator != null && pourAnimator.Busy
                && !activeTransactions.Exists(transaction => transaction.Animator == pourAnimator));

        private bool OwnsTransaction(BartenderPourOperation transaction) =>
            transaction != null && transaction.Lifecycle.IsActive && activeTransactions.Contains(transaction);

        private bool IsGlassPouring(int glassId) => glassId >= 0
            && (activeTransactions.Exists(transaction => transaction.SourceGlassId == glassId
                    || transaction.TargetGlassId == glassId)
                || (controller != null && controller.IsGlassPresentationLocked(glassId)));

        internal bool IsGlassBusy(int glassId) => IsGlassPouring(glassId) || commandLane.Reserves(glassId);

        private PourAnimator AcquirePourAnimator()
        {
            if (pourAnimator != null && !pourAnimator.Busy
                && !activeTransactions.Exists(transaction => transaction.Animator == pourAnimator))
                return pourAnimator;
            foreach (PourAnimator animator in pooledAnimators)
                if (animator != null && animator.isActiveAndEnabled && !animator.Busy
                    && !activeTransactions.Exists(transaction => transaction.Animator == animator))
                    return animator;
            PourAnimator created = pourAnimator != null
                ? pourAnimator.CreateIndependentAnimator(transform) : null;
            if (created != null) pooledAnimators.Add(created);
            return created;
        }

        private void SettleAllPresentationTransactions(bool reconcile, bool attemptOnly = false)
        {
            foreach (BartenderPourOperation transaction in activeTransactions.ToArray())
            {
                bool refresh = false;
                try
                {
                    refresh = reconcile && (attemptOnly
                        ? CanReconcileTransactionBoardForAttempt(transaction)
                        : CanReconcileTransactionBoard(transaction));
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                }
                finally
                {
                    TrySettlePresentationTransaction(transaction, refresh, true);
                }
            }
        }

        private static bool GlassMatches(RtGlass current, RtGlass expected)
        {
            if (current == null || expected == null || current.Id != expected.Id
                || current.Type != expected.Type || current.UnlockAfter != expected.UnlockAfter
                || current.Layers.Count != expected.Layers.Count) return false;
            for (int i = 0; i < current.Layers.Count; i++)
                if (!current.Layers[i].Equals(expected.Layers[i])) return false;
            return true;
        }

        public event Action<int> SelectionChanged;

        public bool TrySetInputPolicy(IBartenderInputPolicy policy)
        {
            if (!CanSetInputPolicy(policy)) return false;
            inputPolicy = policy;
            return true;
        }

        internal bool CanSetInputPolicy(IBartenderInputPolicy policy)
        {
            if (policy == null) return false;
            DropDestroyedInputPolicy();
            return inputPolicy == null || ReferenceEquals(inputPolicy, policy);
        }

        public bool ClearInputPolicy(IBartenderInputPolicy policy)
        {
            if (policy == null || !ReferenceEquals(inputPolicy, policy)) return false;
            inputPolicy = null;
            return true;
        }

        public void ClearSelectionForModal()
        {
            FinishUndoRefill();
            ClearSelection(SelectionRelease.Snap);
            LandAllPutDowns();
        }

        private void Awake() => ResolveDependencies();

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
        }

        private void OnDisable()
        {
            presentationGeneration++;
            Exception cleanupFailure = ExecuteLifecycleCleanup(
                CancelCommandLane,
                () => SettleAllPresentationTransactions(true),
                Unsubscribe,
                () => CancelAllCompletionTails(true),
                () => FinishUndoRefill(),
                () => CancelRejectionFeedbacks(true),
                () => ClearSelection(SelectionRelease.Snap),
                LandAllPutDowns,
                () => inputPolicy = null);
            if (cleanupFailure != null) Debug.LogException(cleanupFailure, this);
        }

        private void OnDestroy()
        {
            CancelAllCompletionTails(false);
            FinishUndoRefill();
            if (undoRefillEffect != null) Destroy(undoRefillEffect.gameObject);
            undoRefillEffect = null;
            foreach (PourAnimator animator in pooledAnimators)
                if (animator != null) Destroy(animator.gameObject);
            pooledAnimators.Clear();
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
            PumpCommandLane();
            TrySettleExpiredPresentations(Time.realtimeSinceStartupAsDouble);
            AdvanceUndoRefill();
            AnimateSelection();
            AdvancePutDowns();
        }

        internal void HandleRoutedPointerDown(Vector2 screenPoint)
        {
            if (!isActiveAndEnabled) return;
            if (TryHandleTerminalPointer()) return;
            if (!CanReadPointer()) return;
            HandlePointerDown(screenPoint);
        }

        public Task<BartenderCommandResult<bool>> PurchaseAndAnimateUndoAsync() =>
            PurchaseAndAnimateUndoCoreAsync(true);

        private async Task<BartenderCommandResult<bool>> PurchaseAndAnimateUndoCoreAsync(
            bool useAsyncPersistence)
        {
            string rejectionReason = null;
            BartenderCommandResult<bool> Complete(bool succeeded) => succeeded
                ? BartenderCommandResult<bool>.Success(true)
                : BartenderCommandResult<bool>.Rejected(rejectionReason);
            LastRejection = null;
            ResolveDependencies();
            if (!isActiveAndEnabled || controller == null || shelfView == null
                || pourAnimator == null || session == null
                || !ReferenceEquals(session.Controller, controller))
                return Complete(Reject("Undo presentation missing.", out rejectionReason));
            if (!session.AcceptsInput || !shelfView.Ready || shelfView.SeatAnimationPlaying
                || shelfView.SynchronizationDeferred || Busy || commandLane.HasPending
                || controller.PersistencePending || controller.PresentationLocked)
                return Complete(Reject("Scene is busy.", out rejectionReason));
            if (!controller.CanPurchaseUndo(out rejectionReason)) return Complete(false);

            CancelAllCompletionTails(true);
            FinishUndoRefill();
            CancelRejectionFeedbacks(true);
            ClearSelection(SelectionRelease.Snap);
            LandAllPutDowns();
            BsBoard before = controller.Board;
            object owner = new object();
            if (!shelfView.TryBeginSynchronizationDeferral(
                    owner, out BsShelfSynchronizationLease lease))
                return Complete(Reject("Glass sync pending.",
                    out rejectionReason));
            BartenderPourOperation transaction = BeginPresentationTransaction(
                controller, shelfView, pourAnimator, lease, owner, -1, -1, true);

            bool committed;
            try
            {
                if (useAsyncPersistence)
                {
                    if (!transaction.TryBeginSaving())
                        throw new InvalidOperationException("Undo reservation is no longer active.");
                    BartenderCommandResult<bool> result =
                        await transaction.Controller.PurchaseUndoAsync();
                    committed = result.Succeeded;
                    rejectionReason = result.RejectionReason;
                    if (this == null || !OwnsTransaction(transaction))
                        return Complete(committed);
                }
                else committed = transaction.Controller.TryPurchaseUndo(out rejectionReason);
            }
            catch
            {
                TrySettlePresentationTransaction(
                    transaction, CanReconcileTransactionBoard(transaction), true, BsPourSettlementReason.Faulted);
                throw;
            }
            if (!committed)
            {
                TrySettlePresentationTransaction(transaction, false, false, BsPourSettlementReason.Rejected);
                return Complete(false);
            }

            int refillGlassId = -1;
            int refillAmount = 0;
            int refillUnitCount = 0;
            try
            {
                // Commit callbacks can disable this component or replace the round. Never animate an old
                // purchase onto the new board, and never charge again if the visual cannot start.
                if (transaction.UndoChange != null
                    && CanContinueTransaction(transaction, controller, shelfView, pourAnimator))
                {
                    BsBoard restored = transaction.Controller.Board;
                    if (TryResolveUndoPour(before, restored,
                            out _, out int refilledId, out int amount))
                    {
                        refillGlassId = refilledId;
                        refillAmount = amount;
                        refillUnitCount = restored.GlassById(refilledId).Layers.Count;
                    }
                }
            }
            catch (Exception exception)
            {
                refillGlassId = -1;
                Debug.LogException(exception, this);
            }
            finally
            {
                TrySettlePresentationTransaction(
                    transaction, CanReconcileTransactionBoard(transaction), true,
                    transaction.State == BsPourOperationState.Committed
                        ? BsPourSettlementReason.PresentedImmediately : BsPourSettlementReason.Faulted);
            }
            if (refillGlassId >= 0)
                TryStartUndoRefill(transaction, refillGlassId, refillAmount, refillUnitCount);
            return Complete(true);
        }

        private void TryStartUndoRefill(BartenderPourOperation transaction, int glassId, int amount,
            int expectedUnitCount)
        {
            FinishUndoRefill();
            BartenderShelfLevelView view = transaction != null ? transaction.View : null;
            if (!isActiveAndEnabled || view == null || controller == null || amount <= 0
                || !ReferenceEquals(shelfView, view)
                || !ReferenceEquals(controller, transaction.Controller)
                || controller.State != BartenderLevelState.Playing
                || !IsTransactionRoundCurrent(transaction)
                || !view.Ready || view.GlobalSynchronizationDeferred
                || view.IsGlassSynchronizationDeferred(glassId)
                || !view.TryGetBottle(glassId, out LiquidBottle bottle)
                || bottle == null || !bottle.isActiveAndEnabled || bottle.IsTransferReserved
                || bottle.UnitCount != expectedUnitCount || amount > bottle.UnitCount)
                return;

            float settled = bottle.UnitCount;
            var refill = new UndoRefillContext(view, glassId, bottle, bottle.ModelVersion,
                settled - amount, settled,
                UndoRefillRiseSeconds + UndoRefillExtraUnitSeconds * (amount - 1), Time.unscaledTime);
            undoRefill = refill;
            try
            {
                bottle.DisplayVolume = refill.From;
                refill.LastVolume = bottle.DisplayVolume;
                bottle.RefreshIfNeeded();
                PourContactEffect effect = AcquireUndoRefillEffect();
                if (effect != null)
                    effect.Begin(bottle, bottle.TopColor, bottle.MouthWorld.x, settled, true);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                FinishUndoRefill();
            }
        }

        private void AdvanceUndoRefill()
        {
            UndoRefillContext refill = undoRefill;
            if (refill == null) return;
            refill.Elapsed += Mathf.Min(Time.unscaledDeltaTime, UndoRefillMaxStepSeconds);
            float elapsed = refill.Elapsed;
            float duration = refill.RiseSeconds + UndoRefillSettleSeconds;
            if (elapsed >= duration || !UndoRefillIsCurrent(refill)
                || Time.unscaledTime - refill.StartTime > duration + UndoRefillWallClockGraceSeconds)
            {
                FinishUndoRefill();
                return;
            }

            bool rising = elapsed < refill.RiseSeconds;
            float rise = rising ? Mathf.Clamp01(elapsed / refill.RiseSeconds) : 1f;
            LiquidBottle bottle = refill.Bottle;
            bottle.DisplayVolume = Mathf.Lerp(refill.From, refill.To, UndoRefillLevelProgress(rise));
            refill.LastVolume = bottle.DisplayVolume;
            bottle.RefreshIfNeeded();
            if (undoRefillEffect == null) return;
            if (rising)
                undoRefillEffect.RenderFrame(rise,
                    Mathf.Clamp01((rise - UndoRefillTailStart) / (1f - UndoRefillTailStart)), 0f);
            else
                undoRefillEffect.RenderFrame(1f, 1f,
                    (elapsed - refill.RiseSeconds) / UndoRefillSettleSeconds);
        }

        private bool UndoRefillIsCurrent(UndoRefillContext refill)
        {
            LiquidBottle bottle = refill.Bottle;
            return isActiveAndEnabled && bottle != null && bottle.isActiveAndEnabled
                && refill.View != null && ReferenceEquals(shelfView, refill.View)
                && refill.View.TryGetBottle(refill.GlassId, out LiquidBottle current)
                && ReferenceEquals(current, bottle)
                && bottle.ModelVersion == refill.ModelVersion
                && !bottle.IsTransferReserved
                && Mathf.Abs(bottle.DisplayVolume - refill.LastVolume) <= UndoRefillVolumeTolerance;
        }

        private void FinishUndoRefill(int glassId)
        {
            if (undoRefill != null && undoRefill.GlassId == glassId) FinishUndoRefill();
        }

        private void FinishUndoRefill()
        {
            UndoRefillContext refill = undoRefill;
            if (refill == null) return;
            undoRefill = null;
            if (undoRefillEffect != null) undoRefillEffect.Clear();
            LiquidBottle bottle = refill.Bottle;
            if (bottle == null || bottle.ModelVersion != refill.ModelVersion || bottle.IsTransferReserved
                || Mathf.Abs(bottle.DisplayVolume - refill.LastVolume) > UndoRefillVolumeTolerance)
                return;
            bottle.DisplayVolume = bottle.UnitCount;
            if (bottle.isActiveAndEnabled) bottle.RefreshIfNeeded();
        }

        private PourContactEffect AcquireUndoRefillEffect()
        {
            if (undoRefillEffect != null) return undoRefillEffect;
            PourContactEffect template = pourAnimator != null ? pourAnimator.ContactEffectTemplate : null;
            if (template == null) return null;
            undoRefillEffect = Instantiate(template, transform);
            undoRefillEffect.name = template.name + " (Undo Refill)";
            return undoRefillEffect;
        }

        private static float UndoRefillLevelProgress(float t)
        {
            t = Mathf.Clamp01(t);
            float slope = 1f / (1f - UndoRefillEaseShare * 0.5f);
            if (t <= 1f - UndoRefillEaseShare) return slope * t;
            float remaining = 1f - t;
            return 1f - slope * remaining * remaining / (2f * UndoRefillEaseShare);
        }

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

        private async Task<BartenderCommandResult<bool>> CommitAndAnimatePourCoreAsync(
            int sourceGlassId, int targetGlassId, bool useAsyncPersistence,
            double? tapClock = null, bool fromCommandLane = false)
        {
            string rejectionReason = null;
            BartenderCommandResult<bool> Complete(bool succeeded) => succeeded
                ? BartenderCommandResult<bool>.Success(true)
                : BartenderCommandResult<bool>.Rejected(rejectionReason);
            BartenderCommandResult<bool> RefuseDirect(bool _)
            {
                if (!fromCommandLane) PutDownRefusedSource(sourceGlassId);
                return Complete(false);
            }
            LastRejection = null;
            ResolveDependencies();

            if (!isActiveAndEnabled)
                return RefuseDirect(Reject("Gameplay inactive.", out rejectionReason));
            // Direct calls cannot jump ahead of taps already waiting in the input lane.
            if (!fromCommandLane && commandLane.HasPending)
                return RefuseDirect(Reject(CommandLaneBusyRejection, out rejectionReason));

            if (!CheckInputPolicy(
                    BartenderInputRequest.Pour(sourceGlassId, targetGlassId),
                    out rejectionReason))
                return RefuseDirect(false);

            if (controller == null || shelfView == null || pourAnimator == null
                || session == null)
                return RefuseDirect(Reject("Gameplay rig incomplete.",
                              out rejectionReason));
            if (!ReferenceEquals(session.Controller, controller))
                return RefuseDirect(Reject("Session uses another controller.",
                              out rejectionReason));
            if (!session.AcceptsInput)
                return RefuseDirect(Reject("Session rejects input.",
                              out rejectionReason));
            if (!shelfView.Ready || shelfView.BlockingSeatRunPlaying
                || shelfView.GlobalSynchronizationDeferred || PourInputBlocked
                || controller.PourInputBlocked)
                return RefuseDirect(Reject("Scene is busy.",
                              out rejectionReason));
            if (IsGlassPouring(sourceGlassId) || IsGlassPouring(targetGlassId))
                return RefuseDirect(Reject("Glass is already pouring.", out rejectionReason));
            if (!shelfView.TryGetBottle(sourceGlassId, out LiquidBottle source)
                || !shelfView.TryGetBottle(targetGlassId, out LiquidBottle target))
                return RefuseDirect(Reject("Glass binding missing.",
                              out rejectionReason));
            // PourAnimator starts from the drawn level, so undo's cosmetic rise must end first.
            FinishUndoRefill(sourceGlassId);
            FinishUndoRefill(targetGlassId);
            SettleCompletionTailOn(sourceGlassId, true);
            SettleCompletionTailOn(targetGlassId, true);

            PourResult rule = controller.CanPour(sourceGlassId, targetGlassId);
            if (!rule.Success)
            {
                BsAudio.Instance?.Play(BsSfx.Invalid);
                PlayRejectedPourFeedback(source, target);
                Reject(rule.Reason, out rejectionReason);
                PutDownRefusedSource(sourceGlassId);
                return Complete(false);
            }

            // Stop only our feedback tweens and restore rotations before PourAnimator takes these roots. Do
            // not use transform.DOKill.
            CancelRejectionFeedback(source, true);
            CancelRejectionFeedback(target, true);

            if (!shelfView.TryGetSeatPose(sourceGlassId,
                    out BartenderGlassSeatPose home))
                return RefuseDirect(Reject("Source seat missing.",
                              out rejectionReason));

            PourAnimator selectedAnimator = AcquirePourAnimator();
            if (selectedAnimator == null)
                return RefuseDirect(Reject("Pour animation unavailable.", out rejectionReason));
            object transactionOwner = new object();
            if (!shelfView.TryBeginSynchronizationDeferral(
                    transactionOwner, sourceGlassId, targetGlassId,
                    out BsShelfSynchronizationLease synchronizationLease))
                return RefuseDirect(Reject("Glass sync pending.",
                              out rejectionReason));

            BartenderLevelController committedController = controller;
            BartenderShelfLevelView deferredView = shelfView;
            BartenderPourOperation transaction = BeginPresentationTransaction(
                committedController, deferredView, selectedAnimator,
                synchronizationLease, transactionOwner, sourceGlassId, targetGlassId, false);

            BartenderPourReceipt receipt;
            string domainRejection;
            bool committed;
            try
            {
                if (useAsyncPersistence)
                {
                    if (!transaction.TryBeginSaving())
                        throw new InvalidOperationException("Pour reservation is no longer active.");
                    BartenderCommandResult<BartenderPourReceipt> result =
                        await committedController.PourAsync(sourceGlassId, targetGlassId, tapClock);
                    committed = result.Succeeded;
                    receipt = result.Value;
                    domainRejection = result.RejectionReason;
                    if (this == null || !OwnsTransaction(transaction))
                    {
                        rejectionReason = domainRejection;
                        return Complete(committed);
                    }
                }
                else committed = committedController.TryPour(
                    sourceGlassId, targetGlassId, out receipt, out domainRejection);
            }
            catch
            {
                TrySettlePresentationTransaction(transaction, true, true, BsPourSettlementReason.Faulted);
                throw;
            }
            if (!committed)
            {
                TrySettlePresentationTransaction(transaction, false, false, BsPourSettlementReason.Rejected);
                BsAudio.Instance?.Play(BsSfx.Invalid);
                PlayRejectedPourFeedback(source, target);
                Reject(domainRejection, out rejectionReason);
                PutDownRefusedSource(sourceGlassId);
                return Complete(false);
            }

            // Only the receiving glass can newly match a full order. Capture the durable receipt
            // and this presentation decision together before acquiring animation resources.
            int completionGlassId = receipt != null && BecameMatched(
                committedController, receipt.TargetBefore, receipt.TargetAfter) ? targetGlassId : -1;
            if (OwnsTransaction(transaction))
                transaction.TryCommitPour(receipt, completionGlassId, Time.realtimeSinceStartupAsDouble);

            if (!CanContinueTransaction(transaction, committedController,
                                        deferredView, selectedAnimator))
            {
                bool stillOwnsTransaction =
                    OwnsTransaction(transaction);
                if (stillOwnsTransaction)
                {
                    TrySettlePresentationTransaction(
                        transaction, CanReconcileTransactionBoard(transaction), false, BsPourSettlementReason.Invalidated);
                    ClearSelectionOfPour(sourceGlassId, targetGlassId, SelectionRelease.Glide);
                }
                LastRejection = "Pour saved; scene changed.";
                rejectionReason = LastRejection;
                return Complete(true);
            }

            if (!transaction.TryAcquirePresentationLock())
            {
                HoldCompletionBadge(deferredView, completionGlassId);
                TrySettlePresentationTransaction(transaction, true, true,
                    transaction.State == BsPourOperationState.Committed
                        ? BsPourSettlementReason.PresentedImmediately : BsPourSettlementReason.Faulted);
                if (CanStartCompletionPresentation(
                        committedController, deferredView, completionGlassId, receipt))
                    TryStartCompletionTail(deferredView, completionGlassId, receipt);
                else if (IsCurrentPourReceipt(committedController, receipt))
                    ReconcileCompletionBadge(deferredView, completionGlassId);
                LastRejection = "Pour saved; no presentation lock.";
                rejectionReason = LastRejection;
                ClearSelectionOfPour(sourceGlassId, targetGlassId, SelectionRelease.Glide);
                return Complete(true);
            }

            SnapPutDown(targetGlassId);
            // A receiving glass that is still sliding is put on its new seat while the animator aims at its mouth, then
            // glides the rest of the way in a few frames, inside the carry, so it never jumps and the stream still hits
            // its mouth. The source leaves the slide from where it is; the animator carries it.
            bool targetCatchesUp = deferredView.FinishSeatMotion(targetGlassId);

            // Different colours may stack under BsBoard.CanPour, so this must pass false.
            bool animationStarted;
            try
            {
                animationStarted = transaction.TryStartAnimation(
                    source, target, receipt.Amount, home, HandlePourFinished);
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
                TrySettlePresentationTransaction(transaction, true, true,
                    transaction.State == BsPourOperationState.Committed
                        ? BsPourSettlementReason.PresentedImmediately : BsPourSettlementReason.Faulted);
                if (CanStartCompletionPresentation(
                        committedController, deferredView, pendingGlassId, receipt))
                    TryStartCompletionTail(deferredView, pendingGlassId, receipt);
                else if (IsCurrentPourReceipt(committedController, receipt))
                    ReconcileCompletionBadge(deferredView, pendingGlassId);
                LastRejection = "Pour saved; animation unavailable.";
                rejectionReason = LastRejection;
                ClearSelectionOfPour(sourceGlassId, targetGlassId, SelectionRelease.Glide);
                return Complete(true);
            }

            if (targetCatchesUp)
                deferredView.StartSeatCatchUp(targetGlassId,
                    Mathf.Min(SeatCatchUpMaxSeconds, selectedAnimator.moveTime * 0.3f));
            ClearSelectionOfPour(sourceGlassId, targetGlassId, SelectionRelease.KeepPose);
            StopPutDown(sourceGlassId, true);
            return Complete(true);
        }

        private void ClearSelectionOfPour(int sourceGlassId, int targetGlassId, SelectionRelease release)
        {
            if (selectedGlassId < 0
                || (selectedGlassId != sourceGlassId && selectedGlassId != targetGlassId)) return;
            ClearSelection(release);
        }

        private void PutDownRefusedSource(int sourceGlassId)
        {
            if (sourceGlassId >= 0 && selectedGlassId == sourceGlassId) ClearSelection(SelectionRelease.Glide);
        }

        private async Task<BartenderCommandResult<bool>> CommitDeliveryCoreAsync(
            int glassId, bool useAsyncPersistence,
            double? tapClock = null, bool fromCommandLane = false)
        {
            string rejectionReason = null;
            BartenderCommandResult<bool> Complete(bool succeeded) => succeeded
                ? BartenderCommandResult<bool>.Success(true)
                : BartenderCommandResult<bool>.Rejected(rejectionReason);
            LastRejection = null;
            ResolveDependencies();

            if (!isActiveAndEnabled)
                return Complete(Reject("Gameplay inactive.", out rejectionReason));
            // Direct calls cannot jump ahead of taps already waiting in the input lane.
            if (!fromCommandLane && commandLane.HasPending)
                return Complete(Reject(CommandLaneBusyRejection, out rejectionReason));

            if (!CheckInputPolicy(BartenderInputRequest.Delivery(glassId),
                                  out rejectionReason))
                return Complete(false);

            if (controller == null || shelfView == null || session == null)
                return Complete(Reject("Gameplay rig incomplete.",
                              out rejectionReason));
            if (!ReferenceEquals(session.Controller, controller))
                return Complete(Reject("Session uses another controller.",
                              out rejectionReason));
            if (!session.AcceptsInput)
                return Complete(Reject("Session rejects input.",
                              out rejectionReason));
            // Other pours, a closing gap and leaving order cards do not hold a delivery. Only the glass's own pour, undo,
            // a blocking seat run and modal states do. The running lane tap still holds its own reservation.
            if (!shelfView.Ready || shelfView.BlockingSeatRunPlaying
                || shelfView.GlobalSynchronizationDeferred
                || controller.ModalInputBlocked || IsGlassPouring(glassId))
                return Complete(Reject("Scene is busy.",
                              out rejectionReason));
            if (!shelfView.TryGetBottle(glassId, out LiquidBottle deliveryBottle))
                return Complete(Reject("Glass binding missing.",
                              out rejectionReason));
            FinishUndoRefill(glassId);
            SettleCompletionTailOn(glassId, true);
            if (controller.MatchedOrderSlot(glassId) < 0)
            {
                BsAudio.Instance?.Play(BsSfx.Invalid);
                PlayRejectionFeedback(deliveryBottle, rejectedTargetColor,
                    targetRejectionWobble);
                return Complete(Reject("Delivery glass is not ready.",
                              out rejectionReason));
            }
            DeliveryBadgePresenter deliveryBadges =
                ResolveDeliveryBadges(shelfView);
            if (deliveryBadges != null
                && !deliveryBadges.IsReadyForDelivery(deliveryBottle))
                return Complete(Reject("Delivery not ready.",
                              out rejectionReason));

            CancelRejectionFeedback(deliveryBottle, true);
            if (selectedGlassId == glassId) ClearSelection(SelectionRelease.Snap);
            SnapPutDown(glassId);
            BartenderLevelController ownerController = controller;
            BsRoundCommandStamp startStamp = ownerController.CurrentRoundStamp;
            int generation = presentationGeneration;
            bool committed;
            string domainRejection;
            if (useAsyncPersistence)
            {
                BartenderCommandResult<BartenderDeliveryReceipt> result =
                    await ownerController.DeliverAsync(glassId, tapClock);
                committed = result.Succeeded;
                domainRejection = result.RejectionReason;
                if (!IsAsyncPresentationCurrent(ownerController, startStamp, generation))
                {
                    rejectionReason = domainRejection;
                    return Complete(committed);
                }
            }
            else committed = ownerController.TryDeliver(
                glassId, out _, out domainRejection);
            if (!committed)
            {
                BsAudio.Instance?.Play(BsSfx.Invalid);
                PlayRejectionFeedback(deliveryBottle, rejectedTargetColor,
                    targetRejectionWobble);
                return Complete(Reject(domainRejection, out rejectionReason));
            }
            return Complete(true);
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
                && !shelfView.BlockingSeatRunPlaying
                && !shelfView.GlobalSynchronizationDeferred
                && !PourInputBlocked;
        }

        private bool CanReadThroughOwnedModalBarrier()
        {
            if (controller == null || !controller.ModalInputBlocked) return true;
            DropDestroyedInputPolicy();
            return inputPolicy != null
                && controller.IsPresentationBarrierExclusivelyOwnedBy(inputPolicy);
        }

        private bool TryHandleTerminalPointer()
        {
            if (controller == null || shelfView == null || session == null
                || !ReferenceEquals(session.Controller, controller))
                return false;

            bool continueAfterWin = session.CanContinueAfterWin;
            bool retryAfterFailure = session.CanRetryAfterFailure;
            if (!continueAfterWin && !retryAfterFailure) return false;
            if (Busy || controller.PresentationLocked || !shelfView.Ready
                || shelfView.SeatAnimationPlaying || shelfView.SynchronizationDeferred
                || BoardPresentationSettling)
                return false;
            if (!CheckInputPolicy(
                    BartenderInputRequest.Background(selectedGlassId), out _))
                return true;

            bool accepted = continueAfterWin
                ? session.RequestContinueAfterWin()
                : session.RequestRetryAfterFailure();
            if (!accepted)
                LastRejection = "Terminal request expired.";
            return true;
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
                ClearSelectionWithSound(SelectionRelease.Glide);
                return;
            }

            FinishUndoRefill(hitId);
            if (IsGlassBusy(hitId)) return;

            BartenderInputRequest bottleTap =
                BartenderInputRequest.Bottle(hitId, selectedGlassId);
            if (!CheckInputPolicy(bottleTap, out _))
                return;
            if (TryConsumeAcceptedInput(bottleTap)) return;
            // The order-ready flourish is cosmetic: a tap cuts it and makes its badge ready, then the tap goes on.
            SettleCompletionTailOn(hitId, true);

            if (controller != null && controller.MatchedOrderSlot(hitId) >= 0)
            {
                DeliveryBadgePresenter badges = ResolveDeliveryBadges(shelfView);
                if (badges != null && !badges.IsReadyForDelivery(hit)) return;
                // Other pours, leaving cards and a closing gap do not hold a delivery; the lane keeps tap order.
                ClearSelectionWithSound(SelectionRelease.Glide);
                RequestDelivery(hitId);
                return;
            }

            if (selectedBottle != null && commandLane.Reserves(selectedGlassId))
            {
                SelectIfUsable(hit, hitId);
                return;
            }

            if (selectedBottle == null)
            {
                SelectIfUsable(hit, hitId);
                return;
            }

            if (hit == selectedBottle)
            {
                ClearSelectionWithSound(SelectionRelease.Glide);
                return;
            }

            int sourceId = selectedGlassId;
            RequestPour(sourceId, hitId);
        }

        private bool IsAsyncPresentationCurrent(BartenderLevelController owner,
            BsRoundCommandStamp startStamp, int generation)
        {
            if (this == null || !isActiveAndEnabled || presentationGeneration != generation
                || owner == null || owner.State != BartenderLevelState.Playing
                || !ReferenceEquals(controller, owner)) return false;
            BsRoundCommandStamp current = owner.CurrentRoundStamp;
            return startStamp.IsValid && current.IsValid
                && startStamp.AttemptId == current.AttemptId
                && startStamp.Token == current.Token;
        }

        public void RequestDelivery(int glassId)
        {
            if (!TryAdmitDeliveryTap(glassId, out BartenderCommandIntent intent)
                || !commandLane.TryEnqueue(intent))
                return;
            SyncCommandLane();
            PumpCommandLane();
        }

        private void RequestPour(int sourceGlassId, int targetGlassId)
        {
            if (!TryAdmitPourTap(sourceGlassId, targetGlassId, out BartenderCommandIntent intent)
                || !commandLane.TryEnqueue(intent))
            {
                if (selectedGlassId == sourceGlassId) ClearSelection(SelectionRelease.Glide);
                return;
            }
            SyncCommandLane();
            PumpCommandLane();
        }

        private bool TryAdmitPourTap(int sourceGlassId, int targetGlassId,
            out BartenderCommandIntent intent)
        {
            intent = null;
            ResolveDependencies();
            if (!isActiveAndEnabled || controller == null || shelfView == null
                || pourAnimator == null || session == null
                || !ReferenceEquals(session.Controller, controller)
                || !session.AcceptsInput || controller.State != BartenderLevelState.Playing)
                return false;
            if (!CheckInputPolicy(BartenderInputRequest.Pour(sourceGlassId, targetGlassId), out _))
                return false;
            if (controller.ModalInputBlocked || !shelfView.Ready
                || shelfView.BlockingSeatRunPlaying || shelfView.GlobalSynchronizationDeferred)
                return false;
            if (controller.PauseRequestPending) return false;
            if (IsGlassBusy(sourceGlassId) || IsGlassBusy(targetGlassId)) return false;
            if (!shelfView.TryGetBottle(sourceGlassId, out LiquidBottle source)
                || !shelfView.TryGetBottle(targetGlassId, out LiquidBottle target))
                return false;
            if (commandLane.IsFull)
            {
                // Refused before it touches any flourish, with the normal cue, so the tap never vanishes silently.
                BsAudio.Instance?.Play(BsSfx.Invalid);
                PlayRejectedPourFeedback(source, target);
                Reject(CommandLaneBusyRejection, out _);
                return false;
            }
            FinishUndoRefill(sourceGlassId);
            FinishUndoRefill(targetGlassId);
            SettleCompletionTailOn(sourceGlassId, true);
            SettleCompletionTailOn(targetGlassId, true);

            PourResult rule = controller.CanPour(sourceGlassId, targetGlassId);
            if (!rule.Success)
            {
                BsAudio.Instance?.Play(BsSfx.Invalid);
                PlayRejectedPourFeedback(source, target);
                Reject(rule.Reason, out _);
                return false;
            }
            intent = StampIntent(BartenderCommandIntent.Pour(sourceGlassId, targetGlassId));
            return true;
        }

        private bool TryAdmitDeliveryTap(int glassId, out BartenderCommandIntent intent)
        {
            intent = null;
            ResolveDependencies();
            if (!isActiveAndEnabled || controller == null || shelfView == null || session == null
                || !ReferenceEquals(session.Controller, controller)
                || !session.AcceptsInput || controller.State != BartenderLevelState.Playing)
                return false;
            if (!CheckInputPolicy(BartenderInputRequest.Delivery(glassId), out _))
                return false;
            if (controller.ModalInputBlocked || !shelfView.Ready
                || shelfView.BlockingSeatRunPlaying || shelfView.GlobalSynchronizationDeferred)
                return false;
            if (controller.PauseRequestPending) return false;
            if (!shelfView.TryGetBottle(glassId, out LiquidBottle bottle)) return false;
            if (IsGlassBusy(glassId)) return false;
            if (commandLane.IsFull)
            {
                // Refused before it cuts any flourish, with the normal cue, so the tap never vanishes silently.
                BsAudio.Instance?.Play(BsSfx.Invalid);
                PlayRejectionFeedback(bottle, rejectedTargetColor, targetRejectionWobble);
                Reject(CommandLaneBusyRejection, out _);
                return false;
            }
            FinishUndoRefill(glassId);
            SettleCompletionTailOn(glassId, true);
            if (controller.MatchedOrderSlot(glassId) < 0)
            {
                BsAudio.Instance?.Play(BsSfx.Invalid);
                PlayRejectionFeedback(bottle, rejectedTargetColor, targetRejectionWobble);
                Reject("Delivery glass is not ready.", out _);
                return false;
            }
            DeliveryBadgePresenter badges = ResolveDeliveryBadges(shelfView);
            if (badges != null && !badges.IsReadyForDelivery(bottle)) return false;
            intent = StampIntent(BartenderCommandIntent.Delivery(glassId));
            return true;
        }

        private BartenderCommandIntent StampIntent(BartenderCommandIntent intent)
        {
            BsRoundCommandStamp stamp = controller.CurrentRoundStamp;
            intent.AttemptId = stamp.AttemptId;
            intent.Token = stamp.Token;
            intent.Generation = presentationGeneration;
            intent.TapClock = controller.OrderClockNow;
            intent.TapRealtime = Time.realtimeSinceStartupAsDouble;
            return intent;
        }

        private void PumpCommandLane()
        {
            if (pumpingCommandLane) return;
            pumpingCommandLane = true;
            try
            {
                while (!commandLane.Running && commandLane.TryPeek(out BartenderCommandIntent head))
                {
                    if (!IsCommandIntentCurrent(head, out bool timedOut))
                    {
                        commandLane.Dequeue();
                        if (timedOut)
                            Debug.LogWarning("Input lane dropped a tap that could not run within "
                                + CommandLaneMaxWaitSeconds + " s; it was never saved or shown.", this);
                        SyncCommandLane();
                        ReleaseDroppedIntent(head);
                        if (timedOut) PlayDroppedIntentFeedback(head);
                        continue;
                    }
                    if (controller.CommandWriterBusy || controller.PourInputBlocked
                        || shelfView.GlobalSynchronizationDeferred || shelfView.BlockingSeatRunPlaying
                        || (head.Kind == BartenderCommandIntentKind.Pour && !shelfView.PresentationCurrent))
                        break;
                    commandLane.Dequeue();
                    if (!commandLane.TryBeginRun(head)) break;
                    commandLaneProgressRealtime = Time.realtimeSinceStartupAsDouble;
                    SyncCommandLane();
                    RunCommandIntent(head);
                }
            }
            finally
            {
                pumpingCommandLane = false;
            }
        }

        private bool IsCommandIntentCurrent(BartenderCommandIntent intent, out bool timedOut)
        {
            timedOut = false;
            if (intent == null || !isActiveAndEnabled || controller == null || shelfView == null
                || session == null || !ReferenceEquals(session.Controller, controller)
                || !session.AcceptsInput
                || controller.State != BartenderLevelState.Playing
                || controller.ModalInputBlocked
                || presentationGeneration != intent.Generation)
                return false;
            BsRoundCommandStamp current = controller.CurrentRoundStamp;
            if (!current.IsValid || current.AttemptId != intent.AttemptId || current.Token != intent.Token)
                return false;
            timedOut = Time.realtimeSinceStartupAsDouble - Math.Max(intent.TapRealtime, commandLaneProgressRealtime)
                       > CommandLaneMaxWaitSeconds;
            return !timedOut;
        }

        // Async void is restricted to UI entry points; controller tasks always retain save ownership.
        private async void RunCommandIntent(BartenderCommandIntent intent)
        {
            BartenderLevelController owner = controller;
            BsRoundCommandStamp stamp = owner != null ? owner.CurrentRoundStamp : default;
            int generation = presentationGeneration;
            bool succeeded = false;
            long cueSerial = refusalCueSerial;
            try
            {
                BartenderCommandResult<bool> result = intent.Kind == BartenderCommandIntentKind.Pour
                    ? await CommitAndAnimatePourCoreAsync(intent.SourceId, intent.TargetId, true,
                        intent.TapClock, true)
                    : await CommitDeliveryCoreAsync(intent.GlassId, true, intent.TapClock, true);
                succeeded = result.Succeeded;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                commandLane.EndRun(intent);
                try { SyncCommandLane(); }
                catch (Exception exception) { Debug.LogException(exception); }
            }

            try
            {
                if (this != null && intent.Kind == BartenderCommandIntentKind.Pour)
                {
                    if (!succeeded && IsAsyncPresentationCurrent(owner, stamp, generation)
                        && selectedGlassId == intent.SourceId)
                        ClearSelection(SelectionRelease.Glide);
                    RestoreIdleGlassPose(intent.SourceId);
                    RestoreIdleGlassPose(intent.TargetId);
                }
                // A tap refused for a presentation reason (no wobble, no policy cue) still gets the refusal cue while
                // its round is current, so an accepted tap never disappears silently.
                if (this != null && !succeeded && refusalCueSerial == cueSerial
                    && IsAsyncPresentationCurrent(owner, stamp, generation))
                    PlayDroppedIntentFeedback(intent);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            if (this != null) PumpCommandLane();
        }

        private void CancelCommandLane() => CancelCommandLane(false);

        private void CancelCommandLane(bool playRefusalCue)
        {
            droppedIntentScratch.Clear();
            commandLane.CancelQueued(droppedIntentScratch);
            SyncCommandLane();
            try
            {
                for (int i = 0; i < droppedIntentScratch.Count; i++)
                {
                    ReleaseDroppedIntent(droppedIntentScratch[i]);
                    if (playRefusalCue) PlayDroppedIntentFeedback(droppedIntentScratch[i]);
                }
            }
            finally
            {
                droppedIntentScratch.Clear();
            }
        }

        private void ReleaseDroppedIntent(BartenderCommandIntent intent)
        {
            if (intent == null || intent.Kind != BartenderCommandIntentKind.Pour) return;
            if (selectedGlassId == intent.SourceId) ClearSelection(SelectionRelease.Glide);
            RestoreIdleGlassPose(intent.SourceId);
        }

        private void PlayDroppedIntentFeedback(BartenderCommandIntent intent)
        {
            if (intent == null || shelfView == null) return;
            BsAudio.Instance?.Play(BsSfx.Invalid);
            if (intent.Kind == BartenderCommandIntentKind.Pour)
            {
                LiquidBottle source = shelfView.TryGetBottle(intent.SourceId, out LiquidBottle foundSource)
                    && !IsGlassPouring(intent.SourceId) ? foundSource : null;
                LiquidBottle target = shelfView.TryGetBottle(intent.TargetId, out LiquidBottle foundTarget)
                    && !IsGlassPouring(intent.TargetId) ? foundTarget : null;
                PlayRejectedPourFeedback(source, target);
            }
            else if (shelfView.TryGetBottle(intent.GlassId, out LiquidBottle glass) && !IsGlassPouring(intent.GlassId))
                PlayRejectionFeedback(glass, rejectedTargetColor, targetRejectionWobble);
        }

        private void SyncCommandLane()
        {
            if (controller != null)
            {
                controller.QueuedTapClockFloor = commandLane.EarliestTapClock;
                controller.QueuedCommandCount = commandLane.Count + (commandLane.Running ? 1 : 0);
            }
            if (shelfView == null) return;
            if (commandLane.HasPending)
                shelfView.TryAcquireSeatLease(commandLaneSeatLeaseOwner, -1, false);
            else
                shelfView.ReleaseSeatLease(commandLaneSeatLeaseOwner);
        }

        private void RestoreIdleGlassPose(int glassId)
        {
            if (glassId < 0 || shelfView == null || glassId == selectedGlassId || FindPutDown(glassId) >= 0
                || IsGlassBusy(glassId)
                || HasCompletionTail(glassId) || shelfView.IsSeatMoving(glassId)
                || !shelfView.TryGetSeatPose(glassId, out BartenderGlassSeatPose seat)
                || seat.MotionRoot == null
                || !shelfView.TryGetBottle(glassId, out LiquidBottle bottle)
                || bottle == null || bottle.IsTransferReserved)
                return;
            Transform root = seat.MotionRoot;
            if ((root.position - seat.Position).sqrMagnitude <= 1e-8f
                && (root.localScale - seat.LocalScale).sqrMagnitude <= 1e-8f) return;
            if (TryStartPutDown(bottle, glassId)) return;
            root.position = seat.Position;
            root.localScale = seat.LocalScale;
            BottleShell shell = bottle.GetComponent<BottleShell>();
            if (shell != null) shell.highlight = 0f;
        }

        private void SettleCompletionTailOn(int glassId, bool reconcileBadge)
        {
            if (glassId >= 0 && completionTails.TryGetValue(glassId, out CompletionTailContext tail)
                && tail != null)
                TrySettleCompletionTail(tail, reconcileBadge);
        }

        private void SelectIfUsable(LiquidBottle bottle, int glassId)
        {
            if (bottle == null || controller == null || IsGlassBusy(glassId)
                || HasCompletionTail(glassId) || !controller.CanSelectAsPourSource(glassId))
            {
                ClearSelection(SelectionRelease.Glide);
                return;
            }

            ClearSelection(SelectionRelease.Glide);
            if (!shelfView.TryGetSeatPose(glassId,
                    out BartenderGlassSeatPose home))
                return;
            StopPutDown(glassId, false);
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
            shelfView.TryAcquireSeatLease(selectionSeatLeaseOwner, glassId, true);
            BsAudio.Instance?.Play(BsSfx.GlassPickup);
            NotifySelectionChanged(glassId);
        }

        private void AnimateSelection()
        {
            if (selectedBottle == null || IsGlassPouring(selectedGlassId)) return;

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

        private void ClearSelection(SelectionRelease release)
        {
            int previousGlassId = selectedGlassId;
            LiquidBottle bottle = selectedBottle;
            if (bottle != null)
            {
                // The running lane tap's own reservation never carries the glass (only its pour transaction does), so a
                // source refused inside that tap, or the glass that tap delivers, goes down; a glass a queued pour will
                // carry stays lifted. Glide and snap use the same rule.
                bool holdsPose = IsGlassPouring(previousGlassId) || IsReservedByQueuedTap(previousGlassId);
                // The glide takes its own following seat lease before the selection lease goes below, so a closing
                // gap never finds the glass unowned. If no glide can run, the seat is written as before.
                bool gliding = release == SelectionRelease.Glide && !holdsPose
                    && TryStartPutDown(bottle, previousGlassId);
                bool writeSeat = release == SelectionRelease.Snap
                    ? !holdsPose
                    : release == SelectionRelease.Glide && !holdsPose && !gliding
                      && !bottle.IsTransferReserved && !HasCompletionTail(previousGlassId);
                if (writeSeat)
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
                BottleShell shell = gliding ? null : bottle.GetComponent<BottleShell>();
                if (shell != null) shell.highlight = 0f;
            }
            selectedBottle = null;
            selectedMotionRoot = null;
            selectedGlassId = -1;
            selectedHomePosition = default;
            selectedHomeRotation = Quaternion.identity;
            selectedHomeScale = Vector3.one;
            selectedRoyalRelativeScale = 1f;
            if (shelfView != null) shelfView.ReleaseSeatLease(selectionSeatLeaseOwner);
            if (previousGlassId >= 0) NotifySelectionChanged(-1);
        }

        private void ClearSelectionWithSound(SelectionRelease release)
        {
            bool hadSelection = selectedBottle != null;
            ClearSelection(release);
            if (hadSelection) BsAudio.Instance?.Play(BsSfx.GlassSet);
        }

        private bool IsReservedByQueuedTap(int glassId) =>
            commandLane.Reserves(glassId)
            && !(commandLane.RunningIntent != null && commandLane.RunningIntent.Reserves(glassId));

        private int FindPutDown(int glassId)
        {
            if (glassId < 0) return -1;
            for (int i = 0; i < putDownGlides.Count; i++)
                if (putDownGlides[i].GlassId == glassId) return i;
            return -1;
        }

        private bool TryStartPutDown(LiquidBottle bottle, int glassId)
        {
            if (bottle == null || glassId < 0 || shelfView == null || !isActiveAndEnabled
                || controller == null || controller.State != BartenderLevelState.Playing
                || session == null || !session.AcceptsInput
                || !bottle.isActiveAndEnabled || bottle.IsTransferReserved || HasCompletionTail(glassId)
                || !shelfView.TryGetBottle(glassId, out LiquidBottle mapped) || !ReferenceEquals(mapped, bottle)
                || !shelfView.TryGetSeatPose(glassId, out BartenderGlassSeatPose seat))
                return false;
            Transform root = seat.MotionRoot != null ? seat.MotionRoot : bottle.transform;
            int existing = FindPutDown(glassId);
            if (existing >= 0)
            {
                PutDownGlide current = putDownGlides[existing];
                if (ReferenceEquals(current.Bottle, bottle) && ReferenceEquals(current.MotionRoot, root)) return true;
                EndPutDownAt(existing, false, true);
            }
            float lift = VesselPresentationMath.ReferenceDistance(selectionLift,
                VesselPresentationMath.RelativeToRoyalReference(bottle.transform, bottle.profile));
            var glide = new PutDownGlide(bottle, root, glassId, PutDownLandShare * Mathf.Max(lift, 0.001f));
            putDownGlides.Add(glide);
            shelfView.TryAcquireSeatLease(glide, glassId, true);
            return true;
        }

        private bool IsPutDownCurrent(PutDownGlide glide, out BartenderGlassSeatPose seat)
        {
            seat = default;
            return glide != null && glide.Bottle != null && glide.MotionRoot != null
                && glide.Bottle.isActiveAndEnabled && shelfView != null
                && shelfView.TryGetBottle(glide.GlassId, out LiquidBottle mapped)
                && ReferenceEquals(mapped, glide.Bottle)
                && shelfView.TryGetSeatPose(glide.GlassId, out seat)
                && ReferenceEquals(seat.MotionRoot != null ? seat.MotionRoot : glide.Bottle.transform,
                    glide.MotionRoot);
        }

        private void AdvancePutDowns()
        {
            if (putDownGlides.Count == 0) return;
            float deltaTime = Time.unscaledDeltaTime;
            float follow = 1f - Mathf.Exp(-selectionSpeed * deltaTime);
            for (int i = putDownGlides.Count - 1; i >= 0; i--)
            {
                if (i >= putDownGlides.Count) continue;
                PutDownGlide glide = putDownGlides[i];
                if (!IsPutDownCurrent(glide, out BartenderGlassSeatPose seat)
                    || glide.GlassId == selectedGlassId || glide.Bottle.IsTransferReserved
                    || HasCompletionTail(glide.GlassId))
                {
                    EndPutDownAt(i, false, !ReferenceEquals(glide.Bottle, selectedBottle));
                    continue;
                }
                shelfView.TryAcquireSeatLease(glide, glide.GlassId, true);

                Transform root = glide.MotionRoot;
                BartenderInvalidMoveFeedback rejection = glide.Bottle.GetComponent<BartenderInvalidMoveFeedback>();
                bool wobbling = rejection != null && rejection.Playing;
                BottleShell shell = glide.Bottle.GetComponent<BottleShell>();
                root.position = Vector3.Lerp(root.position, seat.Position, follow);
                if (!wobbling) root.rotation = Quaternion.Slerp(root.rotation, seat.Rotation, follow);
                root.localScale = Vector3.Lerp(root.localScale, seat.LocalScale, follow);
                if (shell != null) shell.highlight = Mathf.Lerp(shell.highlight, 0f, follow);
                glide.Elapsed += deltaTime;

                if (wobbling) continue;
                float scaleTolerance = PutDownLandShare
                    * Mathf.Max((selectionScale - 1f) * seat.LocalScale.magnitude, 0.001f);
                bool landed = glide.Elapsed >= PutDownMaxSeconds
                    || ((root.position - seat.Position).sqrMagnitude <= glide.LandDistance * glide.LandDistance
                        && (root.localScale - seat.LocalScale).sqrMagnitude <= scaleTolerance * scaleTolerance
                        && Quaternion.Angle(root.rotation, seat.Rotation) <= 0.5f
                        && (shell == null || shell.highlight <= PutDownLandShare));
                if (landed) EndPutDownAt(i, true, true);
            }
        }

        private void SnapPutDown(int glassId)
        {
            int index = FindPutDown(glassId);
            if (index >= 0) EndPutDownAt(index, true, true);
        }

        private void StopPutDown(int glassId, bool clearHighlight)
        {
            int index = FindPutDown(glassId);
            if (index >= 0) EndPutDownAt(index, false, clearHighlight);
        }

        private void LandAllPutDowns()
        {
            for (int i = putDownGlides.Count - 1; i >= 0; i--)
                if (i < putDownGlides.Count) EndPutDownAt(i, true, true);
        }

        private void DropStalePutDowns()
        {
            for (int i = putDownGlides.Count - 1; i >= 0; i--)
            {
                if (i >= putDownGlides.Count) continue;
                PutDownGlide glide = putDownGlides[i];
                if (!IsPutDownCurrent(glide, out _))
                    EndPutDownAt(i, false, !ReferenceEquals(glide.Bottle, selectedBottle));
            }
        }

        private void EndPutDownAt(int index, bool writeSeat, bool clearHighlight)
        {
            PutDownGlide glide = putDownGlides[index];
            putDownGlides.RemoveAt(index);
            try
            {
                if (writeSeat && IsPutDownCurrent(glide, out BartenderGlassSeatPose seat)
                    && !glide.Bottle.IsTransferReserved)
                {
                    BartenderInvalidMoveFeedback rejection = glide.Bottle.GetComponent<BartenderInvalidMoveFeedback>();
                    if (rejection != null && rejection.Playing) glide.MotionRoot.position = seat.Position;
                    else glide.MotionRoot.SetPositionAndRotation(seat.Position, seat.Rotation);
                    glide.MotionRoot.localScale = seat.LocalScale;
                }
                if (clearHighlight && glide.Bottle != null)
                {
                    BottleShell shell = glide.Bottle.GetComponent<BottleShell>();
                    if (shell != null) shell.highlight = 0f;
                }
            }
            finally
            {
                if (shelfView != null) shelfView.ReleaseSeatLease(glide);
            }
        }

        private void HandlePourFinished(BartenderPourOperation transaction, PourOutcome outcome)
        {
            if (!OwnsTransaction(transaction) || transaction.State != BsPourOperationState.Animating)
                return;
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
            TrySettlePresentationTransaction(transaction, roundCurrent, false,
                outcome == PourOutcome.Completed ? BsPourSettlementReason.Completed
                    : BsPourSettlementReason.Cancelled);
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
                SnapPutDown(glassId);
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
                && ownerView.Ready && !ownerView.GlobalSynchronizationDeferred
                && !ownerView.IsGlassSynchronizationDeferred(glassId)
                && !ownerController.ModalInputBlocked
                && !ownerController.IsGlassPresentationLocked(glassId)
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
            BartenderPourOperation expected,
            bool refresh,
            bool cancelAnimator,
            BsPourSettlementReason reason = BsPourSettlementReason.Cancelled)
        {
            if (!OwnsTransaction(expected)) return false;
            return expected.TrySettle(reason, refresh, cancelAnimator,
                transaction => activeTransactions.Remove(transaction));
        }

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

        internal BartenderPourOperation BeginPresentationTransaction(
            BartenderLevelController ownerController,
            BartenderShelfLevelView ownerView,
            PourAnimator ownerAnimator,
            BsShelfSynchronizationLease synchronizationLease,
            object transactionOwner,
            int sourceGlassId,
            int targetGlassId,
            bool isUndo)
        {
            if (transactionOwner == null)
                throw new ArgumentNullException(nameof(transactionOwner));
            nextPresentationRunId = AdvanceRunId(nextPresentationRunId);
            try
            {
                var transaction = new BartenderPourOperation(
                    nextPresentationRunId, transactionOwner, ownerController, ownerView,
                    synchronizationLease, ownerAnimator, session,
                    ownerController != null ? ownerController.CurrentRoundStamp : default,
                    sourceGlassId, targetGlassId, isUndo, Time.realtimeSinceStartupAsDouble,
                    PresentationTransactionTimeoutSeconds);
                activeTransactions.Add(transaction);
                return transaction;
            }
            catch
            {
                if (ownerView != null)
                    ownerView.DropSynchronizationDeferral(transactionOwner, synchronizationLease);
                throw;
            }
        }

        private bool CanContinueTransaction(
            BartenderPourOperation transaction,
            BartenderLevelController ownerController,
            BartenderShelfLevelView ownerView,
            PourAnimator ownerAnimator)
        {
            return this != null && transaction != null
            && OwnsTransaction(transaction)
            && transaction.State == BsPourOperationState.Committed
            && isActiveAndEnabled
            && transaction.Owner != null
            && ReferenceEquals(transaction.Controller, ownerController)
            && ReferenceEquals(transaction.View, ownerView)
            && ReferenceEquals(transaction.Animator, ownerAnimator)
            && ReferenceEquals(transaction.Session, session)
            && ReferenceEquals(controller, ownerController)
            && ReferenceEquals(shelfView, ownerView)
            && (ReferenceEquals(pourAnimator, ownerAnimator) || pooledAnimators.Contains(ownerAnimator))
            && IsTransactionRoundCurrent(transaction)
            && ownerView != null && ownerView.IsSynchronizationDeferredBy(
                transaction.Owner, transaction.SynchronizationLease);
        }

        private bool IsTransactionRoundCurrent(
            BartenderPourOperation transaction)
        {
            if (transaction == null) return false;
            if (transaction.Controller == null || transaction.Session == null
                || !ReferenceEquals(session, transaction.Session)) return false;
            if (transaction.IsUndo)
            {
                if (transaction.UndoChange != null)
                    return transaction.UndoChange.Cause == BsRoundTransitionCause.PlayerUndo
                        && IsCurrentBoardChange(transaction.Controller, transaction.UndoChange);
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

        private bool CanReconcileTransactionBoard(BartenderPourOperation transaction)
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

        private bool CanReconcileTransactionBoardForAttempt(BartenderPourOperation transaction)
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
                && current.BoardRevision >= transaction.StartStamp.BoardRevision;
        }

        private static bool IsCurrentPourReceipt(
            BartenderLevelController owner,
            BartenderPourReceipt receipt)
        {
            if (owner == null || receipt == null || !receipt.AttemptId.IsValid
                || !receipt.OperationId.IsValid || receipt.DomainRevision <= 0L
                || receipt.BoardRevision < 0 || receipt.SourceAfter == null
                || receipt.TargetAfter == null
                || receipt.Cause != BsRoundTransitionCause.PlayerPour) return false;
            BsRoundCommandStamp current = owner.CurrentRoundStamp;
            if (current.AttemptId != receipt.AttemptId || current.Token != receipt.Token
                || current.Revision < receipt.DomainRevision
                || current.BoardRevision < receipt.BoardRevision) return false;
            BsBoard board = owner.Board;
            return GlassMatches(board?.GlassById(receipt.SourceAfter.Id), receipt.SourceAfter)
                && GlassMatches(board?.GlassById(receipt.TargetAfter.Id), receipt.TargetAfter);
        }

        private void HandleLevelLoaded(BsLevel _)
        {
            CancelCommandLane();
            SettleAllPresentationTransactions(false);
            CancelAllCompletionTails(false);
            FinishUndoRefill();
            CancelRejectionFeedbacks(true);
            ClearSelection(SelectionRelease.Snap);
            LandAllPutDowns();
        }

        internal bool HasCompletionTail(int glassId) =>
            completionTails.TryGetValue(glassId, out CompletionTailContext tail)
            && tail != null;

        internal bool TryTrackCompletionTail(CompletionTailContext tail)
        {
            if (tail == null || tail.RunId <= 0L || tail.GlassId < 0
                || completionTails.ContainsKey(tail.GlassId)) return false;
            completionTails.Add(tail.GlassId, tail);
            if (tail.View != null) tail.View.TryAcquireSeatLease(tail, tail.GlassId, false);
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
                    if (expected.Effect != null) expected.Effect.Stop(false);
                    effectStopped = true;
                },
                () =>
                {
                    // The shelf may move during this effect. Restore its current pose, never the old seat.
                    if (!TailReceiptCanAffectGlass(expected.Controller, expected.View,
                            expected.GlassId, expected.Bottle, expected.Receipt, true)
                        || !expected.SeatPose.HasValue || expected.View.IsSeatMoving(expected.GlassId)
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
                },
                () =>
                {
                    if (expected.View != null) expected.View.ReleaseSeatLease(expected);
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
                // Queued taps were never saved or shown; a round that stops playing drops them.
                CancelCommandLane();
                // Pause retains the committed board for both pours and undo. It must be shown before the
                // old view's synchronization hold is released. A win or failure can now land while other pours
                // animate; those glasses show their saved liquid too.
                bool finished = state == BartenderLevelState.Won || state == BartenderLevelState.Failed;
                SettleAllPresentationTransactions(state == BartenderLevelState.Paused || finished, finished);
                // Pause keeps this board. Settle the badge now because stopping its effect also removes the
                // ready callback.
                CancelAllCompletionTails(state == BartenderLevelState.Paused);
                FinishUndoRefill();
                CancelRejectionFeedbacks(true);
                ClearSelectionWithSound(SelectionRelease.Snap);
                LandAllPutDowns();
            }
        }

        private void HandleBoardCommitted(BartenderBoardChange change)
        {
            if (!IsCurrentBoardChange(controller, change)) return;
            if (change.Cause == BsRoundTransitionCause.Shuffle
                || change.Cause == BsRoundTransitionCause.PlayerUndo)
                CancelCommandLane(true);
            BartenderPourOperation transaction = ActiveUndoTransaction;
            if (transaction != null && transaction.IsUndo)
            {
                if (transaction.UndoChange == null
                    && change.Cause == BsRoundTransitionCause.PlayerUndo
                    && change.AttemptId == transaction.StartStamp.AttemptId
                    && change.Token == transaction.StartStamp.Token)
                    transaction.TryCommitUndo(change, Time.realtimeSinceStartupAsDouble);
                else if (transaction.UndoChange != null
                    && change.BoardRevision != transaction.BoardRevision)
                    TrySettlePresentationTransaction(transaction, true, true);
            }
            if (completionTails.Count == 0) return;

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

        internal int TrySettleExpiredPresentations(double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now)) return 0;
            int settled = 0;
            foreach (BartenderPourOperation transaction in activeTransactions.ToArray())
            {
                bool expired = transaction.HasExpired(now);
                if (expired
                    || transaction.View == null
                    || !transaction.View.IsSynchronizationDeferredBy(
                        transaction.Owner, transaction.SynchronizationLease))
                {
                    if (TrySettlePresentationTransaction(
                            transaction, CanReconcileTransactionBoard(transaction), true,
                            expired ? BsPourSettlementReason.TimedOut : BsPourSettlementReason.Invalidated))
                        settled++;
                }
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
            if (undoRefill != null && !UndoRefillIsCurrent(undoRefill)) FinishUndoRefill();
            completionTailScratch.Clear();
            foreach (CompletionTailContext tail in completionTails.Values)
                if (!CompletionTailSeatIsCurrent(tail))
                    completionTailScratch.Add(tail);
            for (int i = 0; i < completionTailScratch.Count; i++)
                TrySettleCompletionTail(completionTailScratch[i], true);
            completionTailScratch.Clear();
            DropStalePutDowns();

            if (activeTransactions.Count > 0) return;

            CancelRejectionFeedbacks(shelfView != null && shelfView.PresentationChangeKeptPoses);
            if (selectedBottle != null && shelfView != null
                && shelfView.TryGetBottle(selectedGlassId, out LiquidBottle current)
                && ReferenceEquals(current, selectedBottle)) return;
            ClearSelection(SelectionRelease.KeepPose);
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
            refusalCueSerial++;
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
            refusalCueSerial++;
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
