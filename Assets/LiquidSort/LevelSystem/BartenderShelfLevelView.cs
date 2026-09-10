using System;
using System.Collections;
using System.Collections.Generic;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>I keep the layout's rest pose so animations always return the glass to its real seat.</summary>
    public readonly struct BartenderGlassSeatPose
    {
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public Vector3 LocalScale { get; }
        public Transform MotionRoot { get; }

        public BartenderGlassSeatPose(Vector3 position, Quaternion rotation,
                                      Vector3 localScale, Transform motionRoot)
        {
            Position = position;
            Rotation = rotation;
            LocalScale = localScale;
            MotionRoot = motionRoot;
        }
    }

    /// <summary>
    /// Connects the level model to the scene's fixed glass pool. Runtime only activates and moves the existing
    /// objects.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderShelfLevelView : MonoBehaviour
    {
        public const int FullCampaignShotPoolSize = 4;
        public const int FullCampaignCocktailPoolSize = 5;
        public const int FullCampaignLattePoolSize = 7;
        public const int FullCampaignTumblerPoolSize = 8;
        /// <summary>Peak use of the five-unit handled glass; level 30 needs all eleven.</summary>
        public const int FullCampaignBiraPoolSize = 11;
        public const int ExtraShotReserveSize = 3;
        public const int MaximumActiveGlasses = 15;
        public const int MaximumColumnsPerRow =
            ShelfLayoutSolver.MaximumColumnsPerRow;
        // Leave enough room for the whole Royal glass and its shadow to start off-screen.
        private const float MinimumEntranceDropHeight = 7.60f;
        private const string SeatRootSuffix = " [SeatRoot]";
        // If the animation stalls, snap to the real seat and release input.
        private const double SeatAnimationWatchdogGrace = 0.75d;
        // Build the shelf behind the cover, then wait for it to hide before dropping the glasses. The wait has
        // a limit so they cannot get stuck.
        private const double CoveredEntranceHandoffTimeout = 1.5d;
        private const double CoveredRefreshWatchdog = 6d;
        private const double SynchronizationDeferralWatchdog = 8d;
        private const double GarnishRevealWatchdog = 4d;

        [Serializable]
        public sealed class ShelfRowBinding
        {
            [Tooltip("The hand-authored plank renderer for this row, ordered top to bottom.")]
            public SpriteRenderer plank;
            [Tooltip("Hand-authored top-centre point where this row's glasses are seated.")]
            public Transform seatAnchor;
        }

        [Serializable]
        public sealed class ShelfSpanBinding
        {
            [Tooltip("A hand-authored post pair used between adjacent shelves.")]
            public SpriteRenderer leftPost;
            public SpriteRenderer rightPost;
        }

        [Serializable]
        public sealed class OverflowSeatLayoutsBinding
        {
            [Tooltip("The single authored surface point used when one glass overflows.")]
            public Transform[] oneGlass = new Transform[1];
            [Tooltip("The two authored surface points used when two glasses overflow.")]
            public Transform[] twoGlasses = new Transform[2];
            [Tooltip("The three authored surface points used when three glasses overflow.")]
            public Transform[] threeGlasses = new Transform[3];

            public Transform[] SeatsForCount(int count)
            {
                if (count == 1) return oneGlass;
                if (count == 2) return twoGlasses;
                return count == 3 ? threeGlasses : null;
            }
        }

        [Serializable]
        public sealed class GlassBinding
        {
            [Tooltip("A pre-placed Royal glass prefab instance. Its direct parent must be "
                   + "the authored '[SeatRoot]' placement transform driven by this view.")]
            public LiquidBottle bottle;
            [Tooltip("Direct front-art reference used to centre the complete row silhouette.")]
            public SpriteRenderer placementRenderer;
        }

        private sealed class Actor
        {
            public LiquidBottle Bottle;
            public Transform SeatRoot;
            public SpriteRenderer PlacementRenderer;
            public GlassType Type;
            public int GlassId = -1;
            public bool Assigned;
            public bool ExtraShotReserve;
            public bool GarnishOrderReady;

            /// <summary>I store the seat in glass space so it follows later parent and safe-area changes.</summary>
            public Vector3 SeatLayoutPosition;
            public Quaternion SeatLayoutRotation = Quaternion.identity;
            public Vector3 SeatScale = Vector3.one;
            public bool Seated;
            public Vector3 PreviousLayoutPosition;
            public Quaternion PreviousLayoutRotation = Quaternion.identity;
            public Vector3 PreviousSeatScale = Vector3.one;
            public bool HasPreviousSeat;
            public int Row;
            public int Column;
            public float EntranceDelay;
            public float EntranceFallDistance;
            public float EntranceFallDuration;
            public bool SortingLifted;
        }

        /// <summary>
        /// Holds the coroutine and load lock for one covered refresh. BsShelfPresentationFlow owns its identity
        /// and deadline.
        /// </summary>
        private sealed class CoveredPresentationRun
        {
            public readonly BartenderLoadingOverlayPresenter Cover;
            public readonly List<Actor> DeactivateActors =
                new List<Actor>(MaximumActiveGlasses);
            public BsShelfFlowToken Token;
            public Coroutine Routine;
            public int RefreshIndex;
            public int DeactivateIndex;
            public BartenderLevelController LockedController;
            public int LockedRevision = -1;

            public CoveredPresentationRun(BartenderLoadingOverlayPresenter cover)
            {
                Cover = cover;
            }
        }

        /// <summary>
        /// Owns one entrance or reseat and its leases. Old callbacks can only release their own run's locks.
        /// </summary>
        internal sealed class SeatPresentationRun
        {
            private bool settled;

            internal long RunId { get; }
            internal Coroutine Routine { get; private set; }
            internal double Deadline { get; private set; }
            internal BartenderLevelController LockController { get; private set; }
            internal object LockOwner { get; private set; }
            internal int LockRevision { get; private set; } = -1;
            internal BartenderLevelController BarrierController { get; private set; }
            internal object BarrierOwner { get; private set; }

            internal SeatPresentationRun(long runId, double deadline)
            {
                if (runId <= 0L) throw new ArgumentOutOfRangeException(nameof(runId));
                if (!ValidDeadline(deadline))
                    throw new ArgumentOutOfRangeException(nameof(deadline));
                RunId = runId;
                Deadline = deadline;
            }

            internal bool TryAttachRoutine(Coroutine routine)
            {
                if (settled || routine == null || Routine != null) return false;
                Routine = routine;
                return true;
            }

            internal bool TryAttachLock(
                BartenderLevelController controller,
                object owner,
                int revision)
            {
                if (settled || controller == null || owner == null || revision < 0
                    || LockController != null)
                    return false;
                LockController = controller;
                LockOwner = owner;
                LockRevision = revision;
                return true;
            }

            internal bool TryAttachBarrier(
                BartenderLevelController controller,
                object owner)
            {
                if (settled || controller == null || owner == null
                    || BarrierController != null)
                    return false;
                BarrierController = controller;
                BarrierOwner = owner;
                return true;
            }

            internal bool TrySetDeadline(double deadline)
            {
                if (settled || !ValidDeadline(deadline)) return false;
                Deadline = deadline;
                return true;
            }

            internal bool IsExpired(double now) =>
                !settled && ValidDeadline(now) && now >= Deadline;

            internal bool TryTakeSettlement(
                BsShelfSettleReason reason,
                out SeatPresentationSettlement settlement)
            {
                settlement = default;
                if (settled || !KnownReason(reason)) return false;
                settled = true;
                settlement = new SeatPresentationSettlement(
                    reason,
                    Routine,
                    LockController,
                    LockOwner,
                    LockRevision,
                    BarrierController,
                    BarrierOwner);
                Routine = null;
                LockController = null;
                LockOwner = null;
                LockRevision = -1;
                BarrierController = null;
                BarrierOwner = null;
                return true;
            }

            private static bool ValidDeadline(double value) =>
                !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0d;

            private static bool KnownReason(BsShelfSettleReason value) =>
                value >= BsShelfSettleReason.Completed
                && value <= BsShelfSettleReason.TimedOut;
        }

        internal readonly struct SeatPresentationSettlement
        {
            internal BsShelfSettleReason Reason { get; }
            internal Coroutine Routine { get; }
            internal BartenderLevelController LockController { get; }
            internal object LockOwner { get; }
            internal int LockRevision { get; }
            internal BartenderLevelController BarrierController { get; }
            internal object BarrierOwner { get; }

            internal SeatPresentationSettlement(
                BsShelfSettleReason reason,
                Coroutine routine,
                BartenderLevelController lockController,
                object lockOwner,
                int lockRevision,
                BartenderLevelController barrierController,
                object barrierOwner)
            {
                Reason = reason;
                Routine = routine;
                LockController = lockController;
                LockOwner = lockOwner;
                LockRevision = lockRevision;
                BarrierController = barrierController;
                BarrierOwner = barrierOwner;
            }
        }

        private sealed class ShelfTransitionState
        {
            public SpriteRenderer Renderer;
            public Vector3 StartLocalPosition;
            public Quaternion StartLocalRotation;
            public Vector3 StartLocalScale;
            public Vector2 StartSize;
            public Vector3 FinalLocalPosition;
            public Quaternion FinalLocalRotation;
            public Vector3 FinalLocalScale;
            public Vector2 FinalSize;
        }

        [Header("Level source")]
        [SerializeField] private BartenderLevelController controller;

        [Header("Scene coordinate space")]
        [Tooltip("All configured surface heights and glass positions are local to this transform.")]
        [SerializeField] private Transform layoutSpace;
        [Tooltip("Artist-authored common offset for every glass. Change this transform's Local "
               + "Position Y to move the complete glass set; runtime layout never writes it.")]
        [SerializeField] private Transform glassOffsetRoot;

        [Header("Responsive portrait fit")]
        [Tooltip("The width-fit provider for the world composition. Tall devices leave extra "
               + "space below a top-aligned reference frame; the shelf group follows a "
               + "controlled share of that space while the delivery stage stays fixed.")]
        [SerializeField] private WorldSpaceSafeAreaFitter safeAreaFitter;
        [Tooltip("Share of the extra height below the 720x1280 frame followed by shelves "
               + "and seated glasses. 0 keeps the authored pose; 1 follows all of it.")]
        [SerializeField, Range(0f, 1f)] private float tallScreenShelfFollow = 0.60f;
        [Tooltip("Safety cap in authored world units so extreme aspect ratios cannot push "
               + "the lowest shelf into the bottom controls.")]
        [SerializeField, Min(0f)] private float maximumTallScreenShelfOffset = 1.65f;

        [Header("Two-shelf focus")]
        [Tooltip("Uniform enlargement of the complete two-shelf group around its initial visual centre. "
               + "Glass and garnish proportions stay intact; shelf end spacing is balanced separately.")]
        [SerializeField, Range(1f, 1.15f)] private float twoRowFocusScale = 1.08f;
        [Tooltip("Length of two-row planks before the shared focus enlargement. Leaves room for both rounded ends.")]
        [SerializeField, Range(0.8f, 1f)] private float twoRowShelfWidthScale = 0.91f;
        [Tooltip("Stable point below the resting order cards, outside their animated group. Only two-row boards fit below it.")]
        [SerializeField] private Transform twoRowTopClearanceAnchor;
        private bool twoRowFocusApplied;
        private Vector3 shelfTransitionStartBoardPosition;
        private Vector3 shelfTransitionStartBoardScale;
        private Vector3 shelfTransitionFinalBoardPosition;
        private Vector3 shelfTransitionFinalBoardScale;
        private bool shelfBoardTransitionPrepared;

        [Header("Timed order headroom")]
        [Tooltip("Extra vertical room for timer plates on three-shelf levels, in layout units. "
               + "The complete shelf and glass group contracts around the lowest plank at level start.")]
        [SerializeField, Min(0f)] private float timedOrderHeadroom = 0.42f;
        [Tooltip("Maximum additional shrink for timer room. 0.08 keeps at least 92% of the authored size.")]
        [SerializeField, Range(0f, 0.08f)] private float maximumTimedBoardShrink = 0.08f;
        private bool boardPoseCaptured;
        private Vector3 authoredBoardPosition;
        private Vector3 authoredBoardScale;

        [Header("Scene-bound Royal glass pools")]
        [Tooltip("Full campaign maximum: 4. Each reference must use ShotRoyal.")]
        [SerializeField] private List<GlassBinding> shotPool = new List<GlassBinding>();
        [Tooltip("Full campaign maximum: 5. Each reference must use CocktailRoyal.")]
        [SerializeField] private List<GlassBinding> cocktailPool = new List<GlassBinding>();
        [Tooltip("Full 40-level campaign maximum: 7. Each reference must use MugRoyal.")]
        [SerializeField] private List<GlassBinding> lattePool = new List<GlassBinding>();
        [Tooltip("Full campaign maximum: 8. Each reference must use TumblerRoyal.")]
        [SerializeField] private List<GlassBinding> tumblerPool = new List<GlassBinding>();
        [Tooltip("Full campaign maximum: 11. Each reference must use BeerRoyal.")]
        [SerializeField] private List<GlassBinding> biraPool = new List<GlassBinding>();
        [Tooltip("Exactly three pre-placed ShotRoyal reserve instances. Each instance must "
               + "have its own active '[SeatRoot]' parent and start inactive.")]
        [SerializeField] private List<GlassBinding> extraShotPool =
            new List<GlassBinding>(ExtraShotReserveSize);

        [Header("Hand-authored shelf references")]
        [Tooltip("Exactly three possible plank rows, ordered top to bottom.")]
        [SerializeField] private ShelfRowBinding[] shelfRows = new ShelfRowBinding[3];
        [Tooltip("Two post pairs spanning row 1-2 and row 2-3. Only spans between "
               + "active adjacent rows are shown.")]
        [SerializeField] private ShelfSpanBinding[] shelfSpans = new ShelfSpanBinding[2];
        [Tooltip("Keep disabled for the floating-wall-shelf presentation. The plank, "
               + "its separate soft shadow and every glass seat remain fully active; "
               + "only the legacy vertical support posts are omitted.")]
        [SerializeField] private bool showShelfPosts = true;
        [Tooltip("The hand-authored short shelf above the ordinary three-row rack.")]
        [SerializeField] private ShelfRowBinding overflowShelfRow;
        [Tooltip("The hand-authored post pair connecting the overflow shelf to row one.")]
        [SerializeField] private ShelfSpanBinding overflowShelfSpan;
        [Tooltip("Authored surface points for the one-, two- and three-glass overflow "
               + "layouts. Every point must be a child of the overflow plank.")]
        [SerializeField] private OverflowSeatLayoutsBinding overflowSeatLayouts =
            new OverflowSeatLayoutsBinding();
        [Header("Hierarchy-authored shelf layouts")]
        [Tooltip("Exactly two final shelf-surface markers, top to bottom. Move these "
               + "Transforms in the Hierarchy to compose every two-shelf level.")]
        [SerializeField] private Transform[] twoShelfSurfaceAnchors = new Transform[2];
        [Tooltip("Exactly three final shelf-surface markers, top to bottom. Move these "
               + "Transforms in the Hierarchy to compose every three-shelf level.")]
        [SerializeField] private Transform[] threeShelfSurfaceAnchors = new Transform[3];
        [Tooltip("Exactly four final shelf-surface markers, top to bottom: overflow first, "
               + "then the three ordinary shelves. These are used after capacity overflow.")]
        [SerializeField] private Transform[] fourShelfSurfaceAnchors = new Transform[4];
        private bool overflowShelfActive;
        // I save the original furniture pose so level changes cannot keep multiplying its scale.
        [SerializeField, HideInInspector]
        private Vector3 authoredPlankLocalScale = new Vector3(1.70f, 0.65f, 1f);
        [SerializeField, HideInInspector]
        private Vector3 authoredPostLocalScale = new Vector3(0.65f, 0.44f, 1f);
        [SerializeField, HideInInspector] private float authoredPostCenterX = 3.4798f;

        [Header("Balanced layout metrics")]
        [Tooltip("Centre-to-centre spacing while a row contains two glasses.")]
        [SerializeField, Min(0.1f)] private float twoAcrossColumnSpacing = 2.8475f;
        [Tooltip("Centre-to-centre spacing while a row contains three glasses.")]
        [SerializeField, Min(0.1f)] private float threeAcrossColumnSpacing = 1.8983334f;
        [Tooltip("Centre-to-centre spacing while a row contains four glasses.")]
        [SerializeField, Min(0.1f)] private float compactColumnSpacing = 1.6620251f;
        // Pick one scale per board using its row count and widest row. Spacing stays per row, so every glass
        // has the same size.
        [Tooltip("Board scale while two rows are visible and no row holds four glasses. "
               + "Every glass on the board uses this authored value.")]
        [SerializeField, Min(0.1f)] private float twoRowSpaciousGlassScale = 0.9297433f;
        [Tooltip("Board scale while three rows are visible and no row holds four glasses.")]
        [SerializeField, Min(0.1f)] private float threeRowSpaciousGlassScale = 0.64f;
        [Tooltip("Additional glass-and-garnish scale for spacious three-shelf boards. "
               + "Two shelves and the four-shelf overflow retain their authored sizes.")]
        [SerializeField, Range(0.8f, 1f)] private float threeRowSpaciousGlassMultiplier = 1f;
        [Tooltip("Board scale once any row holds four glasses, inside the two-row layout.")]
        [SerializeField, Min(0.1f)] private float fourAcrossGlassScale = 0.8379776f;
        [Tooltip("Board scale once any row holds four glasses, inside the three-row layout.")]
        [SerializeField, Min(0.1f)] private float fourAcrossThreeRowGlassScale = 0.5664f;
        [Tooltip("Small optical overlap that seats the vessel artwork into the plank. "
               + "Authored in world units against the two-row board, then held at a fixed "
               + "share of the glass on every other board.")]
        [SerializeField, Min(0f)] private float opticalSeatInset = 0.02f;
        [SerializeField] private float glassPlaneZ;
        [Tooltip("Uniform furniture-and-glass scale used when a third shelf is visible. "
               + "Shelf x/y positions still come directly from the three hierarchy markers.")]
        [SerializeField, Range(0.80f, 1f)]
        private float threeRowCompositionScale = 1f;
        [Tooltip("Width removed per shelf step while moving upward. Zero preserves equal "
               + "plank widths; 0.05 yields 90/95/100% across a three-row board.")]
        [SerializeField, Range(0f, ShelfLayoutSolver.MaximumShelfWidthStep)]
        private float shelfWidthStep;

        [Header("Overflow shelf")]
        [Tooltip("Uniform furniture-and-glass contraction used only after all three ordinary "
               + "shelves are full. All four shelf x/y positions still come directly from "
               + "the overflow hierarchy markers.")]
        [SerializeField, Range(0.80f, 1f)]
        private float overflowCompositionScale = 0.82f;
        [Tooltip("Duration of the overflow-shelf unfold and purchased-glass landing.")]
        [SerializeField, Min(0.1f)] private float overflowRevealDuration = 0.36f;
        [Tooltip("Release height for a newly purchased glass, in layout units.")]
        [SerializeField, Min(0.1f)] private float purchasedGlassDropHeight = 0.72f;

        [Header("Post fitting")]
        [Tooltip("Share of the upper plank height hidden behind a vertical post.")]
        [SerializeField, Range(0f, 1.5f)] private float postUpperPlankOverlap = 0.92f;
        [Tooltip("Small amount each post sinks behind the shelf at its lower end.")]
        [SerializeField, Min(0f)] private float postLowerShelfInset = 0.02f;

        [Header("Level entrance animation")]
        [Tooltip("Animate shelves and glasses on level load.")]
        [SerializeField] private bool animateEntrance = true;
        [Tooltip("Drop height above the top shelf, in layout units. Keep it above the camera edge.")]
        [SerializeField, Min(MinimumEntranceDropHeight)]
        private float entranceDropHeight = MinimumEntranceDropHeight;
        [Tooltip("Time to fall one drop height. Lower shelves take longer at the same acceleration.")]
        [SerializeField, Min(0.01f)] private float entranceDropDuration = 0.32f;
        [Tooltip("Delay between glasses on the same row.")]
        [SerializeField, Min(0f)] private float entranceGlassStagger = 0.055f;
        [Tooltip("Extra delay between rows. The lower row fills first.")]
        [SerializeField, Min(0f)] private float entranceRowStagger = 0.12f;
        [Tooltip("Extra draw order while falling. Set it above the planks; 0 disables it.")]
        [SerializeField, Min(0)] private int entranceSortingBoost = 60;
        [Tooltip("Kept at zero for Royal vessels: their authored silhouette must never be "
               + "non-uniformly stretched when it reaches a shelf.")]
        [SerializeField, Range(0f, 0.4f)] private float entranceLandingSquash;
        [Tooltip("Time to recover from the landing squash.")]
        [SerializeField, Min(0f)] private float entranceSettleDuration = 0.20f;
        [Tooltip("Shelf entrance time. 0 shows it instantly.")]
        [SerializeField, Min(0f)] private float shelfFadeDuration = 0.22f;
        [Tooltip("Glass rearrangement time during a level. 0 moves instantly.")]
        [SerializeField, Min(0f)] private float reseatDuration = 0.22f;

        [Header("Layer presentation")]
        [Tooltip("Neutral fill used while a liquid unit is concealed by either mystery "
               + "or delivery-lock rules.")]
        [SerializeField] private Color hiddenLayerColor = new Color(0.32f, 0.35f, 0.40f, 1f);
        [Tooltip("Brightness retained while the complete glass is delivery-locked.")]
        [SerializeField, Range(0.25f, 1f)] private float chainedGlassBrightness = 0.46f;

        private readonly List<Actor> actors = new List<Actor>(
            FullCampaignShotPoolSize + FullCampaignCocktailPoolSize
            + FullCampaignLattePoolSize + FullCampaignTumblerPoolSize
            + FullCampaignBiraPoolSize + ExtraShotReserveSize);
        private readonly List<Actor> activeActors = new List<Actor>(MaximumActiveGlasses);
        private readonly Dictionary<int, Actor> actorByGlassId =
            new Dictionary<int, Actor>(MaximumActiveGlasses);
        private readonly Dictionary<LiquidBottle, int> glassIdByBottle =
            new Dictionary<LiquidBottle, int>(MaximumActiveGlasses);
        private readonly List<Color> colorScratch = new List<Color>(LiquidBottle.MaxBands);
        private readonly HashSet<LiquidBottle> uniqueBottleScratch =
            new HashSet<LiquidBottle>();
        private readonly HashSet<Transform> uniqueSeatRootScratch =
            new HashSet<Transform>();
        private readonly HashSet<Transform> uniqueOverflowSeatScratch =
            new HashSet<Transform>();
        private readonly List<Actor> entranceOrder = new List<Actor>(MaximumActiveGlasses);
        private readonly List<SpriteRenderer> shelfFadeRenderers =
            new List<SpriteRenderer>(10);
        private readonly List<Color> shelfFadeColors = new List<Color>(10);
        private readonly List<SpriteRenderer> shelfRevealRenderers =
            new List<SpriteRenderer>(5);
        private readonly List<Color> shelfRevealColors = new List<Color>(5);
        private readonly List<ShelfTransitionState> shelfTransitionStates =
            new List<ShelfTransitionState>(9);
        private SpriteRenderer revealingShelfPlank;
        private SpriteRenderer revealingShelfLeftPost;
        private SpriteRenderer revealingShelfRightPost;
        private Vector3 revealingPlankFinalScale;
        private Vector3 revealingLeftPostFinalScale;
        private Vector3 revealingRightPostFinalScale;
        private bool shelfRevealPrepared;
        private bool shelfTransitionPrepared;
        private readonly BsShelfPresentationFlow presentationFlow =
            new BsShelfPresentationFlow();
        private SeatPresentationRun activeSeatPresentation;
        private long nextSeatPresentationRunId;

        private BartenderLevelController subscribedController;
        private BsLevel presentedLevel;
        private int presentedBoardRevision = -1;
        private bool publishingPresentationChanged;
        private bool actorCacheBuilt;
        private int configuredColumns = 1;
        private int configuredRowCount;
        private float appliedResponsiveShelfOffset;
        private string lastLoggedError;

        public BartenderLevelController Controller => controller;
        internal Color HiddenLayerColor => hiddenLayerColor;
        public bool Ready { get; private set; }
        public string LastError { get; private set; }
        public Vector3 LayoutUpWorld
        {
            get
            {
                Vector3 up = GlassSpace.TransformVector(Vector3.up);
                return up.sqrMagnitude > 0.00000001f ? up.normalized : Vector3.up;
            }
        }
        /// <summary>True while glasses are moving to their seats.</summary>
        public bool SeatAnimationPlaying =>
            activeSeatPresentation != null
            && activeSeatPresentation.Routine != null;
        /// <summary>
        /// True while the covered load activates one vessel per frame. Keep the cover up until this finishes;
        /// other loads stay synchronous.
        /// </summary>
        public bool CoveredPresentationPending =>
            presentationFlow.CoveredRefreshPending;
        /// <summary>True while an already committed board change is animating.</summary>
        public bool SynchronizationDeferred =>
            presentationFlow.SynchronizationDeferred;

        public event Action PresentationChanged;

        /// <summary>
        /// Makes only the next LevelLoaded activate across frames. The cover must already be visible.
        /// </summary>
        public bool ArmNextCoveredLoad(BartenderLoadingOverlayPresenter cover)
        {
            if (!Application.isPlaying || !isActiveAndEnabled
                || !gameObject.activeInHierarchy || cover == null || !cover.Visible
                || presentationFlow.CoveredState != BsShelfCoveredLoadState.Idle)
                return false;

            var run = new CoveredPresentationRun(cover);
            if (!presentationFlow.TryArmCovered(
                    cover,
                    run,
                    Time.realtimeSinceStartupAsDouble + CoveredRefreshWatchdog,
                    out BsShelfFlowToken token))
                return false;
            run.Token = token;
            return true;
        }

        public void DisarmCoveredLoad(BartenderLoadingOverlayPresenter cover)
        {
            if (!presentationFlow.TryGetCovered(
                    out object owner,
                    out BsShelfFlowToken token,
                    out _)
                || !ReferenceEquals(owner, cover)) return;
            TrySettleCoveredPresentation(
                cover, token, BsShelfSettleReason.Cancelled);
        }

        private Transform LayoutSpace => layoutSpace != null ? layoutSpace : transform;
        private Transform GlassSpace => glassOffsetRoot != null
            ? glassOffsetRoot
            : LayoutSpace;
        private ShelfLayoutSettings LayoutMetrics
        {
            get
            {
                float effectiveThreeRowScale = overflowShelfActive
                    ? overflowCompositionScale
                    : threeRowCompositionScale;
                return new ShelfLayoutSettings(
                    twoAcrossColumnSpacing,
                    threeAcrossColumnSpacing,
                    compactColumnSpacing,
                    twoRowSpaciousGlassScale,
                    threeRowSpaciousGlassScale * (overflowShelfActive ? 1f : threeRowSpaciousGlassMultiplier),
                    fourAcrossGlassScale,
                    fourAcrossThreeRowGlassScale,
                    opticalSeatInset,
                    effectiveThreeRowScale);
            }
        }

        private void Awake()
        {
            if (safeAreaFitter == null && layoutSpace != null)
                safeAreaFitter = layoutSpace.GetComponentInParent<WorldSpaceSafeAreaFitter>();
            EnsureActorCache(out _);
        }

        private void OnEnable()
        {
            if (Application.isPlaying) EnsureActorCache(out _);
            Subscribe();
            if (!Application.isPlaying) return;
            if (controller != null && controller.CurrentLevel != null)
                RefreshFromController();
            else
                ClearPresentation();
        }

        private void OnDisable()
        {
            // One broken visual must not skip the remaining run/lease cleanup or leave the old shelf live.
            try
            {
                ExecutePresentationCleanup(
                    Unsubscribe,
                    () => SettleShelfAndSeatPresentation(
                        BsShelfSettleReason.Disabled,
                        () => SettleShelfPresentationFlow(BsShelfSettleReason.Disabled)),
                    () =>
                    {
                        if (Application.isPlaying) ClearPresentation();
                    },
                    null);
            }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }

        private void LateUpdate()
        {
            RefreshResponsiveShelfOffset();

            double now = Time.realtimeSinceStartupAsDouble;
            SeatPresentationRun seatRun = activeSeatPresentation;
            ExecutePresentationCleanup(
                null,
                () => presentationFlow.SettleExpired(
                    now,
                    HandleShelfFlowSettlement),
                () =>
                {
                    if (seatRun == null || !seatRun.IsExpired(now)) return;
                    Debug.LogWarning(
                        "Shelf seat animation exceeded its realtime deadline; snapping to the canonical layout.",
                        this);
                    TrySettleSeatPresentation(
                        seatRun, BsShelfSettleReason.TimedOut, true);
                },
                null);
        }

        private void OnValidate()
        {
            twoAcrossColumnSpacing = Mathf.Max(0.1f, twoAcrossColumnSpacing);
            threeAcrossColumnSpacing = Mathf.Max(0.1f, threeAcrossColumnSpacing);
            compactColumnSpacing = Mathf.Max(0.1f, compactColumnSpacing);
            tallScreenShelfFollow = Mathf.Clamp01(tallScreenShelfFollow);
            maximumTallScreenShelfOffset = Mathf.Max(0f, maximumTallScreenShelfOffset);
            timedOrderHeadroom = Mathf.Max(0f, timedOrderHeadroom);
            maximumTimedBoardShrink = Mathf.Clamp(maximumTimedBoardShrink, 0f, 0.08f);
            twoRowSpaciousGlassScale = Mathf.Max(0.1f, twoRowSpaciousGlassScale);
            twoRowFocusScale = Mathf.Clamp(twoRowFocusScale, 1f, 1.15f);
            twoRowShelfWidthScale = Mathf.Clamp(twoRowShelfWidthScale, 0.8f, 1f);
            threeRowSpaciousGlassScale = Mathf.Max(0.1f, threeRowSpaciousGlassScale);
            threeRowSpaciousGlassMultiplier = Mathf.Clamp(threeRowSpaciousGlassMultiplier, 0.8f, 1f);
            fourAcrossGlassScale = Mathf.Max(0.1f, fourAcrossGlassScale);
            fourAcrossThreeRowGlassScale = Mathf.Max(0.1f, fourAcrossThreeRowGlassScale);
            threeRowCompositionScale = Mathf.Clamp(threeRowCompositionScale, 0.80f, 1f);
            overflowCompositionScale = Mathf.Clamp(overflowCompositionScale, 0.80f, 1f);
            overflowRevealDuration = Mathf.Max(0.1f, overflowRevealDuration);
            purchasedGlassDropHeight = Mathf.Max(0.1f, purchasedGlassDropHeight);
            shelfWidthStep = Mathf.Clamp(shelfWidthStep, 0f,
                ShelfLayoutSolver.MaximumShelfWidthStep);
            opticalSeatInset = Mathf.Max(0f, opticalSeatInset);
            postLowerShelfInset = Mathf.Max(0f, postLowerShelfInset);
            entranceDropHeight = Mathf.Max(MinimumEntranceDropHeight,
                                           entranceDropHeight);
            entranceDropDuration = Mathf.Max(0.01f, entranceDropDuration);
            entranceGlassStagger = Mathf.Max(0f, entranceGlassStagger);
            entranceRowStagger = Mathf.Max(0f, entranceRowStagger);
            entranceSettleDuration = Mathf.Max(0f, entranceSettleDuration);
            shelfFadeDuration = Mathf.Max(0f, shelfFadeDuration);
            reseatDuration = Mathf.Max(0f, reseatDuration);
        }

        /// <summary>Refreshes from the controller snapshot if this view enabled after the level loaded.</summary>
        public bool RefreshFromController() => RefreshFromController(null);

        private bool RefreshFromController(BartenderBoardChange change)
        {
            if (controller == null)
                return Reject("BartenderLevelController Inspector referansı eksik.");
            BsLevel level = controller.CurrentLevel;
            BsBoard snapshot = controller.Board;
            if (level == null || snapshot == null)
            {
                ClearPresentation();
                LastError = "Yüklü level yok.";
                return false;
            }

            if (!ReferenceEquals(presentedLevel, level) || !Ready)
            {
                bool presented = TryPresent(
                    level, snapshot, controller.Palette,
                    controller.BoardRevision,
                    out SeatPresentationRun entranceRun);
                if (!presented) return false;
                presentedBoardRevision = controller.BoardRevision;
                if (entranceRun == null || entranceRun.Routine == null) return true;

                int revision = controller.BoardRevision;
                BartenderLevelController lockedController = controller;
                if (lockedController.TryAcquirePresentationLock(this, revision))
                {
                    if (ReferenceEquals(activeSeatPresentation, entranceRun)
                        && entranceRun.TryAttachLock(
                            lockedController, this, revision))
                        return true;
                    lockedController.ReleasePresentationLock(this, revision);
                }

                StopSeatAnimation();
                SnapActorsToSeat();
                return true;
            }
            // This revision is already shown. Skipping it also stops refresh listeners from calling each other
            // forever.
            if (presentedBoardRevision == controller.BoardRevision) return true;
            BartenderDeliveryReceipt deliveryReceipt = change != null
                && IsCurrentBoardChange(change)
                && change.DeliveryReceipt != null
                && DeliveryMatchesBoardChange(change.DeliveryReceipt, change)
                    ? change.DeliveryReceipt
                    : null;
            bool synchronized = TrySynchronize(
                snapshot, controller.Palette,
                controller.BoardRevision, deliveryReceipt);
            if (synchronized) presentedBoardRevision = controller.BoardRevision;
            return synchronized;
        }

        /// <summary>
        /// Applies a detached snapshot to the scene pools without keeping or changing domain objects.
        /// </summary>
        public bool TryPresent(BsLevel level, BsBoard snapshot, BsPalette palette) =>
            TryPresent(level, snapshot, palette, -1, out _);

        private bool TryPresent(
            BsLevel level,
            BsBoard snapshot,
            BsPalette palette,
            int authoritativeRevision,
            out SeatPresentationRun entranceRun)
        {
            entranceRun = null;
            CancelCoveredPresentation();
            if (!TryPreparePresentation(level, snapshot, palette)) return false;
            ActivateAndRefreshActors();
            entranceRun = FinishPreparedPresentationState();
            // Preview revision -1 cannot take a gameplay lease until synced with the controller.
            presentedBoardRevision = authoritativeRevision;
            PublishPresentationChangedSafely();
            return true;
        }

        private bool TryPreparePresentation(
            BsLevel level,
            BsBoard snapshot,
            BsPalette palette,
            CoveredPresentationRun coveredRun = null)
        {
            if (!ValidateSnapshot(level, snapshot, palette, out int rows, out string reason))
                return Reject(reason);

            StopSeatAnimation();
            bool preserveActivePool = coveredRun != null;
            ClearAssignments(!preserveActivePool);
            RestoreBoardCompositionFit();
            Ready = false;
            presentedLevel = level;
            configuredColumns = Mathf.Max(1, level.ColumnsPerRow);
            configuredRowCount = rows;
            overflowShelfActive = ShelfLayoutSolver.OverflowGlassCount(
                snapshot.Glasses.Count, configuredColumns) > 0;

            for (int i = 0; i < snapshot.Glasses.Count; i++)
            {
                RtGlass glass = snapshot.Glasses[i];
                Actor actor = Acquire(
                    glass.Type, IsPurchasedExtraGlass(level, glass));
                if (actor == null)
                    return Reject($"{BsRules.DisplayName(glass.Type)} scene havuzu tükendi.");

                actor.Assigned = true;
                actor.GlassId = glass.Id;
                actorByGlassId.Add(glass.Id, actor);
                glassIdByBottle.Add(actor.Bottle, glass.Id);
                activeActors.Add(actor);
                SetContents(actor.Bottle, glass, palette, snapshot.Delivered);
                actor.GarnishOrderReady = snapshot.MatchedSlot(glass) >= 0;
            }

            ApplyShelfLayout(configuredRowCount);
            LayoutActiveActors();
            ApplyBoardCompositionFit();
            if (preserveActivePool)
            {
                coveredRun.DeactivateActors.Clear();
                coveredRun.DeactivateIndex = 0;
                for (int i = 0; i < actors.Count; i++)
                {
                    Actor actor = actors[i];
                    if (actor != null && !actor.Assigned && actor.Bottle != null
                        && actor.Bottle.gameObject.activeSelf)
                        coveredRun.DeactivateActors.Add(actor);
                }
            }
            return true;
        }

        private SeatPresentationRun FinishPreparedPresentationState()
            => FinishPreparedPresentationState(null);

        /// <summary>
        /// Finishes the prepared view. With a cover, glasses wait at the release line until it hides.
        /// </summary>
        private SeatPresentationRun FinishPreparedPresentationState(
            BartenderLoadingOverlayPresenter cover)
        {
            SeatPresentationRun run = PlayEntrance(cover);
            Ready = true;
            LastError = null;
            lastLoggedError = null;
            return run;
        }

        /// <summary>Maps a hand-authored scene glass back to its current domain id.</summary>
        public bool TryGetGlassId(LiquidBottle bottle, out int glassId)
        {
            if (bottle != null && glassIdByBottle.TryGetValue(bottle, out glassId))
                return true;
            glassId = -1;
            return false;
        }

        /// <summary>Maps a current domain id to its hand-authored scene glass.</summary>
        public bool TryGetBottle(int glassId, out LiquidBottle bottle)
        {
            if (actorByGlassId.TryGetValue(glassId, out Actor actor)
                && actor != null && actor.Bottle != null)
            {
                bottle = actor.Bottle;
                return true;
            }
            bottle = null;
            return false;
        }

        /// <summary>
        /// Returns the layout's rest pose. The animated transform may be moving, so it cannot define the seat.
        /// </summary>
        public bool TryGetSeatPose(int glassId, out BartenderGlassSeatPose pose)
        {
            if (actorByGlassId.TryGetValue(glassId, out Actor actor)
                && actor != null && actor.Bottle != null && actor.Seated)
            {
                Transform motionRoot = MotionRoot(actor);
                pose = new BartenderGlassSeatPose(
                    SeatWorldPosition(actor), SeatWorldRotation(actor), actor.SeatScale,
                    motionRoot);
                return true;
            }

            pose = default;
            return false;
        }

        /// <summary>
        /// Returns the transform animations can move. The LiquidBottle's profile pose stays fixed during
        /// gameplay.
        /// </summary>
        public bool TryGetMotionRoot(LiquidBottle bottle, out Transform motionRoot)
        {
            if (bottle != null && glassIdByBottle.TryGetValue(bottle, out int glassId)
                && actorByGlassId.TryGetValue(glassId, out Actor actor))
            {
                motionRoot = MotionRoot(actor);
                return motionRoot != null;
            }

            motionRoot = null;
            return false;
        }

        /// <summary>True only while one of the three fixed purchased-shot slots is free.</summary>
        public bool HasFreeExtraShotSlot()
        {
            if (!Application.isPlaying) return false;
            if (!ValidateAuthoredOverflowAssets(out _)
                || !EnsureActorCache(out _)) return false;
            return Acquire(GlassType.Shot, true) != null;
        }

        /// <summary>
        /// Keeps the old liquid visible while a committed move animates. Apply queued board changes in one
        /// refresh afterward.
        /// </summary>
        public bool TryBeginSynchronizationDeferral(
            object owner,
            out BsShelfSynchronizationLease lease)
        {
            lease = default;
            if (owner == null || controller == null || !Ready
                || !ReferenceEquals(presentedLevel, controller.CurrentLevel)
                || presentedBoardRevision != controller.BoardRevision)
                return false;
            return presentationFlow.TryBeginSynchronization(
                owner,
                presentedBoardRevision,
                Time.realtimeSinceStartupAsDouble
                    + SynchronizationDeferralWatchdog,
                out lease);
        }

        public bool IsSynchronizationDeferredBy(
            object owner,
            BsShelfSynchronizationLease lease) =>
            presentationFlow.IsSynchronizationOwnedBy(owner, lease);

        /// <summary>Releases only the stale callback's own deferral, leaving the current round alone.</summary>
        public bool DropSynchronizationDeferral(
            object owner,
            BsShelfSynchronizationLease lease)
        {
            return presentationFlow.TryDropSynchronization(
                owner,
                lease,
                BsShelfSettleReason.Cancelled,
                out _);
        }

        /// <summary>
        /// Releases the owner's deferral and refreshes from the controller after a finished or cancelled pour.
        /// </summary>
        public bool EndSynchronizationDeferralAndRefresh(
            object owner,
            BsShelfSynchronizationLease lease,
            bool forceRefresh = false)
        {
            if (!presentationFlow.TryEndSynchronization(
                    owner, lease, forceRefresh,
                    out BsShelfFlowSettlement settlement))
                return false;
            if (!settlement.ShouldRefresh) return true;
            if (!isActiveAndEnabled) return false;
            BartenderBoardChange change = settlement.Change as BartenderBoardChange;
            if (change != null
                && (change.Revision != settlement.Revision
                    || !IsCurrentBoardChange(change))) change = null;
            return RefreshFromController(change);
        }

        /// <summary>
        /// Hit-tests active, bound Royal glasses without colliders. Uses the supplied camera or Camera.main.
        /// </summary>
        public bool TryPickBottle(Camera camera, Vector2 screenPoint, float padding,
                                  out LiquidBottle bottle, out int glassId)
        {
            bottle = null;
            glassId = -1;
            if (!Ready || camera == null) return false;

            float safePadding = Mathf.Max(0f, padding);
            float bestDistance = float.MaxValue;
            for (int i = 0; i < activeActors.Count; i++)
            {
                Actor actor = activeActors[i];
                LiquidBottle candidate = actor != null ? actor.Bottle : null;
                if (candidate == null || !candidate.gameObject.activeInHierarchy)
                    continue;

                float depth = Vector3.Dot(candidate.transform.position
                                          - camera.transform.position,
                                          camera.transform.forward);
                Vector3 world = camera.ScreenToWorldPoint(
                    new Vector3(screenPoint.x, screenPoint.y, depth));
                SpriteRenderer placement = actor.PlacementRenderer;
                if (placement == null) continue;
                Bounds bounds = placement.bounds;
                Vector3 scale = candidate.transform.lossyScale;
                float worldPadding = safePadding * Mathf.Max(
                    Mathf.Abs(scale.x), Mathf.Abs(scale.y));
                if (world.x < bounds.min.x - worldPadding
                    || world.x > bounds.max.x + worldPadding
                    || world.y < bounds.min.y - worldPadding
                    || world.y > bounds.max.y + worldPadding)
                    continue;

                float distance = Mathf.Abs(world.x - bounds.center.x);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bottle = candidate;
                glassId = actor.GlassId;
            }
            return bottle != null && glassId >= 0;
        }

        /// <summary>
        /// Checks pool capacity for every glass type and all three shelf rows in the 40-level campaign.
        /// </summary>
        public bool ValidateFullCampaignBindings(out string reason)
        {
            if (controller == null)
            {
                reason = "BartenderLevelController Inspector referansı eksik.";
                return false;
            }
            if (layoutSpace == null)
            {
                reason = "Layout Space Inspector referansı eksik.";
                return false;
            }
            if (!ValidatePoolCount(shotPool, FullCampaignShotPoolSize, GlassType.Shot,
                    out reason)
                || !ValidatePoolCount(cocktailPool, FullCampaignCocktailPoolSize,
                    GlassType.Kadeh, out reason)
                || !ValidatePoolCount(lattePool, FullCampaignLattePoolSize,
                    GlassType.Latte, out reason)
                || !ValidatePoolCount(tumblerPool, FullCampaignTumblerPoolSize,
                    GlassType.Tumbler, out reason)
                || !ValidatePoolCount(biraPool, FullCampaignBiraPoolSize,
                    GlassType.Bira, out reason))
                return false;

            if (!ValidateAuthoredOverflowAssets(out reason)) return false;

            if (!EnsureActorCache(out reason)) return false;
            if (!ValidateShelfBindings(2, false, out reason)
                || !ValidateShelfBindings(3, false, out reason)
                || !ValidateShelfBindings(3, true, out reason)) return false;
            reason = null;
            return true;
        }

        [ContextMenu("Validate Full Campaign Bindings")]
        private void ValidateFullCampaignBindingsFromContextMenu()
        {
            if (ValidateFullCampaignBindings(out string reason))
                Debug.Log("Bartender shelf: full campaign bindings are valid for all five "
                        + "glass types.", this);
            else
                Debug.LogError("Bartender shelf binding error: " + reason, this);
        }

        private void Subscribe()
        {
            if (subscribedController == controller) return;
            Unsubscribe();
            subscribedController = controller;
            if (subscribedController == null) return;
            subscribedController.LevelLoaded += HandleLevelLoaded;
            subscribedController.BoardCommitted += HandleBoardCommitted;
            subscribedController.StateChanged += HandleStateChanged;
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.BoardCommitted -= HandleBoardCommitted;
                subscribedController.StateChanged -= HandleStateChanged;
            }
            subscribedController = null;
        }

        private void HandleLevelLoaded(BsLevel level)
        {
            // A new level clears the old board's leases, so stale callbacks cannot block its load.
            SettleShelfAndSeatPresentation(
                BsShelfSettleReason.LevelChanged,
                () => presentationFlow.SettleLevelBoundary(
                    HandleShelfFlowSettlement));

            BsBoard snapshot = controller != null ? controller.Board : null;
            bool coverArmed = presentationFlow.TryGetCovered(
                out object coveredOwner,
                out BsShelfFlowToken coveredToken,
                out object coveredContext);
            CoveredPresentationRun coveredRun =
                coveredContext as CoveredPresentationRun;
            BartenderLoadingOverlayPresenter cover =
                coveredOwner as BartenderLoadingOverlayPresenter;
            if (coverArmed && coveredRun != null
                && ReferenceEquals(coveredRun.Cover, cover)
                && coveredRun.Token == coveredToken
                && cover != null && cover.Visible && controller != null)
            {
                BeginCoveredPresentation(
                    level, snapshot, controller.Palette, coveredRun);
                return;
            }
            if (coverArmed)
                TrySettleCoveredPresentation(
                    coveredOwner, coveredToken, BsShelfSettleReason.Cancelled);

            int revision = controller != null ? controller.BoardRevision : -1;
            bool presented = TryPresent(
                level, snapshot, controller != null ? controller.Palette : null,
                revision,
                out SeatPresentationRun entranceRun);
            if (!presented || controller == null) return;

            presentedBoardRevision = revision;
            if (entranceRun == null || entranceRun.Routine == null) return;
            BartenderLevelController lockedController = controller;
            if (lockedController.TryAcquireLoadPresentationLock(this, revision))
            {
                if (ReferenceEquals(activeSeatPresentation, entranceRun)
                    && entranceRun.TryAttachLock(
                        lockedController, this, revision))
                    return;
                lockedController.ReleasePresentationLock(this, revision);
            }

            // Without the timer/input lease, skip the entrance and use the final seat.
            StopSeatAnimation();
            SnapActorsToSeat();
        }

        private void BeginCoveredPresentation(
            BsLevel level,
            BsBoard snapshot,
            BsPalette palette,
            CoveredPresentationRun run)
        {
            try
            {
                BeginCoveredPresentationCore(level, snapshot, palette, run);
            }
            catch
            {
                TrySettleCoveredPresentation(
                    run?.Cover,
                    run != null ? run.Token : default,
                    BsShelfSettleReason.Cancelled);
                throw;
            }
        }

        private void BeginCoveredPresentationCore(
            BsLevel level,
            BsBoard snapshot,
            BsPalette palette,
            CoveredPresentationRun run)
        {
            if (run == null || run.Cover == null
                || !TryPreparePresentation(level, snapshot, palette, run)
                || controller == null)
            {
                TrySettleCoveredPresentation(
                    run?.Cover,
                    run != null ? run.Token : default,
                    BsShelfSettleReason.Cancelled);
                return;
            }

            int revision = controller.BoardRevision;
            presentedBoardRevision = revision;
            if (!controller.TryAcquireLoadPresentationLock(this, revision))
            {
                // Only LevelLoaded can take this lock. If it is busy, finish synchronously so no partial shelf
                // appears.
                for (int i = 0; i < run.DeactivateActors.Count; i++)
                    DeactivateAndResetActor(run.DeactivateActors[i]);
                run.DeactivateActors.Clear();
                run.DeactivateIndex = 0;
                ActivateAndRefreshActors();
                FinishPreparedPresentationState();
                if (SeatAnimationPlaying)
                {
                    StopSeatAnimation();
                    SnapActorsToSeat();
                }
                PublishPresentationChangedSafely();
                TrySettleCoveredPresentation(
                    run.Cover, run.Token, BsShelfSettleReason.Cancelled);
                return;
            }

            run.LockedController = controller;
            run.LockedRevision = revision;
            BsRoundCommandStamp stamp = controller.CurrentRoundStamp;
            if (!presentationFlow.TryBeginCovered(
                    run.Cover,
                    run.Token,
                    revision,
                    stamp,
                    Time.realtimeSinceStartupAsDouble + CoveredRefreshWatchdog))
            {
                for (int i = 0; i < run.DeactivateActors.Count; i++)
                    DeactivateAndResetActor(run.DeactivateActors[i]);
                ActivateAndRefreshActors();
                FinishPreparedPresentationState();
                if (SeatAnimationPlaying) StopSeatAnimation();
                PublishPresentationChangedSafely();
                TrySettleCoveredPresentation(
                    run.Cover, run.Token, BsShelfSettleReason.Cancelled);
                return;
            }

            run.RefreshIndex = 0;
            run.Routine = StartCoroutine(
                CoveredPresentationRoutine(run));
        }

        private IEnumerator CoveredPresentationRoutine(CoveredPresentationRun run)
        {
            // Yield before activating vessels so the caller can take its loading barrier.
            yield return null;

            bool handedLockToEntrance = false;
            bool refreshCompleted = false;
            try
            {
                while (run.DeactivateIndex < run.DeactivateActors.Count)
                {
                    if (!CoveredRevisionIsCurrent(run)) yield break;

                    DeactivateAndResetActor(
                        run.DeactivateActors[run.DeactivateIndex]);
                    run.DeactivateIndex++;

                    // Teardown also costs work, so spread both sides of the swap across frames.
                    if (run.DeactivateIndex < run.DeactivateActors.Count
                        || run.RefreshIndex < activeActors.Count)
                        yield return null;
                }

                while (run.RefreshIndex < activeActors.Count)
                {
                    if (!CoveredRevisionIsCurrent(run)) yield break;

                    ActivateAndRefreshActor(activeActors[run.RefreshIndex]);
                    run.RefreshIndex++;

                    // I activate one vessel per frame so its meshes do not stall the loading animation.
                    if (run.RefreshIndex < activeActors.Count)
                        yield return null;
                }

                if (!CoveredRevisionIsCurrent(run)) yield break;
                SeatPresentationRun entranceRun =
                    FinishPreparedPresentationState(run.Cover);
                PublishPresentationChangedSafely();
                refreshCompleted = true;
                if (entranceRun != null)
                {
                    handedLockToEntrance =
                        ReferenceEquals(activeSeatPresentation, entranceRun)
                        && entranceRun.TryAttachLock(
                            run.LockedController, this, run.LockedRevision);
                    if (handedLockToEntrance)
                    {
                        // Move this exact lease to the seat run before cleanup. Old cleanup cannot release the
                        // next run's lock.
                        run.LockedController = null;
                        run.LockedRevision = -1;
                    }
                    else
                    {
                        TrySettleSeatPresentation(
                            entranceRun, BsShelfSettleReason.Cancelled, true);
                    }
                }
            }
            finally
            {
                run.Routine = null;
                TrySettleCoveredPresentation(
                    run.Cover,
                    run.Token,
                    refreshCompleted
                        ? BsShelfSettleReason.Completed
                        : BsShelfSettleReason.Cancelled);
            }
        }

        private bool CoveredRevisionIsCurrent(CoveredPresentationRun run)
        {
            return isActiveAndEnabled && run != null && controller != null && presentedLevel != null
                && ReferenceEquals(controller.CurrentLevel, presentedLevel)
                && presentationFlow.IsCoveredCurrent(
                    run.Cover, run.Token, controller.CurrentRoundStamp);
        }

        private void CancelCoveredPresentation()
        {
            if (!presentationFlow.TryGetCovered(
                    out object owner,
                    out BsShelfFlowToken token,
                    out _)) return;
            TrySettleCoveredPresentation(
                owner, token, BsShelfSettleReason.Cancelled);
        }

        private bool TrySettleCoveredPresentation(
            object owner,
            BsShelfFlowToken token,
            BsShelfSettleReason reason)
        {
            if (!presentationFlow.TrySettleCovered(
                    owner, token, reason,
                    out BsShelfFlowSettlement settlement))
                return false;
            HandleShelfFlowSettlement(settlement);
            return true;
        }

        private void HandleShelfFlowSettlement(BsShelfFlowSettlement settlement)
        {
            switch (settlement.Track)
            {
                case BsShelfPresentationTrack.CoveredLoad:
                    SettleCoveredPresentationPayload(settlement);
                    break;

                case BsShelfPresentationTrack.Synchronization:
                    if (settlement.Reason == BsShelfSettleReason.TimedOut
                        && settlement.ShouldRefresh && isActiveAndEnabled)
                    {
                        BartenderBoardChange change =
                            settlement.Change as BartenderBoardChange;
                        if (change != null
                            && (change.Revision != settlement.Revision
                                || !IsCurrentBoardChange(change)))
                            change = null;
                        RefreshFromController(change);
                    }
                    break;

                case BsShelfPresentationTrack.GarnishReveal:
                    // The board owner is gone. Any remaining glass effects are local and hold no board lease.
                    break;
            }
        }

        private void SettleCoveredPresentationPayload(
            BsShelfFlowSettlement settlement)
        {
            CoveredPresentationRun run =
                settlement.Context as CoveredPresentationRun;
            if (run == null) return;

            Coroutine routine = run.Routine;
            run.Routine = null;
            ExecutePresentationCleanup(
                routine != null ? (Action)(() => StopCoroutine(routine)) : null,
                () =>
                {
                    if (settlement.Reason != BsShelfSettleReason.TimedOut
                        || !isActiveAndEnabled || controller == null || presentedLevel == null
                        || !ReferenceEquals(controller.CurrentLevel, presentedLevel)
                        || controller.BoardRevision != settlement.Revision)
                        return;

                    // On timeout, finish the prepared view immediately and release the input lease.
                    // Teardown was also spread across frames; finish it before dropping its actor list or
                    // old glasses can remain visible beside a shelf already marked Ready.
                    for (int i = run.DeactivateIndex; i < run.DeactivateActors.Count; i++)
                        DeactivateAndResetActor(run.DeactivateActors[i]);
                    ActivateAndRefreshActors();
                    SnapActorsToSeat();
                    Ready = true;
                    LastError = null;
                    PublishPresentationChangedSafely();
                },
                () => ReleaseCoveredPresentationLock(run),
                () =>
                {
                    run.DeactivateActors.Clear();
                    run.RefreshIndex = 0;
                    run.DeactivateIndex = 0;
                });
        }

        private void ReleaseCoveredPresentationLock(CoveredPresentationRun run)
        {
            if (run == null || run.LockedRevision < 0) return;
            BartenderLevelController lockedController = run.LockedController;
            int lockedRevision = run.LockedRevision;
            run.LockedController = null;
            run.LockedRevision = -1;
            if (lockedController != null)
                lockedController.ReleasePresentationLock(this, lockedRevision);
        }

        private void SettleShelfPresentationFlow(BsShelfSettleReason reason)
        {
            presentationFlow.SettleAll(reason, HandleShelfFlowSettlement);
        }

        private void SettleShelfAndSeatPresentation(
            BsShelfSettleReason reason,
            Action settleShelfFlow)
        {
            // Always try seat cleanup too; it may still own the transferred load lock or reseat barrier.
            ExecutePresentationCleanup(
                null,
                settleShelfFlow,
                () => StopSeatAnimation(reason),
                null);
        }

        private void HandleBoardCommitted(BartenderBoardChange change)
        {
            if (!IsCurrentBoardChange(change)) return;
            bool deliveryChange =
                change.Cause == BsRoundTransitionCause.PlayerDelivery;
            if (deliveryChange != (change.DeliveryReceipt != null)) return;
            if (deliveryChange
                && !DeliveryMatchesBoardChange(change.DeliveryReceipt, change))
                return;
            if (presentationFlow.TryObserveBoardChange(
                    change.Revision, change)) return;
            if (!Ready || controller == null || controller.CurrentLevel == null) return;
            // LevelLoaded already built this revision. Replaying BoardCommitted would stop its entrance early.
            if (presentedBoardRevision == controller.BoardRevision) return;
            // Direct board changes refresh now. Pour animations wait until their own motion ends.
            if (TrySynchronize(
                    controller.Board, controller.Palette,
                    change.Revision, change.DeliveryReceipt))
                presentedBoardRevision = controller.BoardRevision;
        }

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state == BartenderLevelState.Unloaded
                || state == BartenderLevelState.CampaignComplete)
                ClearPresentation();
        }

        private bool TrySynchronize(
            BsBoard snapshot,
            BsPalette palette,
            int boardRevision,
            BartenderDeliveryReceipt deliveryReceipt)
        {
            if (snapshot == null) return Reject("Board snapshot boş.");
            if (palette == null || palette.Count == 0)
                return Reject("BsPalette Inspector/Resources bağlantısı eksik veya boş.");
            if (boardRevision < 0) return Reject("Board revision geçersiz.");

            BsShelfGarnishSynchronizationStart garnishStart =
                presentationFlow.BeginGarnishSynchronization(
                    this,
                    deliveryReceipt,
                    boardRevision,
                    Time.realtimeSinceStartupAsDouble + GarnishRevealWatchdog,
                    out BsShelfFlowToken garnishToken);
            if (garnishStart == BsShelfGarnishSynchronizationStart.Rejected)
                return false;
            bool synchronizationStarted =
                garnishStart == BsShelfGarnishSynchronizationStart.Started;
            bool synchronized = false;
            try
            {
                synchronized = TrySynchronizeCore(
                    snapshot, palette);
            }
            finally
            {
                if (synchronizationStarted)
                {
                    if (presentationFlow.TrySettleGarnishSynchronization(
                            this,
                            garnishToken,
                            deliveryReceipt,
                            boardRevision,
                            synchronized
                                ? BsShelfSettleReason.Completed
                                : BsShelfSettleReason.Cancelled,
                            out BsShelfFlowSettlement garnishSettlement))
                        HandleShelfFlowSettlement(garnishSettlement);
                }
            }
            // Order-ready is committed. Garnish effects finish locally without blocking board input.
            if (synchronized)
            {
                // Save the shown revision before listeners run, so a nested refresh sees it as current.
                presentedBoardRevision = boardRevision;
                PublishPresentationChangedSafely();
            }
            return synchronized;
        }

        private bool TrySynchronizeCore(
            BsBoard snapshot,
            BsPalette palette)
        {
            int previousRowCount = configuredRowCount;
            bool overflowWasActive = overflowShelfActive;
            Actor addedActor = null;
            activeActors.Clear();
            for (int i = 0; i < snapshot.Glasses.Count; i++)
            {
                RtGlass glass = snapshot.Glasses[i];
                if (!actorByGlassId.TryGetValue(glass.Id, out Actor actor))
                {
                    actor = Acquire(
                        glass.Type, IsPurchasedExtraGlass(presentedLevel, glass));
                    if (actor == null)
                        return Reject($"{BsRules.DisplayName(glass.Type)} scene havuzu tükendi.");
                    actor.Assigned = true;
                    actor.GlassId = glass.Id;
                    actorByGlassId.Add(glass.Id, actor);
                    glassIdByBottle.Add(actor.Bottle, glass.Id);
                    addedActor = actor;
                }
                else if (actor.Type != glass.Type)
                {
                    return Reject($"Bardak {glass.Id} tipi runtime sırasında değişti; "
                                + "sunum güvenli olarak temizlendi.");
                }

                actor.Assigned = true;
                activeActors.Add(actor);
                SetContents(actor.Bottle, glass, palette, snapshot.Delivered);
                SetGarnishReadinessForSnapshot(actor, snapshot, glass);
            }

            for (int i = 0; i < actors.Count; i++)
            {
                Actor actor = actors[i];
                if (!actor.Assigned || ContainsActor(activeActors, actor)) continue;
                Release(actor);
            }

            int mainGlassCount;
            int overflowGlassCount;
            try
            {
                mainGlassCount = ShelfLayoutSolver.MainShelfGlassCount(
                    activeActors.Count, configuredColumns);
                overflowGlassCount = ShelfLayoutSolver.OverflowGlassCount(
                    activeActors.Count, configuredColumns);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return Reject(exception.Message);
            }

            if (overflowGlassCount > 0)
            {
                if (!ValidateAuthoredOverflowAssets(out string overflowReason))
                    return Reject(overflowReason);
                if (!ValidateShelfLayoutAnchors(
                        ShelfLayoutSolver.MaximumRowCount, true,
                        out string layoutReason))
                    return Reject(layoutReason);
                // Once revealed, keep the shelf open for this round even if deliveries empty it.
                overflowShelfActive = true;
            }

            int neededRows = ShelfLayoutSolver.RequiredRowCount(
                mainGlassCount, configuredColumns);
            // Keep the level's row count. Deliveries move glasses without collapsing shelves.
            if (neededRows > configuredRowCount)
            {
                if (!ValidateShelfBindings(
                        neededRows, overflowShelfActive, out string reason))
                    return Reject(reason);
                configuredRowCount = neededRows;
            }

            bool revealedOverflow = !overflowWasActive && overflowShelfActive;
            bool revealedMainRow = configuredRowCount > previousRowCount;
            ShelfRowBinding revealedRow = null;
            ShelfSpanBinding revealedSpan = null;
            if (revealedOverflow)
            {
                revealedRow = overflowShelfRow;
                revealedSpan = showShelfPosts ? overflowShelfSpan : null;
            }
            else if (revealedMainRow)
            {
                revealedRow = shelfRows[configuredRowCount - 1];
                revealedSpan = showShelfPosts
                    ? shelfSpans[configuredRowCount - 2]
                    : null;
            }
            if (revealedOverflow || revealedMainRow)
            {
                StopSeatAnimation();
                CaptureShelfTransitionStart(previousRowCount, overflowWasActive);
                ApplyShelfLayout(configuredRowCount);
            }

            LayoutActiveActors(!(revealedOverflow || revealedMainRow));
            if (revealedMainRow && twoRowFocusApplied)
                RestoreBoardCompositionFit();
            if (revealedOverflow || revealedMainRow)
                PrepareShelfTransitionEndAndRestore();
            // Same-level commits change contents, not glass identity. Keep active garnish effects running;
            // only new actors need setup.
            ActivateAndRefreshActors(false);
            PlayReseat(addedActor, revealedRow, revealedSpan);
            return true;
        }

        private bool IsCurrentBoardChange(BartenderBoardChange change)
        {
            return controller != null && change != null
                   && change.AttemptId.IsValid
                   && change.OperationId.IsValid
                   && change.DomainRevision > 0L
                   && change.BoardRevision >= 0
                   && change.Cause != BsRoundTransitionCause.None
                   && controller.CurrentRoundStamp == new BsRoundCommandStamp(
                       change.AttemptId,
                       change.Token,
                       change.DomainRevision,
                       change.BoardRevision);
        }

        private static bool DeliveryMatchesBoardChange(
            BartenderDeliveryReceipt receipt,
            BartenderBoardChange change)
        {
            return receipt != null && change != null
                   && receipt.Cause == BsRoundTransitionCause.PlayerDelivery
                   && receipt.AttemptId == change.AttemptId
                   && receipt.OperationId == change.OperationId
                   && receipt.DomainRevision == change.DomainRevision
                   && receipt.BoardRevision == change.BoardRevision
                   && receipt.Cause == change.Cause
                   && receipt.Token == change.Token
                   && Nullable.Equals(
                       receipt.SettlementReceipt,
                       change.SettlementReceipt);
        }

        private static void SetGarnishReadinessForSnapshot(
            Actor actor,
            BsBoard snapshot,
            RtGlass glass)
        {
            actor.GarnishOrderReady = snapshot.MatchedSlot(glass) >= 0;
        }

        private bool ValidateSnapshot(BsLevel level, BsBoard snapshot, BsPalette palette,
                                      out int rowCount, out string reason)
        {
            rowCount = 0;
            if (level == null)
            {
                reason = "Level asseti boş.";
                return false;
            }
            if (snapshot == null)
            {
                reason = "Board snapshot boş.";
                return false;
            }
            if (palette == null || palette.Count == 0)
            {
                reason = "BsPalette Inspector/Resources bağlantısı eksik veya boş.";
                return false;
            }
            if (level.ColumnsPerRow <= 0 || level.ColumnsPerRow > MaximumColumnsPerRow)
            {
                reason = $"ColumnsPerRow 1-{MaximumColumnsPerRow} aralığında olmalı.";
                return false;
            }
            if (snapshot.Glasses.Count > MaximumActiveGlasses)
            {
                reason = $"Level {snapshot.Glasses.Count} bardak istiyor; statik aktif slot sınırı "
                       + $"{MaximumActiveGlasses}.";
                return false;
            }

            int shots = 0;
            int extraShots = 0;
            int cocktails = 0;
            int lattes = 0;
            int tumblers = 0;
            int biras = 0;
            for (int i = 0; i < snapshot.Glasses.Count; i++)
            {
                RtGlass glass = snapshot.Glasses[i];
                if (glass == null)
                {
                    reason = $"Board bardak {i} boş.";
                    return false;
                }
                bool purchasedExtra = IsPurchasedExtraGlass(level, glass);
                switch (glass.Type)
                {
                    case GlassType.Shot:
                        if (purchasedExtra) extraShots++;
                        else shots++;
                        break;
                    case GlassType.Kadeh: cocktails++; break;
                    case GlassType.Latte: lattes++; break;
                    case GlassType.Tumbler: tumblers++; break;
                    case GlassType.Bira: biras++; break;
                    default:
                        reason = $"Desteklenmeyen bardak tipi: {(int)glass.Type}.";
                        return false;
                }
            }

            int mainGlassCount;
            int overflowGlassCount;
            try
            {
                mainGlassCount = ShelfLayoutSolver.MainShelfGlassCount(
                    snapshot.Glasses.Count, level.ColumnsPerRow);
                overflowGlassCount = ShelfLayoutSolver.OverflowGlassCount(
                    snapshot.Glasses.Count, level.ColumnsPerRow);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                reason = exception.Message;
                return false;
            }

            if ((extraShots > 0 || overflowGlassCount > 0)
                && !ValidateAuthoredOverflowAssets(out reason)) return false;
            if (!ValidatePoolCount(shotPool, shots, GlassType.Shot, out reason)
                || !ValidatePoolCount(extraShotPool, extraShots, GlassType.Shot,
                    out reason)
                || !ValidatePoolCount(cocktailPool, cocktails, GlassType.Kadeh, out reason)
                || !ValidatePoolCount(lattePool, lattes, GlassType.Latte, out reason)
                || !ValidatePoolCount(tumblerPool, tumblers, GlassType.Tumbler, out reason)
                || !ValidatePoolCount(biraPool, biras, GlassType.Bira, out reason))
                return false;
            if (!EnsureActorCache(out reason)) return false;

            rowCount = ShelfLayoutSolver.RequiredRowCount(
                mainGlassCount, level.ColumnsPerRow);
            if (!ValidateShelfBindings(
                    rowCount, overflowGlassCount > 0, out reason)) return false;
            reason = null;
            return true;
        }

        private bool ValidateAuthoredOverflowAssets(out string reason)
        {
            int reserveCount = extraShotPool != null ? extraShotPool.Count : 0;
            if (reserveCount != ExtraShotReserveSize)
            {
                reason = $"Exactly {ExtraShotReserveSize} hand-authored extra-shot "
                       + $"bindings are required; {reserveCount} are connected.";
                return false;
            }

            if (overflowShelfRow == null || overflowShelfRow.plank == null
                || overflowShelfRow.plank.sprite == null
                || overflowShelfRow.seatAnchor == null
                || !overflowShelfRow.seatAnchor.IsChildOf(
                    overflowShelfRow.plank.transform))
            {
                reason = "The authored overflow shelf plank/seat-anchor binding is incomplete.";
                return false;
            }
            if (!overflowShelfRow.plank.gameObject.activeSelf)
            {
                reason = "The authored overflow shelf object must stay active; its renderers "
                       + "are hidden while the shelf is unused.";
                return false;
            }
            if (showShelfPosts
                && (overflowShelfSpan == null
                    || !ValidPost(overflowShelfSpan.leftPost)
                    || !ValidPost(overflowShelfSpan.rightPost)))
            {
                reason = "The authored overflow shelf requires left and right post bindings.";
                return false;
            }
            if (showShelfPosts
                && (!overflowShelfSpan.leftPost.gameObject.activeSelf
                    || !overflowShelfSpan.rightPost.gameObject.activeSelf))
            {
                reason = "Authored overflow post objects must stay active; their renderers "
                       + "are hidden while the shelf is unused.";
                return false;
            }

            // These checks apply to saved scenes. Reserve glasses and shelves can be temporarily active during
            // play.
            if (!Application.isPlaying)
            {
                for (int i = 0; i < extraShotPool.Count; i++)
                {
                    LiquidBottle reserve = extraShotPool[i] != null
                        ? extraShotPool[i].bottle
                        : null;
                    if (reserve == null || !reserve.gameObject.activeSelf) continue;
                    reason = $"Hand-authored extra-shot bottle '{reserve.name}' must start "
                           + "inactive; its active SeatRoot parent stays in the scene.";
                    return false;
                }

                bool overflowPostsEnabled = showShelfPosts
                    && (AnyShelfRendererEnabled(overflowShelfSpan.leftPost)
                        || AnyShelfRendererEnabled(overflowShelfSpan.rightPost));
                if (AnyShelfRendererEnabled(overflowShelfRow.plank)
                    || overflowPostsEnabled)
                {
                    reason = showShelfPosts
                        ? "Authored overflow shelf and post renderers must start disabled; "
                          + "their GameObjects remain active."
                        : "The authored overflow shelf renderers must start disabled; "
                          + "its GameObject remains active.";
                    return false;
                }
            }

            if (overflowSeatLayouts == null)
            {
                reason = "The authored overflow seat layouts are not connected.";
                return false;
            }
            uniqueOverflowSeatScratch.Clear();
            if (!ValidateOverflowSeatLayout(
                    overflowSeatLayouts.oneGlass, 1, uniqueOverflowSeatScratch,
                    out reason)
                || !ValidateOverflowSeatLayout(
                    overflowSeatLayouts.twoGlasses, 2, uniqueOverflowSeatScratch,
                    out reason)
                || !ValidateOverflowSeatLayout(
                    overflowSeatLayouts.threeGlasses, 3, uniqueOverflowSeatScratch,
                    out reason))
                return false;

            reason = null;
            return true;
        }

        private static bool AnyShelfRendererEnabled(SpriteRenderer root)
        {
            if (root == null) return false;
            SpriteRenderer[] renderers =
                root.GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null && renderers[i].enabled) return true;
            return false;
        }

        private bool ValidateOverflowSeatLayout(
            Transform[] seats, int expectedCount, HashSet<Transform> seen,
            out string reason)
        {
            int actualCount = seats != null ? seats.Length : 0;
            if (actualCount != expectedCount)
            {
                reason = $"Overflow {expectedCount}-glass layout requires exactly "
                       + $"{expectedCount} authored seat transform(s); {actualCount} "
                       + "are connected.";
                return false;
            }

            for (int i = 0; i < seats.Length; i++)
            {
                Transform seat = seats[i];
                if (seat == null)
                {
                    reason = $"Overflow {expectedCount}-glass seat {i + 1} is missing.";
                    return false;
                }
                if (!seat.IsChildOf(overflowShelfRow.plank.transform))
                {
                    reason = $"Overflow {expectedCount}-glass seat {i + 1} must be a "
                           + "child of the authored overflow plank.";
                    return false;
                }
                if (!seat.gameObject.activeSelf)
                {
                    reason = $"Overflow {expectedCount}-glass seat {i + 1} must stay active.";
                    return false;
                }
                if (!seen.Add(seat))
                {
                    reason = $"Overflow seat '{seat.name}' is connected more than once; "
                           + "each count-specific layout requires its own authored points.";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        private bool EnsureActorCache(out string reason)
        {
            if (actorCacheBuilt)
            {
                // Caches may survive Play Mode. Rebind existing direct parents without creating or repairing
                // the hierarchy.
                for (int i = 0; i < actors.Count; i++)
                {
                    Actor actor = actors[i];
                    if (actor == null || actor.Bottle == null)
                    {
                        reason = $"Authored glass actor cache entry {i} is missing.";
                        return false;
                    }

                    if (!TryResolveAuthoredSeatRoot(
                            actor.Bottle, out actor.SeatRoot, out reason))
                        return false;
                }
                reason = null;
                return true;
            }

            actors.Clear();
            uniqueBottleScratch.Clear();
            uniqueSeatRootScratch.Clear();
            if (!AddPoolToCache(shotPool, GlassType.Shot, false, out reason)
                || !AddPoolToCache(extraShotPool, GlassType.Shot, true, out reason)
                || !AddPoolToCache(cocktailPool, GlassType.Kadeh, false, out reason)
                || !AddPoolToCache(lattePool, GlassType.Latte, false, out reason)
                || !AddPoolToCache(tumblerPool, GlassType.Tumbler, false, out reason)
                || !AddPoolToCache(biraPool, GlassType.Bira, false, out reason))
                return false;

            actorCacheBuilt = true;
            reason = null;
            return true;
        }

        private bool AddPoolToCache(List<GlassBinding> pool, GlassType type,
                                    bool extraShotReserve, out string reason)
        {
            if (pool == null)
            {
                reason = BsRules.DisplayName(type) + " havuzu null.";
                return false;
            }
            for (int i = 0; i < pool.Count; i++)
            {
                GlassBinding binding = pool[i];
                if (!ValidateBottle(binding, type, i, out reason)) return false;
                LiquidBottle bottle = binding.bottle;
                if (!uniqueBottleScratch.Add(bottle))
                {
                    reason = $"'{bottle.name}' birden fazla scene havuzuna bağlanmış.";
                    return false;
                }
                Transform seatRoot = bottle.transform.parent;
                if (!uniqueSeatRootScratch.Add(seatRoot))
                {
                    reason = $"'{seatRoot.name}' birden fazla scene bardağına bağlanmış.";
                    return false;
                }
                actors.Add(new Actor
                {
                    Bottle = bottle,
                    SeatRoot = seatRoot,
                    PlacementRenderer = binding.placementRenderer,
                    Type = type,
                    ExtraShotReserve = extraShotReserve
                });
            }
            reason = null;
            return true;
        }

        private static Transform MotionRoot(Actor actor) =>
            actor != null ? actor.SeatRoot : null;

        private static void CanonicaliseBottleLocalPose(Actor actor)
        {
            if (actor == null || actor.Bottle == null || actor.Bottle.profile == null)
                return;
            Transform bottle = actor.Bottle.transform;
            VesselProfile profile = actor.Bottle.profile;
            bottle.localPosition = Vector3.zero;
            bottle.localRotation = profile.ShelfReferenceLocalRotation;
            bottle.localScale = profile.ShelfReferenceLocalScale;
        }

        private static bool ValidatePoolCount(List<GlassBinding> pool, int required,
                                              GlassType type, out string reason)
        {
            int count = pool != null ? pool.Count : 0;
            if (count < required)
            {
                reason = $"{BsRules.DisplayName(type)} havuzunda {required} scene objesi "
                       + $"gerekiyor, {count} bağlı.";
                return false;
            }
            reason = null;
            return true;
        }

        private static bool TryResolveAuthoredSeatRoot(
            LiquidBottle bottle, out Transform seatRoot, out string reason)
        {
            seatRoot = bottle != null ? bottle.transform.parent : null;
            if (seatRoot == null
                || !seatRoot.name.EndsWith(SeatRootSuffix, StringComparison.Ordinal)
                || seatRoot.childCount != 1
                || seatRoot.GetChild(0) != bottle.transform)
            {
                string bottleName = bottle != null ? bottle.name : "Missing Bottle";
                reason = $"'{bottleName}' must be the only direct child of its authored "
                       + $"'{SeatRootSuffix.Trim()}' placement root.";
                seatRoot = null;
                return false;
            }
            if (!seatRoot.gameObject.activeSelf)
            {
                reason = $"Authored placement root '{seatRoot.name}' must stay active; "
                       + "the pooled bottle child is what gets enabled and disabled.";
                seatRoot = null;
                return false;
            }

            reason = null;
            return true;
        }

        private static bool ValidateBottle(GlassBinding binding, GlassType type, int index,
                                           out string reason)
        {
            LiquidBottle bottle = binding != null ? binding.bottle : null;
            if (bottle == null)
            {
                reason = $"{BsRules.DisplayName(type)} havuzu [{index}] boş.";
                return false;
            }
            if (!TryResolveAuthoredSeatRoot(bottle, out _, out reason)) return false;
            if (bottle.profile == null || !bottle.profile.IsBaked
                || bottle.profile.front == null)
            {
                reason = $"'{bottle.name}' bake edilmiş Royal profile/front taşımıyor.";
                return false;
            }
            if (bottle.profile.shelfReferenceScale <= 0f)
            {
                reason = $"'{bottle.name}' profile shelf reference scale değeri geçersiz.";
                return false;
            }
            if (bottle.transform.localPosition.sqrMagnitude > 0.00000001f
                || Quaternion.Angle(
                    bottle.transform.localRotation,
                    bottle.profile.ShelfReferenceLocalRotation) > 0.01f
                || (bottle.transform.localScale
                    - bottle.profile.ShelfReferenceLocalScale).sqrMagnitude > 0.00000001f)
            {
                reason = $"'{bottle.name}' authored SeatRoot altında profile'ın shelf "
                       + "reference local pose'unda değil.";
                return false;
            }
            if (binding.placementRenderer == null
                || !binding.placementRenderer.transform.IsChildOf(bottle.transform))
            {
                reason = $"'{bottle.name}' doğrudan bağlı ön-görsel referansı eksik.";
                return false;
            }

            // Refresh the FrontGlass cache from VesselProfile before reading bounds. BottleShell applies the
            // matching material on activation.
            if (Application.isPlaying
                && binding.placementRenderer.sprite != bottle.profile.front)
                binding.placementRenderer.sprite = bottle.profile.front;

            // Saved scenes should report stale or incorrect references instead of silently fixing assets.
            if (binding.placementRenderer.sprite != bottle.profile.front)
            {
                reason = $"'{bottle.name}' ön-görseli güncel Royal profile ile eşleşmiyor.";
                return false;
            }
            int expected = BsRules.Capacity(type);
            if (bottle.profile.capacity != expected)
            {
                reason = $"'{bottle.name}' profile kapasitesi {bottle.profile.capacity}; "
                       + $"{BsRules.DisplayName(type)} için {expected} olmalı.";
                return false;
            }
            reason = null;
            return true;
        }

        private bool ValidateShelfLayoutAnchors(int rowCount,
                                                bool useOverflowLayout,
                                                out string reason)
        {
            if (rowCount < ShelfLayoutSolver.MinimumRowCount
                || rowCount > ShelfLayoutSolver.MaximumRowCount)
            {
                reason = $"Ana raf düzeni {ShelfLayoutSolver.MinimumRowCount}-"
                       + $"{ShelfLayoutSolver.MaximumRowCount} satır olmalı.";
                return false;
            }
            if (useOverflowLayout
                && rowCount != ShelfLayoutSolver.MaximumRowCount)
            {
                reason = "Dört raflı düzen yalnızca üç ana raf + overflow rafı olarak "
                       + "kullanılabilir.";
                return false;
            }

            Transform[] anchors = ShelfSurfaceAnchorsFor(
                rowCount, useOverflowLayout);
            int expected = rowCount + (useOverflowLayout ? 1 : 0);
            int actual = anchors != null ? anchors.Length : 0;
            if (actual != expected)
            {
                reason = $"Hierarchy'deki {expected}-raflı düzen tam {expected} yüzey "
                       + $"noktası istiyor; {actual} tane bağlı.";
                return false;
            }

            float previousY = float.PositiveInfinity;
            float commonX = 0f;
            for (int i = 0; i < anchors.Length; i++)
            {
                Transform anchor = anchors[i];
                if (anchor == null)
                {
                    reason = $"{expected}-raflı düzenin {i + 1}. yüzey noktası eksik.";
                    return false;
                }
                if (anchor == LayoutSpace || !anchor.IsChildOf(LayoutSpace))
                {
                    reason = $"Raf yüzey noktası '{anchor.name}' Layout Space altında "
                           + "authored bir Transform olmalı.";
                    return false;
                }
                if (IsUnderRuntimeDrivenShelfPiece(anchor))
                {
                    reason = $"Raf yüzey noktası '{anchor.name}' runtime'ın hareket ettirdiği "
                           + "bir raf/dikme altında olamaz; bağımsız Layout grubunda tutulmalı.";
                    return false;
                }

                Vector3 local = LayoutSpace.InverseTransformPoint(anchor.position);
                if (!IsFinite(local.x) || !IsFinite(local.y))
                {
                    reason = $"Raf yüzey noktası '{anchor.name}' geçerli bir x/y konumu "
                           + "taşımıyor.";
                    return false;
                }
                if (i == 0) commonX = local.x;
                else if (Mathf.Abs(local.x - commonX) > 0.001f)
                {
                    reason = $"{expected}-raflı düzende bütün yüzey noktaları aynı "
                           + "merkez X çizgisinde olmalı; grubu root Transform'dan taşı.";
                    return false;
                }
                if (local.y >= previousY)
                {
                    reason = $"{expected}-raflı yüzey noktaları Hierarchy referansında "
                           + "tepeden alta sıralanmalı.";
                    return false;
                }
                for (int earlier = 0; earlier < i; earlier++)
                {
                    if (anchors[earlier] != anchor) continue;
                    reason = $"Raf yüzey noktası '{anchor.name}' aynı düzende birden "
                           + "fazla kez bağlanmış.";
                    return false;
                }
                previousY = local.y;
            }

            reason = null;
            return true;
        }

        private Transform[] ShelfSurfaceAnchorsFor(int rowCount,
                                                   bool useOverflowLayout)
        {
            if (useOverflowLayout) return fourShelfSurfaceAnchors;
            return rowCount == ShelfLayoutSolver.MinimumRowCount
                ? twoShelfSurfaceAnchors
                : threeShelfSurfaceAnchors;
        }

        private bool IsUnderRuntimeDrivenShelfPiece(Transform anchor)
        {
            if (shelfRows != null)
            {
                for (int i = 0; i < shelfRows.Length; i++)
                {
                    SpriteRenderer plank = shelfRows[i] != null
                        ? shelfRows[i].plank
                        : null;
                    if (IsSelfOrDescendantOf(anchor,
                            plank != null ? plank.transform : null)) return true;
                }
            }

            if (IsSelfOrDescendantOf(anchor,
                    overflowShelfRow != null && overflowShelfRow.plank != null
                        ? overflowShelfRow.plank.transform
                        : null)) return true;

            if (shelfSpans != null)
            {
                for (int i = 0; i < shelfSpans.Length; i++)
                {
                    ShelfSpanBinding span = shelfSpans[i];
                    if (span == null) continue;
                    if (IsSelfOrDescendantOf(anchor,
                            span.leftPost != null ? span.leftPost.transform : null)
                        || IsSelfOrDescendantOf(anchor,
                            span.rightPost != null ? span.rightPost.transform : null))
                        return true;
                }
            }

            return overflowShelfSpan != null
                && (IsSelfOrDescendantOf(anchor,
                        overflowShelfSpan.leftPost != null
                            ? overflowShelfSpan.leftPost.transform
                            : null)
                    || IsSelfOrDescendantOf(anchor,
                        overflowShelfSpan.rightPost != null
                            ? overflowShelfSpan.rightPost.transform
                            : null));
        }

        private static bool IsSelfOrDescendantOf(Transform candidate, Transform root)
            => candidate != null && root != null
                && (candidate == root || candidate.IsChildOf(root));

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private bool ValidateShelfBindings(int rowCount, bool useOverflowLayout,
                                           out string reason)
        {
            if (layoutSpace == null)
            {
                reason = "Layout Space Inspector referansı eksik.";
                return false;
            }
            if (!ValidateShelfLayoutAnchors(
                    rowCount, useOverflowLayout, out reason)) return false;
            if (shelfRows == null || shelfRows.Length < rowCount)
            {
                reason = $"{rowCount} satır için yeterli plank binding yok.";
                return false;
            }
            for (int row = 0; row < rowCount; row++)
            {
                SpriteRenderer plank = shelfRows[row] != null ? shelfRows[row].plank : null;
                if (plank == null || plank.sprite == null)
                {
                    reason = $"Shelf row {row + 1} plank/sprite referansı eksik.";
                    return false;
                }
                if (shelfRows[row].seatAnchor == null
                    || !shelfRows[row].seatAnchor.IsChildOf(plank.transform))
                {
                    reason = $"Shelf row {row + 1} doğrudan bağlı üst-merkez noktası eksik.";
                    return false;
                }
            }

            // Floating shelves need no posts. Still check their planks, shadows and seat anchors.
            if (!showShelfPosts)
            {
                reason = null;
                return true;
            }

            // Every adjacent shelf pair needs exactly one supporting post span.
            int spanCount = Mathf.Max(0, rowCount - 1);
            if (shelfSpans == null || shelfSpans.Length < spanCount)
            {
                reason = $"{rowCount} satır için yeterli post-span binding yok.";
                return false;
            }
            for (int span = 0; span < spanCount; span++)
            {
                ShelfSpanBinding binding = shelfSpans[span];
                if (binding == null
                    || !ValidPost(binding.leftPost)
                    || !ValidPost(binding.rightPost))
                {
                    reason = $"Shelf span {span + 1} için iki post/sprite referansı gerekli.";
                    return false;
                }
            }
            reason = null;
            return true;
        }

        private static bool ValidPost(SpriteRenderer post) =>
            post != null && post.sprite != null;

        private static bool IsPurchasedExtraGlass(BsLevel level, RtGlass glass)
        {
            int authoredCount = level != null && level.Glasses != null
                ? level.Glasses.Count
                : 0;
            return glass != null && glass.Id >= authoredCount;
        }

        private Actor Acquire(GlassType type, bool purchasedExtra)
        {
            bool requiresReserve = purchasedExtra && type == GlassType.Shot;
            for (int i = 0; i < actors.Count; i++)
            {
                Actor actor = actors[i];
                if (!actor.Assigned && actor.Type == type
                    && actor.ExtraShotReserve == requiresReserve) return actor;
            }
            return null;
        }

        private void SetContents(LiquidBottle bottle, RtGlass glass, BsPalette palette,
                                 int delivered)
        {
            colorScratch.Clear();
            bool wholeGlassLocked = glass.IsChained(delivered);
            for (int i = 0; i < glass.Layers.Count; i++)
            {
                Layer layer = glass.Layers[i];
                bool concealed = layer.Hidden || layer.IsLocked(delivered);
                Color color = concealed
                    ? hiddenLayerColor
                    : palette.ColorAt(layer.Color);
                if (wholeGlassLocked)
                    color = DimLockPresentationColor(color, chainedGlassBrightness);
                colorScratch.Add(color);
            }
            // The level gives liquid units; the vessel asset gives capacity and waterlines.
            bottle.capacity = bottle.profile.capacity;
            bottle.SetUnits(colorScratch);
        }

        private static Color DimLockPresentationColor(Color source, float brightness)
        {
            // Keep a hint of the original colour so locked glasses look disabled.
            Color.RGBToHSV(source, out float hue, out float saturation, out float value);
            saturation *= 0.24f;
            Color dimmed = Color.HSVToRGB(
                hue, saturation, value * Mathf.Clamp01(brightness));
            dimmed.a = source.a;
            return dimmed;
        }

        private void LayoutActiveActors(bool stopAnimation = true)
        {
            // Stop tweens before layout moves a glass so both cannot write its pose.
            if (stopAnimation) StopSeatAnimation();

            int count = activeActors.Count;
            if (count == 0 || configuredRowCount <= 0) return;

            int mainCount = ShelfLayoutSolver.MainShelfGlassCount(
                count, configuredColumns);
            int overflowCount = ShelfLayoutSolver.OverflowGlassCount(
                count, configuredColumns);

            RememberCurrentSeats();

            // Runtime and the editor share one layout plan. This view turns its local seats into scene
            // transforms.
            ShelfRowTarget row0Target = MainShelfTarget(configuredRowCount, 0);
            ShelfRowTarget row1Target = MainShelfTarget(configuredRowCount, 1);
            ShelfRowTarget row2Target = configuredRowCount > 2
                ? MainShelfTarget(configuredRowCount, 2)
                : default;
            ShelfLayoutPlan plan = ShelfLayoutSolver.SolveForAnchoredRows(
                mainCount,
                configuredRowCount,
                row0Target,
                row1Target,
                row2Target,
                LayoutMetrics,
                appliedResponsiveShelfOffset);

            for (int row = 0; row < plan.RowCount; row++)
            {
                ShelfRowLayout rowPlan = plan.RowAt(row);
                for (int column = 0; column < rowPlan.ItemCount; column++)
                {
                    Actor actor = activeActors[rowPlan.ItemStart + column];
                    actor.Row = overflowShelfActive ? row + 1 : row;
                    actor.Column = column;
                    SeatActor(actor, rowPlan.XAt(column),
                        rowPlan.SeatY, plan.GlassScale);
                }
                CenterRowSilhouette(rowPlan.ItemStart, rowPlan.ItemCount,
                    rowPlan.ShelfCenterX);
            }

            if (overflowCount > 0)
            {
                float centerX = OverflowShelfTarget().CenterX;
                Transform[] seats = overflowSeatLayouts.SeatsForCount(overflowCount);
                float seatInset = ShelfLayoutSolver.SeatInset(
                    LayoutMetrics, plan.GlassScale);
                for (int column = 0; column < overflowCount; column++)
                {
                    Actor actor = activeActors[mainCount + column];
                    Vector3 authoredSurface = LayoutSpace.InverseTransformPoint(
                        seats[column].position);
                    actor.Row = 0;
                    actor.Column = column;
                    SeatActor(actor, authoredSurface.x,
                        authoredSurface.y - seatInset, plan.GlassScale);
                }
                CenterRowSilhouette(
                    mainCount, overflowCount, centerX);
            }

            RecordSeatPoses();
        }

        /// <summary>Saves old poses so glasses glide to their new seats. New glasses have no previous seat.</summary>
        private void RememberCurrentSeats()
        {
            for (int i = 0; i < activeActors.Count; i++)
            {
                Actor actor = activeActors[i];
                Transform actorTransform = MotionRoot(actor);
                actor.HasPreviousSeat = actor.Seated;
                actor.PreviousLayoutPosition = GlassSpace.InverseTransformPoint(
                    actorTransform.position);
                actor.PreviousLayoutRotation = Quaternion.Inverse(GlassSpace.rotation)
                                             * actorTransform.rotation;
                actor.PreviousSeatScale = actorTransform.localScale;
            }
        }

        /// <summary>Stores the final centred layout as the target pose for every animation.</summary>
        private void RecordSeatPoses()
        {
            for (int i = 0; i < activeActors.Count; i++)
            {
                Actor actor = activeActors[i];
                Transform actorTransform = MotionRoot(actor);
                actor.SeatLayoutPosition = GlassSpace.InverseTransformPoint(
                    actorTransform.position);
                actor.SeatLayoutRotation = Quaternion.Inverse(GlassSpace.rotation)
                                         * actorTransform.rotation;
                actor.SeatScale = actorTransform.localScale;
                actor.Seated = true;
            }
        }

        private Vector3 SeatWorldPosition(Actor actor) =>
            GlassSpace.TransformPoint(actor.SeatLayoutPosition);

        private Quaternion SeatWorldRotation(Actor actor) =>
            GlassSpace.rotation * actor.SeatLayoutRotation;

        private void SeatActor(Actor actor, float slotCenterX, float surfaceY, float scale)
        {
            Transform actorTransform = MotionRoot(actor);
            VesselProfile profile = actor.Bottle.profile;
            CanonicaliseBottleLocalPose(actor);
            actorTransform.localScale = Vector3.one * scale;
            actorTransform.rotation = GlassSpace.rotation;

            Vector3 desiredFoot = GlassSpace.TransformPoint(
                new Vector3(slotCenterX, surfaceY, glassPlaneZ));
            Transform assetSpace = actor.PlacementRenderer != null
                ? actor.PlacementRenderer.transform
                : actor.Bottle.transform;
            Vector2 support = profile.SupportLocal;
            actorTransform.position = VesselPresentationMath.RootPositionForAnchoredPoint(
                actorTransform, assetSpace, new Vector3(support.x, support.y, 0f),
                desiredFoot);
        }

        /// <summary>Finds shelf contact from the vessel asset after applying its final scale and rotation.</summary>
        private static Vector3 AssetSupportWorld(Actor actor)
        {
            VesselProfile profile = actor.Bottle.profile;
            Vector2 support = profile.SupportLocal;
            Transform assetSpace = actor.PlacementRenderer != null
                ? actor.PlacementRenderer.transform
                : actor.Bottle.transform;
            Vector3 world = assetSpace.TransformPoint(
                new Vector3(support.x, support.y, 0f));
            return world;
        }

        private void CenterRowSilhouette(int start, int count, float shelfCenterX)
        {
            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            for (int i = start; i < start + count; i++)
            {
                SpriteRenderer renderer = activeActors[i].PlacementRenderer;
                Bounds bounds = renderer.sprite.bounds;
                AddLayoutX(renderer.transform, bounds.min.x, bounds.min.y,
                    ref minX, ref maxX);
                AddLayoutX(renderer.transform, bounds.min.x, bounds.max.y,
                    ref minX, ref maxX);
                AddLayoutX(renderer.transform, bounds.max.x, bounds.min.y,
                    ref minX, ref maxX);
                AddLayoutX(renderer.transform, bounds.max.x, bounds.max.y,
                    ref minX, ref maxX);
            }

            if (float.IsInfinity(minX) || float.IsInfinity(maxX)) return;
            float shift = shelfCenterX - (minX + maxX) * 0.5f;
            if (Mathf.Abs(shift) < 0.0001f) return;

            for (int i = start; i < start + count; i++)
            {
                Transform root = MotionRoot(activeActors[i]);
                Vector3 local = GlassSpace.InverseTransformPoint(root.position);
                local.x += shift;
                root.position = GlassSpace.TransformPoint(local);
            }
        }

        private void AddLayoutX(Transform rendererTransform, float x, float y,
                                ref float minX, ref float maxX)
        {
            float layoutX = GlassSpace.InverseTransformPoint(
                rendererTransform.TransformPoint(new Vector3(x, y, 0f))).x;
            minX = Mathf.Min(minX, layoutX);
            maxX = Mathf.Max(maxX, layoutX);
        }

        private void ActivateAndRefreshActors(
            bool resetReusedPresentation = true)
        {
            for (int i = 0; i < activeActors.Count; i++)
                ActivateAndRefreshActor(
                    activeActors[i], resetReusedPresentation);
        }

        private static void ActivateAndRefreshActor(Actor actor,
            bool resetReusedPresentation = true)
        {
            LiquidBottle bottle = actor != null ? actor.Bottle : null;
            if (bottle == null) return;
            BottleShell shell = bottle.GetComponent<BottleShell>();
            bool reusedWhileActive = bottle.gameObject.activeSelf;
            if (!reusedWhileActive)
            {
                bottle.gameObject.SetActive(true);
            }
            else if (resetReusedPresentation)
            {
                // Reuse active vessels during covered loads, but still reset the state normally cleared by
                // OnDisable/OnEnable.
                if (shell != null)
                {
                    shell.highlight = 0f;
                    shell.StopCompletionPresentation();
                }
                else
                {
                    bottle.ClearCompletionEffect();
                }
                VesselRimGarnish.Ensure(bottle);
                VesselFloatingGarnish.Ensure(bottle);
            }
            // Refresh the shell first so the liquid uses its latest geometry. Then refresh the liquid once
            // without clearing its new quad.
            shell?.Refresh();
            // Rebuild geometry only when the profile or baked assets change. Otherwise reuse the quad and
            // update liquid state.
            bottle.Refresh();
            ApplyGarnishReadiness(actor);
        }

        private static void ApplyGarnishReadiness(Actor actor)
        {
            LiquidBottle bottle = actor != null ? actor.Bottle : null;
            if (bottle == null) return;
            VesselRimGarnish.SetOrderReady(bottle, actor.GarnishOrderReady);
            VesselFloatingGarnish.SetOrderReady(
                bottle, actor.GarnishOrderReady);
        }

        // Level entrance: animate the layout's finished poses. Edit Mode and disabled animations keep that
        // same layout.

        private bool CanAnimate =>
            Application.isPlaying && isActiveAndEnabled && gameObject.activeInHierarchy;

        /// <summary>
        /// Drops glasses from the shared release line. A visible cover holds them there until the player can
        /// see the fall.
        /// </summary>
        private SeatPresentationRun PlayEntrance(
            BartenderLoadingOverlayPresenter cover)
        {
            if (!CanAnimate || !animateEntrance || activeActors.Count == 0)
                return null;
            StopSeatAnimation();

            OrderActorsForEntrance();
            float total = MeasureEntranceFalls();
            if (!(total > 0f) || float.IsNaN(total) || float.IsInfinity(total))
            {
                SnapActorsToSeat();
                return null;
            }

            bool waitsForCover = cover != null && cover.Visible;
            SeatPresentationRun run = BeginSeatPresentation(
                Time.realtimeSinceStartupAsDouble
                + (waitsForCover ? CoveredEntranceHandoffTimeout : 0d)
                + total + SeatAnimationWatchdogGrace);
            if (run == null)
            {
                SnapActorsToSeat();
                return null;
            }
            BeginShelfFade();
            PrepareEntranceActors();
            if (!TryStartSeatPresentationRoutine(
                    run,
                    EntranceRoutine(
                        run, total, waitsForCover ? cover : null)))
                return null;
            return run;
        }

        /// <summary>
        /// Slides existing glasses to new seats, lands the purchased glass and unfolds any new shelf. This
        /// works even if level entrances are off.
        /// </summary>
        private void PlayReseat(Actor addedActor, ShelfRowBinding revealedRow,
                                ShelfSpanBinding revealedSpan)
        {
            bool revealsShelf = revealedRow != null && revealedRow.plank != null;
            bool animatesPurchase = addedActor != null;
            bool animatesFurniture = shelfTransitionPrepared;
            if (!CanAnimate || reseatDuration <= 0f
                || (!animateEntrance && !animatesPurchase && !revealsShelf))
            {
                FinishShelfTransition();
                return;
            }

            bool moved = false;
            for (int i = 0; i < activeActors.Count && !moved; i++)
            {
                Actor actor = activeActors[i];
                moved = actor.HasPreviousSeat
                    && ((actor.PreviousLayoutPosition - actor.SeatLayoutPosition).sqrMagnitude > 1e-6f
                        || Quaternion.Angle(actor.PreviousLayoutRotation,
                                            actor.SeatLayoutRotation) > 0.01f
                        || (actor.PreviousSeatScale - actor.SeatScale).sqrMagnitude > 1e-6f);
            }
            if (!moved && !animatesPurchase && !revealsShelf && !animatesFurniture)
            {
                FinishShelfTransition();
                return;
            }

            float duration = revealsShelf
                ? Mathf.Max(reseatDuration, overflowRevealDuration)
                : Mathf.Max(reseatDuration, 0.28f);
            Vector3 addedStartPosition = Vector3.zero;
            Vector3 addedStartScale = Vector3.one;
            if (animatesPurchase)
            {
                addedStartPosition = addedActor.SeatLayoutPosition
                                   + Vector3.up * purchasedGlassDropHeight;
                addedStartScale = addedActor.SeatScale * 0.72f;
                Transform actorTransform = MotionRoot(addedActor);
                actorTransform.position = GlassSpace.TransformPoint(addedStartPosition);
                actorTransform.rotation = SeatWorldRotation(addedActor);
                actorTransform.localScale = addedStartScale;
                LiftEntranceSorting(addedActor);
            }
            if (revealsShelf) PrepareShelfReveal(revealedRow, revealedSpan);

            SeatPresentationRun run = BeginSeatPresentation(
                Time.realtimeSinceStartupAsDouble
                + duration + SeatAnimationWatchdogGrace);
            if (run == null)
            {
                FinishSeatPresentationVisuals();
                return;
            }
            BartenderLevelController barrierController = controller;
            if (barrierController != null
                && barrierController.AcquirePresentationBarrier(this)
                && !run.TryAttachBarrier(barrierController, this))
            {
                ExecutePresentationCleanup(
                    null,
                    () => TrySettleSeatPresentation(
                        run, BsShelfSettleReason.Cancelled, false),
                    null,
                    () => barrierController.ReleasePresentationBarrier(this));
                return;
            }
            // Start the purchase sound with the fall; its impact is at 0.28 seconds. Level loads do not play
            // it.
            if (animatesPurchase) BsAudio.Instance?.Play(BsSfx.GlassArrive, 0.9f);

            TryStartSeatPresentationRoutine(
                run,
                ReseatRoutine(
                    run, addedActor, addedStartPosition, addedStartScale, duration));
        }

        private IEnumerator EntranceRoutine(
            SeatPresentationRun run,
            float total,
            BartenderLoadingOverlayPresenter cover)
        {
            bool completed = false;
            try
            {
                // Wait behind the opaque cover so the whole drop stays visible after it hides.
                if (cover != null)
                {
                    double handoffDeadline = Time.realtimeSinceStartupAsDouble
                                           + CoveredEntranceHandoffTimeout;
                    while (cover != null && cover.Visible
                           && Time.realtimeSinceStartupAsDouble < handoffDeadline)
                    {
                        if (!ReferenceEquals(activeSeatPresentation, run) || !CanAnimate) yield break;
                        yield return null;
                    }

                    // Restart the timeout for the fall that begins now.
                    run.TrySetDeadline(
                        Time.realtimeSinceStartupAsDouble
                        + total + SeatAnimationWatchdogGrace);
                }

                float elapsed = 0f;
                while (elapsed < total)
                {
                    if (!ReferenceEquals(activeSeatPresentation, run) || !CanAnimate) yield break;
                    StepShelfFade(elapsed);
                    Vector3 layoutUp = GlassSpace.TransformVector(Vector3.up);
                    for (int i = 0; i < entranceOrder.Count; i++)
                    {
                        Actor actor = entranceOrder[i];
                        StepEntranceActor(actor, elapsed - actor.EntranceDelay, layoutUp);
                    }
                    yield return null;
                    // I use unscaled time so pausing cannot leave a glass in the air.
                    elapsed += Time.unscaledDeltaTime;
                }
                completed = true;
            }
            finally
            {
                TrySettleSeatPresentation(
                    run,
                    completed
                        ? BsShelfSettleReason.Completed
                        : BsShelfSettleReason.Cancelled,
                    false);
            }
        }

        /// <summary>
        /// Drops every glass from one line above the frame with the same acceleration. Lower glasses fall
        /// longer; returns the total entrance time.
        /// </summary>
        private float MeasureEntranceFalls()
        {
            // Clamp here too because an open Editor can write back an older saved value.
            float effectiveDropHeight = Mathf.Max(MinimumEntranceDropHeight,
                                                  entranceDropHeight);
            float topSurface = overflowShelfActive
                ? OverflowShelfTarget().SurfaceY + appliedResponsiveShelfOffset
                : SurfaceY(configuredRowCount, 0);
            float releaseY = topSurface + effectiveDropHeight;
            float total = 0f;
            for (int i = 0; i < entranceOrder.Count; i++)
            {
                Actor actor = entranceOrder[i];
                // Measure from the glass foot, since the root may sit anywhere in the artwork.
                float footY = GlassSpace.InverseTransformPoint(
                    AssetSupportWorld(actor)).y;
                actor.EntranceFallDistance = Mathf.Max(0.01f, releaseY - footY);
                actor.EntranceFallDuration = entranceDropDuration
                    * Mathf.Sqrt(actor.EntranceFallDistance / effectiveDropHeight);
                total = Mathf.Max(total, actor.EntranceDelay + actor.EntranceFallDuration
                                       + entranceSettleDuration);
            }
            return total;
        }

        /// <summary>
        /// Moves active glasses to the off-screen release line. Keep them enabled so the entrance does not
        /// discard and rebuild their art.
        /// </summary>
        private void PrepareEntranceActors()
        {
            Vector3 layoutUp = GlassSpace.TransformVector(Vector3.up);
            for (int i = 0; i < entranceOrder.Count; i++)
            {
                Actor actor = entranceOrder[i];
                GameObject actorObject = actor.Bottle.gameObject;
                if (!actorObject.activeSelf)
                {
                    actorObject.SetActive(true);
                    actor.Bottle.Refresh();
                }

                DropEntranceSorting(actor);
                Transform actorTransform = MotionRoot(actor);
                actorTransform.position = SeatWorldPosition(actor)
                    + layoutUp * actor.EntranceFallDistance;
                actorTransform.rotation = SeatWorldRotation(actor);
                actorTransform.localScale = actor.SeatScale;
            }
        }

        private void StepEntranceActor(Actor actor, float time, Vector3 layoutUp)
        {
            Transform actorTransform = MotionRoot(actor);
            GameObject actorObject = actor.Bottle.gameObject;

            if (time < 0f)
            {
                // Keep it active here so the next drop can reuse the generated shell.
                actorTransform.position = SeatWorldPosition(actor)
                    + layoutUp * actor.EntranceFallDistance;
                actorTransform.rotation = SeatWorldRotation(actor);
                actorTransform.localScale = actor.SeatScale;
                return;
            }
            if (!actorObject.activeSelf)
            {
                // Restore the actor if something else hid it during the entrance.
                actorObject.SetActive(true);
                actor.Bottle.Refresh();
            }

            if (time < actor.EntranceFallDuration)
            {
                LiftEntranceSorting(actor);
                // A quadratic curve makes the glass fall like gravity.
                float fall = time / actor.EntranceFallDuration;
                actorTransform.position = SeatWorldPosition(actor)
                    + layoutUp * (actor.EntranceFallDistance * (1f - fall * fall));
                actorTransform.rotation = SeatWorldRotation(actor);
                actorTransform.localScale = actor.SeatScale;
                return;
            }

            DropEntranceSorting(actor);
            actorTransform.SetPositionAndRotation(
                SeatWorldPosition(actor), SeatWorldRotation(actor));
            actorTransform.localScale =
                LandingScale(actor.SeatScale, time - actor.EntranceFallDuration);
        }

        /// <summary>
        /// Raises falling glasses above the shelves every frame. BottleShell resets draw order after enabling,
        /// so one update is not enough.
        /// </summary>
        private void LiftEntranceSorting(Actor actor)
        {
            if (entranceSortingBoost <= 0) return;
            actor.SortingLifted = true;
            actor.Bottle.SetSortingOffset(entranceSortingBoost);
        }

        private static void DropEntranceSorting(Actor actor)
        {
            if (!actor.SortingLifted) return;
            actor.SortingLifted = false;
            if (actor.Bottle != null) actor.Bottle.SetSortingOffset(0);
        }

        /// <summary>Adds one damped squash on landing, then restores the exact seat scale.</summary>
        private Vector3 LandingScale(Vector3 seatScale, float sinceLanding)
        {
            if (entranceLandingSquash <= 0f || entranceSettleDuration <= 0f) return seatScale;
            float life = sinceLanding / entranceSettleDuration;
            if (life >= 1f) return seatScale;

            float amount = entranceLandingSquash * (1f - life)
                         * Mathf.Cos(life * Mathf.PI * 1.5f);
            return new Vector3(seatScale.x * (1f + amount),
                               seatScale.y * (1f - amount),
                               seatScale.z);
        }

        private IEnumerator ReseatRoutine(SeatPresentationRun run,
                                          Actor addedActor,
                                          Vector3 addedStartPosition,
                                          Vector3 addedStartScale,
                                          float duration)
        {
            bool completed = false;
            try
            {
                float elapsed = 0f;
                while (elapsed < duration)
                {
                    if (!ReferenceEquals(activeSeatPresentation, run) || !CanAnimate) yield break;
                    float raw = Mathf.Clamp01(elapsed / duration);
                    float k = Mathf.SmoothStep(0f, 1f, raw);
                    StepShelfReveal(raw);
                    StepShelfTransition(k);
                    for (int i = 0; i < activeActors.Count; i++)
                    {
                        Actor actor = activeActors[i];
                        if (!actor.HasPreviousSeat || ReferenceEquals(actor, addedActor))
                            continue;
                        Transform actorTransform = MotionRoot(actor);
                        Vector3 layoutPosition = Vector3.Lerp(
                            actor.PreviousLayoutPosition, actor.SeatLayoutPosition, k);
                        Quaternion layoutRotation = Quaternion.Slerp(
                            actor.PreviousLayoutRotation, actor.SeatLayoutRotation, k);
                        actorTransform.position = GlassSpace.TransformPoint(layoutPosition);
                        actorTransform.rotation = GlassSpace.rotation * layoutRotation;
                        actorTransform.localScale = Vector3.Lerp(
                            actor.PreviousSeatScale, actor.SeatScale, k);
                    }
                    if (addedActor != null)
                    {
                        float landing = 1f - Mathf.Pow(1f - raw, 3f);
                        Transform actorTransform = MotionRoot(addedActor);
                        Vector3 layoutPosition = Vector3.LerpUnclamped(
                            addedStartPosition, addedActor.SeatLayoutPosition, landing);
                        actorTransform.position = GlassSpace.TransformPoint(layoutPosition);
                        actorTransform.rotation = SeatWorldRotation(addedActor);
                        float bounce = 1f + 0.08f * Mathf.Sin(raw * Mathf.PI)
                                     * (1f - raw);
                        actorTransform.localScale = Vector3.Lerp(
                            addedStartScale, addedActor.SeatScale, k) * bounce;
                    }
                    yield return null;
                    elapsed += Time.unscaledDeltaTime;
                }
                completed = true;
            }
            finally
            {
                TrySettleSeatPresentation(
                    run,
                    completed
                        ? BsShelfSettleReason.Completed
                        : BsShelfSettleReason.Cancelled,
                    false);
            }
        }

        /// <summary>
        /// Saves visible furniture before adding a row so it can move with the glasses. The new row has its own
        /// unfold.
        /// </summary>
        private void CaptureShelfTransitionStart(int rowCount, bool includeOverflow)
        {
            FinishShelfTransition();
            if (layoutSpace != null)
            {
                shelfTransitionStartBoardPosition = layoutSpace.localPosition;
                shelfTransitionStartBoardScale = layoutSpace.localScale;
            }
            int rows = Mathf.Min(Mathf.Max(0, rowCount),
                shelfRows != null ? shelfRows.Length : 0);
            for (int row = 0; row < rows; row++)
                AddShelfTransitionStart(
                    shelfRows[row] != null ? shelfRows[row].plank : null);

            if (showShelfPosts)
            {
                int spans = Mathf.Min(Mathf.Max(0, rowCount - 1),
                    shelfSpans != null ? shelfSpans.Length : 0);
                for (int span = 0; span < spans; span++)
                {
                    ShelfSpanBinding binding = shelfSpans[span];
                    if (binding == null) continue;
                    AddShelfTransitionStart(binding.leftPost);
                    AddShelfTransitionStart(binding.rightPost);
                }
            }

            if (!includeOverflow) return;
            AddShelfTransitionStart(
                overflowShelfRow != null ? overflowShelfRow.plank : null);
            if (!showShelfPosts || overflowShelfSpan == null) return;
            AddShelfTransitionStart(overflowShelfSpan.leftPost);
            AddShelfTransitionStart(overflowShelfSpan.rightPost);
        }

        private void AddShelfTransitionStart(SpriteRenderer renderer)
        {
            if (renderer == null) return;
            for (int i = 0; i < shelfTransitionStates.Count; i++)
                if (shelfTransitionStates[i].Renderer == renderer) return;

            Transform target = renderer.transform;
            shelfTransitionStates.Add(new ShelfTransitionState
            {
                Renderer = renderer,
                StartLocalPosition = target.localPosition,
                StartLocalRotation = target.localRotation,
                StartLocalScale = target.localScale,
                StartSize = renderer.size,
                FinalLocalPosition = target.localPosition,
                FinalLocalRotation = target.localRotation,
                FinalLocalScale = target.localScale,
                FinalSize = renderer.size
            });
        }

        /// <summary>
        /// Saves the final furniture pose, then restores the start pose for the tween. Glass targets already
        /// use the finished layout.
        /// </summary>
        private void PrepareShelfTransitionEndAndRestore()
        {
            if (layoutSpace != null)
            {
                shelfTransitionFinalBoardPosition = layoutSpace.localPosition;
                shelfTransitionFinalBoardScale = layoutSpace.localScale;
                shelfBoardTransitionPrepared =
                    (shelfTransitionStartBoardPosition - shelfTransitionFinalBoardPosition).sqrMagnitude > 1e-8f
                    || (shelfTransitionStartBoardScale - shelfTransitionFinalBoardScale).sqrMagnitude > 1e-8f;
                if (shelfBoardTransitionPrepared)
                {
                    layoutSpace.localPosition = shelfTransitionStartBoardPosition;
                    layoutSpace.localScale = shelfTransitionStartBoardScale;
                }
            }
            for (int i = 0; i < shelfTransitionStates.Count; i++)
            {
                ShelfTransitionState state = shelfTransitionStates[i];
                if (state.Renderer == null) continue;
                Transform target = state.Renderer.transform;
                state.FinalLocalPosition = target.localPosition;
                state.FinalLocalRotation = target.localRotation;
                state.FinalLocalScale = target.localScale;
                state.FinalSize = state.Renderer.size;
                target.localPosition = state.StartLocalPosition;
                target.localRotation = state.StartLocalRotation;
                target.localScale = state.StartLocalScale;
                state.Renderer.size = state.StartSize;
            }
            shelfTransitionPrepared = shelfTransitionStates.Count > 0;
        }

        private void StepShelfTransition(float progress)
        {
            if (!shelfTransitionPrepared) return;
            float k = Mathf.Clamp01(progress);
            if (shelfBoardTransitionPrepared && layoutSpace != null)
            {
                layoutSpace.localPosition = Vector3.LerpUnclamped(
                    shelfTransitionStartBoardPosition, shelfTransitionFinalBoardPosition, k);
                layoutSpace.localScale = Vector3.LerpUnclamped(
                    shelfTransitionStartBoardScale, shelfTransitionFinalBoardScale, k);
            }
            for (int i = 0; i < shelfTransitionStates.Count; i++)
            {
                ShelfTransitionState state = shelfTransitionStates[i];
                if (state.Renderer == null) continue;
                Transform target = state.Renderer.transform;
                target.localPosition = Vector3.LerpUnclamped(
                    state.StartLocalPosition, state.FinalLocalPosition, k);
                target.localRotation = Quaternion.SlerpUnclamped(
                    state.StartLocalRotation, state.FinalLocalRotation, k);
                target.localScale = Vector3.LerpUnclamped(
                    state.StartLocalScale, state.FinalLocalScale, k);
                state.Renderer.size = Vector2.LerpUnclamped(
                    state.StartSize, state.FinalSize, k);
            }
        }

        private void FinishShelfTransition()
        {
            if (shelfBoardTransitionPrepared && layoutSpace != null)
            {
                layoutSpace.localPosition = shelfTransitionFinalBoardPosition;
                layoutSpace.localScale = shelfTransitionFinalBoardScale;
            }
            shelfBoardTransitionPrepared = false;
            if (shelfTransitionPrepared)
            {
                for (int i = 0; i < shelfTransitionStates.Count; i++)
                {
                    ShelfTransitionState state = shelfTransitionStates[i];
                    if (state.Renderer == null) continue;
                    Transform target = state.Renderer.transform;
                    target.localPosition = state.FinalLocalPosition;
                    target.localRotation = state.FinalLocalRotation;
                    target.localScale = state.FinalLocalScale;
                    state.Renderer.size = state.FinalSize;
                }
            }
            shelfTransitionStates.Clear();
            shelfTransitionPrepared = false;
        }

        private void PrepareShelfReveal(ShelfRowBinding row, ShelfSpanBinding span)
        {
            FinishShelfReveal();
            revealingShelfPlank = row != null ? row.plank : null;
            revealingShelfLeftPost = span != null ? span.leftPost : null;
            revealingShelfRightPost = span != null ? span.rightPost : null;
            if (revealingShelfPlank == null) return;

            revealingPlankFinalScale = revealingShelfPlank.transform.localScale;
            revealingLeftPostFinalScale = revealingShelfLeftPost != null
                ? revealingShelfLeftPost.transform.localScale
                : Vector3.one;
            revealingRightPostFinalScale = revealingShelfRightPost != null
                ? revealingShelfRightPost.transform.localScale
                : Vector3.one;
            CollectShelfRevealRenderers(revealingShelfPlank);
            CollectShelfRevealRenderers(revealingShelfLeftPost);
            CollectShelfRevealRenderers(revealingShelfRightPost);

            Vector3 plankStart = revealingPlankFinalScale;
            plankStart.x *= 0.22f;
            plankStart.y *= 0.84f;
            revealingShelfPlank.transform.localScale = plankStart;
            SetPostRevealScale(revealingShelfLeftPost,
                revealingLeftPostFinalScale, 0.12f);
            SetPostRevealScale(revealingShelfRightPost,
                revealingRightPostFinalScale, 0.12f);
            for (int i = 0; i < shelfRevealRenderers.Count; i++)
            {
                SpriteRenderer renderer = shelfRevealRenderers[i];
                if (renderer == null) continue;
                Color hidden = shelfRevealColors[i];
                hidden.a = 0f;
                renderer.color = hidden;
            }
            shelfRevealPrepared = true;
        }

        private void CollectShelfRevealRenderers(SpriteRenderer root)
        {
            if (root == null) return;
            foreach (SpriteRenderer renderer in
                     root.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (!renderer.enabled) continue;
                shelfRevealRenderers.Add(renderer);
                shelfRevealColors.Add(renderer.color);
            }
        }

        private void StepShelfReveal(float progress)
        {
            if (!shelfRevealPrepared) return;
            float smooth = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(progress));
            float back = EaseOutBack(Mathf.Clamp01(progress));
            Vector3 plankScale = revealingPlankFinalScale;
            plankScale.x *= Mathf.LerpUnclamped(0.22f, 1f, back);
            plankScale.y *= Mathf.Lerp(0.84f, 1f, smooth);
            revealingShelfPlank.transform.localScale = plankScale;
            SetPostRevealScale(revealingShelfLeftPost,
                revealingLeftPostFinalScale, Mathf.Lerp(0.12f, 1f, smooth));
            SetPostRevealScale(revealingShelfRightPost,
                revealingRightPostFinalScale, Mathf.Lerp(0.12f, 1f, smooth));
            for (int i = 0; i < shelfRevealRenderers.Count; i++)
            {
                SpriteRenderer renderer = shelfRevealRenderers[i];
                if (renderer == null) continue;
                Color color = shelfRevealColors[i];
                color.a *= smooth;
                renderer.color = color;
            }
        }

        private static void SetPostRevealScale(
            SpriteRenderer post, Vector3 finalScale, float verticalFactor)
        {
            if (post == null) return;
            Vector3 scale = finalScale;
            scale.y *= verticalFactor;
            post.transform.localScale = scale;
        }

        private static float EaseOutBack(float value)
        {
            const float overshoot = 1.35f;
            float shifted = value - 1f;
            return 1f + (overshoot + 1f) * shifted * shifted * shifted
                     + overshoot * shifted * shifted;
        }

        private void FinishShelfReveal()
        {
            if (shelfRevealPrepared)
            {
                if (revealingShelfPlank != null)
                    revealingShelfPlank.transform.localScale = revealingPlankFinalScale;
                if (revealingShelfLeftPost != null)
                    revealingShelfLeftPost.transform.localScale =
                        revealingLeftPostFinalScale;
                if (revealingShelfRightPost != null)
                    revealingShelfRightPost.transform.localScale =
                        revealingRightPostFinalScale;
            }
            for (int i = 0; i < shelfRevealRenderers.Count; i++)
                if (shelfRevealRenderers[i] != null)
                    shelfRevealRenderers[i].color = shelfRevealColors[i];
            shelfRevealRenderers.Clear();
            shelfRevealColors.Clear();
            revealingShelfPlank = null;
            revealingShelfLeftPost = null;
            revealingShelfRightPost = null;
            shelfRevealPrepared = false;
        }

        /// <summary>Ends on the exact layout pose, even if the coroutine stopped early.</summary>
        private void SnapActorsToSeat()
        {
            for (int i = 0; i < activeActors.Count; i++)
            {
                Actor actor = activeActors[i];
                DropEntranceSorting(actor);
                if (actor.Bottle == null || !actor.Seated) continue;
                GameObject actorObject = actor.Bottle.gameObject;
                if (!actorObject.activeSelf) actorObject.SetActive(true);
                Transform actorTransform = MotionRoot(actor);
                actorTransform.SetPositionAndRotation(
                    SeatWorldPosition(actor), SeatWorldRotation(actor));
                actorTransform.localScale = actor.SeatScale;
            }
        }

        private SeatPresentationRun BeginSeatPresentation(double deadline)
        {
            if (activeSeatPresentation != null
                || nextSeatPresentationRunId == long.MaxValue)
                return null;
            nextSeatPresentationRunId++;
            var run = new SeatPresentationRun(
                nextSeatPresentationRunId, deadline);
            activeSeatPresentation = run;
            return run;
        }

        private bool TryStartSeatPresentationRoutine(
            SeatPresentationRun run,
            IEnumerator routine)
        {
            if (run == null || routine == null
                || !ReferenceEquals(activeSeatPresentation, run)) return false;

            Coroutine started;
            try
            {
                started = StartCoroutine(routine);
            }
            catch
            {
                TrySettleSeatPresentation(
                    run, BsShelfSettleReason.Cancelled, false);
                throw;
            }

            if (started != null
                && ReferenceEquals(activeSeatPresentation, run)
                && run.TryAttachRoutine(started)) return true;

            ExecutePresentationCleanup(
                started != null ? (Action)(() => StopCoroutine(started)) : null,
                () => TrySettleSeatPresentation(
                    run, BsShelfSettleReason.Cancelled, false),
                null,
                null);
            return false;
        }

        internal static bool TryClaimSeatPresentation(
            ref SeatPresentationRun active,
            SeatPresentationRun expected,
            BsShelfSettleReason reason,
            out SeatPresentationSettlement settlement)
        {
            settlement = default;
            if (expected == null || !ReferenceEquals(active, expected)
                || !expected.TryTakeSettlement(reason, out settlement))
                return false;
            active = null;
            return true;
        }

        private bool TrySettleSeatPresentation(
            SeatPresentationRun expected,
            BsShelfSettleReason reason,
            bool stopRoutine)
        {
            if (!TryClaimSeatPresentation(
                    ref activeSeatPresentation,
                    expected,
                    reason,
                    out SeatPresentationSettlement settlement))
                return false;

            ExecutePresentationCleanup(
                stopRoutine && settlement.Routine != null
                    ? (Action)(() => StopCoroutine(settlement.Routine))
                    : null,
                FinishSeatPresentationVisuals,
                settlement.LockController != null
                    && settlement.LockOwner != null
                    && settlement.LockRevision >= 0
                        ? (Action)(() => settlement.LockController.ReleasePresentationLock(
                            settlement.LockOwner, settlement.LockRevision))
                        : null,
                settlement.BarrierController != null
                    && settlement.BarrierOwner != null
                        ? (Action)(() => settlement.BarrierController.ReleasePresentationBarrier(
                            settlement.BarrierOwner))
                        : null);
            return true;
        }

        private void StopSeatAnimation(
            BsShelfSettleReason reason = BsShelfSettleReason.Cancelled)
        {
            SeatPresentationRun run = activeSeatPresentation;
            if (run != null)
            {
                TrySettleSeatPresentation(run, reason, true);
                return;
            }
            FinishSeatPresentationVisuals();
        }

        private void FinishSeatPresentationVisuals()
        {
            for (int i = 0; i < activeActors.Count; i++)
                DropEntranceSorting(activeActors[i]);
            FinishShelfFade();
            FinishShelfReveal();
            FinishShelfTransition();
            SnapActorsToSeat();
        }

        /// <summary>
        /// Tries every cleanup step before rethrowing the first error, so all owned leases get a release
        /// attempt.
        /// </summary>
        internal static void ExecutePresentationCleanup(
            Action stopRoutine,
            Action finishEffects,
            Action releaseLock,
            Action releaseBarrier)
        {
            Exception failure = null;
            try
            {
                TryCleanupStep(stopRoutine, ref failure);
                TryCleanupStep(finishEffects, ref failure);
            }
            finally
            {
                TryCleanupStep(releaseLock, ref failure);
                TryCleanupStep(releaseBarrier, ref failure);
            }
            if (failure != null) throw failure;
        }

        private static void TryCleanupStep(Action step, ref Exception failure)
        {
            if (step == null) return;
            try
            {
                step();
            }
            catch (Exception exception)
            {
                if (failure == null) failure = exception;
            }
        }

        private void OrderActorsForEntrance()
        {
            entranceOrder.Clear();
            // Bottom row first: the shelf visibly fills from the plank the player reads last.
            int visibleRows = configuredRowCount + (overflowShelfActive ? 1 : 0);
            for (int row = visibleRows - 1; row >= 0; row--)
            {
                for (int i = 0; i < activeActors.Count; i++)
                    if (activeActors[i].Row == row) entranceOrder.Add(activeActors[i]);
            }

            float delay = 0f;
            int previousRow = -1;
            for (int i = 0; i < entranceOrder.Count; i++)
            {
                Actor actor = entranceOrder[i];
                if (previousRow >= 0)
                    delay += actor.Row == previousRow
                        ? entranceGlassStagger
                        : entranceRowStagger;
                actor.EntranceDelay = delay;
                previousRow = actor.Row;
            }
        }

        private void BeginShelfFade()
        {
            shelfFadeRenderers.Clear();
            shelfFadeColors.Clear();
            if (shelfFadeDuration <= 0f) return;

            if (overflowShelfActive && overflowShelfRow != null)
                AddShelfFadeRenderer(overflowShelfRow.plank);
            for (int row = 0; row < configuredRowCount; row++)
                AddShelfFadeRenderer(shelfRows[row] != null ? shelfRows[row].plank : null);
            if (showShelfPosts)
            {
                // Only posts that connect two active shelves participate in the fade.
                int visiblePostPairs = Mathf.Min(
                    Mathf.Max(0, configuredRowCount - 1),
                    shelfSpans != null ? shelfSpans.Length : 0);
                for (int span = 0; span < visiblePostPairs; span++)
                {
                    ShelfSpanBinding binding = shelfSpans[span];
                    if (binding == null) continue;
                    AddShelfFadeRenderer(binding.leftPost);
                    AddShelfFadeRenderer(binding.rightPost);
                }
                if (overflowShelfActive && overflowShelfSpan != null)
                {
                    AddShelfFadeRenderer(overflowShelfSpan.leftPost);
                    AddShelfFadeRenderer(overflowShelfSpan.rightPost);
                }
            }
            StepShelfFade(0f);
        }

        private void AddShelfFadeRenderer(SpriteRenderer renderer)
        {
            if (renderer == null) return;

            // Fade the plank and its child renderers together so shadows cannot remain visible alone.
            foreach (SpriteRenderer part in
                     renderer.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (!part.enabled) continue;
                shelfFadeRenderers.Add(part);
                shelfFadeColors.Add(part.color);
            }
        }

        private void StepShelfFade(float elapsed)
        {
            if (shelfFadeRenderers.Count == 0) return;
            float k = shelfFadeDuration <= 0f
                ? 1f
                : Mathf.Clamp01(elapsed / shelfFadeDuration);
            for (int i = 0; i < shelfFadeRenderers.Count; i++)
            {
                SpriteRenderer renderer = shelfFadeRenderers[i];
                if (renderer == null) continue;
                Color color = shelfFadeColors[i];
                color.a *= k;
                renderer.color = color;
            }
            if (k >= 1f) FinishShelfFade();
        }

        /// <summary>Restores the authored plank/post colours the fade borrowed.</summary>
        private void FinishShelfFade()
        {
            for (int i = 0; i < shelfFadeRenderers.Count; i++)
                if (shelfFadeRenderers[i] != null)
                    shelfFadeRenderers[i].color = shelfFadeColors[i];
            shelfFadeRenderers.Clear();
            shelfFadeColors.Clear();
        }

        private void ApplyShelfLayout(int rowCount)
        {
            ApplyShelfLayout(rowCount, ResolveResponsiveShelfOffset());
        }

        private void ApplyShelfLayout(int rowCount, float responsiveShelfOffset)
        {
            appliedResponsiveShelfOffset = responsiveShelfOffset;
            DisableAllShelves();
            ApplyShelfCompositionScale(rowCount);
            for (int row = 0; row < rowCount; row++)
            {
                ShelfRowBinding binding = shelfRows[row];
                SpriteRenderer plank = binding.plank;
                MovePlankSurfaceTo(binding, MainShelfTarget(rowCount, row),
                    appliedResponsiveShelfOffset);
                SetShelfPieceEnabled(plank, true);
            }

            if (showShelfPosts)
            {
                for (int span = 0; span < rowCount - 1; span++)
                {
                    float postInset = postLowerShelfInset * CompositionScale(rowCount);
                    float upperSurface = SurfaceY(rowCount, span);
                    float lowerSurface = SurfaceY(rowCount, span + 1);
                    float upperPlankHeight = SpriteHeightInLayout(shelfRows[span].plank);
                    float top = upperSurface - upperPlankHeight * postUpperPlankOverlap;
                    float bottom = lowerSurface - postInset;
                    FitPost(shelfSpans[span].leftPost, top, bottom);
                    FitPost(shelfSpans[span].rightPost, top, bottom);
                }
            }
            if (overflowShelfActive) ApplyOverflowShelfLayout(rowCount);
        }

        private void ApplyOverflowShelfLayout(int rowCount)
        {
            if (overflowShelfRow == null || overflowShelfRow.plank == null) return;

            float factor = CompositionScale(rowCount);
            ShelfRowTarget overflowTarget = OverflowShelfTarget();
            float widthScale = ShelfLayoutSolver.OverflowShelfWidthScale(shelfWidthStep);
            Vector3 plankScale = authoredPlankLocalScale;
            plankScale.x *= factor * widthScale;
            plankScale.y *= factor;
            overflowShelfRow.plank.transform.localScale = plankScale;

            if (showShelfPosts && overflowShelfSpan != null)
            {
                Vector3 postScale = authoredPostLocalScale;
                postScale.x *= factor;
                postScale.y *= factor;
                float postCenterX = PostCenterForPlank(
                    overflowShelfRow.plank, widthScale);
                ApplyPostCompositionScale(overflowShelfSpan.leftPost, -1f,
                    postScale, factor, overflowTarget.CenterX, postCenterX);
                ApplyPostCompositionScale(overflowShelfSpan.rightPost, 1f,
                    postScale, factor, overflowTarget.CenterX, postCenterX);
            }

            float overflowSurface = overflowTarget.SurfaceY
                                  + appliedResponsiveShelfOffset;
            MovePlankSurfaceTo(overflowShelfRow, overflowTarget,
                appliedResponsiveShelfOffset);
            SetShelfPieceEnabled(overflowShelfRow.plank, true);

            if (showShelfPosts && overflowShelfSpan != null)
            {
                float postInset = postLowerShelfInset * factor;
                float lowerSurface = SurfaceY(rowCount, 0);
                float upperPlankHeight = SpriteHeightInLayout(overflowShelfRow.plank);
                float top = overflowSurface
                          - upperPlankHeight * postUpperPlankOverlap;
                float bottom = lowerSurface - postInset;
                FitPost(overflowShelfSpan.leftPost, top, bottom);
                FitPost(overflowShelfSpan.rightPost, top, bottom);
            }
        }

        /// <summary>
        /// Always apply scale and position from the original furniture pose so switching row counts cannot
        /// cause drift.
        /// </summary>
        private void ApplyShelfCompositionScale(int rowCount)
        {
            float factor = CompositionScale(rowCount);
            if (shelfRows != null)
            {
                for (int row = 0; row < shelfRows.Length; row++)
                {
                    SpriteRenderer plank = shelfRows[row] != null
                        ? shelfRows[row].plank
                        : null;
                    if (plank == null) continue;

                    float widthScale = row < rowCount
                        ? ShelfLayoutSolver.ShelfWidthScale(
                            rowCount, row, shelfWidthStep)
                        : 1f;
                    if (rowCount == ShelfLayoutSolver.MinimumRowCount && !overflowShelfActive)
                        widthScale *= twoRowShelfWidthScale;
                    Vector3 plankScale = authoredPlankLocalScale;
                    plankScale.x *= factor * widthScale;
                    plankScale.y *= factor;
                    plank.transform.localScale = plankScale;
                }
            }

            if (!showShelfPosts) return;

            Vector3 postScale = authoredPostLocalScale;
            postScale.x *= factor;
            postScale.y *= factor;
            if (shelfSpans == null) return;
            for (int span = 0; span < shelfSpans.Length; span++)
            {
                ShelfSpanBinding binding = shelfSpans[span];
                if (binding == null) continue;
                float widthScale = span < rowCount - 1
                    ? ShelfLayoutSolver.ShelfWidthScale(
                        rowCount, span, shelfWidthStep)
                    : 1f;
                if (rowCount == ShelfLayoutSolver.MinimumRowCount && !overflowShelfActive)
                    widthScale *= twoRowShelfWidthScale;
                float postCenterX = PostCenterForShelf(span, widthScale);
                float shelfCenterX = MainShelfTarget(
                    rowCount, Mathf.Min(span, rowCount - 1)).CenterX;
                ApplyPostCompositionScale(binding.leftPost, -1f, postScale,
                    factor, shelfCenterX, postCenterX);
                ApplyPostCompositionScale(binding.rightPost, 1f, postScale,
                    factor, shelfCenterX, postCenterX);
            }
        }

        private float PostCenterForShelf(int row, float shelfWidthScale)
        {
            SpriteRenderer plank = shelfRows != null && row >= 0
                && row < shelfRows.Length && shelfRows[row] != null
                ? shelfRows[row].plank
                : null;
            return PostCenterForPlank(plank, shelfWidthScale);
        }

        private float PostCenterForPlank(SpriteRenderer plank, float shelfWidthScale)
        {
            if (plank == null || plank.sprite == null)
                return authoredPostCenterX * shelfWidthScale;

            float authoredHalfWidth = plank.sprite.bounds.extents.x
                                    * Mathf.Abs(authoredPlankLocalScale.x);
            if (authoredHalfWidth <= 0f || authoredPostCenterX < 0f
                || authoredPostCenterX > authoredHalfWidth)
                return authoredPostCenterX * shelfWidthScale;
            return ShelfLayoutSolver.ShelfPostCenterX(
                authoredHalfWidth, authoredPostCenterX, shelfWidthScale);
        }

        private void ApplyPostCompositionScale(SpriteRenderer post, float side,
                                               Vector3 scale, float factor,
                                               float shelfCenterX,
                                               float postCenterX)
        {
            if (post == null) return;
            Transform postTransform = post.transform;
            postTransform.localScale = scale;
            Vector3 layoutPosition = LayoutSpace.InverseTransformPoint(
                postTransform.position);
            layoutPosition.x = shelfCenterX + side * postCenterX * factor;
            postTransform.position = LayoutSpace.TransformPoint(layoutPosition);
        }

        private ShelfRowTarget MainShelfTarget(int rowCount, int row)
            => MainShelfTarget(rowCount, row, overflowShelfActive);

        private ShelfRowTarget MainShelfTarget(int rowCount, int row,
                                               bool useOverflowLayout)
        {
            Transform[] anchors = ShelfSurfaceAnchorsFor(
                rowCount, useOverflowLayout);
            int anchorIndex = useOverflowLayout ? row + 1 : row;
            return ShelfTargetFrom(anchors[anchorIndex]);
        }

        private ShelfRowTarget OverflowShelfTarget()
            => ShelfTargetFrom(fourShelfSurfaceAnchors[0]);

        private ShelfRowTarget ShelfTargetFrom(Transform anchor)
        {
            Vector3 local = LayoutSpace.InverseTransformPoint(anchor.position);
            return new ShelfRowTarget(local.x, local.y);
        }

        private void MovePlankSurfaceTo(ShelfRowBinding binding,
                                        ShelfRowTarget target,
                                        float responsiveOffset)
        {
            SpriteRenderer plank = binding.plank;
            Vector3 currentSurface = LayoutSpace.InverseTransformPoint(
                binding.seatAnchor.position);
            Vector3 local = LayoutSpace.InverseTransformPoint(plank.transform.position);
            local.x += target.CenterX - currentSurface.x;
            local.y += target.SurfaceY + responsiveOffset - currentSurface.y;
            plank.transform.position = LayoutSpace.TransformPoint(local);
        }

        private float SpriteHeightInLayout(SpriteRenderer renderer)
        {
            if (renderer == null || renderer.sprite == null) return 0f;
            float worldHeight = renderer.transform
                .TransformVector(Vector3.up * renderer.sprite.bounds.size.y).magnitude;
            float layoutWorldUnit = LayoutSpace.TransformVector(Vector3.up).magnitude;
            return worldHeight / Mathf.Max(0.0001f, layoutWorldUnit);
        }

        private void FitPost(SpriteRenderer post, float top, float bottom)
        {
            if (post == null || post.sprite == null || top <= bottom) return;

            Transform postTransform = post.transform;
            Vector3 local = LayoutSpace.InverseTransformPoint(postTransform.position);
            local.y = (top + bottom) * 0.5f;
            postTransform.position = LayoutSpace.TransformPoint(local);

            Transform parent = postTransform.parent;
            Vector3 localUp = postTransform.localRotation * Vector3.up;
            float parentWorldPerUnit = parent != null
                ? parent.TransformVector(localUp).magnitude
                : localUp.magnitude;
            float layoutWorldUnit = LayoutSpace.TransformVector(Vector3.up).magnitude;
            float wantedWorldHeight = (top - bottom) * layoutWorldUnit;
            if (post.drawMode == SpriteDrawMode.Sliced)
            {
                // Keep transform scale fixed for the gold collars. Change SpriteRenderer.size to stretch only
                // the sprite's centre.
                float authoredScaleY = Mathf.Abs(postTransform.localScale.y);
                Vector2 size = post.size;
                size.y = wantedWorldHeight
                       / Mathf.Max(0.0001f, parentWorldPerUnit * authoredScaleY);
                post.size = size;
            }
            else
            {
                float scaleY = wantedWorldHeight
                             / Mathf.Max(0.0001f,
                                 post.sprite.bounds.size.y * parentWorldPerUnit);
                Vector3 scale = postTransform.localScale;
                scale.y = Mathf.Sign(Mathf.Approximately(scale.y, 0f) ? 1f : scale.y)
                        * scaleY;
                postTransform.localScale = scale;
            }
            SetShelfPieceEnabled(post, true);
        }

        private float SurfaceY(int rowCount, int row)
            => MainShelfTarget(rowCount, row).SurfaceY
             + appliedResponsiveShelfOffset;

        /// <summary>
        /// Move shelves partway into extra space on taller phones while keeping the stage and cards fixed. The
        /// 720x1280 layout stays unchanged.
        /// </summary>
        private float ResolveResponsiveShelfOffset()
        {
            if (safeAreaFitter == null) return 0f;

            Rect safePixels = safeAreaFitter.AppliedSafeAreaPixels;
            // Resolve the fitter now so the entrance starts from the final seats instead of jumping a frame
            // later.
            if ((safePixels.width <= 0f || safePixels.height <= 0f)
                && safeAreaFitter.ApplyNow())
                safePixels = safeAreaFitter.AppliedSafeAreaPixels;
            Vector2Int reference = safeAreaFitter.ReferenceResolution;
            if (safePixels.width <= 0f || safePixels.height <= 0f
                || reference.x <= 0 || reference.y <= 0)
                return 0f;

            float safeAspect = safePixels.width / safePixels.height;
            float referenceAspect = (float)reference.x / reference.y;
            if (safeAspect >= referenceAspect) return 0f;

            float referenceHeight = 2f * safeAreaFitter.ReferenceOrthographicSize;
            float visibleHeightInReferenceUnits =
                referenceHeight * referenceAspect / Mathf.Max(0.0001f, safeAspect);
            float unusedHeight = Mathf.Max(0f,
                visibleHeightInReferenceUnits - referenceHeight);
            float unusedBelow = unusedHeight
                              * Mathf.Clamp01(safeAreaFitter.ContentAlignment.y);
            float downward = Mathf.Min(maximumTallScreenShelfOffset,
                unusedBelow * tallScreenShelfFollow);
            return -downward;
        }

        private void RefreshResponsiveShelfOffset()
        {
            if (!Application.isPlaying || configuredRowCount <= 0) return;

            float wanted = ResolveResponsiveShelfOffset();
            if (Mathf.Abs(wanted - appliedResponsiveShelfOffset) <= 0.001f) return;
            // Wait until completion lifts restore their roots before applying safe-area or rotation changes.
            if (SeatAnimationPlaying || SynchronizationDeferred
                || (controller != null && controller.PresentationLocked)) return;

            ApplyShelfLayout(configuredRowCount);
            LayoutActiveActors();
            if (twoRowFocusApplied) ApplyTwoRowFocus();
            // Only the layout moved. Skip PresentationChanged so the selected glass follows without being
            // deselected.
        }

        private float CompositionScale(int rowCount) =>
            ShelfLayoutSolver.CompositionScale(LayoutMetrics, rowCount);

        /// <summary>
        /// Fit the complete board once per level: bring two shelves closer, or reserve timer room on larger
        /// boards. Reading the whole order queue keeps later deliveries from resizing the board.
        /// The dedicated root sits below the intro target, so entrance motion cannot overwrite this fit.
        /// </summary>
        private void ApplyBoardCompositionFit()
        {
            if (configuredRowCount == ShelfLayoutSolver.MinimumRowCount && !overflowShelfActive)
            {
                ApplyTwoRowFocus();
                return;
            }
            if (layoutSpace == null || configuredRowCount < 3 || presentedLevel == null
                || !presentedLevel.AllowTimedOrders || presentedLevel.Orders == null) return;
            bool hasTimer = false;
            foreach (OrderDef order in presentedLevel.Orders)
                if (order != null && order.TimeLimit > 0f) { hasTimer = true; break; }
            if (!hasTimer) return;

            SpriteRenderer bottomPlank = shelfRows[configuredRowCount - 1].plank;
            Bounds bounds = RendererBoundsInLayout(bottomPlank);
            Vector3 pivot = new Vector3(bounds.center.x, bounds.min.y, 0f);
            for (int row = 0; row < configuredRowCount; row++)
                bounds.Encapsulate(RendererBoundsInLayout(shelfRows[row].plank));
            if (overflowShelfActive)
                bounds.Encapsulate(RendererBoundsInLayout(overflowShelfRow.plank));
            // Actors are already seated but can still be inactive under the loading cover.
            foreach (Actor actor in activeActors)
                bounds.Encapsulate(RendererBoundsInLayout(actor.PlacementRenderer));

            float scale = TimedBoardScale(bounds.max.y - pivot.y,
                timedOrderHeadroom, maximumTimedBoardShrink);
            layoutSpace.localScale = authoredBoardScale * scale;
            Vector3 pivotInParent = layoutSpace.localRotation
                                  * Vector3.Scale(authoredBoardScale, pivot);
            layoutSpace.localPosition = authoredBoardPosition + pivotInParent * (1f - scale);
        }

        private void ApplyTwoRowFocus()
        {
            if (layoutSpace == null || configuredRowCount != ShelfLayoutSolver.MinimumRowCount
                || overflowShelfActive) return;

            Bounds bounds = RendererBoundsInLayout(shelfRows[0].plank);
            for (int row = 1; row < configuredRowCount; row++)
                bounds.Encapsulate(RendererBoundsInLayout(shelfRows[row].plank));
            foreach (Actor actor in activeActors)
                bounds.Encapsulate(RendererBoundsInLayout(actor.PlacementRenderer));

            // Fit once from the seated composition, before entrance motion. Delivery and pouring never
            // recalculate this pivot, so the board does not breathe as individual glasses move.
            float scale = Mathf.Clamp(twoRowFocusScale, 1f, 1.15f);
            Vector3 pivot = new Vector3(bounds.center.x, bounds.center.y, 0f);
            layoutSpace.localScale = authoredBoardScale * scale;
            Vector3 pivotInParent = layoutSpace.localRotation
                                  * Vector3.Scale(authoredBoardScale, pivot);
            layoutSpace.localPosition = authoredBoardPosition + pivotInParent * (1f - scale);
            if (twoRowTopClearanceAnchor != null)
            {
                float boardTop = layoutSpace.TransformPoint(
                    new Vector3(bounds.center.x, bounds.max.y, 0f)).y;
                float downward = Mathf.Max(0f, boardTop - twoRowTopClearanceAnchor.position.y);
                layoutSpace.position += Vector3.down * downward;
            }
            twoRowFocusApplied = scale > 1f
                || (layoutSpace.localPosition - authoredBoardPosition).sqrMagnitude > 1e-8f;
        }

        internal static float TimedBoardScale(float height, float headroom, float maximumShrink)
        {
            if (!IsFinite(height) || height <= 0f || !IsFinite(headroom)
                || !IsFinite(maximumShrink)) return 1f;
            return Mathf.Clamp(1f - Mathf.Max(0f, headroom) / height,
                1f - Mathf.Clamp(maximumShrink, 0f, 0.08f), 1f);
        }

        private Bounds RendererBoundsInLayout(SpriteRenderer renderer)
        {
            Bounds source = renderer.localBounds;
            Matrix4x4 toLayout = layoutSpace.worldToLocalMatrix
                              * renderer.transform.localToWorldMatrix;
            Bounds result = new Bounds(toLayout.MultiplyPoint3x4(source.center), Vector3.zero);
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = source.center + Vector3.Scale(source.extents, new Vector3(
                    (corner & 1) == 0 ? -1f : 1f,
                    (corner & 2) == 0 ? -1f : 1f,
                    (corner & 4) == 0 ? -1f : 1f));
                result.Encapsulate(toLayout.MultiplyPoint3x4(point));
            }
            return result;
        }

        private void RestoreBoardCompositionFit()
        {
            if (layoutSpace == null) return;
            if (!boardPoseCaptured)
            {
                authoredBoardPosition = layoutSpace.localPosition;
                authoredBoardScale = layoutSpace.localScale;
                boardPoseCaptured = true;
            }
            layoutSpace.localPosition = authoredBoardPosition;
            layoutSpace.localScale = authoredBoardScale;
            twoRowFocusApplied = false;
        }

        private static bool ContainsActor(List<Actor> list, Actor wanted)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], wanted)) return true;
            return false;
        }

        private void Release(Actor actor)
        {
            DropEntranceSorting(actor);
            actorByGlassId.Remove(actor.GlassId);
            if (actor.Bottle != null)
            {
                glassIdByBottle.Remove(actor.Bottle);
                ResetGarnishReadiness(actor.Bottle);
                actor.Bottle.SetUnits(null);
                CanonicaliseBottleLocalPose(actor);
                actor.SeatRoot.localScale = Vector3.one;
                actor.SeatRoot.localRotation = Quaternion.identity;
                actor.Bottle.gameObject.SetActive(false);
            }
            actor.GlassId = -1;
            actor.Assigned = false;
            actor.GarnishOrderReady = false;
            actor.Seated = false;
            actor.HasPreviousSeat = false;
        }

        private void ClearAssignments(bool deactivatePool = true)
        {
            activeActors.Clear();
            actorByGlassId.Clear();
            glassIdByBottle.Clear();

            if (deactivatePool)
            {
                ClearPool(shotPool);
                ClearPool(extraShotPool);
                ClearPool(cocktailPool);
                ClearPool(lattePool);
                ClearPool(tumblerPool);
                ClearPool(biraPool);
            }
            for (int i = 0; i < actors.Count; i++)
            {
                Actor actor = actors[i];
                if (actor == null) continue;
                if (!deactivatePool)
                {
                    ResetBottleLocalPose(actor.Bottle);
                    ResetGarnishReadiness(actor.Bottle);
                }
                CanonicaliseBottleLocalPose(actor);
                if (actor.SeatRoot != null)
                {
                    actor.SeatRoot.localScale = Vector3.one;
                    actor.SeatRoot.localRotation = Quaternion.identity;
                }
                actor.GlassId = -1;
                actor.Assigned = false;
                actor.GarnishOrderReady = false;
                actor.Seated = false;
                actor.HasPreviousSeat = false;
            }
        }

        private static void ClearPool(List<GlassBinding> pool)
        {
            if (pool == null) return;
            for (int i = 0; i < pool.Count; i++)
            {
                GlassBinding binding = pool[i];
                LiquidBottle bottle = binding != null ? binding.bottle : null;
                if (bottle == null) continue;
                ResetGarnishReadiness(bottle);
                bottle.SetUnits(null);
                ResetBottleLocalPose(bottle);
                bottle.gameObject.SetActive(false);
            }
        }

        private static void DeactivateAndResetActor(Actor actor)
        {
            LiquidBottle bottle = actor != null ? actor.Bottle : null;
            if (bottle == null) return;
            ResetGarnishReadiness(bottle);
            bottle.SetUnits(null);
            ResetBottleLocalPose(bottle);
            if (bottle.gameObject.activeSelf) bottle.gameObject.SetActive(false);
        }

        private static void ResetGarnishReadiness(LiquidBottle bottle)
        {
            VesselRimGarnish.SetOrderReady(bottle, false, true);
            VesselFloatingGarnish.SetOrderReady(bottle, false, true);
        }

        private static void ResetBottleLocalPose(LiquidBottle bottle)
        {
            if (bottle == null) return;
            VesselProfile profile = bottle.profile;
            bottle.transform.localScale = profile != null
                ? profile.ShelfReferenceLocalScale
                : Vector3.one;
            bottle.transform.localRotation = profile != null
                ? profile.ShelfReferenceLocalRotation
                : Quaternion.identity;
        }

        private void ClearPresentation()
        {
            SettleShelfAndSeatPresentation(
                BsShelfSettleReason.Cancelled,
                () => SettleShelfPresentationFlow(BsShelfSettleReason.Cancelled));
            ClearAssignments();
            DisableAllShelves();
            RestoreBoardCompositionFit();
            overflowShelfActive = false;
            configuredRowCount = 0;
            configuredColumns = 1;
            presentedLevel = null;
            presentedBoardRevision = -1;
            Ready = false;
            PublishPresentationChangedSafely();
        }

        private void PublishPresentationChangedSafely()
        {
            if (publishingPresentationChanged) return;
            Action handlers = PresentationChanged;
            if (handlers == null) return;
            publishingPresentationChanged = true;
            try
            {
                Delegate[] invocationList = handlers.GetInvocationList();
                for (int i = 0; i < invocationList.Length; i++)
                {
                    try
                    {
                        ((Action)invocationList[i])();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception, this);
                    }
                }
            }
            finally { publishingPresentationChanged = false; }
        }

        private void DisableAllShelves()
        {
            if (shelfRows != null)
            {
                for (int i = 0; i < shelfRows.Length; i++)
                    if (shelfRows[i] != null && shelfRows[i].plank != null)
                        SetShelfPieceEnabled(shelfRows[i].plank, false);
            }
            if (overflowShelfRow != null && overflowShelfRow.plank != null)
                SetShelfPieceEnabled(overflowShelfRow.plank, false);

            if (shelfSpans != null)
            {
                for (int i = 0; i < shelfSpans.Length; i++)
                {
                    ShelfSpanBinding span = shelfSpans[i];
                    if (span == null) continue;
                    SetShelfPieceEnabled(span.leftPost, false);
                    SetShelfPieceEnabled(span.rightPost, false);
                }
            }
            if (overflowShelfSpan == null) return;
            SetShelfPieceEnabled(overflowShelfSpan.leftPost, false);
            SetShelfPieceEnabled(overflowShelfSpan.rightPost, false);
        }

        /// <summary>
        /// Toggles the shelf and its child renderers together. Children do not inherit SpriteRenderer.enabled.
        /// </summary>
        private static void SetShelfPieceEnabled(SpriteRenderer renderer, bool enabled)
        {
            if (renderer == null) return;
            foreach (SpriteRenderer part in
                     renderer.GetComponentsInChildren<SpriteRenderer>(true))
                part.enabled = enabled;
        }

        private bool Reject(string reason)
        {
            ClearPresentation();
            LastError = string.IsNullOrEmpty(reason) ? "Bilinmeyen sunum hatası." : reason;
            if (lastLoggedError != LastError)
            {
                Debug.LogError("Bartender shelf presentation rejected: " + LastError, this);
                lastLoggedError = LastError;
            }
            return false;
        }
    }
}
