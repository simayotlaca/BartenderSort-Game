using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    public enum BartenderLevelState
    {
        Unloaded,
        Playing,
        Paused,
        Won,
        Failed,
        CampaignComplete
    }

    /// <summary>I pass move snapshots to the view so animations cannot change the live board.</summary>
    public sealed class BartenderPourReceipt
    {
        public BsAttemptId AttemptId { get; }
        public BsOperationId OperationId { get; }
        public long DomainRevision { get; }
        public int Revision { get; }
        public int BoardRevision => Revision;
        public BsRoundTransitionCause Cause { get; }
        public BsRoundToken Token { get; }
        public BsSettlementReceipt? SettlementReceipt { get; }
        public int Amount { get; }
        public RtGlass SourceBefore { get; }
        public RtGlass SourceAfter { get; }
        public RtGlass TargetBefore { get; }
        public RtGlass TargetAfter { get; }

        internal BartenderPourReceipt(BsBoardCommit commit)
        {
            BsPourCommitEvidence evidence = commit?.Pour
                ?? throw new ArgumentNullException(nameof(commit));
            AttemptId = commit.AttemptId;
            OperationId = commit.OperationId;
            DomainRevision = commit.Revision;
            Revision = commit.BoardRevision;
            Cause = commit.Cause;
            Token = commit.Token;
            SettlementReceipt = commit.SettlementReceipt;
            Amount = evidence.Amount;
            SourceBefore = evidence.SourceBefore;
            SourceAfter = evidence.SourceAfter;
            TargetBefore = evidence.TargetBefore;
            TargetAfter = evidence.TargetAfter;
        }
    }

    /// <summary>Detached data needed to present one committed delivery.</summary>
    public sealed class BartenderDeliveryReceipt
    {
        public BsAttemptId AttemptId { get; }
        public BsOperationId OperationId { get; }
        public long DomainRevision { get; }
        public int Revision { get; }
        public int BoardRevision => Revision;
        public BsRoundTransitionCause Cause { get; }
        public BsRoundToken Token { get; }
        public BsSettlementReceipt? SettlementReceipt { get; }
        public int SlotIndex { get; }
        public RtGlass DeliveredGlass { get; }
        public OrderDef DeliveredOrder { get; }

        internal BartenderDeliveryReceipt(BsBoardCommit commit)
        {
            BsDeliveryCommitEvidence evidence = commit?.Delivery
                ?? throw new ArgumentNullException(nameof(commit));
            AttemptId = commit.AttemptId;
            OperationId = commit.OperationId;
            DomainRevision = commit.Revision;
            Revision = commit.BoardRevision;
            Cause = commit.Cause;
            Token = commit.Token;
            SettlementReceipt = commit.SettlementReceipt;
            SlotIndex = evidence.SlotIndex;
            DeliveredGlass = evidence.DeliveredGlass;
            DeliveredOrder = evidence.DeliveredOrder;
        }
    }

    /// <summary>
    /// Delivery updates include the receipt sent to presenters. Other board updates leave <see
    /// cref="DeliveryReceipt"/> null.
    /// </summary>
    public sealed class BartenderBoardChange
    {
        public BsAttemptId AttemptId { get; }
        public BsOperationId OperationId { get; }
        public long DomainRevision { get; }
        public int Revision { get; }
        public int BoardRevision => Revision;
        public BsRoundTransitionCause Cause { get; }
        public BsRoundToken Token { get; }
        public BsSettlementReceipt? SettlementReceipt { get; }
        public BartenderDeliveryReceipt DeliveryReceipt { get; }
        public bool IsDelivery => DeliveryReceipt != null;

        internal BartenderBoardChange(
            BsBoardCommit commit,
            BartenderDeliveryReceipt deliveryReceipt)
        {
            if (commit == null) throw new ArgumentNullException(nameof(commit));
            AttemptId = commit.AttemptId;
            OperationId = commit.OperationId;
            DomainRevision = commit.Revision;
            Revision = commit.BoardRevision;
            Cause = commit.Cause;
            Token = commit.Token;
            SettlementReceipt = commit.SettlementReceipt;
            DeliveryReceipt = deliveryReceipt;
        }
    }

    /// <summary>This owns levels, rules, timers and saved progress. Views handle input, layout and animation.</summary>
    [DisallowMultipleComponent]
    public sealed class BartenderLevelController : MonoBehaviour
    {
        private const string DefaultPaletteResource = "BsPalette";

        private enum OrderExpiryResult
        {
            NotExpired,
            Settled,
            SettlementRejected,
        }

        /// <summary>
        /// Undo restores the board and its original deadlines on <see cref="activeGameplayTime"/>. Time
        /// already spent stays spent.
        /// </summary>
        private sealed class BoardMemento
        {
            public BsBoard Board;
            public double?[] SlotDeadlines;
            public BsRoundCommandStamp Stamp;
        }

        /// <summary>
        /// I save these values so a failed purchase can restore timers, undo deadlines and stock before the
        /// commit.
        /// </summary>
        private sealed class TimeBoostMutationSnapshot
        {
            public double[] TimeBonusByOrderIndex;
            public int TimeBoostRemaining;
            public Dictionary<OrderDef, double> LiveDeadlines;
            public double?[][] UndoDeadlines;
        }

        [Serializable]
        private sealed class PersistedBoardMemento
        {
            public BsBoardSnapshot Board;
            public double[] SlotDeadlines = Array.Empty<double>();
            public bool[] HasSlotDeadline = Array.Empty<bool>();
            public string AttemptId = string.Empty;
            public int RoundId;
            public int GameplayEpoch;
            public long DomainRevision;
            public int BoardRevision;
        }

        [Serializable]
        private sealed class ActiveRoundSnapshot
        {
            public const int CurrentVersion = 2;

            public int Version = CurrentVersion;
            public string LevelSignature = string.Empty;
            public BsBoardSnapshot Board;
            public double ActiveGameplayTime;
            public double[] TimeBonusByOrderIndex = Array.Empty<double>();
            public double[] SlotDeadlines = Array.Empty<double>();
            public bool[] HasSlotDeadline = Array.Empty<bool>();
            public int UndoRemaining;
            public int ExtraGlassRemaining;
            public int TimeBoostRemaining;
            public int ShuffleRemaining;
            public bool UserPaused;
            public string AttemptId = string.Empty;
            public int RoundState;
            public int RoundCompletion;
            public int RoundAttemptKind;
            public int RoundId;
            public int GameplayEpoch;
            public long DomainRevision;
            public long LastOperationId;
            public int BoardRevision;
            public int CompletionFrom;
            public long CompletionOperationId;
            public int CompletionCause;
            public bool HasSettlementReceipt;
            public string SettlementReceiptId = string.Empty;
            public bool HasTimeOfferSettlement;
            public long TimeOfferSettlementId;
            public int TimeOfferDeclineDisposition;
            public List<PersistedBoardMemento> UndoHistory =
                new List<PersistedBoardMemento>();
        }

        private sealed class RestoredActiveRound
        {
            public BsRoundSnapshot RoundSnapshot;
            public double ActiveGameplayTime;
            public double[] TimeBonusByOrderIndex;
            public double?[] SlotDeadlines;
            public int UndoRemaining;
            public int ExtraGlassRemaining;
            public int TimeBoostRemaining;
            public int ShuffleRemaining;
            public bool UserPaused;
            public BsTimeOfferSettlementSnapshot? TimeOfferSettlement;
            public List<BoardMemento> UndoHistory;
        }

        [Header("Campaign")]
        [SerializeField] private bool loadOnStart = true;
        [SerializeField] private bool resumeSavedProgress = true;
        [SerializeField, Min(1)] private int startingLevelNumber = 1;

        [Header("Campaign data")]
        [SerializeField] private BsPalette palette;

        [Header("Booster kapasitesi")]
        [Tooltip("Maximum glass slots in this scene, including extra glasses.")]
        [SerializeField, Min(1)] private int maxActiveGlasses = 15;
        [Tooltip("Undo history size in board copies. 0 disables undo.")]
        [SerializeField, Min(0)] private int undoHistoryDepth = 32;

        [Header("Time offer")]
        [Tooltip("Seconds added by the offer. Match the +Time booster.")]
        [SerializeField, Min(1f)] private float timeOfferSeconds = 30f;
        [Tooltip("Offer price. Match the +Time booster price.")]
        [SerializeField, Min(1)] private int timeOfferCoinCost = 900;

        [Header("Dead-end check")]
        [Tooltip("End the round when no winning path remains, even if moves are available.")]
        [SerializeField] private bool detectDeadEnd = true;
        [Tooltip("Total search nodes allowed after each move.")]
        [SerializeField, Min(1)] private int deadEndNodeBudget = 40000;
        [Tooltip("Total search CPU time in ms. A timeout means inconclusive.")]
        [SerializeField, Min(1)] private int deadEndMaxMs = 10;
        [Tooltip("Search time per frame in ms. Start with 1 ms at 60 FPS.")]
        [SerializeField, Range(1, 4)] private int deadEndSliceMs = 1;
        [Tooltip("Idle delay before checking the last move. Avoids searches between quick moves.")]
        [SerializeField, Min(0f)] private float deadEndIdleDelaySeconds = 0.2f;
        [Tooltip("Confirmed board results to cache per level. 0 disables caching.")]
        [SerializeField, Min(0)] private int deadEndCacheCapacity = 128;

        private static List<BsLevel> cachedCampaign;

        private readonly Dictionary<OrderDef, double> orderDeadlines =
            new Dictionary<OrderDef, double>(ReferenceComparer<OrderDef>.Instance);
        private readonly List<OrderDef> timerRemovalScratch = new List<OrderDef>();
        private readonly List<int> timeBoostOrderScratch = new List<int>(4);
        /// <summary>Committed boardProjection/deadline mementos, oldest first. Undo pops the last one.</summary>
        private readonly List<BoardMemento> undoHistory = new List<BoardMemento>();

        // The controller owns the offer and barrier. Presenters send the offer ID; closing a view cannot
        // cancel the decision.
        private readonly object timeOfferBarrierOwner = new object();
        private readonly BsTimeOfferStateMachine timeOfferMachine =
            new BsTimeOfferStateMachine();
        private BsTimeOfferSnapshot timeOfferContext;
        private BsTimeOfferSettlementSnapshot? timeOfferSettlementContext;
        private readonly Dictionary<string, SolveOutcome> deadEndOutcomeCache =
            new Dictionary<string, SolveOutcome>(StringComparer.Ordinal);
        private readonly Queue<string> deadEndCacheOrder = new Queue<string>();

        private BsRoundCoordinator coordinator;
        private BsBoard boardProjection;
        private double activeGameplayTime;
        private double[] timeBonusByOrderIndex = Array.Empty<double>();
        private bool commandInProgress;
        private bool notificationInProgress;
        private object presentationLockOwner;
        private int presentationLockRevision = -1;
        private readonly HashSet<object> presentationBarrierOwners =
            new HashSet<object>(ReferenceComparer<object>.Instance);
        private bool hasPendingStateNotification;
        private BartenderLevelState pendingStateNotification;
        private bool standaloneAbortRequested;
        private bool automaticLoadDisabledAtRuntime;
        private bool startHasRun;
        private bool applicationPaused;
        private bool applicationFocusLost;
        private bool suppressNextGameplayTick;
        private BsRoundCommandStamp automaticPauseStamp;
        private bool userPauseOwned;
        private BartenderLevelState publishedState = BartenderLevelState.Unloaded;
        private BsSettlementReceipt? retainedSettlementReceipt;
        private BsSettlementReceipt? announcedSettlementReceipt;
        private float retainedSettlementRetryAt;
        private bool campaignCompleteProjection;
        private BsSolver.IncrementalSearch deadEndProbe;
        private BsBoard deadEndProbeBoard;
        private int deadEndProbeRevision = -1;
        private bool deadEndProbePending;
        private bool deadEndAwaitingEscape;
        private string deadEndProbeStateKey;
        private float deadEndProbeEarliestTime;

        public BsLevel CurrentLevel { get; private set; }
        public int CurrentCampaignSlot { get; private set; } = -1;
        public int BoardRevision => coordinator?.BoardRevision ?? 0;
        public BsRoundState CurrentRoundState =>
            coordinator?.State ?? BsRoundState.Empty;
        public BsRoundCompletion CurrentRoundCompletion =>
            coordinator?.Completion ?? BsRoundCompletion.None;
        public BartenderLevelState State => campaignCompleteProjection
            ? BartenderLevelState.CampaignComplete
            : ProjectState(coordinator);
        public BsRoundSnapshot CurrentRoundSnapshot => coordinator?.CaptureSnapshot();
        public BsRoundToken CurrentRoundToken => coordinator?.CurrentToken ?? default;
        public BsRoundCommandStamp CurrentRoundStamp =>
            coordinator?.CurrentStamp ?? default;

        /// <summary>Persisted player intent, distinct from an application suspension.</summary>
        public bool IsUserPaused => State == BartenderLevelState.Paused
            && userPauseOwned;
        public bool IsTerminalSettlementCommitted
        {
            get
            {
                if (coordinator?.State != BsRoundState.Completed) return false;
                if (coordinator.AttemptKind == BsRoundAttemptKind.Standalone)
                    return true;
                if (!retainedSettlementReceipt.HasValue) return false;
                BsSettlementReceipt expected = retainedSettlementReceipt.Value;
                return BartenderProgressService.TryGetSettlementOutbox(
                           out BsSettlementOutboxSnapshot outbox)
                       && outbox.IsValid
                       && outbox.State == BsSettlementOutboxState.Committed
                       && outbox.Receipt == expected;
            }
        }
        public bool HasPendingTerminalSettlement =>
            coordinator?.State == BsRoundState.Completed
            && coordinator.AttemptKind == BsRoundAttemptKind.Durable
            && retainedSettlementReceipt.HasValue
            && !IsTerminalSettlementCommitted;
        public BsPalette Palette => palette;
        /// <summary>Standalone boards, including First Shift, leave lives, coins and campaign progress unchanged.</summary>
        public bool IsStandaloneRound => coordinator != null
            && coordinator.HasBoard
            && coordinator.AttemptKind == BsRoundAttemptKind.Standalone;

        /// <summary>Booster stock copied from the level asset on load.</summary>
        public int UndoRemaining { get; private set; }
        public int ExtraGlassRemaining { get; private set; }
        public int TimeBoostRemaining { get; private set; }

        /// <summary>Offer price, also used when sending the player to the shop.</summary>
        public int TimeOfferCoinCost => Mathf.Max(1, timeOfferCoinCost);
        /// <summary>
        /// I keep the open offer here so a returning presenter can show the same ID without changing the
        /// decision.
        /// </summary>
        public BsTimeOfferSnapshot? CurrentTimeOffer =>
            timeOfferMachine.IsPresentable ? timeOfferContext : null;
        /// <summary>
        /// Keeps the decline receipt and destination through retries and failure. The next round or unload
        /// clears it.
        /// </summary>
        public BsTimeOfferSettlementSnapshot? CurrentTimeOfferSettlement =>
            timeOfferSettlementContext;
        public int ShuffleRemaining { get; private set; }
        /// <summary>Whether a move can be undone. Stock is checked separately.</summary>
        public bool HasUndoableMove => undoHistory.Count > 0;
        /// <summary>Maximum glasses allowed in this level.</summary>
        public int MaxActiveGlasses => Mathf.Max(1, maxActiveGlasses);
        /// <summary>Current glass count, used to check extra-glass purchases.</summary>
        public int ActiveGlassCount => boardProjection != null
            ? boardProjection.Glasses.Count
            : 0;

        /// <summary>A board copy that views can read without changing live rules.</summary>
        public BsBoard Board => coordinator?.CaptureBoard();

        /// <summary>Next unlocked slot, starting at zero. A slot equal to the campaign size means completion.</summary>
        public int NextUnlockedCampaignSlot => Mathf.Clamp(
            BartenderProgressService.NextUnlockedCampaignSlot, 0, Campaign.Count);

        /// <summary>Level number from the asset. Imported levels may skip numbers, so this is not slot + 1.</summary>
        public int NextUnlockedLevelNumber
        {
            get => ResolveCampaignLevelNumber(NextUnlockedCampaignSlot);
        }

        /// <summary>
        /// I read saved progress from the same sorted level list so menus do not need a live gameplay
        /// controller.
        /// </summary>
        internal static bool IsSavedCampaignComplete(int nextUnlockedCampaignSlot) =>
            Campaign.Count > 0 && nextUnlockedCampaignSlot >= Campaign.Count;

        internal static IReadOnlyList<BsLevel> RecipeCatalogueLevels => Campaign;

        /// <summary>Campaign progress stays monotonic; replays are durable normal rounds.</summary>
        internal static int ResolveNextPlayableSlot()
        {
            if (BartenderProgressService.TryGetResumableAttempt(out _, out int activeSlot))
                return activeSlot;
            int count = Campaign.Count;
            if (count == 0) return -1;
            int unlocked = BartenderProgressService.NextUnlockedCampaignSlot;
            int selected = BartenderProgressService.SelectedReplayCampaignSlot;
            if (selected >= 0 && selected < Math.Min(unlocked, count)) return selected;
            if (unlocked < count) return Math.Max(0, unlocked);
            return BartenderProgressService.ReplayCursor % count;
        }

        internal static bool CanReplayCampaignSlot(int slot) => slot >= 0
            && slot < Campaign.Count
            && slot < BartenderProgressService.NextUnlockedCampaignSlot;

        internal static int ResolveCampaignLevelNumber(int nextUnlockedCampaignSlot)
        {
            int count = Campaign.Count;
            if (count == 0) return 1;

            int slot = Mathf.Clamp(nextUnlockedCampaignSlot, 0, count);
            if (slot >= count)
            {
                BsLevel finalLevel = Campaign[count - 1];
                return finalLevel != null ? finalLevel.Index : count;
            }

            BsLevel level = Campaign[slot];
            return level != null ? level.Index : slot + 1;
        }

        /// <summary>Timers and commands wait while the view finishes animating a committed board update.</summary>
        public bool PresentationLocked => presentationLockOwner != null
                                          || presentationBarrierOwners.Count > 0;

        /// <summary>Each presenter owns its barrier. Timers and commands wait until all owners release theirs.</summary>
        public bool AcquirePresentationBarrier(object owner)
        {
            if (owner == null) return false;
            presentationBarrierOwners.Add(owner);
            return true;
        }

        /// <summary>
        /// Visible modals get a barrier only when their root can render. Non-visual owners use <see
        /// cref="AcquirePresentationBarrier(object)"/>.
        /// </summary>
        public bool AcquireVisiblePresentationBarrier(
            object owner, GameObject presentationRoot)
        {
            if (owner == null
                || !TryValidatePresentationRoot(presentationRoot, out _)
                || !presentationRoot.activeInHierarchy)
                return false;

            presentationBarrierOwners.Add(owner);
            return true;
        }

        /// <summary>A modal may start inactive, but its hierarchy and scale must allow it to appear.</summary>
        public static bool TryValidatePresentationRoot(
            GameObject presentationRoot, out string reason)
        {
            if (presentationRoot == null)
            {
                reason = "Presentation root is missing.";
                return false;
            }

            Transform root = presentationRoot.transform;
            Vector3 localScale = root.localScale;
            Vector3 effectiveScale = root.lossyScale;
            if (ScaleIsCollapsed(localScale) || ScaleIsCollapsed(effectiveScale))
            {
                reason = "Presentation root or one of its parents has a collapsed scale.";
                return false;
            }

            Transform parent = root.parent;
            if (parent != null && !parent.gameObject.activeInHierarchy)
            {
                reason = "Presentation root is under an inactive hierarchy.";
                return false;
            }

            reason = null;
            return true;
        }

        private static bool ScaleIsCollapsed(Vector3 scale) =>
            Mathf.Abs(scale.x) < 0.001f
            || Mathf.Abs(scale.y) < 0.001f
            || Mathf.Abs(scale.z) < 0.001f;

        /// <summary>Releases a barrier previously registered by the same owner token.</summary>
        public bool ReleasePresentationBarrier(object owner) =>
            owner != null && presentationBarrierOwners.Remove(owner);

        public bool IsPresentationBarrierOwnedBy(object owner) =>
            owner != null && presentationBarrierOwners.Contains(owner);

        /// <summary>
        /// A modal can read its own guided taps while gameplay stays frozen. Another barrier or revision
        /// lock blocks those taps too.
        /// </summary>
        public bool IsPresentationBarrierExclusivelyOwnedBy(object owner) =>
            owner != null && presentationLockOwner == null
            && presentationBarrierOwners.Count == 1
            && presentationBarrierOwners.Contains(owner);

        public event Action<BsLevel> LevelLoaded;
        /// <summary>Publishes the coordinator snapshot only after its required save or outbox step succeeds.</summary>
        public event Action<BsRoundCommit> RoundCommitted;
        /// <summary>
        /// Fires once when settlement commits, without repeating RoundCommitted. Navigation can then
        /// continue.
        /// </summary>
        public event Action<BsSettlementReceipt> TerminalSettlementCommitted;
        /// <summary>Matches a view's board update to its exact commit.</summary>
        public event Action<BartenderBoardChange> BoardCommitted;
        public event Action OrdersChanged;
        public event Action<BartenderLevelState> StateChanged;
        public event Action<BartenderPourReceipt> Poured;
        public event Action<BartenderDeliveryReceipt> Delivered;
        /// <summary>Refreshes tray counters after stock or undo history changes.</summary>
        public event Action BoostersChanged;
        /// <summary>Accepted time bonus, used to pulse order cards.</summary>
        public event Action<float> TimeBoosted;

        /// <summary>
        /// The round is still alive and paused behind the controller's offer barrier. Decisions must use
        /// this snapshot's offer ID, slot and purchase values.
        /// </summary>
        public event Action<BsTimeOfferSnapshot> TimeOfferRequested;
        /// <summary>Tracks settlement updates for the same consumed offer. Retries must not look like new offers.</summary>
        public event Action<BsTimeOfferSettlementSnapshot> TimeOfferSettlementChanged;

        public bool IsRoundCurrent(
            BsAttemptId attemptId,
            BsRoundToken token,
            long revision) =>
            coordinator != null
            && coordinator.IsCurrent(attemptId, token, revision);

        public bool TryGetCurrentTerminalTransition(
            out BsRoundTransition transition)
        {
            transition = null;
            return coordinator != null
                && coordinator.TryGetCompletedTransition(out transition);
        }

        private static List<BsLevel> Campaign
        {
            get
            {
                if (cachedCampaign != null) return cachedCampaign;

                BsLevel[] found = Resources.LoadAll<BsLevel>("Levels");
                cachedCampaign = new List<BsLevel>(found);
                cachedCampaign.Sort((a, b) =>
                {
                    if (ReferenceEquals(a, b)) return 0;
                    if (a == null) return 1;
                    if (b == null) return -1;
                    return a.Index.CompareTo(b.Index);
                });
                return cachedCampaign;
            }
        }

        private void Start()
        {
            startHasRun = true;
            ResolveDependencies();
            if (!loadOnStart || automaticLoadDisabledAtRuntime)
            {
                if (NextUnlockedCampaignSlot >= Campaign.Count && Campaign.Count > 0)
                    SetCampaignCompleteProjection(true);
                return;
            }

            if (resumeSavedProgress)
            {
                ResumeSavedCampaign();
                return;
            }

            int slot = FindCampaignSlot(startingLevelNumber);

            if (slot >= Campaign.Count)
            {
                SetCampaignCompleteProjection(true);
                return;
            }

            if (slot < 0) slot = 0;
            LoadCampaignSlot(slot);
        }

        private void Update()
        {
            MaintainApplicationPause();
            if (ConsumeStandaloneAbortRequest()) return;
            Tick(Time.unscaledDeltaTime);
            AdvanceDeadEndProbe();
        }

        private void OnEnable()
        {
            if (startHasRun
                && (State == BartenderLevelState.Playing
                    || State == BartenderLevelState.Paused)
                && boardProjection != null)
                ScheduleDeadEndProbe();
        }

        private void OnDisable() => CancelDeadEndProbe();

        private void OnApplicationPause(bool paused)
        {
            applicationPaused = paused;
            suppressNextGameplayTick = true;
            BartenderProgressService.Refresh();
            MaintainApplicationPause();
            if (paused) CheckpointActiveRound();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            applicationFocusLost = !hasFocus;
            suppressNextGameplayTick = true;
            BartenderProgressService.Refresh();
            MaintainApplicationPause();
            if (!hasFocus) CheckpointActiveRound();
        }

        private void OnApplicationQuit() => CheckpointActiveRound();

        /// <summary>
        /// The embedded menu calls this before Start to skip auto-load without changing saved scene
        /// settings.
        /// </summary>
        public void DisableAutomaticLoadAtRuntime()
        {
            if (startHasRun || State != BartenderLevelState.Unloaded) return;
            automaticLoadDisabledAtRuntime = true;
        }

        public void Tick(float unscaledDeltaTime)
        {
            if (HasPendingTerminalSettlement)
            {
                if (Time.unscaledTime >= retainedSettlementRetryAt)
                    RetryRetainedSettlement();
                return;
            }
            if (applicationPaused || applicationFocusLost) return;
            if (suppressNextGameplayTick)
            {
                suppressNextGameplayTick = false;
                return;
            }
            if (MutationBlocked || State != BartenderLevelState.Playing
                || unscaledDeltaTime <= 0f) return;
            activeGameplayTime += unscaledDeltaTime;
            ExpireOrderIfNeeded(out _);
        }

#if UNITY_EDITOR
        internal bool EditorLevelJumpReady => startHasRun;

        /// <summary>
        /// Editor-only level jump: checks the target and lives, transfers the attempt, then uses normal
        /// unload and load events.
        /// </summary>
        internal bool EditorTryJumpToLevelNumber(
            int oneBasedLevelNumber, out bool ownershipTouched,
            out string ownedAttemptId, out int ownedAttemptSlot,
            out string rejectionReason)
        {
            ownershipTouched = false;
            ownedAttemptId = null;
            ownedAttemptSlot = -1;
            rejectionReason = null;
            if (!startHasRun)
            {
                rejectionReason = "Level sunumu henüz hazırlanıyor";
                return false;
            }
            if (MutationBlocked)
            {
                rejectionReason = "Sunum veya başka bir level işlemi sürüyor";
                return false;
            }

            int targetSlot = FindCampaignSlot(oneBasedLevelNumber);
            if (targetSlot < 0 || targetSlot >= Campaign.Count)
            {
                rejectionReason = $"Level {oneBasedLevelNumber} kampanyada yok";
                return false;
            }
            if (BartenderProgressService.Lives <= 0)
            {
                rejectionReason = "Can 0; Level Jumper'dan canı doldur";
                return false;
            }

            BsLevel targetLevel = Campaign[targetSlot];
            if (!TryValidateLevel(targetLevel, out string validationError))
            {
                rejectionReason = $"Level {oneBasedLevelNumber} geçersiz: {validationError}";
                return false;
            }
            try { BsBoard.FromLevel(targetLevel); }
            catch (Exception exception)
            {
                rejectionReason = "Level kuralları oluşturulamadı: " + exception.Message;
                return false;
            }

            bool hasActiveAttempt = !string.IsNullOrEmpty(CurrentAttemptValue)
                                 && CurrentCampaignSlot >= 0;
            if ((State == BartenderLevelState.Playing
                 || State == BartenderLevelState.Paused)
                && !hasActiveAttempt)
            {
                rejectionReason = "Etkin turun Editor makbuzu bulunamadı";
                return false;
            }
            if (hasActiveAttempt
                && !BartenderProgressService.EditorTryRetargetActiveAttempt(
                    CurrentAttemptValue, CurrentCampaignSlot, targetSlot,
                    out rejectionReason))
                return false;
            if (hasActiveAttempt)
            {
                ownershipTouched = true;
                ownedAttemptId = CurrentAttemptValue;
                ownedAttemptSlot = targetSlot;
            }

            if (hasActiveAttempt)
            {
                // The editor already transferred the attempt. Release live ownership but keep the
                // operation-ID floor; normal unload must still reject unsettled rounds.
                EditorReleaseRetargetedRoundAuthorityForJump();
            }
            else if ((State != BartenderLevelState.Unloaded
                      || boardProjection != null || CurrentLevel != null
                      || CurrentCampaignSlot >= 0)
                     && !UnloadInternal(BartenderLevelState.Unloaded))
            {
                rejectionReason = "Mevcut level sunumu kapatılamadı";
                return false;
            }

            // Retarget clears the board JSON but keeps the attempt ID. I reuse that ID so the replacement
            // snapshot is accepted.
            bool loaded = hasActiveAttempt
                ? TryLoadCampaignSlot(
                    targetSlot,
                    0,
                    new BsAttemptId(ownedAttemptId),
                    false,
                    out rejectionReason)
                : TryLoadCampaignSlot(targetSlot, out rejectionReason);
            if (loaded)
            {
                ownershipTouched = true;
                ownedAttemptId = CurrentAttemptValue;
                ownedAttemptSlot = CurrentCampaignSlot;
                return true;
            }

            // Clean up the attempt if presentation or saving fails after retarget. A leaked receipt would
            // cost a life on the next load.
            if (string.IsNullOrEmpty(ownedAttemptId)
                && BartenderProgressService.EditorTryGetActiveAttempt(
                    out string openedAttemptId, out int openedAttemptSlot)
                && openedAttemptSlot == targetSlot)
            {
                ownershipTouched = true;
                ownedAttemptId = openedAttemptId;
                ownedAttemptSlot = openedAttemptSlot;
            }
            if (string.IsNullOrEmpty(ownedAttemptId)) return false;

            string loadReason = rejectionReason;
            if (BartenderProgressService.EditorTryDiscardActiveAttempt(
                    ownedAttemptId, ownedAttemptSlot, out string cleanupReason))
            {
                ownedAttemptId = null;
                ownedAttemptSlot = -1;
                return false;
            }

            rejectionReason = loadReason + ". Editor turu da kapatılamadı: "
                            + cleanupReason;
            return false;
        }

        private void EditorReleaseRetargetedRoundAuthorityForJump()
        {
            BsRoundSnapshot previous = coordinator.CaptureSnapshot();
            long operationFloor = Math.Max(
                BartenderProgressService.SettlementOperationFloor,
                previous.LastOperationId.Value);
            coordinator = new BsRoundCoordinator(operationFloor);
            ClearProjectedRound(BartenderLevelState.Unloaded);
        }
#endif

        /// <summary>
        /// Explicitly reloads from the embedded menu and refreshes views through LevelLoaded. I keep this
        /// out of OnEnable so UI toggles cannot restart a round.
        /// </summary>
        public bool ResumeSavedCampaign()
        {
            bool started = TryStartSavedCampaign(out string rejectionReason);
            if (!started && !string.IsNullOrEmpty(rejectionReason))
                Debug.LogWarning(rejectionReason, this);
            return started;
        }

        /// <summary>Play button contract: saved slot + positive life + durable attempt.</summary>
        public bool TryStartSavedCampaign(out string rejectionReason) =>
            TryStartSavedCampaign(true, out rejectionReason);

        /// <summary>
        /// I allow one retry after closing an unusable saved round. The flag prevents repeated recovery from
        /// discarding new rounds.
        /// </summary>
        private bool TryStartSavedCampaign(
            bool allowRecoveryRetry,
            out string rejectionReason)
        {
            rejectionReason = null;
            if (MutationBlocked)
            {
                rejectionReason = "Another level operation is in progress";
                return false;
            }
            if (State != BartenderLevelState.Unloaded
                && State != BartenderLevelState.CampaignComplete)
            {
                rejectionReason = "The game is already running";
                return false;
            }
            ResolveDependencies();

            if (!BartenderProgressService.TryQuarantineInvalidSettlementOutbox(
                    out bool quarantinedOutbox, out rejectionReason))
                return false;
            if (quarantinedOutbox)
            {
                if (allowRecoveryRetry)
                    return TryStartSavedCampaign(false, out rejectionReason);
                rejectionReason =
                    "The damaged settlement was quarantined; retry the campaign";
                return false;
            }

            if (BartenderProgressService.TryGetSettlementOutbox(
                    out BsSettlementOutboxSnapshot settlementOutbox)
                && settlementOutbox.IsValid
                && settlementOutbox.State >= BsSettlementOutboxState.Pending
                && settlementOutbox.State <= BsSettlementOutboxState.Acknowledged)
            {
                if (TryRestoreTerminalSettlementOutbox(
                        settlementOutbox, out rejectionReason))
                    return true;

                // Resolve the saved outcome before dropping its outdated presentation data. The receipt
                // still owns that result.
                string restoreReason = rejectionReason;
                string recoveryReason = null;
                if (allowRecoveryRetry
                    && BartenderProgressService
                        .TryResolveUnrestorableSettlementOutbox(
                            settlementOutbox.Receipt,
                            settlementOutbox.ActiveRoundJson,
                            out recoveryReason))
                    return TryStartSavedCampaign(false, out rejectionReason);

                if (allowRecoveryRetry && !string.IsNullOrEmpty(recoveryReason))
                    rejectionReason = string.IsNullOrEmpty(restoreReason)
                        ? "Settlement recovery failed: " + recoveryReason
                        : restoreReason + ". Recovery failed: " + recoveryReason;
                return false;
            }

            bool hasResumableAttempt =
                BartenderProgressService.TryGetResumableAttempt(
                    out string resumableAttemptId,
                    out int resumableSlot,
                    out string resumableRoundJson);
            int slot = hasResumableAttempt
                ? resumableSlot
                : ResolveNextPlayableSlot();
            if (slot >= Campaign.Count)
            {
                if (hasResumableAttempt)
                {
                    string failure =
                        "The saved active level is no longer in the campaign";
                    return TryRecoverUnrestorableStoredRound(
                        allowRecoveryRetry,
                        true,
                        resumableAttemptId,
                        resumableSlot,
                        resumableRoundJson,
                        failure,
                        out rejectionReason);
                }
                UnloadInternal(BartenderLevelState.CampaignComplete);
                rejectionReason = "All levels are complete";
                return false;
            }
            if (slot < 0)
            {
                rejectionReason = "The saved active level is invalid";
                return false;
            }
            if (!hasResumableAttempt && BartenderProgressService.Lives <= 0)
            {
                rejectionReason = "Wait for a life to refill";
                return false;
            }

            return TryLoadCampaignSlot(
                slot, allowRecoveryRetry, out rejectionReason);
        }

        private bool TryRestoreTerminalSettlementOutbox(
            BsSettlementOutboxSnapshot outbox,
            out string rejectionReason)
        {
            rejectionReason = null;
            int slot = outbox.Draft.CampaignSlot;
            if (!outbox.Receipt.IsValid
                || slot < 0 || slot >= Campaign.Count
                || string.IsNullOrWhiteSpace(outbox.ActiveRoundJson))
            {
                rejectionReason = "The saved terminal round is invalid";
                return false;
            }

            BsLevel level = Campaign[slot];
            if (!TryValidateLevel(level, out rejectionReason)
                || !TryDecodeActiveRound(
                    level,
                    outbox.Draft.AttemptId.Value,
                    outbox.ActiveRoundJson,
                    out RestoredActiveRound restoredRound,
                    out rejectionReason))
                return false;

            // I validate the decoded snapshot on a separate coordinator before changing any live board or
            // view state.
            if (!BsRoundCoordinator.TryRestore(
                    restoredRound.RoundSnapshot,
                    BartenderProgressService.SettlementOperationFloor,
                    outbox.Receipt,
                    out BsRoundCoordinator restoredCoordinator,
                    out rejectionReason)
                || !restoredCoordinator.TryGetCompletedTransition(
                    out BsRoundTransition transition))
            {
                if (string.IsNullOrEmpty(rejectionReason))
                    rejectionReason =
                        "The saved settlement has no terminal transition";
                return false;
            }

            commandInProgress = true;
            try
            {
                ResetTimeOffer();
                ClearAutomaticPauseOwnership();
                standaloneAbortRequested = false;
                retainedSettlementReceipt = outbox.Receipt;
                announcedSettlementReceipt = null;
                retainedSettlementRetryAt = 0f;
                campaignCompleteProjection = false;
                CurrentCampaignSlot = slot;
                CurrentLevel = level;
                timeOfferSettlementContext = restoredRound.TimeOfferSettlement;
                if (timeOfferSettlementContext.HasValue)
                    timeOfferMachine.InvalidateThrough(
                        timeOfferSettlementContext.Value.OfferId);
                coordinator = restoredCoordinator;
                userPauseOwned = false;

                SyncBoardProjection();
                activeGameplayTime = restoredRound.ActiveGameplayTime;
                timeBonusByOrderIndex = restoredRound.TimeBonusByOrderIndex;
                RestoreLiveDeadlines(restoredRound.SlotDeadlines);
                UndoRemaining = restoredRound.UndoRemaining;
                ExtraGlassRemaining = restoredRound.ExtraGlassRemaining;
                TimeBoostRemaining = restoredRound.TimeBoostRemaining;
                ShuffleRemaining = restoredRound.ShuffleRemaining;
                undoHistory.Clear();
                undoHistory.AddRange(restoredRound.UndoHistory);
                InvokeSafely(LevelLoaded, level);
                InvokeSafely(OrdersChanged);
                InvokeSafely(BoostersChanged);

                BsRoundCommit replay = new BsRoundCommit(
                    transition.OperationId,
                    transition.Revision,
                    transition.Cause,
                    null,
                    transition);
                PublishCoordinatorCommit(replay, publishBoard: false);
                RetryRetainedSettlement();
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        /// <summary>
        /// Loads a standalone board through normal rules and views. It opens no saved attempt and cannot
        /// change player progress.
        /// </summary>
        public bool TryStartStandalone(BsLevel level, out string rejectionReason)
        {
            rejectionReason = null;
            if (MutationBlocked)
            {
                rejectionReason = "Another level operation is in progress";
                return false;
            }
            if (State != BartenderLevelState.Unloaded
                && State != BartenderLevelState.CampaignComplete)
            {
                rejectionReason = "The game is already running";
                return false;
            }

            ResolveDependencies();
            if (!TryValidateLevel(level, out string validationError))
            {
                rejectionReason = "The standalone boardProjection is invalid: " + validationError;
                return false;
            }

            BsBoard loadedBoard;
            try { loadedBoard = BsBoard.FromLevel(level); }
            catch (Exception exception)
            {
                rejectionReason = "Standalone rules could not be created";
                Debug.LogException(exception, this);
                return false;
            }

            var attemptId = new BsAttemptId(
                "standalone-" + Guid.NewGuid().ToString("N"));
            BsRoundSnapshot previousAuthority = coordinator.CaptureSnapshot();
            if (previousAuthority.State != BsRoundState.Empty
                || previousAuthority.HasBoard)
            {
                rejectionReason =
                    "A standalone round can only replace an empty authority";
                return false;
            }
            long candidateOperationFloor = Math.Max(
                BartenderProgressService.SettlementOperationFloor,
                previousAuthority.LastOperationId.Value);
            if (!BsRoundCoordinator.TryRestore(
                    previousAuthority,
                    candidateOperationFloor,
                    null,
                    out BsRoundCoordinator candidateCoordinator,
                    out rejectionReason)
                || !candidateCoordinator.TryPrepareRound(
                    attemptId,
                    BsRoundAttemptKind.Standalone,
                    loadedBoard,
                    BsRoundTransitionCause.StandaloneRoundPrepared,
                    out BsRoundCommit prepared,
                    out rejectionReason)
                || !candidateCoordinator.TryActivatePreparedRound(
                    candidateCoordinator.CurrentStamp,
                    out BsRoundCommit activated,
                    out rejectionReason))
                return false;

            if (boardProjection != null || CurrentLevel != null || CurrentCampaignSlot >= 0)
            {
                if (!UnloadInternal(BartenderLevelState.Unloaded))
                {
                    rejectionReason = "The previous level could not be unloaded";
                    return false;
                }
            }

            commandInProgress = true;
            try
            {
                standaloneAbortRequested = false;
                ClearAutomaticPauseOwnership();
                retainedSettlementReceipt = null;
                announcedSettlementReceipt = null;
                retainedSettlementRetryAt = 0f;
                campaignCompleteProjection = false;
                CurrentCampaignSlot = -1;
                CurrentLevel = level;
                coordinator = candidateCoordinator;
                userPauseOwned = false;
                SyncBoardProjection();
                activeGameplayTime = 0d;
                ResetTimeBonuses(level);
                ResetOrderDeadlines();
                ResetBoosters(level);
                InvokeSafely(LevelLoaded, level);
                PublishCoordinatorCommit(prepared);
                PublishCoordinatorCommit(activated, publishBoard: false);
                InvokeSafely(OrdersChanged);
                InvokeSafely(BoostersChanged);
                ScheduleDeadEndProbe();
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        public bool LoadCampaignSlot(int zeroBasedSlot)
        {
            if (State == BartenderLevelState.Failed
                && zeroBasedSlot != CurrentCampaignSlot)
            {
                Debug.LogWarning("A failed round can only retry the same level.", this);
                return false;
            }
            if (State == BartenderLevelState.Won
                && zeroBasedSlot != CurrentCampaignSlot + 1)
            {
                Debug.LogWarning("A won round can only advance to the next level.", this);
                return false;
            }
            if ((State == BartenderLevelState.Unloaded
                 || State == BartenderLevelState.CampaignComplete)
                && resumeSavedProgress
                && zeroBasedSlot != ResolveNextPlayableSlot())
            {
                Debug.LogWarning("The campaign can only start from the saved unlocked level.", this);
                return false;
            }
            return TryLoadCampaignSlot(zeroBasedSlot, out _);
        }

        private bool TryLoadCampaignSlot(int zeroBasedSlot, out string rejectionReason) =>
            TryLoadCampaignSlot(
                zeroBasedSlot, 0, null, false, out rejectionReason);

        private bool TryLoadCampaignSlot(
            int zeroBasedSlot,
            bool allowRecoveryRetry,
            out string rejectionReason) =>
            TryLoadCampaignSlot(
                zeroBasedSlot, 0, null, allowRecoveryRetry, out rejectionReason);

        private bool TryLoadCampaignSlot(int zeroBasedSlot, int paidLifeCoinCost,
                                         out string rejectionReason) =>
            TryLoadCampaignSlot(
                zeroBasedSlot, paidLifeCoinCost, null, false,
                out rejectionReason);

        private bool TryLoadCampaignSlot(
            int zeroBasedSlot,
            int paidLifeCoinCost,
            BsAttemptId? retainedAttemptId,
            bool allowRecoveryRetry,
            out string rejectionReason)
        {
            rejectionReason = null;
            if (MutationBlocked)
            {
                rejectionReason = "Another level operation is in progress";
                Debug.LogWarning(rejectionReason, this);
                return false;
            }
            if (State == BartenderLevelState.Won
                || State == BartenderLevelState.Failed)
                return TryReplaceTerminalRound(
                    zeroBasedSlot, paidLifeCoinCost, out rejectionReason);
            if ((State == BartenderLevelState.Playing
                 || State == BartenderLevelState.Paused)
                && !string.IsNullOrEmpty(CurrentAttemptValue))
            {
                rejectionReason = "Another level cannot be loaded until the active round is settled";
                Debug.LogWarning(rejectionReason, this);
                return false;
            }
            ResolveDependencies();
            if (coordinator.State != BsRoundState.Empty || coordinator.HasBoard)
            {
                rejectionReason =
                    "A campaign round can only load into an empty authority";
                return false;
            }
            if (zeroBasedSlot < 0 || zeroBasedSlot >= Campaign.Count)
            {
                rejectionReason = $"LiquidSort level slot was not found: {zeroBasedSlot}.";
                Debug.LogError(rejectionReason, this);
                return false;
            }

            BsLevel level = Campaign[zeroBasedSlot];
            if (!TryValidateLevel(level, out string error))
            {
                string levelName = level != null
                    ? level.Index.ToString()
                    : zeroBasedSlot.ToString();
                rejectionReason = $"Level {levelName} could not be loaded: {error}";
                Debug.LogError(rejectionReason, this);
                return false;
            }

            BsBoard loadedBoard;
            try { loadedBoard = BsBoard.FromLevel(level); }
            catch (Exception exception)
            {
                rejectionReason = "Level rules could not be created";
                Debug.LogException(exception, this);
                return false;
            }

            BsRoundSnapshot emptyAuthority = coordinator.CaptureSnapshot();
            long operationFloor = Math.Max(
                emptyAuthority.LastOperationId.Value,
                BartenderProgressService.SettlementOperationFloor);
            BsAttemptId requestedAttemptId = retainedAttemptId.HasValue
                ? retainedAttemptId.Value
                : new BsAttemptId(Guid.NewGuid().ToString("N"));
            if (!requestedAttemptId.IsValid)
            {
                rejectionReason = "The requested attempt id is invalid";
                return false;
            }
            if (!BsRoundCoordinator.TryRestore(
                    emptyAuthority, operationFloor, null,
                    out BsRoundCoordinator preflightCoordinator,
                    out rejectionReason)
                || !preflightCoordinator.TryPrepareRound(
                    requestedAttemptId,
                    BsRoundAttemptKind.Durable,
                    loadedBoard,
                    BsRoundTransitionCause.CampaignRoundPrepared,
                    out _,
                    out rejectionReason)
                || !preflightCoordinator.TryActivatePreparedRound(
                    preflightCoordinator.CurrentStamp,
                    out _,
                    out rejectionReason)
                || !TryCreateInitialRoundJson(
                    level,
                    loadedBoard,
                    preflightCoordinator.CaptureSnapshot(),
                    out string initialRoundJson,
                    out rejectionReason))
            {
                Debug.LogError(rejectionReason, this);
                return false;
            }

            string attemptId;
            string activeRoundJson;
            bool attemptOpened = paidLifeCoinCost > 0
                ? BartenderProgressService.TryPurchaseLifeAndBeginAttempt(
                    zeroBasedSlot, paidLifeCoinCost, requestedAttemptId,
                    initialRoundJson,
                    out attemptId, out activeRoundJson, out rejectionReason)
                : BartenderProgressService.TryBeginAttempt(
                    zeroBasedSlot, requestedAttemptId, initialRoundJson, out attemptId,
                    out activeRoundJson, out rejectionReason);
            if (!attemptOpened)
                return false;

            // A different returned ID belongs to a resumable attempt. Recovery must not delete a fresh
            // snapshot because of a controller error.
            bool restoringPersistedAttempt = !string.Equals(
                attemptId, requestedAttemptId.Value, StringComparison.Ordinal);
            if (!TryDecodeActiveRound(level, attemptId, activeRoundJson,
                    out RestoredActiveRound restoredRound, out string restoreError))
            {
                string failure =
                    "The saved round could not be restored: " + restoreError;
                Debug.LogWarning(failure, this);
                return TryRecoverUnrestorableStoredRound(
                    allowRecoveryRetry,
                    restoringPersistedAttempt,
                    attemptId,
                    zeroBasedSlot,
                    activeRoundJson,
                    failure,
                    out rejectionReason);
            }

            BsSettlementReceipt? recoveredReceipt = null;
            BsSettlementOutboxSnapshot outbox = default;
            bool hasMatchingOutbox =
                BartenderProgressService.TryGetSettlementOutbox(out outbox)
                && outbox.Draft.AttemptId == new BsAttemptId(attemptId);
            if (hasMatchingOutbox && outbox.Receipt.IsValid)
                recoveredReceipt = outbox.Receipt;
            if (!BsRoundCoordinator.TryRestore(
                    restoredRound.RoundSnapshot,
                    BartenderProgressService.SettlementOperationFloor,
                    recoveredReceipt,
                    out BsRoundCoordinator restoredCoordinator,
                    out rejectionReason))
            {
                string failure = "The saved round authority could not be restored: "
                               + rejectionReason;
                return TryRecoverUnrestorableStoredRound(
                    allowRecoveryRetry,
                    restoringPersistedAttempt,
                    attemptId,
                    zeroBasedSlot,
                    activeRoundJson,
                    failure,
                    out rejectionReason);
            }
            if (!TryResolveRestoredAutomaticPause(
                    restoredCoordinator,
                    restoredRound.UserPaused,
                    activeRoundJson,
                    attemptId,
                    zeroBasedSlot,
                    out BsRoundCoordinator resolvedCoordinator,
                    out BsRoundCommit automaticResume,
                    out BsRoundCommandStamp restoredAutomaticPauseStamp,
                    out rejectionReason))
            {
                string failure = string.IsNullOrEmpty(rejectionReason)
                    ? "The saved round pause authority could not be restored"
                    : "The saved round pause authority could not be restored: "
                      + rejectionReason;
                return TryRecoverUnrestorableStoredRound(
                    allowRecoveryRetry,
                    restoringPersistedAttempt,
                    attemptId,
                    zeroBasedSlot,
                    activeRoundJson,
                    failure,
                    out rejectionReason);
            }

            commandInProgress = true;
            try
            {
                ResetTimeOffer();
                ClearAutomaticPauseOwnership();
                standaloneAbortRequested = false;
                retainedSettlementReceipt = null;
                announcedSettlementReceipt = null;
                retainedSettlementRetryAt = 0f;
                campaignCompleteProjection = false;
                timeOfferSettlementContext = restoredRound.TimeOfferSettlement;
                if (timeOfferSettlementContext.HasValue)
                    timeOfferMachine.InvalidateThrough(
                        timeOfferSettlementContext.Value.OfferId);
                CurrentCampaignSlot = zeroBasedSlot;
                CurrentLevel = level;
                coordinator = resolvedCoordinator;
                userPauseOwned = restoredRound.UserPaused
                    && coordinator.State == BsRoundState.Paused;
                automaticPauseStamp = restoredAutomaticPauseStamp;
                SyncBoardProjection();
                activeGameplayTime = restoredRound.ActiveGameplayTime;
                timeBonusByOrderIndex = restoredRound.TimeBonusByOrderIndex;
                RestoreLiveDeadlines(restoredRound.SlotDeadlines);
                UndoRemaining = restoredRound.UndoRemaining;
                ExtraGlassRemaining = restoredRound.ExtraGlassRemaining;
                TimeBoostRemaining = restoredRound.TimeBoostRemaining;
                ShuffleRemaining = restoredRound.ShuffleRemaining;
                undoHistory.Clear();
                undoHistory.AddRange(restoredRound.UndoHistory);
                InvokeSafely(LevelLoaded, level);
                InvokeSafely(OrdersChanged);
                InvokeSafely(BoostersChanged);
                if (automaticResume != null)
                    PublishCoordinatorCommit(
                        automaticResume, publishBoard: false);
                else
                    NotifyProjectedState();

                if (coordinator.TryGetCompletedTransition(
                        out BsRoundTransition restoredTransition))
                {
                    retainedSettlementReceipt = restoredTransition.SettlementReceipt;
                    BsRoundCommit replay = new BsRoundCommit(
                        restoredTransition.OperationId,
                        restoredTransition.Revision,
                        restoredTransition.Cause,
                        null,
                        restoredTransition);
                    PublishCoordinatorCommit(replay, publishBoard: false);
                    RetryRetainedSettlement();
                }
                else if (State == BartenderLevelState.Playing)
                {
                    ScheduleDeadEndProbe();
                }
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        /// <summary>
        /// I validate the replacement on a separate coordinator, then save the old receipt and new attempt
        /// together. Live state changes only after that commit succeeds.
        /// </summary>
        private bool TryReplaceTerminalRound(
            int zeroBasedSlot,
            int paidLifeCoinCost,
            out string rejectionReason)
        {
            rejectionReason = null;
            if (MutationBlocked)
            {
                rejectionReason = "Another level operation is in progress";
                return false;
            }

            BartenderLevelState terminalState = State;
            if ((terminalState != BartenderLevelState.Won
                 && terminalState != BartenderLevelState.Failed)
                || coordinator?.State != BsRoundState.Completed
                || coordinator.AttemptKind != BsRoundAttemptKind.Durable)
            {
                rejectionReason = "Only a completed campaign round can be replaced";
                return false;
            }
            if (!retainedSettlementReceipt.HasValue
                || !IsTerminalSettlementCommitted)
            {
                rejectionReason = "The round result settlement is still pending";
                return false;
            }
            if (paidLifeCoinCost < 0
                || (paidLifeCoinCost > 0
                    && terminalState != BartenderLevelState.Failed))
            {
                rejectionReason = "The retry cost is invalid for this result";
                return false;
            }
            if ((terminalState == BartenderLevelState.Failed
                 && zeroBasedSlot != CurrentCampaignSlot)
                || (terminalState == BartenderLevelState.Won
                    && zeroBasedSlot != CurrentCampaignSlot + 1))
            {
                rejectionReason = "The replacement campaign slot is invalid";
                return false;
            }

            ResolveDependencies();
            if (zeroBasedSlot < 0 || zeroBasedSlot >= Campaign.Count)
            {
                rejectionReason =
                    $"LiquidSort level slot was not found: {zeroBasedSlot}.";
                return false;
            }
            BsLevel level = Campaign[zeroBasedSlot];
            if (!TryValidateLevel(level, out string validationError))
            {
                rejectionReason = "The replacement level is invalid: "
                                + validationError;
                return false;
            }

            BsBoard initialBoard;
            try { initialBoard = BsBoard.FromLevel(level); }
            catch (Exception exception)
            {
                rejectionReason = "The replacement level rules could not be created";
                Debug.LogException(exception, this);
                return false;
            }

            BsRoundSnapshot terminalSnapshot = coordinator.CaptureSnapshot();
            BsSettlementReceipt terminalReceipt =
                retainedSettlementReceipt.Value;
            long operationFloor = Math.Max(
                BartenderProgressService.SettlementOperationFloor,
                terminalSnapshot.LastOperationId.Value);
            if (!BsRoundCoordinator.TryRestore(
                    terminalSnapshot,
                    operationFloor,
                    terminalReceipt,
                    out BsRoundCoordinator transactionCoordinator,
                    out rejectionReason)
                || !transactionCoordinator.TryClear(
                    transactionCoordinator.CurrentStamp,
                    BsRoundTransitionCause.ReturnToMenu,
                    out BsRoundCommit clearCommit,
                    out rejectionReason))
                return false;

            BsRoundSnapshot clearedSnapshot =
                transactionCoordinator.CaptureSnapshot();
            var newAttemptId = new BsAttemptId(
                Guid.NewGuid().ToString("N"));
            if (!transactionCoordinator.TryPrepareRound(
                    newAttemptId,
                    BsRoundAttemptKind.Durable,
                    initialBoard,
                    BsRoundTransitionCause.CampaignRoundPrepared,
                    out BsRoundCommit preparedCommit,
                    out rejectionReason))
                return false;
            BsRoundSnapshot preparedSnapshot =
                transactionCoordinator.CaptureSnapshot();
            if (!transactionCoordinator.TryActivatePreparedRound(
                    transactionCoordinator.CurrentStamp,
                    out BsRoundCommit activatedCommit,
                    out rejectionReason))
                return false;

            BsRoundSnapshot activatedSnapshot =
                transactionCoordinator.CaptureSnapshot();
            if (!TryCreateInitialRoundJson(
                    level, initialBoard, activatedSnapshot,
                    out string initialRoundJson, out rejectionReason)
                || !TryDecodeActiveRound(
                    level, newAttemptId.Value, initialRoundJson,
                    out RestoredActiveRound restoredRound,
                    out rejectionReason)
                || !BsRoundCoordinator.TryRestore(
                    clearedSnapshot, operationFloor, null,
                    out BsRoundCoordinator clearedCoordinator,
                    out rejectionReason)
                || !BsRoundCoordinator.TryRestore(
                    preparedSnapshot, operationFloor, null,
                    out BsRoundCoordinator preparedCoordinator,
                    out rejectionReason)
                || !BsRoundCoordinator.TryRestore(
                    restoredRound.RoundSnapshot, operationFloor, null,
                    out BsRoundCoordinator activatedCoordinator,
                    out rejectionReason))
                return false;

            commandInProgress = true;
            try
            {
                if (!BartenderProgressService
                    .TryAcknowledgeSettlementAndBeginAttempt(
                        terminalReceipt,
                        newAttemptId,
                        zeroBasedSlot,
                        initialRoundJson,
                        paidLifeCoinCost,
                        out _,
                        out rejectionReason))
                    return false;

                ResetTimeOffer();
                coordinator = clearedCoordinator;
                PublishCoordinatorCommit(clearCommit, publishBoard: false);
                ClearProjectedRound(BartenderLevelState.Unloaded);

                coordinator = preparedCoordinator;
                campaignCompleteProjection = false;
                CurrentCampaignSlot = zeroBasedSlot;
                CurrentLevel = level;
                SyncBoardProjection();
                activeGameplayTime = restoredRound.ActiveGameplayTime;
                timeBonusByOrderIndex = restoredRound.TimeBonusByOrderIndex;
                RestoreLiveDeadlines(restoredRound.SlotDeadlines);
                UndoRemaining = restoredRound.UndoRemaining;
                ExtraGlassRemaining = restoredRound.ExtraGlassRemaining;
                TimeBoostRemaining = restoredRound.TimeBoostRemaining;
                ShuffleRemaining = restoredRound.ShuffleRemaining;
                undoHistory.Clear();
                undoHistory.AddRange(restoredRound.UndoHistory);
                InvokeSafely(LevelLoaded, level);
                PublishCoordinatorCommit(preparedCommit);

                coordinator = activatedCoordinator;
                PublishCoordinatorCommit(
                    activatedCommit, publishBoard: false);
                InvokeSafely(OrdersChanged);
                InvokeSafely(BoostersChanged);
                ScheduleDeadEndProbe();
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        /// <summary>
        /// Pause restart resets this attempt's board, timers and stock without spending a life or settling
        /// it. LevelLoaded gives callbacks a fresh round token.
        /// </summary>
        public bool TryRestartPausedAttempt(out string rejectionReason)
        {
            rejectionReason = null;
            if (MutationBlocked)
            {
                rejectionReason = "Another level operation is in progress";
                return false;
            }
            if (applicationPaused || applicationFocusLost
                || automaticPauseStamp.IsValid)
            {
                rejectionReason = "The level cannot be restarted while the app is in the background";
                return false;
            }
            if (State != BartenderLevelState.Paused || CurrentLevel == null
                || boardProjection == null || CurrentCampaignSlot < 0
                || string.IsNullOrEmpty(CurrentAttemptValue))
            {
                rejectionReason = "Only a paused active round can be restarted";
                return false;
            }
            if (BartenderProgressService.Lives <= 0)
            {
                rejectionReason = "The round cannot be restarted without a life";
                return false;
            }
            if (!TryValidateLevel(CurrentLevel, out string validationError))
            {
                rejectionReason = "The level could not be restarted: " + validationError;
                return false;
            }

            BsBoard freshBoard;
            try { freshBoard = BsBoard.FromLevel(CurrentLevel); }
            catch (Exception exception)
            {
                rejectionReason = "Level rules could not be recreated";
                Debug.LogException(exception, this);
                return false;
            }

            BoardMemento previousBoard = CaptureCurrentMemento();
            double previousGameplayTime = activeGameplayTime;
            double[] previousTimeBonuses = (double[])timeBonusByOrderIndex.Clone();
            int previousUndoRemaining = UndoRemaining;
            int previousExtraGlassRemaining = ExtraGlassRemaining;
            int previousTimeBoostRemaining = TimeBoostRemaining;
            int previousShuffleRemaining = ShuffleRemaining;
            var previousUndoHistory = new List<BoardMemento>(undoHistory);
            if (!coordinator.TryStagePausedBoardReplacement(
                    coordinator.CurrentStamp, freshBoard,
                    out BsStagedBoardMutation staged, out rejectionReason))
                return false;

            commandInProgress = true;
            try
            {
                CancelDeadEndProbe();
                activeGameplayTime = 0d;
                ResetTimeBonuses(CurrentLevel);
                ResetOrderDeadlines(freshBoard);
                ResetBoosters(CurrentLevel);

                if (!TryCommitBoardStage(
                        staged, 0, out BsRoundCommit commit,
                        out bool publishNow, out rejectionReason))
                {
                    coordinator.TryDiscard(staged);
                    activeGameplayTime = previousGameplayTime;
                    timeBonusByOrderIndex = previousTimeBonuses;
                    UndoRemaining = previousUndoRemaining;
                    ExtraGlassRemaining = previousExtraGlassRemaining;
                    TimeBoostRemaining = previousTimeBoostRemaining;
                    ShuffleRemaining = previousShuffleRemaining;
                    undoHistory.Clear();
                    undoHistory.AddRange(previousUndoHistory);
                    RestoreDeadlinesForBoard(
                        previousBoard.Board, previousBoard.SlotDeadlines);
                    ScheduleDeadEndProbe();
                    return false;
                }
                InvokeSafely(LevelLoaded, CurrentLevel);
                if (publishNow) PublishCoordinatorCommit(commit);
                InvokeSafely(OrdersChanged);
                InvokeSafely(BoostersChanged);
                // Arm after publishing so the probe follows the new board copy.
                ScheduleDeadEndProbe();
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        /// <summary>Only a Failed round can use this retry path.</summary>
        public bool TryRetryAfterFailure()
        {
            if (MutationBlocked || State != BartenderLevelState.Failed
                || CurrentCampaignSlot < 0)
                return false;
            int retrySlot = CurrentCampaignSlot;
            return TryReplaceTerminalRound(retrySlot, 0, out _);
        }

        /// <summary>
        /// Paid continue saves the coin charge, refunded life and new attempt together. It also works with
        /// zero lives.
        /// </summary>
        public bool TryPaidRetryAfterFailure(int coinCost)
        {
            if (MutationBlocked || State != BartenderLevelState.Failed
                || CurrentCampaignSlot < 0 || coinCost <= 0)
                return false;

            int retrySlot = CurrentCampaignSlot;
            return TryReplaceTerminalRound(retrySlot, coinCost, out _);
        }

        /// <summary>
        /// Win Continue clears the settled board for the menu; progress is already saved. The final level
        /// may enter CampaignComplete.
        /// </summary>
        public bool TryContinueAfterWin()
        {
            if (MutationBlocked || State != BartenderLevelState.Won
                || CurrentCampaignSlot < 0)
                return false;

            int next = CurrentCampaignSlot + 1;
            if (next >= Campaign.Count)
                return UnloadInternal(BartenderLevelState.CampaignComplete);

            return UnloadInternal(BartenderLevelState.Unloaded);
        }

        /// <summary>
        /// Closes a settled result and clears the board for the embedded menu. It does not settle the round
        /// again.
        /// </summary>
        public bool TryReturnToMainMenuFromTerminal()
        {
            bool terminalState = State == BartenderLevelState.Won
                              || State == BartenderLevelState.Failed;
            if (MutationBlocked || !terminalState || CurrentCampaignSlot < 0)
                return false;

            return UnloadInternal(BartenderLevelState.Unloaded);
        }

        public void UnloadLevel()
        {
            if (MutationBlocked) return;
            if ((State == BartenderLevelState.Playing
                 || State == BartenderLevelState.Paused)
                && !string.IsNullOrEmpty(CurrentAttemptValue))
            {
                Debug.LogWarning(
                    "Etkin tur doğrudan boşaltılamaz; TryQuitToFailure kullanın.", this);
                return;
            }
            UnloadInternal(BartenderLevelState.Unloaded);
        }

        /// <summary>
        /// Queues standalone cleanup for the next safe Update. The controller keeps ownership even if the
        /// tutorial view is disabled.
        /// </summary>
        public bool RequestAbortStandalone(out string rejectionReason)
        {
            rejectionReason = null;
            if (!IsStandaloneRound)
            {
                rejectionReason = "Only a standalone round can queue a tutorial abort";
                return false;
            }
            standaloneAbortRequested = true;
            return true;
        }

        private bool ConsumeStandaloneAbortRequest()
        {
            if (!standaloneAbortRequested) return false;
            if (!IsStandaloneRound)
            {
                standaloneAbortRequested = false;
                return false;
            }
            if (commandInProgress || notificationInProgress
                || coordinator.HasStagedOperation)
                return true;

            standaloneAbortRequested = false;
            BsRoundCommit commit;
            string rejectionReason;
            bool aborted = coordinator.State == BsRoundState.Completed
                ? coordinator.TryClear(
                    coordinator.CurrentStamp,
                    BsRoundTransitionCause.StandaloneAbort,
                    out commit,
                    out rejectionReason)
                : coordinator.TryAbortStandalone(
                    coordinator.CurrentStamp, out commit,
                    out rejectionReason);
            if (aborted)
            {
                PublishCoordinatorCommit(commit, publishBoard: false);
                ClearProjectedRound(BartenderLevelState.Unloaded);
            }
            else if (!string.IsNullOrEmpty(rejectionReason))
                Debug.LogWarning(rejectionReason, this);
            return true;
        }

        /// <summary>
        /// Confirmed quit spends one life and keeps the board for the failure card. A failed save leaves the
        /// paused round and confirmation open.
        /// </summary>
        public bool TryQuitToFailure(out string rejectionReason)
        {
            rejectionReason = null;
            if (MutationBlocked)
            {
                rejectionReason = "Another level operation is in progress";
                return false;
            }
            if (State != BartenderLevelState.Paused || CurrentCampaignSlot < 0
                || string.IsNullOrEmpty(CurrentAttemptValue))
            {
                rejectionReason = "Only a paused active round can be abandoned";
                return false;
            }

            return TryCompleteRound(
                BsRoundCompletion.Quit,
                BsRoundTransitionCause.PauseMenuQuit,
                out rejectionReason);
        }

        public bool Pause() => TrySetPaused(
            true, BsRoundTransitionCause.PlayerPause, true);

        public bool Resume() => TrySetPaused(
            false, BsRoundTransitionCause.PlayerResume, false);

        private bool TrySetPaused(
            bool paused,
            BsRoundTransitionCause cause,
            bool userPaused)
        {
            if (MutationBlocked
                || (paused
                    ? State != BartenderLevelState.Playing
                    : State != BartenderLevelState.Paused))
                return false;

            commandInProgress = true;
            try
            {
                BsRoundSnapshot prior = coordinator.CaptureSnapshot();
                long operationFloor = Math.Max(
                    prior.LastOperationId.Value,
                    IsStandaloneRound
                        ? 0L
                        : BartenderProgressService.SettlementOperationFloor);
                if (!BsRoundCoordinator.TryRestore(
                        prior,
                        operationFloor,
                        null,
                        out BsRoundCoordinator candidateCoordinator,
                        out string restoreReason))
                {
                    if (!string.IsNullOrEmpty(restoreReason))
                        Debug.LogWarning(restoreReason, this);
                    return false;
                }
                BsRoundCommit commit;
                string transitionReason;
                bool accepted = paused
                    ? candidateCoordinator.TryPause(
                        candidateCoordinator.CurrentStamp, cause, out commit,
                        out transitionReason)
                    : candidateCoordinator.TryResume(
                        candidateCoordinator.CurrentStamp, cause, out commit,
                        out transitionReason);
                if (!accepted)
                {
                    if (!string.IsNullOrEmpty(transitionReason))
                        Debug.LogWarning(transitionReason, this);
                    return false;
                }
                if (!IsStandaloneRound)
                {
                    bool captured = TryCaptureActiveRoundJson(
                            candidateCoordinator.CaptureSnapshot(),
                            candidateCoordinator.CaptureBoard(),
                            userPaused, out string json, out string saveReason);
                    bool saved = captured
                        && BartenderProgressService.TryCommitActiveRound(
                            CurrentAttemptValue, CurrentCampaignSlot, json, 0,
                            out saveReason);
                    if (!saved)
                    {
                        if (!string.IsNullOrEmpty(saveReason))
                            Debug.LogWarning(saveReason, this);
                        return false;
                    }
                }
                coordinator = candidateCoordinator;
                userPauseOwned = paused && userPaused;
                // Pause swaps the board copy. I re-arm the probe on that copy so its identity check still
                // passes.
                bool deadEndProbeArmed = deadEndProbePending || deadEndProbe != null;
                SyncBoardProjection();
                if (deadEndAwaitingEscape)
                    deadEndProbeBoard = boardProjection;
                else if (deadEndProbeArmed) ScheduleDeadEndProbe();
                PublishCoordinatorCommit(commit, publishBoard: false);
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        private bool TryRecoverUnrestorableStoredRound(
            bool allowRecoveryRetry,
            bool restoringPersistedAttempt,
            string attemptId,
            int campaignSlot,
            string activeRoundJson,
            string restoreFailure,
            out string rejectionReason)
        {
            rejectionReason = restoreFailure;
            if (!allowRecoveryRetry || !restoringPersistedAttempt) return false;

            if (!BartenderProgressService.TryQuarantineUnrestorableActiveAttempt(
                    attemptId,
                    campaignSlot,
                    activeRoundJson,
                    out string recoveryReason))
            {
                if (!string.IsNullOrEmpty(recoveryReason))
                    rejectionReason = restoreFailure + ". Recovery failed: "
                                    + recoveryReason;
                return false;
            }

            return TryStartSavedCampaign(false, out rejectionReason);
        }

        private bool TryResolveRestoredAutomaticPause(
            BsRoundCoordinator restoredCoordinator,
            bool userPaused,
            string activeRoundJson,
            string attemptId,
            int campaignSlot,
            out BsRoundCoordinator resolvedCoordinator,
            out BsRoundCommit resumeCommit,
            out BsRoundCommandStamp restoredAutomaticPauseStamp,
            out string rejectionReason)
        {
            resolvedCoordinator = restoredCoordinator;
            resumeCommit = null;
            restoredAutomaticPauseStamp = default;
            rejectionReason = null;
            if (restoredCoordinator == null)
            {
                rejectionReason = "The restored round coordinator is missing";
                return false;
            }
            if (restoredCoordinator.State != BsRoundState.Paused || userPaused)
                return true;

            if (applicationPaused || applicationFocusLost)
            {
                restoredAutomaticPauseStamp = restoredCoordinator.CurrentStamp;
                return true;
            }

            BsRoundSnapshot prior = restoredCoordinator.CaptureSnapshot();
            long operationFloor = Math.Max(
                BartenderProgressService.SettlementOperationFloor,
                prior.LastOperationId.Value);
            if (!BsRoundCoordinator.TryRestore(
                    prior,
                    operationFloor,
                    null,
                    out BsRoundCoordinator resumeCandidate,
                    out rejectionReason)
                || !resumeCandidate.TryResume(
                    resumeCandidate.CurrentStamp,
                    BsRoundTransitionCause.ApplicationResume,
                    out BsRoundCommit accepted,
                    out rejectionReason))
                return false;

            bool captured = TryRewriteActiveRoundCoreJson(
                activeRoundJson,
                resumeCandidate.CaptureSnapshot(),
                false,
                out string json,
                out string saveReason);
            bool saved = captured
                && BartenderProgressService.TryCommitActiveRound(
                    attemptId, campaignSlot, json, 0,
                    out saveReason);
            if (saved)
            {
                resolvedCoordinator = resumeCandidate;
                resumeCommit = accepted;
                return true;
            }

            // Keep the original paused coordinator until saving succeeds. A failed save can retry the same
            // pause stamp next frame.
            restoredAutomaticPauseStamp = restoredCoordinator.CurrentStamp;
            if (!string.IsNullOrEmpty(saveReason))
                Debug.LogWarning(saveReason, this);
            rejectionReason = null;
            return true;
        }

        private static bool TryRewriteActiveRoundCoreJson(
            string sourceJson,
            BsRoundSnapshot exactRound,
            bool userPaused,
            out string json,
            out string rejectionReason)
        {
            json = null;
            rejectionReason = null;
            ActiveRoundSnapshot snapshot;
            try
            {
                snapshot = string.IsNullOrWhiteSpace(sourceJson)
                    ? null
                    : JsonUtility.FromJson<ActiveRoundSnapshot>(sourceJson);
            }
            catch (Exception exception)
            {
                rejectionReason = "The active round could not be rewritten: "
                                + exception.Message;
                return false;
            }
            BsBoard exactBoard = exactRound?.CaptureBoard();
            if (snapshot == null || exactRound == null || exactBoard == null)
            {
                rejectionReason = "The active round could not be rewritten";
                return false;
            }

            snapshot.Version = ActiveRoundSnapshot.CurrentVersion;
            snapshot.Board = exactBoard.CaptureSnapshot();
            snapshot.UserPaused = userPaused;
            snapshot.AttemptId = exactRound.AttemptId.Value;
            snapshot.RoundState = (int)exactRound.State;
            snapshot.RoundCompletion = (int)exactRound.Completion;
            snapshot.RoundAttemptKind = (int)exactRound.AttemptKind;
            snapshot.RoundId = exactRound.Token.RoundId;
            snapshot.GameplayEpoch = exactRound.Token.GameplayEpoch;
            snapshot.DomainRevision = exactRound.Revision;
            snapshot.LastOperationId = exactRound.LastOperationId.Value;
            snapshot.BoardRevision = exactRound.BoardRevision;
            snapshot.CompletionFrom = (int)exactRound.CompletionFrom;
            snapshot.CompletionOperationId =
                exactRound.CompletionOperationId.Value;
            snapshot.CompletionCause = (int)exactRound.CompletionCause;
            snapshot.HasSettlementReceipt = exactRound.SettlementReceipt.HasValue;
            snapshot.SettlementReceiptId = exactRound.SettlementReceipt.HasValue
                ? exactRound.SettlementReceipt.Value.PersistenceReceiptId
                : string.Empty;
            return TrySerializeActiveRound(snapshot, out json, out rejectionReason);
        }

        private void MaintainApplicationPause()
        {
            bool suspended = applicationPaused || applicationFocusLost;
            if (suspended)
            {
                if (automaticPauseStamp.IsValid
                    || State != BartenderLevelState.Playing) return;
                if (TrySetPaused(
                        true, BsRoundTransitionCause.ApplicationPause, false)
                    && State == BartenderLevelState.Paused)
                {
                    automaticPauseStamp = coordinator.CurrentStamp;
                    return;
                }
                ClearAutomaticPauseOwnership();
                return;
            }

            if (!automaticPauseStamp.IsValid) return;
            bool shouldResume = State == BartenderLevelState.Paused
                             && coordinator.IsCurrent(automaticPauseStamp);
            ClearAutomaticPauseOwnership();
            if (shouldResume
                && !TrySetPaused(
                    false, BsRoundTransitionCause.ApplicationResume, false)
                && State == BartenderLevelState.Paused)
                automaticPauseStamp = coordinator.CurrentStamp;
        }

        private void ClearAutomaticPauseOwnership()
        {
            automaticPauseStamp = default;
        }

        /// <summary>
        /// Locks one committed revision during animation. The same owner must release that revision; the
        /// board stays unchanged.
        /// </summary>
        public bool TryAcquirePresentationLock(object owner, int committedRevision)
        {
            if (owner == null || presentationLockOwner != null
                || commandInProgress || notificationInProgress
                || committedRevision != BoardRevision)
                return false;

            presentationLockOwner = owner;
            presentationLockRevision = committedRevision;
            return true;
        }

        /// <summary>
        /// LevelLoaded fires before normal lock acquisition is allowed. This entry lock holds timers and
        /// commands until that loaded revision finishes entering.
        /// </summary>
        public bool TryAcquireLoadPresentationLock(object owner, int loadedRevision)
        {
            if (owner == null || presentationLockOwner != null
                || !commandInProgress || !notificationInProgress
                || loadedRevision != BoardRevision)
                return false;

            presentationLockOwner = owner;
            presentationLockRevision = loadedRevision;
            return true;
        }

        /// <summary>Releases a presentation lock owned by <paramref name="owner"/>.</summary>
        public bool ReleasePresentationLock(object owner, int committedRevision)
        {
            if (owner == null || !ReferenceEquals(presentationLockOwner, owner)
                || presentationLockRevision != committedRevision)
                return false;

            presentationLockOwner = null;
            presentationLockRevision = -1;
            return true;
        }

        public PourResult CanPour(int sourceGlassId, int targetGlassId)
        {
            if (coordinator == null || !coordinator.HasBoard)
                return PourResult.Fail("No level is loaded");
            return coordinator.CanPour(sourceGlassId, targetGlassId);
        }

        /// <summary>
        /// Commits the move immediately. Views receive before/after copies and cannot delay or undo the rule
        /// change.
        /// </summary>
        public bool TryPour(int sourceGlassId, int targetGlassId,
                            out BartenderPourReceipt receipt,
                            out string rejectionReason)
        {
            receipt = null;
            rejectionReason = null;
            if (!CanAcceptCommand(out rejectionReason)) return false;
            if (!coordinator.TryStagePour(
                    coordinator.CurrentStamp,
                    sourceGlassId,
                    targetGlassId,
                    out BsStagedBoardMutation staged,
                    out rejectionReason))
                return false;

            commandInProgress = true;
            try
            {
                BoardMemento undoSnapshot = undoHistoryDepth > 0
                    ? CaptureCurrentMemento()
                    : null;
                BoardMemento evictedUndo = CommitUndoSnapshot(undoSnapshot);
                if (!TryCommitBoardStage(
                        staged, 0, out BsRoundCommit commit,
                        out bool publishNow, out rejectionReason))
                {
                    RollBackCommittedUndo(undoSnapshot, evictedUndo);
                    coordinator.TryDiscard(staged);
                    return false;
                }
                receipt = new BartenderPourReceipt(commit.BoardCommit);
                if (publishNow)
                    PublishCoordinatorCommit(commit, receipt);
                if (commit.Transition?.To == BsRoundState.Completed)
                    RetryRetainedSettlement();
                InvokeSafely(BoostersChanged);
                if (commit.Transition == null) ScheduleDeadEndProbe();
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        public int MatchedOrderSlot(int glassId)
        {
            return boardProjection == null ? -1 : boardProjection.MatchedSlot(boardProjection.GlassById(glassId));
        }

        /// <summary>Checks a receipt snapshot against the current orders.</summary>
        public int MatchedOrderSlot(RtGlass glass)
        {
            return boardProjection == null ? -1 : boardProjection.MatchedSlot(glass);
        }

        /// <summary>Uses the normal pickup rules for pointer selection and sound.</summary>
        public bool CanSelectAsPourSource(int glassId)
        {
            if (boardProjection == null) return false;
            RtGlass glass = boardProjection.GlassById(glassId);
            return glass != null && !glass.IsEmpty && !glass.IsChained(boardProjection.Delivered)
                   && glass.TopChainLength(boardProjection.Delivered) > 0;
        }

        /// <summary>The controller chooses shuffle targets. Views only highlight the IDs it returns.</summary>
        public bool CanSelectAsShuffleTarget(int glassId)
        {
            if (boardProjection == null) return false;
            return boardProjection.IsShuffleTarget(boardProjection.GlassById(glassId));
        }

        /// <summary>Writes valid shuffle targets into the caller's reusable list.</summary>
        public int CollectShuffleTargetIds(List<int> destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            return boardProjection != null ? boardProjection.CollectShuffleTargetIds(destination) : 0;
        }

        public bool TryDeliver(int glassId, out BartenderDeliveryReceipt receipt,
                               out string rejectionReason)
        {
            receipt = null;
            rejectionReason = null;
            if (!CanAcceptCommand(out rejectionReason)) return false;
            if (!coordinator.TryStageDelivery(
                    coordinator.CurrentStamp,
                    glassId,
                    out BsStagedBoardMutation staged,
                    out rejectionReason))
                return false;

            commandInProgress = true;
            try
            {
                BoardMemento undoSnapshot = undoHistoryDepth > 0
                    ? CaptureCurrentMemento()
                    : null;
                BoardMemento evictedUndo = CommitUndoSnapshot(undoSnapshot);
                if (!TryCommitBoardStage(
                        staged, 0, out BsRoundCommit commit,
                        out bool publishNow, out rejectionReason))
                {
                    RollBackCommittedUndo(undoSnapshot, evictedUndo);
                    coordinator.TryDiscard(staged);
                    return false;
                }
                RefreshOrderDeadlinesAfterDelivery();
                receipt = new BartenderDeliveryReceipt(commit.BoardCommit);
                if (publishNow)
                    PublishCoordinatorCommit(commit, deliveryReceipt: receipt);
                if (commit.Transition?.To == BsRoundState.Completed)
                    RetryRetainedSettlement();
                InvokeSafely(OrdersChanged);
                InvokeSafely(BoostersChanged);
                if (commit.Transition == null) ScheduleDeadEndProbe();
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        // Boosters require Playing with no command or view lock. Undo only reverts moves; paid boosters
        // stay spent, and failed rounds must use retry.

        private static bool IsValidPaidBoosterCost(int coinCost,
                                                   out string rejectionReason)
        {
            if (coinCost > 0)
            {
                rejectionReason = null;
                return true;
            }
            rejectionReason = "Invalid booster price";
            return false;
        }

        /// <summary>Zero means free inside this helper. Public tray purchases must pass a positive cost.</summary>
        private static bool CanAffordBoosterPurchase(int coinCost,
                                                     out string rejectionReason)
        {
            rejectionReason = null;
            if (coinCost == 0) return true;
            if (!IsValidPaidBoosterCost(coinCost, out rejectionReason)) return false;
            if (BartenderProgressService.CanAfford(coinCost)) return true;
            rejectionReason =
                $"Not enough coins: {BartenderProgressService.Coins}/{coinCost}";
            return false;
        }

        public bool CanPurchaseUndo(out string rejectionReason)
        {
            int coinCost = BartenderProgressTuning.UndoBoosterCoinCost;
            if (!CanAcceptCommand(out rejectionReason)) return false;
            if (undoHistory.Count == 0)
            {
                rejectionReason = "There is no move to undo";
                return false;
            }
            BoardMemento candidate = undoHistory[undoHistory.Count - 1];
            if (!CanImportUndoMemento(candidate, out rejectionReason)) return false;
            if (UndoRemaining <= 0)
            {
                rejectionReason = "No undo uses remain";
                return false;
            }
            if (!CanAffordBoosterPurchase(coinCost, out rejectionReason)) return false;
            if (!MementoHasExpiredTimedOrder(undoHistory[undoHistory.Count - 1]))
                return true;
            rejectionReason =
                "The move can no longer be undone because an order timed out";
            return false;
        }

        private bool CanImportUndoMemento(
            BoardMemento memento,
            out string rejectionReason)
        {
            BsRoundCommandStamp current = coordinator.CurrentStamp;
            if (memento?.Board == null
                || !memento.Stamp.IsValid
                || memento.Stamp.AttemptId != current.AttemptId
                || memento.Stamp.Token != current.Token
                || memento.Stamp.Revision > current.Revision
                || memento.Stamp.BoardRevision > current.BoardRevision
                || memento.Board.IsWin()
                || memento.Board.IsFail())
            {
                rejectionReason = "The saved undo checkpoint is stale or invalid";
                return false;
            }

            rejectionReason = null;
            return true;
        }

        /// <summary>The controller checks both price and eligibility so the tray and saved purchase agree.</summary>
        public bool TryPurchaseUndo(out string rejectionReason)
        {
            return TryUndoInternal(
                BartenderProgressTuning.UndoBoosterCoinCost, out rejectionReason);
        }

        private bool TryUndoInternal(int coinCost, out string rejectionReason)
        {
            if (!CanPurchaseUndo(out rejectionReason)) return false;

            commandInProgress = true;
            try
            {
                BoardMemento rollback = CaptureCurrentMemento();
                int last = undoHistory.Count - 1;
                BoardMemento memento = undoHistory[last];
                if (!coordinator.TryImportBoardCheckpoint(
                        memento.Stamp,
                        memento.Board,
                        out BsBoardCheckpoint checkpoint,
                        out rejectionReason)
                    || !coordinator.TryStageCheckpointRestore(
                        coordinator.CurrentStamp,
                        checkpoint,
                        out BsStagedBoardMutation staged,
                        out rejectionReason))
                {
                    // Re-arm the probe after a stale undo snapshot so detection is not lost.
                    ScheduleDeadEndProbe();
                    return false;
                }

                undoHistory.RemoveAt(last);
                UndoRemaining--;
                RestoreDeadlinesForBoard(memento.Board, memento.SlotDeadlines);
                if (!TryCommitBoardStage(
                        staged, coinCost, out BsRoundCommit commit,
                        out bool publishNow, out rejectionReason))
                {
                    coordinator.TryDiscard(staged);
                    undoHistory.Add(memento);
                    UndoRemaining++;
                    RestoreLiveDeadlines(rollback.SlotDeadlines);
                    ScheduleDeadEndProbe();
                    return false;
                }
                RestoreLiveDeadlines(memento.SlotDeadlines);
                if (publishNow) PublishCoordinatorCommit(commit);
                InvokeSafely(OrdersChanged);
                InvokeSafely(BoostersChanged);
                if (ExpireOrderIfNeeded(out rejectionReason)
                    == OrderExpiryResult.SettlementRejected)
                    RetryRetainedSettlement();
                else ScheduleDeadEndProbe();
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        public bool CanPurchaseExtraGlass(GlassType type,
                                          out string rejectionReason)
        {
            if (!CanAcceptCommand(out rejectionReason)) return false;
            if (ExtraGlassRemaining <= 0)
            {
                rejectionReason = "No extra glass uses remain";
                return false;
            }
            if (!IsKnownGlassType(type))
            {
                rejectionReason = "Invalid glass type";
                return false;
            }
            if (boardProjection.Glasses.Count >= MaxActiveGlasses)
            {
                rejectionReason = $"At most {MaxActiveGlasses} glasses can be active";
                return false;
            }
            return CanAffordBoosterPurchase(
                BartenderProgressTuning.ExtraGlassBoosterCoinCost,
                out rejectionReason);
        }

        public bool TryPurchaseExtraGlass(GlassType type,
                                          out int newGlassId,
                                          out string rejectionReason)
        {
            return TryAddExtraGlassInternal(
                type, BartenderProgressTuning.ExtraGlassBoosterCoinCost,
                out newGlassId, out rejectionReason);
        }

        private bool TryAddExtraGlassInternal(GlassType type, int coinCost,
                                              out int newGlassId,
                                              out string rejectionReason)
        {
            newGlassId = -1;
            if (!CanPurchaseExtraGlass(type, out rejectionReason)) return false;

            commandInProgress = true;
            try
            {
                var previousUndoHistory = new List<BoardMemento>(undoHistory);
                if (!coordinator.TryStageAddEmptyGlass(
                        coordinator.CurrentStamp,
                        type,
                        MaxActiveGlasses,
                        out BsStagedBoardMutation staged,
                        out rejectionReason))
                    return false;

                newGlassId = staged.GlassEvidence.GlassId;
                ExtraGlassRemaining--;
                undoHistory.Clear();
                if (!TryCommitBoardStage(
                        staged, coinCost, out BsRoundCommit commit,
                        out bool publishNow, out rejectionReason))
                {
                    coordinator.TryDiscard(staged);
                    undoHistory.AddRange(previousUndoHistory);
                    ExtraGlassRemaining++;
                    newGlassId = -1;
                    ScheduleDeadEndProbe();
                    return false;
                }

                // I clear undo history so an older board cannot remove the bought glass. Undo becomes safe
                // again after the next move.
                if (publishNow) PublishCoordinatorCommit(commit);
                InvokeSafely(BoostersChanged);
                ScheduleDeadEndProbe();
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        /// <summary>
        /// Checks whether time can be bought now. Expired orders fail first; boosters cannot undo a terminal
        /// result.
        /// </summary>
        public bool CanPurchaseTimeBoost(float seconds, int coinCost,
                                         out string rejectionReason)
        {
            return CanPurchaseTimeBoost(
                seconds, coinCost, true, out rejectionReason);
        }

        /// <summary>
        /// Checks tray availability each frame without allocating an unused balance message. Command and
        /// expiry checks still run.
        /// </summary>
        internal bool CanPurchaseTimeBoost(float seconds, int coinCost)
        {
            return CanPurchaseTimeBoost(
                seconds, coinCost, false, false, false, out _);
        }

        private bool CanPurchaseTimeBoost(float seconds, int coinCost,
                                          bool includeDetailedBalanceReason,
                                          out string rejectionReason)
        {
            return CanPurchaseTimeBoost(seconds, coinCost,
                includeDetailedBalanceReason, false, false, out rejectionReason);
        }

        private bool CanPurchaseTimeBoost(float seconds, int coinCost,
                                          bool includeDetailedBalanceReason,
                                          bool ignoreCoins,
                                          bool authorizedOfferPurchase,
                                          out string rejectionReason)
        {
            if (!TryCollectTimeBoostTargets(
                    seconds, authorizedOfferPurchase, out rejectionReason)) return false;
            if (TimeBoostRemaining <= 0)
            {
                rejectionReason = "No time booster uses remain";
                return false;
            }
            if (coinCost <= 0)
            {
                rejectionReason = "Invalid time booster price";
                return false;
            }
            if (!ignoreCoins && !BartenderProgressService.CanAfford(coinCost))
            {
                rejectionReason = includeDetailedBalanceReason
                    ? $"Not enough coins: {BartenderProgressService.Coins}/{coinCost}"
                    : null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Adds time to currently open timed orders. I update matching undo deadlines too, so undo cannot
        /// refund bought time or coins.
        /// </summary>
        public bool TryPurchaseTimeBoost(float seconds, int coinCost,
                                         out string rejectionReason)
        {
            return TryPurchaseTimeBoostInternal(
                seconds, coinCost, false, out rejectionReason);
        }

        private bool TryPurchaseTimeBoostInternal(
            float seconds,
            int coinCost,
            bool authorizedOfferPurchase,
            out string rejectionReason)
        {
            if (!CanPurchaseTimeBoost(seconds, coinCost, true, false,
                    authorizedOfferPurchase, out rejectionReason)) return false;
            int[] targetOrders = timeBoostOrderScratch.ToArray();

            commandInProgress = true;
            TimeBoostMutationSnapshot rollback = null;
            bool durableCommitAccepted = false;
            try
            {
                rollback = CaptureTimeBoostMutation();
                // The target set stays fixed on Unity's main thread; commandInProgress blocks nested
                // commands.
                for (int target = 0; target < targetOrders.Length; target++)
                {
                    int orderIndex = targetOrders[target];
                    timeBonusByOrderIndex[orderIndex] += seconds;
                    ExtendLiveDeadline(orderIndex, seconds);
                    ExtendUndoDeadlines(orderIndex, seconds);
                }
                TimeBoostRemaining--;

                // Apply the bonus before CoinsChanged so listeners see matching balance, timers and stock.
                // Roll back silently if saving fails.
                if (!TryCommitActiveRound(coinCost, out rejectionReason))
                {
                    RestoreTimeBoostMutation(rollback);
                    return false;
                }
                durableCommitAccepted = true;

                InvokeSafely(TimeBoosted, seconds);
                InvokeSafely(OrdersChanged);
                InvokeSafely(BoostersChanged);
                return true;
            }
            catch
            {
                // Saving may throw before commit. Restore every changed value before reopening the offer.
                if (!durableCommitAccepted && rollback != null)
                    RestoreTimeBoostMutation(rollback);
                throw;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        private TimeBoostMutationSnapshot CaptureTimeBoostMutation()
        {
            var snapshot = new TimeBoostMutationSnapshot
            {
                TimeBonusByOrderIndex = (double[])timeBonusByOrderIndex.Clone(),
                TimeBoostRemaining = TimeBoostRemaining,
                LiveDeadlines = new Dictionary<OrderDef, double>(
                    orderDeadlines, ReferenceComparer<OrderDef>.Instance),
                UndoDeadlines = new double?[undoHistory.Count][],
            };
            for (int history = 0; history < undoHistory.Count; history++)
            {
                double?[] deadlines = undoHistory[history]?.SlotDeadlines;
                snapshot.UndoDeadlines[history] = deadlines != null
                    ? (double?[])deadlines.Clone()
                    : null;
            }
            return snapshot;
        }

        private void RestoreTimeBoostMutation(TimeBoostMutationSnapshot snapshot)
        {
            timeBonusByOrderIndex = snapshot.TimeBonusByOrderIndex;
            TimeBoostRemaining = snapshot.TimeBoostRemaining;

            orderDeadlines.Clear();
            foreach (KeyValuePair<OrderDef, double> deadline in snapshot.LiveDeadlines)
                orderDeadlines.Add(deadline.Key, deadline.Value);

            int historyCount = Math.Min(
                undoHistory.Count, snapshot.UndoDeadlines.Length);
            for (int history = 0; history < historyCount; history++)
            {
                BoardMemento memento = undoHistory[history];
                if (memento != null)
                    memento.SlotDeadlines = snapshot.UndoDeadlines[history];
            }
        }

        private bool TryCollectTimeBoostTargets(
            float seconds,
            bool authorizedOfferPurchase,
            out string rejectionReason)
        {
            timeBoostOrderScratch.Clear();
            rejectionReason = null;
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0f)
            {
                rejectionReason = "The added time must be positive and finite";
                return false;
            }
            if (authorizedOfferPurchase)
            {
                if (!CanAcceptTimeOfferPurchaseCommand(out rejectionReason)) return false;
            }
            else if (!CanAcceptCommand(out rejectionReason)) return false;
            if (!boardProjection.TimedOrdersEnabled)
            {
                rejectionReason = "This level has no timed orders";
                return false;
            }

            for (int slot = 0; slot < boardProjection.Slots.Length; slot++)
            {
                OrderDef order = boardProjection.Slots[slot];
                if (order == null || order.TimeLimit <= 0f
                    || !orderDeadlines.ContainsKey(order))
                    continue;

                int orderIndex = order.RuntimeOrderIndex;
                if (orderIndex < 0 || orderIndex >= timeBonusByOrderIndex.Length)
                {
                    rejectionReason = "Invalid timed order identifier";
                    timeBoostOrderScratch.Clear();
                    return false;
                }
                if (!timeBoostOrderScratch.Contains(orderIndex))
                    timeBoostOrderScratch.Add(orderIndex);
            }

            if (timeBoostOrderScratch.Count > 0) return true;
            rejectionReason = "There are no open timed orders";
            return false;
        }

        private void ExtendLiveDeadline(int orderIndex, double seconds)
        {
            for (int slot = 0; slot < boardProjection.Slots.Length; slot++)
            {
                OrderDef order = boardProjection.Slots[slot];
                if (order == null || order.RuntimeOrderIndex != orderIndex
                    || !orderDeadlines.TryGetValue(order, out double deadline))
                    continue;
                orderDeadlines[order] = deadline + seconds;
            }
        }

        private void ExtendUndoDeadlines(int orderIndex, double seconds)
        {
            for (int history = 0; history < undoHistory.Count; history++)
            {
                BoardMemento memento = undoHistory[history];
                if (memento?.Board?.Slots == null || memento.SlotDeadlines == null)
                    continue;

                int count = Math.Min(memento.Board.Slots.Length,
                                     memento.SlotDeadlines.Length);
                for (int slot = 0; slot < count; slot++)
                {
                    OrderDef order = memento.Board.Slots[slot];
                    if (order == null || order.RuntimeOrderIndex != orderIndex
                        || !memento.SlotDeadlines[slot].HasValue)
                        continue;
                    memento.SlotDeadlines[slot] =
                        memento.SlotDeadlines[slot].Value + seconds;
                }
            }
        }

        /// <summary>
        /// Checks for a valid shuffle target before selection starts. Selecting alone spends no coins or
        /// stock.
        /// </summary>
        public bool CanPurchaseShuffle(out string rejectionReason)
        {
            if (!CanAcceptCommand(out rejectionReason)) return false;
            if (ShuffleRemaining <= 0)
            {
                rejectionReason = "No shuffle uses remain";
                return false;
            }
            if (!CanAffordBoosterPurchase(
                    BartenderProgressTuning.ShuffleBoosterCoinCost,
                    out rejectionReason)) return false;
            if (boardProjection != null && boardProjection.HasShuffleTarget()) return true;
            rejectionReason = "There is no bottle whose layers can be shuffled";
            return false;
        }

        public bool TryPurchaseShuffle(int glassId, int expectedBoardRevision,
                                       out string rejectionReason)
        {
            return TryShuffleInternal(
                glassId, expectedBoardRevision,
                BartenderProgressTuning.ShuffleBoosterCoinCost,
                out rejectionReason);
        }

        private bool TryShuffleInternal(int glassId, int expectedBoardRevision,
                                        int coinCost, out string rejectionReason)
        {
            if (!CanPurchaseShuffle(out rejectionReason)) return false;
            if (BoardRevision != expectedBoardRevision)
            {
                rejectionReason = "The boardProjection changed before the bottle was selected";
                return false;
            }
            commandInProgress = true;
            try
            {
                int selectionKey = UnityEngine.Random.Range(0, int.MaxValue);
                if (!coordinator.TryStageShuffle(
                        coordinator.CurrentStamp,
                        glassId,
                        selectionKey,
                        out BsStagedBoardMutation staged,
                        out rejectionReason))
                    return false;

                ShuffleRemaining--;
                var previousUndoHistory = new List<BoardMemento>(undoHistory);
                undoHistory.Clear();
                if (!TryCommitBoardStage(
                        staged, coinCost, out BsRoundCommit commit,
                        out bool publishNow, out rejectionReason))
                {
                    coordinator.TryDiscard(staged);
                    undoHistory.AddRange(previousUndoHistory);
                    ShuffleRemaining++;
                    ScheduleDeadEndProbe();
                    return false;
                }
                if (publishNow) PublishCoordinatorCommit(commit);
                InvokeSafely(BoostersChanged);
                ScheduleDeadEndProbe();
                return true;
            }
            finally
            {
                commandInProgress = false;
                FlushPendingStateChanged();
            }
        }

        private bool CanAcceptTimeOfferPurchaseCommand(out string rejectionReason)
        {
            if (timeOfferMachine.State != BsTimeOfferState.Accepting
                || !timeOfferContext.Id.IsValid
                || commandInProgress || notificationInProgress
                || !IsPresentationBarrierExclusivelyOwnedBy(timeOfferBarrierOwner))
            {
                rejectionReason = "The time offer cannot be purchased right now";
                return false;
            }
            if (State != BartenderLevelState.Playing || boardProjection == null)
            {
                rejectionReason = "The level is not ready to play";
                return false;
            }

            rejectionReason = null;
            return true;
        }

        private bool TryCreateInitialRoundJson(
            BsLevel level,
            BsBoard initialBoard,
            BsRoundSnapshot exactRound,
            out string json,
            out string rejectionReason)
        {
            json = null;
            rejectionReason = null;
            if (level == null || initialBoard == null || exactRound == null)
            {
                rejectionReason = "The initial round could not be captured";
                return false;
            }

            int slotCount = initialBoard.Slots?.Length ?? 0;
            var deadlines = new double[slotCount];
            var hasDeadline = new bool[slotCount];
            if (initialBoard.TimedOrdersEnabled)
            {
                for (int slot = 0; slot < slotCount; slot++)
                {
                    OrderDef order = initialBoard.Slots[slot];
                    if (order == null || order.TimeLimit <= 0f) continue;
                    deadlines[slot] = order.TimeLimit;
                    hasDeadline[slot] = true;
                }
            }

            var snapshot = new ActiveRoundSnapshot
            {
                Version = ActiveRoundSnapshot.CurrentVersion,
                LevelSignature = ComputeLevelSignature(level),
                Board = initialBoard.CaptureSnapshot(),
                ActiveGameplayTime = 0d,
                TimeBonusByOrderIndex = new double[level.Orders.Count],
                SlotDeadlines = deadlines,
                HasSlotDeadline = hasDeadline,
                UndoRemaining = Mathf.Max(0, level.UndoCount),
                ExtraGlassRemaining = Mathf.Max(0, level.ExtraGlassCount),
                TimeBoostRemaining = Mathf.Max(0, level.TimeBoostCount),
                ShuffleRemaining = Mathf.Max(0, level.ShuffleCount),
                UserPaused = false,
                HasTimeOfferSettlement = false,
            };
            snapshot.AttemptId = exactRound.AttemptId.Value;
            snapshot.RoundState = (int)exactRound.State;
            snapshot.RoundCompletion = (int)exactRound.Completion;
            snapshot.RoundAttemptKind = (int)exactRound.AttemptKind;
            snapshot.RoundId = exactRound.Token.RoundId;
            snapshot.GameplayEpoch = exactRound.Token.GameplayEpoch;
            snapshot.DomainRevision = exactRound.Revision;
            snapshot.LastOperationId = exactRound.LastOperationId.Value;
            snapshot.BoardRevision = exactRound.BoardRevision;
            snapshot.CompletionFrom = (int)exactRound.CompletionFrom;
            snapshot.CompletionOperationId =
                exactRound.CompletionOperationId.Value;
            snapshot.CompletionCause = (int)exactRound.CompletionCause;
            snapshot.HasSettlementReceipt =
                exactRound.SettlementReceipt.HasValue;
            snapshot.SettlementReceiptId =
                exactRound.SettlementReceipt.HasValue
                    ? exactRound.SettlementReceipt.Value.PersistenceReceiptId
                    : string.Empty;
            if (!TrySerializeActiveRound(snapshot, out json, out rejectionReason))
                return false;

            // Validate the new board payload before saving its attempt receipt.
            return TryDecodeActiveRound(
                level,
                exactRound.AttemptId.Value,
                json,
                out _,
                out rejectionReason);
        }

        private bool TryCaptureActiveRoundJson(out string json,
                                               out string rejectionReason)
        {
            json = null;
            rejectionReason = null;
            if (IsStandaloneRound || boardProjection == null || CurrentLevel == null
                || CurrentCampaignSlot < 0 || string.IsNullOrEmpty(CurrentAttemptValue))
            {
                rejectionReason = "There is no active campaign round to save";
                return false;
            }

            return TryCaptureActiveRoundJson(
                coordinator.CaptureSnapshot(),
                boardProjection,
                null,
                out json,
                out rejectionReason);
        }

        private bool TryCaptureActiveRoundJson(
            BsRoundSnapshot roundSnapshot,
            BsBoard snapshotBoard,
            bool? userPausedOverride,
            out string json,
            out string rejectionReason)
        {
            json = null;
            rejectionReason = null;
            if (roundSnapshot == null || snapshotBoard == null
                || CurrentLevel == null || CurrentCampaignSlot < 0
                || !roundSnapshot.AttemptId.IsValid)
            {
                rejectionReason = "There is no active campaign round to save";
                return false;
            }

            CaptureDeadlinesForBoard(
                snapshotBoard, out double[] deadlines, out bool[] hasDeadline);
            var snapshot = new ActiveRoundSnapshot
            {
                LevelSignature = ComputeLevelSignature(CurrentLevel),
                Board = snapshotBoard.CaptureSnapshot(),
                ActiveGameplayTime = activeGameplayTime,
                TimeBonusByOrderIndex = (double[])timeBonusByOrderIndex.Clone(),
                SlotDeadlines = deadlines,
                HasSlotDeadline = hasDeadline,
                UndoRemaining = UndoRemaining,
                ExtraGlassRemaining = ExtraGlassRemaining,
                TimeBoostRemaining = TimeBoostRemaining,
                ShuffleRemaining = ShuffleRemaining,
                UserPaused = userPausedOverride
                    ?? (roundSnapshot.State == BsRoundState.Paused
                        && userPauseOwned),
                AttemptId = roundSnapshot.AttemptId.Value,
                RoundState = (int)roundSnapshot.State,
                RoundCompletion = (int)roundSnapshot.Completion,
                RoundAttemptKind = (int)roundSnapshot.AttemptKind,
                RoundId = roundSnapshot.Token.RoundId,
                GameplayEpoch = roundSnapshot.Token.GameplayEpoch,
                DomainRevision = roundSnapshot.Revision,
                LastOperationId = roundSnapshot.LastOperationId.Value,
                BoardRevision = roundSnapshot.BoardRevision,
                CompletionFrom = (int)roundSnapshot.CompletionFrom,
                CompletionOperationId = roundSnapshot.CompletionOperationId.Value,
                CompletionCause = (int)roundSnapshot.CompletionCause,
                HasSettlementReceipt = roundSnapshot.SettlementReceipt.HasValue,
                SettlementReceiptId = roundSnapshot.SettlementReceipt.HasValue
                    ? roundSnapshot.SettlementReceipt.Value.PersistenceReceiptId
                    : string.Empty,
                HasTimeOfferSettlement = timeOfferSettlementContext.HasValue
                    && timeOfferSettlementContext.Value.IsPending,
            };
            if (snapshot.HasTimeOfferSettlement)
            {
                BsTimeOfferSettlementSnapshot settlement =
                    timeOfferSettlementContext.Value;
                snapshot.TimeOfferSettlementId = settlement.OfferId.Value;
                snapshot.TimeOfferDeclineDisposition = (int)settlement.Disposition;
            }
            for (int i = 0; i < undoHistory.Count; i++)
                snapshot.UndoHistory.Add(CapturePersistedMemento(undoHistory[i]));
            return TrySerializeActiveRound(snapshot, out json, out rejectionReason);
        }

        private bool TryCommitActiveRound(int coinCost, out string rejectionReason)
        {
            if (IsStandaloneRound)
            {
                if (coinCost == 0)
                {
                    rejectionReason = null;
                    return true;
                }
                return BartenderProgressService.TrySpendCoins(coinCost,
                    out rejectionReason);
            }
            if (!TryCaptureActiveRoundJson(out string json, out rejectionReason))
                return false;
            return BartenderProgressService.TryCommitActiveRound(
                CurrentAttemptValue, CurrentCampaignSlot, json, coinCost,
                out rejectionReason);
        }

        private bool TryCommitBoardStage(
            BsStagedBoardMutation staged,
            int coinCost,
            out BsRoundCommit commit,
            out bool publishNow,
            out string rejectionReason)
        {
            commit = null;
            publishNow = false;
            rejectionReason = null;
            if (coordinator == null
                || !coordinator.TryCaptureProspectiveSnapshot(
                    staged, out BsRoundSnapshot prospective,
                    out rejectionReason))
                return false;
            BsBoard candidate = staged.CaptureCandidateBoard();
            BartenderDailyActivityReceipt dailyActivity =
                BartenderDailyActivityReceipt.From(staged);

            BsSettlementReceipt? settlementReceipt = null;
            if (IsStandaloneRound)
            {
                if (coinCost > 0
                    && !BartenderProgressService.TrySpendCoins(
                        coinCost, out rejectionReason))
                    return false;
            }
            else
            {
                if (!TryCaptureActiveRoundJson(
                        prospective, candidate, null,
                        out string json, out rejectionReason))
                    return false;
                if (staged.RequiresSettlement)
                {
                    BsSettlementRequest request = staged.SettlementRequest.Value;
                    var draft = new BsSettlementDraft(
                        request,
                        CurrentCampaignSlot,
                        request.Completion == BsRoundCompletion.Won
                            ? CurrentCampaignSlot + 1
                            : -1,
                        TimeOfferIdForCause(request.Cause));
                    if (!BartenderProgressService.TryStageSettlement(
                            draft, json, coinCost, dailyActivity,
                            out BsSettlementReceipt durableReceipt,
                            out rejectionReason))
                        return false;
                    settlementReceipt = durableReceipt;
                }
                else if (!BartenderProgressService.TryCommitActiveRound(
                    CurrentAttemptValue,
                    CurrentCampaignSlot,
                    json,
                    coinCost,
                    dailyActivity,
                    out rejectionReason))
                {
                    return false;
                }
            }

            if (!coordinator.TryCommitBoardMutation(
                    staged, settlementReceipt,
                    out commit, out rejectionReason))
                return false;
            SyncBoardProjection();
            publishNow = true;
            if (staged.RequiresSettlement && !IsStandaloneRound)
            {
                retainedSettlementReceipt = settlementReceipt;
                announcedSettlementReceipt = null;
                retainedSettlementRetryAt = 0f;
            }
            rejectionReason = null;
            return true;
        }

        private bool TryCommitCompletionStage(
            BsStagedRoundCompletion staged,
            int coinCost,
            out BsRoundCommit commit,
            out bool publishNow,
            out string rejectionReason)
        {
            commit = null;
            publishNow = false;
            rejectionReason = null;
            if (coordinator == null
                || !coordinator.TryCaptureProspectiveSnapshot(
                    staged, out BsRoundSnapshot prospective,
                    out rejectionReason))
                return false;

            BsSettlementReceipt? settlementReceipt = null;
            if (!IsStandaloneRound)
            {
                if (!TryCaptureActiveRoundJson(
                        prospective, boardProjection, null,
                        out string json, out rejectionReason))
                    return false;
                BsSettlementRequest request = staged.SettlementRequest;
                var draft = new BsSettlementDraft(
                    request,
                    CurrentCampaignSlot,
                    request.Completion == BsRoundCompletion.Won
                        ? CurrentCampaignSlot + 1
                        : -1,
                    TimeOfferIdForCause(request.Cause));
                if (!BartenderProgressService.TryStageSettlement(
                        draft, json, coinCost,
                        out BsSettlementReceipt durableReceipt,
                        out rejectionReason))
                    return false;
                settlementReceipt = durableReceipt;
            }
            else if (coinCost > 0
                     && !BartenderProgressService.TrySpendCoins(
                         coinCost, out rejectionReason))
            {
                return false;
            }

            if (!coordinator.TryCommitCompletion(
                    staged, settlementReceipt,
                    out commit, out rejectionReason))
                return false;
            publishNow = true;
            if (!IsStandaloneRound)
            {
                retainedSettlementReceipt = settlementReceipt;
                announcedSettlementReceipt = null;
                retainedSettlementRetryAt = 0f;
            }
            rejectionReason = null;
            return true;
        }

        private BsTimeOfferId TimeOfferIdForCause(
            BsRoundTransitionCause cause)
        {
            bool timeOffer =
                cause == BsRoundTransitionCause.TimeOfferDeclinedPresentFailure
                || cause == BsRoundTransitionCause.TimeOfferDeclinedReturnToMenu;
            return timeOffer && timeOfferSettlementContext.HasValue
                ? timeOfferSettlementContext.Value.OfferId
                : default;
        }

        private bool RetryRetainedSettlement()
        {
            if (!retainedSettlementReceipt.HasValue) return true;
            bool retryingOffer = timeOfferMachine.State
                == BsTimeOfferState.DeclineSettlementPending;
            BsTimeOfferId offerId = retryingOffer
                ? timeOfferMachine.CurrentId
                : default;
            if (IsTerminalSettlementCommitted)
            {
                PublishTerminalSettlementCommitted(
                    retainedSettlementReceipt.Value);
                if (retryingOffer)
                {
                    RequireTimeOfferTransition(
                        BsTimeOfferTrigger.RetryDue, offerId);
                    RequireTimeOfferTransition(
                        BsTimeOfferTrigger.SettlementCommitted, offerId);
                    timeOfferContext = default;
                }
                if (timeOfferSettlementContext.HasValue
                    && !timeOfferSettlementContext.Value.TerminalCommitted)
                    MarkTimeOfferSettlementCommitted();
                return true;
            }
            if (retryingOffer)
                RequireTimeOfferTransition(BsTimeOfferTrigger.RetryDue, offerId);
            BsSettlementReceipt receipt = retainedSettlementReceipt.Value;
            if (!BartenderProgressService.TryCommitSettlement(
                    receipt, out string rejectionReason))
            {
                retainedSettlementRetryAt = Time.unscaledTime + 1f;
                if (retryingOffer)
                    RequireTimeOfferTransition(
                        BsTimeOfferTrigger.SettlementDeferred, offerId);
                if (!string.IsNullOrEmpty(rejectionReason))
                    Debug.LogWarning(
                        "The staged round result is still pending: "
                        + rejectionReason, this);
                return false;
            }
            retainedSettlementRetryAt = 0f;
            PublishTerminalSettlementCommitted(receipt);
            if (retryingOffer)
            {
                RequireTimeOfferTransition(
                    BsTimeOfferTrigger.SettlementCommitted, offerId);
                timeOfferContext = default;
            }
            if (timeOfferSettlementContext.HasValue)
                MarkTimeOfferSettlementCommitted();
            return true;
        }

        private void PublishTerminalSettlementCommitted(
            BsSettlementReceipt receipt)
        {
            if (!receipt.IsValid
                || (announcedSettlementReceipt.HasValue
                    && announcedSettlementReceipt.Value == receipt))
                return;
            announcedSettlementReceipt = receipt;
            InvokeSafely(TerminalSettlementCommitted, receipt);
        }

        private bool TryCompleteRound(
            BsRoundCompletion completion,
            BsRoundTransitionCause cause,
            out string rejectionReason)
        {
            rejectionReason = null;
            if (coordinator == null
                || !coordinator.TryStageCompletion(
                    coordinator.CurrentStamp, completion, cause,
                    out BsStagedRoundCompletion staged,
                    out rejectionReason))
                return false;

            bool ownsCommand = !commandInProgress;
            if (ownsCommand) commandInProgress = true;
            try
            {
                if (!TryCommitCompletionStage(
                        staged, 0, out BsRoundCommit commit,
                        out bool publishNow, out rejectionReason))
                {
                    coordinator.TryDiscard(staged);
                    return false;
                }
                if (publishNow) PublishCoordinatorCommit(commit, publishBoard: false);
                bool timeOfferCause = cause
                    == BsRoundTransitionCause.TimeOfferDeclinedPresentFailure
                    || cause == BsRoundTransitionCause.TimeOfferDeclinedReturnToMenu;
                if (!timeOfferCause) RetryRetainedSettlement();
                return true;
            }
            finally
            {
                if (ownsCommand)
                {
                    commandInProgress = false;
                    FlushPendingStateChanged();
                }
            }
        }

        private void CheckpointActiveRound()
        {
            if (commandInProgress || coordinator?.HasStagedOperation == true
                || IsStandaloneRound
                || boardProjection == null || CurrentCampaignSlot < 0
                || string.IsNullOrEmpty(CurrentAttemptValue)
                || (State != BartenderLevelState.Playing
                    && State != BartenderLevelState.Paused))
                return;
            if (!TryCommitActiveRound(0, out string rejectionReason)
                && !string.IsNullOrEmpty(rejectionReason))
                Debug.LogWarning("The active round could not be checkpointed: "
                               + rejectionReason, this);
        }

        private static bool TrySerializeActiveRound(ActiveRoundSnapshot snapshot,
                                                    out string json,
                                                    out string rejectionReason)
        {
            json = null;
            rejectionReason = null;
            try
            {
                json = JsonUtility.ToJson(snapshot);
                if (!string.IsNullOrWhiteSpace(json) && json != "{}") return true;
            }
            catch (Exception exception)
            {
                rejectionReason = "The active round could not be serialized: "
                                + exception.Message;
                return false;
            }
            rejectionReason = "The active round snapshot is empty";
            return false;
        }

        private bool TryDecodeActiveRound(BsLevel level, string attemptId, string json,
                                          out RestoredActiveRound restored,
                                          out string rejectionReason)
        {
            restored = null;
            rejectionReason = null;
            ActiveRoundSnapshot snapshot;
            try
            {
                snapshot = string.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonUtility.FromJson<ActiveRoundSnapshot>(json);
            }
            catch (Exception exception)
            {
                rejectionReason = "The round snapshot JSON is invalid: "
                                + exception.Message;
                return false;
            }

            if (snapshot == null
                || snapshot.Version != ActiveRoundSnapshot.CurrentVersion
                || !string.Equals(snapshot.LevelSignature,
                    ComputeLevelSignature(level), StringComparison.Ordinal))
            {
                rejectionReason = "The round snapshot no longer matches the level";
                return false;
            }
            if (snapshot.DomainRevision == long.MaxValue
                || snapshot.LastOperationId == long.MaxValue
                || snapshot.BoardRevision == int.MaxValue
                || snapshot.RoundId == int.MaxValue
                || snapshot.GameplayEpoch == int.MaxValue)
            {
                rejectionReason =
                    "The round snapshot identity space is exhausted";
                return false;
            }
            if (!BsBoard.TryRestore(level, snapshot.Board,
                    out BsBoard restoredBoard, out rejectionReason))
                return false;
            if (restoredBoard.Glasses.Count > MaxActiveGlasses
                || !ValidateSnapshotColors(restoredBoard))
            {
                rejectionReason = "The round snapshot contains an invalid glass";
                return false;
            }
            if (!IsFiniteNonNegative(snapshot.ActiveGameplayTime)
                || snapshot.TimeBonusByOrderIndex == null
                || snapshot.TimeBonusByOrderIndex.Length != level.Orders.Count)
            {
                rejectionReason = "The round clock state is invalid";
                return false;
            }
            for (int i = 0; i < snapshot.TimeBonusByOrderIndex.Length; i++)
            {
                if (IsFiniteNonNegative(snapshot.TimeBonusByOrderIndex[i])) continue;
                rejectionReason = "The round time bonus is invalid";
                return false;
            }
            if (!ValidBoosterStock(snapshot.UndoRemaining, level.UndoCount)
                || !ValidBoosterStock(snapshot.ExtraGlassRemaining,
                    level.ExtraGlassCount)
                || !ValidBoosterStock(snapshot.TimeBoostRemaining,
                    level.TimeBoostCount)
                || !ValidBoosterStock(snapshot.ShuffleRemaining,
                    level.ShuffleCount))
            {
                rejectionReason = "The saved booster stock is invalid";
                return false;
            }

            BsTimeOfferSettlementSnapshot? restoredTimeOfferSettlement = null;
            if (snapshot.HasTimeOfferSettlement)
            {
                int disposition = snapshot.TimeOfferDeclineDisposition;
                var offerId = new BsTimeOfferId(snapshot.TimeOfferSettlementId);
                if (!offerId.IsValid
                    || snapshot.TimeOfferSettlementId == long.MaxValue
                    || disposition < (int)BsTimeOfferDeclineDisposition.PresentFailure
                    || disposition > (int)BsTimeOfferDeclineDisposition.ReturnToMainMenu)
                {
                    rejectionReason = "The saved time-offer settlement is invalid";
                    return false;
                }
                restoredTimeOfferSettlement = new BsTimeOfferSettlementSnapshot(
                    offerId,
                    (BsTimeOfferDeclineDisposition)disposition,
                    BsTimeOfferSettlementStatus.SettlementPending);
            }
            if (!TryDecodeDeadlineVector(restoredBoard, snapshot.SlotDeadlines,
                    snapshot.HasSlotDeadline, out double?[] liveDeadlines,
                    out rejectionReason))
                return false;

            if (snapshot.UndoHistory == null
                || snapshot.UndoHistory.Count > Mathf.Max(0, undoHistoryDepth))
            {
                rejectionReason = "The saved undo history is invalid";
                return false;
            }
            var restoredHistory = new List<BoardMemento>(snapshot.UndoHistory.Count);
            int expectedHistoryGameplayEpoch =
                snapshot.RoundState == (int)BsRoundState.Completed
                    ? snapshot.GameplayEpoch - 1
                    : snapshot.GameplayEpoch;
            for (int i = 0; i < snapshot.UndoHistory.Count; i++)
            {
                PersistedBoardMemento saved = snapshot.UndoHistory[i];
                if (saved == null
                    || !BsBoard.TryRestore(level, saved.Board,
                        out BsBoard historyBoard, out rejectionReason)
                    || historyBoard.Glasses.Count > MaxActiveGlasses
                    || !ValidateSnapshotColors(historyBoard)
                    || historyBoard.IsWin()
                    || historyBoard.IsFail()
                    || !TryDecodeDeadlineVector(historyBoard,
                        saved.SlotDeadlines, saved.HasSlotDeadline,
                        out double?[] historyDeadlines, out rejectionReason))
                {
                    if (string.IsNullOrEmpty(rejectionReason))
                        rejectionReason = "A saved undo state is invalid";
                    return false;
                }

                bool legacyStamp = string.IsNullOrEmpty(saved.AttemptId)
                    && saved.RoundId == 0
                    && saved.GameplayEpoch == 0
                    && saved.DomainRevision == 0L
                    && saved.BoardRevision == 0;
                var historyStamp = legacyStamp
                    ? default
                    : new BsRoundCommandStamp(
                        new BsAttemptId(saved.AttemptId),
                        new BsRoundToken(saved.RoundId, saved.GameplayEpoch),
                        saved.DomainRevision,
                        saved.BoardRevision);
                if (!legacyStamp
                    && (!historyStamp.IsValid
                        || !string.Equals(saved.AttemptId, snapshot.AttemptId,
                            StringComparison.Ordinal)
                        || saved.RoundId != snapshot.RoundId
                        || saved.GameplayEpoch != expectedHistoryGameplayEpoch
                        || saved.DomainRevision > snapshot.DomainRevision
                        || saved.BoardRevision > snapshot.BoardRevision))
                {
                    rejectionReason =
                        "A saved undo checkpoint is stale or foreign";
                    return false;
                }
                restoredHistory.Add(new BoardMemento
                {
                    Board = historyBoard,
                    SlotDeadlines = historyDeadlines,
                    Stamp = historyStamp,
                });
            }

            var savedAttempt = new BsAttemptId(snapshot.AttemptId);
            if (savedAttempt != new BsAttemptId(attemptId))
            {
                rejectionReason = "The saved round attempt id is stale";
                return false;
            }
            BsRoundTransition completedTransition = null;
            BsRoundState savedState = (BsRoundState)snapshot.RoundState;
            BsRoundCompletion savedCompletion =
                (BsRoundCompletion)snapshot.RoundCompletion;
            var savedToken = new BsRoundToken(
                snapshot.RoundId, snapshot.GameplayEpoch);
            var completionOperation = new BsOperationId(
                snapshot.CompletionOperationId);
            if (savedState == BsRoundState.Completed)
            {
                BsSettlementReceipt? savedReceipt = null;
                var request = new BsSettlementRequest(
                    savedAttempt,
                    completionOperation,
                    snapshot.DomainRevision,
                    snapshot.BoardRevision,
                    savedCompletion,
                    (BsRoundTransitionCause)snapshot.CompletionCause);
                if (snapshot.HasSettlementReceipt)
                    savedReceipt = BsSettlementReceipt.Durable(
                        request, snapshot.SettlementReceiptId);
                completedTransition = new BsRoundTransition(
                    savedAttempt,
                    completionOperation,
                    snapshot.DomainRevision,
                    snapshot.BoardRevision,
                    (BsRoundState)snapshot.CompletionFrom,
                    savedState,
                    (BsRoundTransitionCause)snapshot.CompletionCause,
                    savedCompletion,
                    savedToken,
                    savedReceipt);
            }
            var coreSnapshot = new BsRoundSnapshot(
                savedState,
                savedCompletion,
                (BsRoundAttemptKind)snapshot.RoundAttemptKind,
                savedAttempt,
                savedToken,
                snapshot.DomainRevision,
                new BsOperationId(snapshot.LastOperationId),
                snapshot.BoardRevision,
                restoredBoard,
                completedTransition);

            BsRoundCommandStamp importedStamp = new BsRoundCommandStamp(
                coreSnapshot.AttemptId,
                coreSnapshot.Token,
                coreSnapshot.Revision,
                coreSnapshot.BoardRevision);
            for (int i = 0; i < restoredHistory.Count; i++)
            {
                if (!restoredHistory[i].Stamp.IsValid)
                    restoredHistory[i].Stamp = importedStamp;
            }

            restored = new RestoredActiveRound
            {
                RoundSnapshot = coreSnapshot,
                ActiveGameplayTime = snapshot.ActiveGameplayTime,
                TimeBonusByOrderIndex =
                    (double[])snapshot.TimeBonusByOrderIndex.Clone(),
                SlotDeadlines = liveDeadlines,
                UndoRemaining = snapshot.UndoRemaining,
                ExtraGlassRemaining = snapshot.ExtraGlassRemaining,
                TimeBoostRemaining = snapshot.TimeBoostRemaining,
                ShuffleRemaining = snapshot.ShuffleRemaining,
                UserPaused = snapshot.UserPaused,
                TimeOfferSettlement = restoredTimeOfferSettlement,
                UndoHistory = restoredHistory,
            };
            return true;
        }

        private void CaptureDeadlinesForBoard(
            BsBoard targetBoard,
            out double[] deadlines,
            out bool[] hasDeadline)
        {
            int count = targetBoard?.Slots?.Length ?? 0;
            deadlines = new double[count];
            hasDeadline = new bool[count];
            var byOrderIndex = new Dictionary<int, double>();
            foreach (KeyValuePair<OrderDef, double> pair in orderDeadlines)
            {
                int orderIndex = pair.Key?.RuntimeOrderIndex ?? -1;
                if (orderIndex >= 0) byOrderIndex[orderIndex] = pair.Value;
            }
            for (int slot = 0; slot < count; slot++)
            {
                OrderDef order = targetBoard.Slots[slot];
                if (!targetBoard.TimedOrdersEnabled
                    || order == null || order.TimeLimit <= 0f)
                    continue;
                int orderIndex = order.RuntimeOrderIndex;
                deadlines[slot] = byOrderIndex.TryGetValue(
                    orderIndex, out double existing)
                    ? existing
                    : activeGameplayTime + order.TimeLimit
                      + TimeBonusFor(order);
                hasDeadline[slot] = true;
            }
        }

        private static PersistedBoardMemento CapturePersistedMemento(
            BoardMemento source)
        {
            int count = source?.Board?.Slots?.Length ?? 0;
            var saved = new PersistedBoardMemento
            {
                Board = source?.Board?.CaptureSnapshot(),
                SlotDeadlines = new double[count],
                HasSlotDeadline = new bool[count],
                AttemptId = source == null
                    ? string.Empty : source.Stamp.AttemptId.Value,
                RoundId = source == null ? 0 : source.Stamp.Token.RoundId,
                GameplayEpoch = source == null
                    ? 0 : source.Stamp.Token.GameplayEpoch,
                DomainRevision = source == null ? 0L : source.Stamp.Revision,
                BoardRevision = source == null ? 0 : source.Stamp.BoardRevision,
            };
            for (int slot = 0; slot < count; slot++)
            {
                if (source.SlotDeadlines == null
                    || slot >= source.SlotDeadlines.Length
                    || !source.SlotDeadlines[slot].HasValue)
                    continue;
                saved.SlotDeadlines[slot] = source.SlotDeadlines[slot].Value;
                saved.HasSlotDeadline[slot] = true;
            }
            return saved;
        }

        private static bool TryDecodeDeadlineVector(
            BsBoard targetBoard, double[] deadlines, bool[] hasDeadline,
            out double?[] restored, out string rejectionReason)
        {
            restored = null;
            rejectionReason = null;
            int count = targetBoard?.Slots?.Length ?? -1;
            if (count < 0 || deadlines == null || hasDeadline == null
                || deadlines.Length != count || hasDeadline.Length != count)
            {
                rejectionReason = "The saved order deadlines are invalid";
                return false;
            }

            restored = new double?[count];
            for (int slot = 0; slot < count; slot++)
            {
                OrderDef order = targetBoard.Slots[slot];
                bool requiresDeadline = targetBoard.TimedOrdersEnabled
                                     && order != null && order.TimeLimit > 0f;
                if (hasDeadline[slot] != requiresDeadline)
                {
                    rejectionReason = "A timed order deadline is missing or misplaced";
                    return false;
                }
                if (!hasDeadline[slot]) continue;
                double deadline = deadlines[slot];
                if (!IsFiniteNonNegative(deadline))
                {
                    rejectionReason = "A timed order deadline is invalid";
                    return false;
                }
                restored[slot] = deadline;
            }
            return true;
        }

        private void RestoreLiveDeadlines(double?[] deadlines)
        {
            orderDeadlines.Clear();
            if (boardProjection?.Slots == null || deadlines == null) return;
            int count = Math.Min(boardProjection.Slots.Length, deadlines.Length);
            for (int slot = 0; slot < count; slot++)
            {
                OrderDef order = boardProjection.Slots[slot];
                if (order != null && deadlines[slot].HasValue)
                    orderDeadlines[order] = deadlines[slot].Value;
            }
        }

        private bool ValidateSnapshotColors(BsBoard snapshotBoard)
        {
            if (snapshotBoard?.Glasses == null || palette == null) return false;
            for (int glass = 0; glass < snapshotBoard.Glasses.Count; glass++)
            {
                List<Layer> layers = snapshotBoard.Glasses[glass].Layers;
                for (int layer = 0; layer < layers.Count; layer++)
                {
                    int color = layers[layer].Color;
                    if (color < 0 || color >= palette.Count) return false;
                }
            }
            return true;
        }

        private static bool ValidBoosterStock(int saved, int authored) =>
            saved >= 0 && saved <= Mathf.Max(0, authored);

        private static bool IsFiniteNonNegative(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0d;

        private static string ComputeLevelSignature(BsLevel level)
        {
            if (level == null) return string.Empty;
            var builder = new StringBuilder(512);
            builder.Append("v2|").Append(level.Index).Append('|')
                .Append(level.OrderSlots).Append('|')
                .Append(level.AllowTimedOrders ? 1 : 0).Append('|')
                .Append(level.AllowHiddenColors ? 1 : 0).Append('|')
                .Append(level.UndoCount).Append('|')
                .Append(level.ExtraGlassCount).Append('|')
                .Append(level.TimeBoostCount).Append('|')
                .Append(level.ShuffleCount).Append('|');

            int glassCount = level.Glasses?.Count ?? -1;
            builder.Append(glassCount).Append('|');
            if (level.Glasses != null)
            {
                for (int glassIndex = 0; glassIndex < level.Glasses.Count; glassIndex++)
                {
                    GlassDef glass = level.Glasses[glassIndex];
                    if (glass == null)
                    {
                        builder.Append("null;");
                        continue;
                    }
                    builder.Append((int)glass.Type).Append(',')
                        .Append(glass.UnlockAfter).Append(',')
                        .Append(glass.Layers?.Count ?? -1).Append(':');
                    if (glass.Layers != null)
                    {
                        for (int layer = 0; layer < glass.Layers.Count; layer++)
                        {
                            Layer value = glass.Layers[layer];
                            builder.Append(value.Color).Append(',')
                                .Append(value.Hidden ? 1 : 0).Append(',')
                                .Append(value.LockUntil).Append('/');
                        }
                    }
                    builder.Append(';');
                }
            }

            int orderCount = level.Orders?.Count ?? -1;
            builder.Append('|').Append(orderCount).Append('|');
            if (level.Orders != null)
            {
                for (int orderIndex = 0; orderIndex < level.Orders.Count; orderIndex++)
                {
                    OrderDef order = level.Orders[orderIndex];
                    if (order == null)
                    {
                        builder.Append("null;");
                        continue;
                    }
                    builder.Append((int)order.Kind).Append(',')
                        .Append((int)order.Glass).Append(',')
                        .Append(order.TimeLimit.ToString("R",
                            CultureInfo.InvariantCulture)).Append(',')
                        .Append(order.Contents?.Count ?? -1).Append(':');
                    if (order.Contents != null)
                    {
                        for (int content = 0; content < order.Contents.Count; content++)
                            builder.Append(order.Contents[content]).Append('/');
                    }
                    builder.Append(';');
                }
            }
            return builder.ToString();
        }

        private void ResetBoosters(BsLevel level)
        {
            // Reset the offer each round and release its timer barrier.
            ResetTimeOffer();
            undoHistory.Clear();
            UndoRemaining = Mathf.Max(0, level != null ? level.UndoCount : 0);
            ExtraGlassRemaining = Mathf.Max(0,
                level != null ? level.ExtraGlassCount : 0);
            TimeBoostRemaining = Mathf.Max(0, level != null ? level.TimeBoostCount : 0);
            ShuffleRemaining = Mathf.Max(0, level != null ? level.ShuffleCount : 0);
        }

        private BoardMemento CaptureCurrentMemento()
        {
            if (boardProjection == null) return null;

            var deadlines = new double?[boardProjection.Slots.Length];
            for (int slot = 0; slot < boardProjection.Slots.Length; slot++)
            {
                OrderDef order = boardProjection.Slots[slot];
                if (order != null
                    && orderDeadlines.TryGetValue(order, out double deadline))
                    deadlines[slot] = deadline;
            }

            return new BoardMemento
            {
                Board = boardProjection.Clone(),
                SlotDeadlines = deadlines,
                Stamp = coordinator.CurrentStamp,
            };
        }

        private bool MementoHasExpiredTimedOrder(BoardMemento memento)
        {
            if (memento?.Board?.Slots == null || memento.SlotDeadlines == null)
                return false;

            int count = Math.Min(
                memento.Board.Slots.Length, memento.SlotDeadlines.Length);
            for (int slot = 0; slot < count; slot++)
            {
                OrderDef order = memento.Board.Slots[slot];
                double? deadline = memento.SlotDeadlines[slot];
                if (order != null && order.TimeLimit > 0f && deadline.HasValue
                    && deadline.Value <= activeGameplayTime)
                    return true;
            }
            return false;
        }

        private void RestoreDeadlinesForBoard(BsBoard restoredBoard, double?[] deadlines)
        {
            orderDeadlines.Clear();
            if (restoredBoard?.Slots == null || deadlines == null) return;
            int count = Math.Min(restoredBoard.Slots.Length, deadlines.Length);
            for (int slot = 0; slot < count; slot++)
            {
                OrderDef order = restoredBoard.Slots[slot];
                if (order != null && deadlines[slot].HasValue)
                    orderDeadlines[order] = deadlines[slot].Value;
            }
        }

        private BoardMemento CommitUndoSnapshot(BoardMemento snapshot)
        {
            if (snapshot == null) return null;
            undoHistory.Add(snapshot);
            // Oldest first: dropping index 0 keeps the most recent moves reachable.
            BoardMemento evicted = null;
            while (undoHistory.Count > undoHistoryDepth)
            {
                evicted = undoHistory[0];
                undoHistory.RemoveAt(0);
            }
            return evicted;
        }

        private void RollBackCommittedUndo(BoardMemento appended,
                                           BoardMemento evicted)
        {
            if (appended != null && undoHistory.Count > 0
                && ReferenceEquals(undoHistory[undoHistory.Count - 1], appended))
                undoHistory.RemoveAt(undoHistory.Count - 1);
            if (evicted != null) undoHistory.Insert(0, evicted);
        }

        public OrderDef OrderAtSlot(int slotIndex)
        {
            return LiveOrderAtSlot(slotIndex)?.Clone();
        }

        public bool TryGetOrderTimeRemaining(int slotIndex, out float remaining,
                                             out float duration)
        {
            OrderDef order = LiveOrderAtSlot(slotIndex);
            duration = order != null
                ? order.TimeLimit + (float)TimeBonusFor(order)
                : 0f;
            if (order != null
                && orderDeadlines.TryGetValue(order, out double deadline))
            {
                remaining = Mathf.Max(0f, (float)(deadline - activeGameplayTime));
                return true;
            }
            remaining = 0f;
            return false;
        }

        public bool TryValidateLevel(BsLevel level, out string error)
        {
            ResolveDependencies();
            if (level == null)
            {
                error = "Level asset is missing.";
                return false;
            }
            if (palette == null || palette.Count == 0)
            {
                error = "BsPalette is missing or empty.";
                return false;
            }
            if (level.ColumnsPerRow <= 0 || level.OrderSlots <= 0)
            {
                error = "The column or order slot count is invalid.";
                return false;
            }
            if (level.Glasses == null || level.Glasses.Count == 0)
            {
                error = "The level has no glasses.";
                return false;
            }

            for (int i = 0; i < level.Glasses.Count; i++)
            {
                GlassDef glass = level.Glasses[i];
                if (glass == null || glass.Layers == null)
                {
                    error = $"Glass {i} is missing or has no layer list.";
                    return false;
                }
                if (!IsKnownGlassType(glass.Type))
                {
                    error = $"Glass {i}: type value {(int)glass.Type} is invalid.";
                    return false;
                }
                if (glass.Layers.Count > glass.Capacity)
                {
                    error = $"Glass {i} contains more layers than its capacity.";
                    return false;
                }
                for (int layer = 0; layer < glass.Layers.Count; layer++)
                {
                    int color = glass.Layers[layer].Color;
                    if (color >= 0 && color < palette.Count) continue;
                    error = $"Glass {i}, layer {layer}: color index {color} is invalid.";
                    return false;
                }
            }

            if (level.Orders == null || level.Orders.Count == 0)
            {
                error = "The level has no orders.";
                return false;
            }

            for (int i = 0; i < level.Orders.Count; i++)
            {
                OrderDef order = level.Orders[i];
                if (order == null || order.Contents == null)
                {
                    error = $"Order {i} is missing or has no contents list.";
                    return false;
                }
                if (!IsKnownGlassType(order.Glass))
                {
                    error = $"Order {i}: glass type value {(int)order.Glass} is invalid.";
                    return false;
                }
                if (order.Contents.Count != order.Capacity)
                {
                    error = $"Order {i} contains {order.Contents.Count} units "
                          + $"instead of {order.Capacity}.";
                    return false;
                }
                for (int content = 0; content < order.Contents.Count; content++)
                {
                    int color = order.Contents[content];
                    if (color >= 0 && color < palette.Count) continue;
                    error = $"Order {i}, content {content}: color index {color} is invalid.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        private void ResolveDependencies()
        {
            if (palette == null)
                palette = Resources.Load<BsPalette>(DefaultPaletteResource);
            if (coordinator == null)
                coordinator = new BsRoundCoordinator(
                    BartenderProgressService.SettlementOperationFloor);
        }

        private string CurrentAttemptValue => coordinator?.AttemptId.Value;

        private static BartenderLevelState ProjectState(BsRoundCoordinator source)
        {
            if (source == null) return BartenderLevelState.Unloaded;
            switch (source.State)
            {
                case BsRoundState.Playing:
                    return BartenderLevelState.Playing;
                case BsRoundState.Paused:
                    return BartenderLevelState.Paused;
                case BsRoundState.Completed:
                    return source.Completion == BsRoundCompletion.Won
                        ? BartenderLevelState.Won
                        : BartenderLevelState.Failed;
                default:
                    return BartenderLevelState.Unloaded;
            }
        }

        private static BartenderLevelState ProjectState(
            BsRoundState state,
            BsRoundCompletion completion)
        {
            if (state == BsRoundState.Playing) return BartenderLevelState.Playing;
            if (state == BsRoundState.Paused) return BartenderLevelState.Paused;
            if (state == BsRoundState.Completed)
                return completion == BsRoundCompletion.Won
                    ? BartenderLevelState.Won
                    : BartenderLevelState.Failed;
            return BartenderLevelState.Unloaded;
        }

        private void SyncBoardProjection()
        {
            var deadlinesByOrder = new Dictionary<int, double>();
            foreach (KeyValuePair<OrderDef, double> pair in orderDeadlines)
            {
                int orderIndex = pair.Key?.RuntimeOrderIndex ?? -1;
                if (orderIndex >= 0) deadlinesByOrder[orderIndex] = pair.Value;
            }

            boardProjection = coordinator?.CaptureBoard();
            orderDeadlines.Clear();
            if (boardProjection?.Slots == null) return;
            for (int slot = 0; slot < boardProjection.Slots.Length; slot++)
            {
                OrderDef order = boardProjection.Slots[slot];
                if (order != null
                    && deadlinesByOrder.TryGetValue(
                        order.RuntimeOrderIndex, out double deadline))
                    orderDeadlines[order] = deadline;
            }
        }

        private void PublishCoordinatorCommit(
            BsRoundCommit commit,
            BartenderPourReceipt pourReceipt = null,
            BartenderDeliveryReceipt deliveryReceipt = null,
            bool publishBoard = true)
        {
            if (commit == null) return;
            if (commit.BoardCommit != null) SyncBoardProjection();
            InvokeSafely(RoundCommitted, commit);

            if (pourReceipt != null) InvokeSafely(Poured, pourReceipt);
            if (deliveryReceipt != null) InvokeSafely(Delivered, deliveryReceipt);
            if (publishBoard && commit.BoardCommit != null)
                InvokeSafely(BoardCommitted,
                    new BartenderBoardChange(commit.BoardCommit, deliveryReceipt));

            if (commit.Transition != null) NotifyProjectedState();
        }

        private void NotifyProjectedState()
        {
            BartenderLevelState current = State;
            if (publishedState == current) return;
            publishedState = current;
            if (current != BartenderLevelState.Playing
                && current != BartenderLevelState.Paused)
                CancelDeadEndProbe();
            if (commandInProgress)
            {
                pendingStateNotification = current;
                hasPendingStateNotification = true;
            }
            else InvokeSafely(StateChanged, current);
        }

        private void SetCampaignCompleteProjection(bool completed)
        {
            campaignCompleteProjection = completed;
            NotifyProjectedState();
        }

        private static bool IsKnownGlassType(GlassType type)
        {
            int index = (int)type;
            return index >= 0 && index < BsRules.CapacityTable.Length;
        }

        private bool MutationBlocked => commandInProgress || notificationInProgress
                                     || coordinator?.HasStagedOperation == true
                                     || timeOfferMachine.IsActive
                                     || presentationLockOwner != null
                                     || presentationBarrierOwners.Count > 0;

        private bool CanAcceptCommand(out string reason)
        {
            if (MutationBlocked)
            {
                reason = "Another level command is being processed";
                return false;
            }
            if (State != BartenderLevelState.Playing || boardProjection == null)
            {
                reason = "The level is not ready to play";
                return false;
            }
            OrderExpiryResult expiry = ExpireOrderIfNeeded(out string expiryReason);
            if (expiry != OrderExpiryResult.NotExpired)
            {
                reason = string.IsNullOrEmpty(expiryReason)
                    ? "The order timed out"
                    : expiryReason;
                return false;
            }
            reason = null;
            return true;
        }

        private void ResetOrderDeadlines()
            => ResetOrderDeadlines(boardProjection);

        private void ResetOrderDeadlines(BsBoard source)
        {
            orderDeadlines.Clear();
            if (source?.Slots == null || !source.TimedOrdersEnabled) return;
            for (int slot = 0; slot < source.Slots.Length; slot++)
            {
                OrderDef order = source.Slots[slot];
                if (order == null || order.TimeLimit <= 0f) continue;
                orderDeadlines[order] = activeGameplayTime
                    + order.TimeLimit + TimeBonusFor(order);
            }
        }

        private void ResetTimeBonuses(BsLevel level)
        {
            int count = level != null && level.Orders != null ? level.Orders.Count : 0;
            timeBonusByOrderIndex = count > 0 ? new double[count] : Array.Empty<double>();
        }

        private double TimeBonusFor(OrderDef order)
        {
            if (order == null) return 0d;
            int index = order.RuntimeOrderIndex;
            return index >= 0 && index < timeBonusByOrderIndex.Length
                ? timeBonusByOrderIndex[index]
                : 0d;
        }

        private void RefreshOrderDeadlinesAfterDelivery()
        {
            if (boardProjection == null || boardProjection.Slots == null)
            {
                orderDeadlines.Clear();
                return;
            }

            timerRemovalScratch.Clear();
            foreach (KeyValuePair<OrderDef, double> pair in orderDeadlines)
            {
                if (!ContainsOrderReference(boardProjection.Slots, pair.Key))
                    timerRemovalScratch.Add(pair.Key);
            }
            for (int i = 0; i < timerRemovalScratch.Count; i++)
                orderDeadlines.Remove(timerRemovalScratch[i]);

            if (!boardProjection.TimedOrdersEnabled) return;
            for (int i = 0; i < boardProjection.Slots.Length; i++)
            {
                OrderDef order = boardProjection.Slots[i];
                if (order == null || order.TimeLimit <= 0f
                    || orderDeadlines.ContainsKey(order))
                    continue;
                orderDeadlines.Add(order,
                    activeGameplayTime + order.TimeLimit + TimeBonusFor(order));
            }
        }

        private OrderExpiryResult ExpireOrderIfNeeded(out string rejectionReason)
        {
            rejectionReason = null;
            if (boardProjection == null || !boardProjection.TimedOrdersEnabled)
                return OrderExpiryResult.NotExpired;

            // An active or settling offer blocks another expiry decision, including while its bonus has not
            // reached the deadline yet.
            if (timeOfferMachine.IsActive) return OrderExpiryResult.NotExpired;
            for (int slot = 0; slot < boardProjection.Slots.Length; slot++)
            {
                OrderDef order = boardProjection.Slots[slot];
                if (order != null
                    && orderDeadlines.TryGetValue(order, out double deadline)
                    && deadline <= activeGameplayTime)
                {
                    // Offer extra time before failing. Accepting continues this round without losing a
                    // life, settling or reloading.
                    if (TryArmTimeOffer(slot)) return OrderExpiryResult.Settled;
                    return TryCompleteRound(
                            BsRoundCompletion.Failed,
                            BsRoundTransitionCause.TimedOrderExpired,
                            out rejectionReason)
                        ? OrderExpiryResult.Settled
                        : OrderExpiryResult.SettlementRejected;
                }
            }
            return OrderExpiryResult.NotExpired;
        }

        /// <summary>Tries to open an offer. If it cannot, the caller follows the normal failure path.</summary>
        private bool TryArmTimeOffer(int slot)
        {
            if (timeOfferMachine.IsActive || IsStandaloneRound) return false;
            if (TimeOfferRequested == null) return false;      // No presenter is listening.
            if (CurrentLevel == null || !CurrentLevel.AllowTimedOrders) return false;
            if (TimeBoostRemaining <= 0) return false;
            if (timeOfferSeconds <= 0f || timeOfferCoinCost <= 0) return false;

            // Show the offer even with too few coins so the player can reach the shop. Later timeouts may
            // offer again.

            // Set state before firing events. The controller keeps the snapshot and barrier so a returning
            // presenter can restore the same offer.
            if (!timeOfferMachine.TryOpen(out BsTimeOfferId offerId)) return false;
            timeOfferContext = new BsTimeOfferSnapshot(
                offerId, slot, TimeOfferCoinCost, timeOfferSeconds);
            if (!AcquirePresentationBarrier(timeOfferBarrierOwner))
            {
                timeOfferMachine.Reset();
                timeOfferContext = default;
                return false;
            }

            InvokeSafely(TimeOfferRequested, timeOfferContext);
            return true;
        }

        /// <summary>
        /// Spends coins, adds time and resumes play. A rejected purchase leaves the offer open and the clock
        /// paused.
        /// </summary>
        public BsTimeOfferAcceptResult TryAcceptTimeOffer(
            BsTimeOfferId expectedOfferId)
        {
            if (!CanBeginTimeOfferDecision(
                    expectedOfferId, out string decisionRejection))
                return BsTimeOfferAcceptResult.Reject(
                    expectedOfferId, decisionRejection);
            if (!timeOfferMachine.Dispatch(
                    BsTimeOfferTrigger.BeginAccept, expectedOfferId))
                return BsTimeOfferAcceptResult.Reject(
                    expectedOfferId, "The time offer is stale or no longer open");

            BsTimeOfferSnapshot offer = timeOfferContext;
            // Keep the offer lease during purchase. Only this Accepting state with its single matching
            // lease can bypass the normal mutation block.
            try
            {
                if (!TryPurchaseTimeBoostInternal(
                        offer.AddedSeconds, offer.CoinCost, true,
                        out string rejectionReason))
                {
                    RequireTimeOfferTransition(
                        BsTimeOfferTrigger.AcceptRejected, expectedOfferId);
                    return BsTimeOfferAcceptResult.RejectPurchase(
                        expectedOfferId, rejectionReason);
                }
            }
            catch (Exception exception)
            {
                // An unexpected pre-commit error must restore Open so the player can decide again.
                // Supported save paths do not throw after commit.
                if (timeOfferMachine.State == BsTimeOfferState.Accepting
                    && timeOfferMachine.CurrentId == expectedOfferId)
                    RequireTimeOfferTransition(
                        BsTimeOfferTrigger.AcceptRejected, expectedOfferId);
                Debug.LogException(exception, this);
                return BsTimeOfferAcceptResult.RejectPurchase(
                    expectedOfferId, "The time offer purchase could not be completed");
            }

            RequireTimeOfferTransition(
                BsTimeOfferTrigger.AcceptCommitted, expectedOfferId);
            ReleasePresentationBarrier(timeOfferBarrierOwner);
            timeOfferContext = default;
            return BsTimeOfferAcceptResult.Accept(expectedOfferId);
        }

        /// <summary>
        /// Locks the decline while writing its outbox. A successful write consumes the offer; a failed write
        /// reopens the same offer.
        /// </summary>
        public BsTimeOfferDeclineResult DeclineTimeOffer(
            BsTimeOfferId expectedOfferId) =>
            DeclineTimeOffer(
                expectedOfferId, BsTimeOfferDeclineDisposition.PresentFailure);

        public BsTimeOfferDeclineResult DeclineTimeOffer(
            BsTimeOfferId expectedOfferId,
            BsTimeOfferDeclineDisposition disposition)
        {
            int dispositionValue = (int)disposition;
            if (dispositionValue <
                    (int)BsTimeOfferDeclineDisposition.PresentFailure
                || dispositionValue >
                    (int)BsTimeOfferDeclineDisposition.ReturnToMainMenu)
                return BsTimeOfferDeclineResult.Reject(
                    expectedOfferId, "The time-offer disposition is invalid");
            if (!CanBeginTimeOfferDecision(
                    expectedOfferId, out string decisionRejection))
                return BsTimeOfferDeclineResult.Reject(
                    expectedOfferId, decisionRejection);
            if (!timeOfferMachine.Dispatch(
                    BsTimeOfferTrigger.BeginDecline, expectedOfferId))
                return BsTimeOfferDeclineResult.Reject(
                    expectedOfferId, "The time offer is stale or no longer open");

            // Capture the offer and destination before saving. Fail fires StateChanged immediately, and
            // Session needs that receipt in the callback.
            timeOfferSettlementContext = new BsTimeOfferSettlementSnapshot(
                expectedOfferId,
                disposition,
                BsTimeOfferSettlementStatus.SettlementPending);
            BsRoundTransitionCause cause = disposition
                == BsTimeOfferDeclineDisposition.ReturnToMainMenu
                    ? BsRoundTransitionCause.TimeOfferDeclinedReturnToMenu
                    : BsRoundTransitionCause.TimeOfferDeclinedPresentFailure;
            string rejectionReason;
            try
            {
                if (!TryCompleteRound(
                        BsRoundCompletion.Failed, cause, out rejectionReason))
                {
                    RejectTimeOfferDeclineOutbox(expectedOfferId);
                    return BsTimeOfferDeclineResult.Reject(
                        expectedOfferId, rejectionReason);
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                RejectTimeOfferDeclineOutbox(expectedOfferId);
                return BsTimeOfferDeclineResult.Reject(
                    expectedOfferId,
                    "The time-offer decision could not be saved");
            }

            RequireTimeOfferTransition(
                BsTimeOfferTrigger.DeclineOutboxCommitted, expectedOfferId);
            ReleasePresentationBarrier(timeOfferBarrierOwner);
            PublishTimeOfferSettlement();
            RetryRetainedSettlement();
            if (!IsTerminalSettlementCommitted)
            {
                RequireTimeOfferTransition(
                    BsTimeOfferTrigger.SettlementDeferred, expectedOfferId);
                PublishTimeOfferSettlement();
                return BsTimeOfferDeclineResult.Defer(
                    expectedOfferId, "The round result is queued for retry");
            }

            RequireTimeOfferTransition(
                BsTimeOfferTrigger.SettlementCommitted, expectedOfferId);
            timeOfferContext = default;
            MarkTimeOfferSettlementCommitted();
            return BsTimeOfferDeclineResult.Commit(expectedOfferId);
        }

        private void RejectTimeOfferDeclineOutbox(BsTimeOfferId expectedOfferId)
        {
            timeOfferSettlementContext = null;
            RequireTimeOfferTransition(
                BsTimeOfferTrigger.DeclineRejected, expectedOfferId);
        }

        private void MarkTimeOfferSettlementCommitted()
        {
            if (!timeOfferSettlementContext.HasValue) return;
            timeOfferSettlementContext = timeOfferSettlementContext.Value.WithStatus(
                BsTimeOfferSettlementStatus.TerminalCommitted);
            PublishTimeOfferSettlement();
        }

        private void PublishTimeOfferSettlement()
        {
            if (timeOfferSettlementContext.HasValue)
                InvokeSafely(
                    TimeOfferSettlementChanged, timeOfferSettlementContext.Value);
        }

        private bool CanBeginTimeOfferDecision(
            BsTimeOfferId expectedOfferId, out string rejectionReason)
        {
            if (commandInProgress || notificationInProgress
                || coordinator?.HasStagedOperation == true)
            {
                rejectionReason = "Another level operation is in progress";
                return false;
            }
            if (State != BartenderLevelState.Playing || boardProjection == null)
            {
                rejectionReason = "The level is not ready to play";
                return false;
            }
            if (!expectedOfferId.IsValid
                || timeOfferMachine.State != BsTimeOfferState.Open
                || timeOfferMachine.CurrentId != expectedOfferId
                || timeOfferContext.Id != expectedOfferId
                || !IsPresentationBarrierOwnedBy(timeOfferBarrierOwner))
            {
                rejectionReason = "The time offer is stale or no longer open";
                return false;
            }

            rejectionReason = null;
            return true;
        }

        private void RequireTimeOfferTransition(
            BsTimeOfferTrigger trigger, BsTimeOfferId expectedOfferId)
        {
            if (timeOfferMachine.Dispatch(trigger, expectedOfferId)) return;
            throw new InvalidOperationException(
                $"Time-offer invariant failed: {trigger} for {expectedOfferId}.");
        }

        /// <summary>Clears the offer and releases its clock barrier between rounds.</summary>
        private void ResetTimeOffer()
        {
            ReleasePresentationBarrier(timeOfferBarrierOwner);
            timeOfferMachine.Reset();
            timeOfferContext = default;
            timeOfferSettlementContext = null;
        }

        /// <summary>
        /// Retries settlement for the matching offer. Stale taps are harmless, and a committed receipt
        /// succeeds without spending another life.
        /// </summary>
        public BsTimeOfferSettlementRetryResult RetryTimeOfferSettlement(
            BsTimeOfferId expectedOfferId)
        {
            if (!timeOfferSettlementContext.HasValue
                || !expectedOfferId.IsValid
                || timeOfferSettlementContext.Value.OfferId != expectedOfferId)
                return BsTimeOfferSettlementRetryResult.Reject(
                    expectedOfferId, "The time-offer settlement is stale or missing");

            if (timeOfferSettlementContext.Value.TerminalCommitted)
                return BsTimeOfferSettlementRetryResult.AlreadyCommitted(
                    expectedOfferId);

            if (!HasPendingTerminalSettlement
                || commandInProgress || notificationInProgress
                || applicationPaused || applicationFocusLost)
                return BsTimeOfferSettlementRetryResult.Reject(
                    expectedOfferId,
                    "The time-offer settlement cannot be retried right now");

            RetryRetainedSettlement();
            return timeOfferSettlementContext.HasValue
                   && timeOfferSettlementContext.Value.OfferId == expectedOfferId
                   && timeOfferSettlementContext.Value.TerminalCommitted
                ? BsTimeOfferSettlementRetryResult.Commit(expectedOfferId)
                : BsTimeOfferSettlementRetryResult.Pending(expectedOfferId);
        }

        private void ScheduleDeadEndProbe()
        {
            CancelDeadEndProbe();
            if (!detectDeadEnd
                || (State != BartenderLevelState.Playing
                    && State != BartenderLevelState.Paused)
                || boardProjection == null)
                return;

            // I defer the board copy and solver work to a quiet Update so commands only write three fields.
            deadEndProbeBoard = boardProjection;
            deadEndProbeRevision = BoardRevision;
            deadEndProbePending = true;
            deadEndProbeEarliestTime = Time.unscaledTime
                                     + Mathf.Max(0f, deadEndIdleDelaySeconds);
        }

        private void AdvanceDeadEndProbe()
        {
            BsSolver.IncrementalSearch probe = deadEndProbe;
            if (probe == null && !deadEndProbePending && !deadEndAwaitingEscape) return;

            BsBoard expectedBoard = deadEndProbeBoard;
            int expectedRevision = deadEndProbeRevision;
            if (!detectDeadEnd || boardProjection == null
                || !ReferenceEquals(boardProjection, expectedBoard)
                || BoardRevision != expectedRevision)
            {
                CancelDeadEndProbe();
                return;
            }

            // Keep the snapshot while paused but do no search work. Terminal states discard it; view locks
            // protect animation frames.
            if (State == BartenderLevelState.Paused
                || applicationPaused || applicationFocusLost)
                return;
            if (State != BartenderLevelState.Playing)
            {
                CancelDeadEndProbe();
                return;
            }
            if (MutationBlocked) return;
            if (Time.unscaledTime < deadEndProbeEarliestTime) return;

            if (deadEndAwaitingEscape)
            {
                ReevaluateKnownDeadEnd();
                return;
            }

            SolveResult result = null;
            if (probe == null)
            {
                try
                {
                    string stateKey = expectedBoard.StateKey();
                    deadEndProbeStateKey = stateKey;
                    if (TryGetCachedDeadEndOutcome(stateKey,
                            out SolveOutcome cachedOutcome))
                    {
                        result = new SolveResult { Outcome = cachedOutcome };
                    }
                    else
                    {
                        probe = BsSolver.BeginIncremental(
                            expectedBoard, Mathf.Max(1, deadEndNodeBudget),
                            BsSolver.DefaultMaxDepth, Mathf.Max(1, deadEndMaxMs),
                            stateKey);
                        deadEndProbe = probe;
                        deadEndProbePending = false;
                    }
                }
                catch (Exception exception)
                {
                    // A broken dead-end probe must never reject a valid move.
                    CancelDeadEndProbe();
                    Debug.LogException(exception, this);
                    return;
                }
            }

            if (result == null)
            {
                try
                {
                    if (!probe.Step(Mathf.Clamp(deadEndSliceMs, 1, 4))) return;
                    result = probe.Result;
                }
                catch (Exception exception)
                {
                    CancelDeadEndProbe();
                    Debug.LogException(exception, this);
                    return;
                }
            }

            string completedStateKey = deadEndProbeStateKey;
            CancelDeadEndProbe();
            RememberDeadEndOutcome(completedStateKey, result);
            if (result == null || result.Outcome != SolveOutcome.Unsolvable)
                return;

            // Use a result only for the exact live snapshot it searched. This check stays atomic on the
            // main thread.
            if (!ReferenceEquals(boardProjection, expectedBoard)
                || BoardRevision != expectedRevision
                || State != BartenderLevelState.Playing)
                return;

            // Keep the board proof while paid escape options change. Expiring undo time or spent coins do
            // not require another solve.
            deadEndProbeBoard = expectedBoard;
            deadEndProbeRevision = expectedRevision;
            deadEndAwaitingEscape = true;
            ReevaluateKnownDeadEnd();
        }

        private void ReevaluateKnownDeadEnd()
        {
            // The solver checks free pour and delivery moves. Fail only when no rescue can currently be
            // bought and used.
            if (HasUsableDeadEndEscape()) return;
            // Availability checks can open an offer or end the round. Respect that result before declaring
            // a dead end.
            if (MutationBlocked || State != BartenderLevelState.Playing) return;

            if (!TryCompleteRound(
                    BsRoundCompletion.Failed,
                    BsRoundTransitionCause.DeadEndDetected,
                    out string rejectionReason))
            {
                // If outbox saving fails, keep the round playable and retry the retained proof at a limited
                // rate.
                deadEndProbeEarliestTime = Time.unscaledTime + 1f;
                if (!string.IsNullOrEmpty(rejectionReason))
                    Debug.LogWarning(rejectionReason, this);
            }
        }

        internal bool HasUsableDeadEndEscape()
        {
            if (CanPurchaseUndo(out _)) return true;
            if (CanPurchaseExtraGlass(
                    BartenderProgressTuning.PurchasedExtraGlassType, out _)) return true;
            return CanPurchaseShuffle(out _);
        }

        private void CancelDeadEndProbe()
        {
            BsSolver.IncrementalSearch probe = deadEndProbe;
            deadEndProbe = null;
            deadEndProbeBoard = null;
            deadEndProbeRevision = -1;
            deadEndProbePending = false;
            deadEndAwaitingEscape = false;
            deadEndProbeStateKey = null;
            deadEndProbeEarliestTime = 0f;
            probe?.Cancel();
        }

        private bool TryGetCachedDeadEndOutcome(string stateKey,
                                                out SolveOutcome outcome)
        {
            outcome = SolveOutcome.Inconclusive;
            return deadEndCacheCapacity > 0
                && !string.IsNullOrEmpty(stateKey)
                && deadEndOutcomeCache.TryGetValue(stateKey, out outcome);
        }

        private void RememberDeadEndOutcome(string stateKey, SolveResult result)
        {
            if (deadEndCacheCapacity <= 0 || string.IsNullOrEmpty(stateKey)
                || result == null
                || (result.Outcome != SolveOutcome.Solvable
                    && result.Outcome != SolveOutcome.Unsolvable))
                return;

            if (deadEndOutcomeCache.ContainsKey(stateKey))
            {
                deadEndOutcomeCache[stateKey] = result.Outcome;
                return;
            }

            int capacity = Mathf.Max(1, deadEndCacheCapacity);
            while (deadEndOutcomeCache.Count >= capacity
                   && deadEndCacheOrder.Count > 0)
                deadEndOutcomeCache.Remove(deadEndCacheOrder.Dequeue());

            deadEndOutcomeCache.Add(stateKey, result.Outcome);
            deadEndCacheOrder.Enqueue(stateKey);
        }

        private void FlushPendingStateChanged()
        {
            if (!hasPendingStateNotification) return;
            BartenderLevelState pending = pendingStateNotification;
            hasPendingStateNotification = false;
            InvokeSafely(StateChanged, pending);
        }

        private bool UnloadInternal(BartenderLevelState finalState)
        {
            ResolveDependencies();
            BsRoundCommit clearCommit = null;
            if (coordinator.State == BsRoundState.Completed)
            {
                bool durable = coordinator.AttemptKind
                    == BsRoundAttemptKind.Durable;
                BsSettlementReceipt terminalReceipt =
                    retainedSettlementReceipt ?? default;
                if (durable
                    && (!terminalReceipt.IsValid
                        || !terminalReceipt.IsDurable
                        || !IsTerminalSettlementCommitted))
                {
                    Debug.LogWarning(
                        "The completed durable round has no committed exact receipt.",
                        this);
                    return false;
                }

                BsRoundSnapshot completedSnapshot =
                    coordinator.CaptureSnapshot();
                if (!coordinator.TryClear(
                        coordinator.CurrentStamp,
                        BsRoundTransitionCause.ReturnToMenu,
                        out clearCommit, out string clearReason))
                {
                    Debug.LogWarning(clearReason, this);
                    return false;
                }
                if (durable
                    && !BartenderProgressService.TryAcknowledgeSettlement(
                        terminalReceipt, out string acknowledgeReason))
                {
                    long rollbackFloor = Math.Max(
                        BartenderProgressService.SettlementOperationFloor,
                        clearCommit.OperationId.Value);
                    if (!BsRoundCoordinator.TryRestore(
                            completedSnapshot,
                            rollbackFloor,
                            terminalReceipt,
                            out BsRoundCoordinator restored,
                            out string rollbackReason))
                        throw new InvalidOperationException(
                            "The rejected terminal acknowledgement could not be "
                            + "rolled back: " + rollbackReason);
                    coordinator = restored;
                    SyncBoardProjection();
                    if (!string.IsNullOrEmpty(acknowledgeReason))
                        Debug.LogWarning(acknowledgeReason, this);
                    return false;
                }
                if (durable)
                {
                    retainedSettlementReceipt = null;
                    announcedSettlementReceipt = null;
                }
            }
            else if (coordinator.State == BsRoundState.Preparing)
            {
                if (!coordinator.TryClear(
                        coordinator.CurrentStamp,
                        BsRoundTransitionCause.PreparationCancelled,
                        out clearCommit, out string clearReason))
                {
                    Debug.LogWarning(clearReason, this);
                    return false;
                }
            }
            else if (IsStandaloneRound)
            {
                if (!coordinator.TryAbortStandalone(
                        coordinator.CurrentStamp, out clearCommit,
                        out string abortReason))
                {
                    Debug.LogWarning(abortReason, this);
                    return false;
                }
            }
            else if (coordinator.State == BsRoundState.Playing
                     || coordinator.State == BsRoundState.Paused)
            {
                Debug.LogWarning(
                    "An active durable round cannot be unloaded without settlement.", this);
                return false;
            }

            if (clearCommit != null)
                PublishCoordinatorCommit(clearCommit, publishBoard: false);
            ClearProjectedRound(finalState);
            return true;
        }

        private void ClearProjectedRound(BartenderLevelState finalState)
        {
            presentationLockOwner = null;
            presentationLockRevision = -1;
            presentationBarrierOwners.Clear();
            standaloneAbortRequested = false;
            ClearAutomaticPauseOwnership();
            userPauseOwned = false;
            boardProjection = null;
            CurrentLevel = null;
            CurrentCampaignSlot = -1;
            deadEndOutcomeCache.Clear();
            deadEndCacheOrder.Clear();
            activeGameplayTime = 0d;
            timeBonusByOrderIndex = Array.Empty<double>();
            orderDeadlines.Clear();
            ResetBoosters(null);
            retainedSettlementReceipt = null;
            announcedSettlementReceipt = null;
            retainedSettlementRetryAt = 0f;
            campaignCompleteProjection =
                finalState == BartenderLevelState.CampaignComplete;
            NotifyProjectedState();
            InvokeSafely(BoostersChanged);
        }

        private OrderDef LiveOrderAtSlot(int slotIndex)
        {
            return boardProjection != null && boardProjection.Slots != null
                && slotIndex >= 0 && slotIndex < boardProjection.Slots.Length
                ? boardProjection.Slots[slotIndex]
                : null;
        }

        private int FindCampaignSlot(int oneBasedLevelNumber)
        {
            for (int i = 0; i < Campaign.Count; i++)
                if (Campaign[i] != null && Campaign[i].Index == oneBasedLevelNumber)
                    return i;
            return -1;
        }

        private static bool ContainsOrderReference(OrderDef[] slots, OrderDef wanted)
        {
            for (int i = 0; i < slots.Length; i++)
                if (ReferenceEquals(slots[i], wanted)) return true;
            return false;
        }

        private void InvokeSafely(Action handlers)
        {
            if (handlers == null) return;
            Delegate[] invocationList = handlers.GetInvocationList();
            bool previousNotificationState = notificationInProgress;
            notificationInProgress = true;
            try
            {
                for (int i = 0; i < invocationList.Length; i++)
                {
                    try { ((Action)invocationList[i])(); }
                    catch (Exception exception) { Debug.LogException(exception, this); }
                }
            }
            finally
            {
                notificationInProgress = previousNotificationState;
            }
        }

        private void InvokeSafely<T>(Action<T> handlers, T value)
        {
            if (handlers == null) return;
            Delegate[] invocationList = handlers.GetInvocationList();
            bool previousNotificationState = notificationInProgress;
            notificationInProgress = true;
            try
            {
                for (int i = 0; i < invocationList.Length; i++)
                {
                    try { ((Action<T>)invocationList[i])(value); }
                    catch (Exception exception) { Debug.LogException(exception, this); }
                }
            }
            finally
            {
                notificationInProgress = previousNotificationState;
            }
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
