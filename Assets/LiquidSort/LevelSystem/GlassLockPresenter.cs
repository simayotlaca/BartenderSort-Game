using System;
using System.Collections.Generic;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    [DefaultExecutionOrder(900)]
    [DisallowMultipleComponent]
    [AddComponentMenu("Liquid Sort/Gameplay/Glass Lock Presenter")]
    public sealed partial class GlassLockPresenter : MonoBehaviour
    {
        private sealed class BottleVisuals
        {
            public LiquidBottle Bottle;
            public readonly List<SpriteRenderer> Questions =
                new List<SpriteRenderer>(LiquidBottle.MaxBands);
            public readonly List<Material> QuestionMaterials =
                new List<Material>(LiquidBottle.MaxBands);
            public readonly List<Vector2> QuestionRestCenters =
                new List<Vector2>(LiquidBottle.MaxBands);
            public readonly List<float> QuestionRestHeights =
                new List<float>(LiquidBottle.MaxBands);
            public readonly List<LockMotion> LockMotions = new List<LockMotion>(LiquidBottle.MaxBands);
            public SpriteRenderer WholeInteriorDim;
            public SpriteRenderer WholeGlassDim;
            public SpriteRenderer WholeLock;
            public CountBadge Badge;
            public bool QuestionsTilted;
            public int GlassId = int.MinValue;
        }

        private const string MarkerPrefix = "SimpleLock_";

        private const int QuestionOrder = 9;
        private const int WholeInteriorDimOrder = 9;
        private const int WholeGlassDimOrder = 10;
        private const int WholeLockOrder = 11;

        private const float QuestionRoyalHeightPixels = 44f;
        private const float UnprofiledQuestionHeightShare = 0.125f;
        private const float QuestionVisibleHeightShare = 0.80f;
        private const float MinimumTiltedQuestionShare = 0.5f;
        private const float LayerLockRoyalHeightPixels = 78f;
        private const float LayerMarkerGapRoyalPixels = 4f;
        private const float WholeLockDiameterShare = 0.235f;
        private const float WholeLockMaxColumnHeightShare = 0.85f;
        private const float WholeLockMaxBodyWidthShare = 0.72f;
        private const float WholeLockVisibleHeightShare = 0.81f;
        private const float WholeLockOffsetShare = 0.02f;
        private static readonly Color WholeInteriorDimColor =
            new Color(0.10f, 0.14f, 0.22f, 0.46f);
        private static readonly Color WholeGlassDimColor =
            new Color(0.18f, 0.22f, 0.30f, 0.52f);

        [SerializeField] private BartenderLevelController controller;
        [SerializeField] private BartenderShelfLevelView shelfView;

        private readonly Dictionary<LiquidBottle, BottleVisuals> visuals =
            new Dictionary<LiquidBottle, BottleVisuals>();
        private readonly HashSet<LiquidBottle> touched =
            new HashSet<LiquidBottle>();

        private BartenderLevelController subscribedController;
        private BartenderShelfLevelView subscribedView;
        private bool refreshPending;

        internal bool WillAnimateUnlock(RtGlass glass, int beforeDelivered,
                                        int afterDelivered)
        {
            if (!isActiveAndEnabled || glass == null) return false;
            if (glass.IsChained(beforeDelivered)
                && !glass.IsChained(afterDelivered))
                return true;
            if (glass.IsChained(beforeDelivered)) return false;

            return OpensPresentedLock(glass, beforeDelivered, afterDelivered);
        }

        internal static bool LayerNeedsQuestion(RtGlass glass, int layerIndex, int delivered) =>
            glass.Layers[layerIndex].Hidden && !IsLayerPresentedLocked(glass, layerIndex, delivered);

        private void Awake() => ResolveDependencies();

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
            RequestRefresh();
        }

        private void OnDisable()
        {
            Unsubscribe();
            refreshPending = false;
            ClearUnlockFeedback();
            HideAll();
        }

        private void LateUpdate()
        {
            RebindIfNeeded();
            if (refreshPending && shelfView != null && shelfView.Ready
                && !shelfView.SeatAnimationPlaying
                && !shelfView.GlobalSynchronizationDeferred)
                RequestRefresh();
            TickUnlockFeedback();
            TickLockMotions();
            TickCountBadges();
            TickQuestionContainment();
        }

        private void HandlePresentationChanged() => RequestRefresh();

        private void HandleLevelLoaded(BsLevel _)
        {
            ClearUnlockFeedback();
            HideAll();
            refreshPending = true;
        }

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state != BartenderLevelState.Unloaded
                && state != BartenderLevelState.CampaignComplete)
                return;

            refreshPending = false;
            ClearUnlockFeedback();
            HideAll();
        }

        private void RequestRefresh()
        {
            if (shelfView != null
                && (shelfView.SeatAnimationPlaying
                    || shelfView.GlobalSynchronizationDeferred))
            {
                refreshPending = true;
                return;
            }

            refreshPending = false;
            RefreshLocks();
        }

        private void RefreshLocks()
        {
            touched.Clear();
            if (controller == null || shelfView == null || !shelfView.Ready)
            {
                HideAll();
                return;
            }

            BsBoard snapshot = controller.Board;
            if (snapshot == null)
            {
                HideAll();
                return;
            }

            int delivered = snapshot.Delivered;
            for (int i = 0; i < snapshot.Glasses.Count; i++)
            {
                RtGlass glass = snapshot.Glasses[i];
                if (glass == null
                    || !shelfView.TryGetBottle(glass.Id, out LiquidBottle bottle)
                    || bottle == null || !bottle.gameObject.activeInHierarchy)
                    continue;

                touched.Add(bottle);
                if (shelfView.IsGlassSynchronizationDeferred(glass.Id))
                {
                    refreshPending = true;
                    continue;
                }

                BottleVisuals set = GetAuthoredVisuals(bottle);
                if (set == null) continue;
                if (set.GlassId != glass.Id)
                {
                    Hide(set);
                    set.GlassId = glass.Id;
                }

                if (glass.IsChained(delivered))
                {
                    HideQuestions(set);
                    ResetLockMotions(set);
                    RefreshWholeGlassDim(set);
                    RefreshWholeLock(set);
                    RefreshWholeCount(set, glass, delivered);
                }
                else
                {
                    if (!RefreshUnlockDim(set, glass.Id)) HideWholeGlassDim(set);
                    HideWholeLock(set);
                    RefreshQuestions(set, glass, delivered);
                }
            }

            foreach (KeyValuePair<LiquidBottle, BottleVisuals> pair in visuals)
            {
                if (pair.Key != null && touched.Contains(pair.Key)) continue;
                Hide(pair.Value);
                if (pair.Value != null) pair.Value.GlassId = int.MinValue;
            }
        }

        private void RefreshQuestions(BottleVisuals set, RtGlass glass, int delivered)
        {
            HideQuestions(set);
            foreach (LockMotion motion in set.LockMotions) motion.Seen = false;
            CollectLockSegments(glass, delivered, segmentScratch);
            MeasureLockSegments(set.Bottle, glass, delivered, segmentScratch);
            for (int i = 0; i < segmentScratch.Count; i++)
            {
                LockSegment segment = segmentScratch[i];
                SpriteRenderer marker = GetAuthoredQuestion(set, segment.Bottom);
                if (marker == null || !segment.HasGeometry) continue;
                Material lockMaterial = i == segmentScratch.Count - 1
                    ? MinimalLockSprites.NumberedLockMaterial : MinimalLockSprites.LayerLockMaterial;
                marker.sharedMaterial = lockMaterial != null
                    ? lockMaterial
                    : set.QuestionMaterials[segment.Bottom];
                // A delivery threshold must read as a lock even after its covering liquid is poured away.
                PlaceGroupedLock(set, segment, delivered);
            }
            foreach (LockMotion motion in set.LockMotions)
                if (!motion.Seen) ResetLockMotion(motion);

            float artHeight = ArtHeight(set.Bottle);
            for (int layerIndex = 0; layerIndex < glass.Layers.Count; layerIndex++)
            {
                if (!LayerNeedsQuestion(glass, layerIndex, delivered)
                    || !set.Bottle.TryGetUnitVisualBand(layerIndex, out Vector2 center, out float bandHeight))
                    continue;

                SpriteRenderer question = GetAuthoredQuestion(set, layerIndex);
                if (question == null) continue;
                float wantedVisibleHeight = set.Bottle.profile != null
                    ? VesselPresentationMath.RoyalPixelsToLocal(
                        QuestionRoyalHeightPixels, set.Bottle.profile)
                    : artHeight * UnprofiledQuestionHeightShare;
                wantedVisibleHeight = FitQuestionHeight(set.Bottle, center, bandHeight, wantedVisibleHeight);
                question.sharedMaterial = MinimalLockSprites.LayerLockMaterial ?? set.QuestionMaterials[layerIndex];
                PlaceSprite(question, MinimalLockSprites.Question, center,
                            wantedVisibleHeight, QuestionVisibleHeightShare);
                set.QuestionRestCenters[layerIndex] = center;
                set.QuestionRestHeights[layerIndex] = wantedVisibleHeight;
            }
            set.QuestionsTilted = false;
            RefreshCountBadge(set, glass, delivered, segmentScratch);
        }

        // Full sprite bounds (including its outline padding) fit inside the unit. Sample
        // the taper at both ends so a question cannot leak out of a narrow cocktail bowl.
        private static float FitQuestionHeight(LiquidBottle bottle, Vector2 center, float bandHeight, float wanted)
        {
            Sprite sprite = MinimalLockSprites.Question;
            if (sprite == null) return 0f;
            float fullHeight = Mathf.Min(wanted / QuestionVisibleHeightShare, bandHeight * 0.88f);
            float ratio = sprite.bounds.size.x / Mathf.Max(0.0001f, sprite.bounds.size.y);
            for (int i = 0; i < 3; i++)
            {
                float y = center.y + (i - 1) * fullHeight * 0.5f;
                float half = VesselFillMath.HalfWidthAt(bottle.InteriorPolygon, y, out float cx);
                float available = Mathf.Max(0f, half - Mathf.Abs(center.x-cx)) * 1.65f;
                fullHeight = Mathf.Min(fullHeight, available / Mathf.Max(0.01f, ratio));
            }
            return fullHeight * QuestionVisibleHeightShare;
        }

        private void TickQuestionContainment()
        {
            foreach (BottleVisuals set in visuals.Values)
            {
                if (set.Bottle == null) continue;
                float angle = Mathf.DeltaAngle(0f, set.Bottle.transform.eulerAngles.z);
                bool tilted = Mathf.Abs(angle) > 0.01f;
                if (!tilted && !set.QuestionsTilted) continue;

                for (int i = 0; i < set.Questions.Count; i++)
                {
                    SpriteRenderer marker = set.Questions[i];
                    if (marker == null || !marker.enabled || marker.sprite != MinimalLockSprites.Question)
                        continue;
                    if (!tilted)
                        PlaceSprite(marker, MinimalLockSprites.Question, set.QuestionRestCenters[i],
                                    set.QuestionRestHeights[i], QuestionVisibleHeightShare);
                    else if (!FollowTiltedBand(set, i, angle))
                        marker.enabled = false;
                }

                if (!tilted) refreshPending = true;
                set.QuestionsTilted = tilted;
            }
        }

        private static bool FollowTiltedBand(BottleVisuals set, int unitIndex, float angle)
        {
            Sprite sprite = MinimalLockSprites.Question;
            float restHeight = set.QuestionRestHeights[unitIndex];
            if (sprite == null || sprite.bounds.size.y <= 0.0001f || restHeight <= 0.0001f
                || !set.Bottle.TryGetUnitBandAtTilt(unitIndex, 0f, out Vector2 uprightMiddle,
                                                    out float uprightThickness, out float uprightHalf)
                || !set.Bottle.TryGetUnitBandAtTilt(unitIndex, angle, out Vector2 tiltedMiddle,
                                                    out float tiltedThickness, out float tiltedHalf))
                return false;

            float ratio = sprite.bounds.size.x / sprite.bounds.size.y;
            float restFull = restHeight / QuestionVisibleHeightShare;
            float uprightRoom = QuestionRoom(uprightThickness, uprightHalf, ratio);
            float tiltedRoom = QuestionRoom(tiltedThickness, tiltedHalf, ratio);
            float full = Mathf.Min(restFull,
                tiltedRoom * Mathf.Max(1f, restFull / Mathf.Max(0.0001f, uprightRoom)));
            full = Mathf.Max(full, restFull * MinimumTiltedQuestionShare);

            Vector2 center = set.QuestionRestCenters[unitIndex] + (tiltedMiddle - uprightMiddle);
            Transform marker = set.Questions[unitIndex].transform;
            float scale = full / sprite.bounds.size.y;
            marker.localPosition = new Vector3(center.x, center.y, 0f);
            marker.localRotation = Quaternion.Euler(0f, 0f, -angle);
            marker.localScale = new Vector3(scale, scale, 1f);
            return true;
        }

        private static float QuestionRoom(float thickness, float halfWidth, float ratio) =>
            Mathf.Min(thickness * 0.88f, halfWidth * 1.65f / Mathf.Max(0.01f, ratio));

        private static float FitLayerLockBesideMarkers(
            LiquidBottle bottle, RtGlass glass, int delivered,
            List<LockSegment> segments, int segmentIndex, float wantedHeight)
        {
            LockSegment segment = segments[segmentIndex];
            // Thin cocktail bands must not shrink the lock below its readable reference size.
            // Crowded markers may use the previous compact size, but never disappear or shrink further.
            float minimumHeight = Mathf.Min(wantedHeight, segment.Height * 0.72f);
            float gap = bottle.profile != null
                ? VesselPresentationMath.RoyalPixelsToLocal(LayerMarkerGapRoyalPixels, bottle.profile)
                : ArtHeight(bottle) * 0.02f;
            for (int direction = -1; direction <= 1; direction += 2)
            {
                int neighborIndex = direction < 0 ? segment.Bottom - 1 : segment.Top + 1;
                if (neighborIndex < 0 || neighborIndex >= glass.Layers.Count) continue;

                float available;
                int neighborSegment = segmentIndex + direction;
                if (neighborSegment >= 0 && neighborSegment < segments.Count
                    && segments[neighborSegment].Bottom <= neighborIndex
                    && neighborIndex <= segments[neighborSegment].Top)
                {
                    if (!segments[neighborSegment].HasGeometry) continue;
                    available = Mathf.Max(0f, Mathf.Abs(
                        segments[neighborSegment].Center.y - segment.Center.y) - gap);
                }
                else if (LayerNeedsQuestion(glass, neighborIndex, delivered)
                         && bottle.TryGetUnitVisualBand(neighborIndex, out Vector2 neighborCenter, out float neighborHeight))
                {
                    float questionHeight = bottle.profile != null
                        ? VesselPresentationMath.RoyalPixelsToLocal(QuestionRoyalHeightPixels, bottle.profile)
                        : ArtHeight(bottle) * UnprofiledQuestionHeightShare;
                    questionHeight = FitQuestionHeight(bottle, neighborCenter, neighborHeight, questionHeight);
                    available = Mathf.Max(0f, 2f * Mathf.Max(
                        0f, Mathf.Abs(neighborCenter.y - segment.Center.y) - gap) - questionHeight);
                }
                else
                {
                    continue;
                }
                wantedHeight = Mathf.Min(wantedHeight, available);
            }
            return Mathf.Max(minimumHeight, wantedHeight);
        }

        private void RefreshWholeLock(BottleVisuals set)
        {
            GetWholeLockLayout(set.Bottle, out Vector2 center, out float wantedVisibleHeight);
            set.WholeLock.sharedMaterial = MinimalLockSprites.NumberedLockMaterial ?? MinimalLockSprites.LayerLockMaterial;
            PlaceSprite(set.WholeLock, MinimalLockSprites.ClosedLock, center,
                        wantedVisibleHeight, WholeLockVisibleHeightShare);
        }

        private static void GetWholeLockLayout(LiquidBottle bottle, out Vector2 center,
                                               out float wantedVisibleHeight)
        {
            float artHeight = ArtHeight(bottle);
            float verticalOffset = artHeight * WholeLockOffsetShare;
            if (!bottle.TryGetWholeLockBandGeometry(
                    verticalOffset, out center, out float columnHeight,
                    out float bodyWidth))
            {
                Rect bounds = bottle.InteriorBounds;
                center = bounds.center + Vector2.up * verticalOffset;
                columnHeight = bounds.height;
                bodyWidth = bounds.width;
            }

            wantedVisibleHeight = Mathf.Min(
                bottle.profile != null ? VesselPresentationMath.RoyalPixelsToLocal(78f, bottle.profile) : artHeight * WholeLockDiameterShare,
                Mathf.Min(columnHeight * WholeLockMaxColumnHeightShare,
                          bodyWidth * WholeLockMaxBodyWidthShare));
        }

        private BottleVisuals GetAuthoredVisuals(LiquidBottle bottle)
        {
            if (visuals.TryGetValue(bottle, out BottleVisuals found)) return found;

            VesselPresentationSlots slots =
                bottle.GetComponentInChildren<VesselPresentationSlots>(true);
            if (slots == null) return null;

            Renderer source = FindVisualSource(bottle);
            int sortingLayerId = source != null
                ? source.sortingLayerID
                : SortingLayer.NameToID(bottle.sortingLayer);
            var set = new BottleVisuals
            {
                Bottle = bottle,
                WholeInteriorDim = ConfigureAuthoredRenderer(
                    bottle, slots.WholeInteriorDim, sortingLayerId,
                    WholeInteriorDimOrder),
                WholeGlassDim = ConfigureAuthoredRenderer(
                    bottle, slots.WholeGlassDim, sortingLayerId,
                    WholeGlassDimOrder),
                WholeLock = ConfigureAuthoredRenderer(
                    bottle, slots.WholeGlassLock, sortingLayerId, WholeLockOrder)
            };
            SpriteRenderer[] questions = slots.LockQuestions;
            for (int i = 0; questions != null && i < questions.Length; i++)
            {
                set.QuestionMaterials.Add(questions[i] != null ? questions[i].sharedMaterial : null);
                set.QuestionRestCenters.Add(Vector2.zero);
                set.QuestionRestHeights.Add(0f);
                set.Questions.Add(ConfigureAuthoredRenderer(
                    bottle, questions[i], sortingLayerId, QuestionOrder));
                set.LockMotions.Add(new LockMotion());
            }
            Transform markerParent = questions != null && questions.Length > 0
                                     && questions[0] != null && questions[0].transform.parent != null
                ? questions[0].transform.parent
                : slots.transform;
            set.Badge = CreateCountBadge(bottle, markerParent, sortingLayerId);
            visuals[bottle] = set;
            bottle.InvalidateRenderers();
            Hide(set);
            return set;
        }

        private static void RefreshWholeGlassDim(BottleVisuals set)
        {
            if (set?.Bottle == null) return;

            Transform frontTransform = set.Bottle.transform.Find("FrontGlass");
            SpriteRenderer front = frontTransform != null
                ? frontTransform.GetComponent<SpriteRenderer>()
                : null;
            RefreshWholeInteriorDim(set, front);

            if (set.WholeGlassDim == null) return;
            if (front == null || front.sprite == null)
            {
                set.WholeGlassDim.enabled = false;
                return;
            }

            SpriteRenderer dim = set.WholeGlassDim;
            dim.sprite = front.sprite;
            dim.color = WholeGlassDimColor;
            dim.flipX = front.flipX;
            dim.flipY = front.flipY;
            dim.drawMode = front.drawMode;
            dim.size = front.size;
            dim.spriteSortPoint = front.spriteSortPoint;
            dim.sortingLayerID = front.sortingLayerID;
            dim.sortingOrder = WholeGlassDimOrder;
            dim.maskInteraction = SpriteMaskInteraction.None;
            dim.transform.localPosition = front.transform.localPosition;
            dim.transform.localRotation = front.transform.localRotation;
            dim.transform.localScale = front.transform.localScale;
            dim.enabled = true;
        }

        private static void RefreshWholeInteriorDim(BottleVisuals set,
                                                     SpriteRenderer front)
        {
            SpriteRenderer dim = set.WholeInteriorDim;
            VesselProfile profile = set.Bottle.profile;
            Texture2D mask = profile != null ? profile.interiorMask : null;
            Rect rect = profile != null ? profile.QuadRect : default;
            if (dim == null || mask == null || mask.width <= 0 || mask.height <= 0
                || rect.width <= 0.0001f || rect.height <= 0.0001f)
            {
                if (dim != null) dim.enabled = false;
                return;
            }

            Sprite sprite = MinimalLockSprites.InteriorMask(mask);
            if (sprite == null)
            {
                dim.enabled = false;
                return;
            }

            dim.sprite = sprite;
            dim.color = WholeInteriorDimColor;
            dim.flipX = false;
            dim.flipY = false;
            dim.drawMode = SpriteDrawMode.Simple;
            dim.spriteSortPoint = SpriteSortPoint.Center;
            dim.sortingLayerID = front != null
                ? front.sortingLayerID
                : SortingLayer.NameToID(set.Bottle.sortingLayer);
            dim.sortingOrder = WholeInteriorDimOrder;
            dim.maskInteraction = SpriteMaskInteraction.None;
            dim.transform.localPosition = new Vector3(
                rect.center.x, rect.center.y, 0f);
            dim.transform.localRotation = Quaternion.identity;
            dim.transform.localScale = new Vector3(
                rect.width / mask.width, rect.height / mask.height, 1f);
            dim.enabled = true;
        }

        private static SpriteRenderer GetAuthoredQuestion(BottleVisuals set, int index)
        {
            return set != null && index >= 0 && index < set.Questions.Count
                ? set.Questions[index]
                : null;
        }

        private static SpriteRenderer ConfigureAuthoredRenderer(
            LiquidBottle bottle, SpriteRenderer renderer,
            int sortingLayerId, int sortingOrder)
        {
            if (renderer == null) return null;
            renderer.gameObject.layer = bottle.gameObject.layer;
            renderer.sprite = null;
            renderer.color = Color.white;
            renderer.sortingLayerID = sortingLayerId;
            renderer.sortingOrder = sortingOrder;
            renderer.maskInteraction = SpriteMaskInteraction.None;
            renderer.drawMode = SpriteDrawMode.Simple;
            renderer.SetPropertyBlock(null);
            renderer.enabled = false;
            return renderer;
        }

        private static bool PlaceSprite(SpriteRenderer renderer, Sprite sprite,
                                        Vector2 center, float wantedVisibleHeight,
                                        float visibleHeightShare)
        {
            if (renderer == null || sprite == null || wantedVisibleHeight <= 0.0001f
                || sprite.bounds.size.y <= 0.0001f)
            {
                if (renderer != null) renderer.enabled = false;
                return false;
            }

            float scale = wantedVisibleHeight
                          / (sprite.bounds.size.y
                             * Mathf.Max(0.01f, visibleHeightShare));
            Transform marker = renderer.transform;
            marker.localPosition = new Vector3(center.x, center.y, 0f);
            marker.localRotation = Quaternion.identity;
            marker.localScale = new Vector3(scale, scale, 1f);
            renderer.sprite = sprite;
            renderer.color = Color.white;
            renderer.enabled = true;
            return true;
        }

        private static Renderer FindVisualSource(LiquidBottle bottle)
        {
            Transform front = bottle.transform.Find("FrontGlass");
            Renderer exact = front != null ? front.GetComponent<Renderer>() : null;
            if (exact != null) return exact;

            Renderer[] renderers = bottle.GetComponentsInChildren<Renderer>(true);
            Renderer best = null;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer candidate = renderers[i];
                if (candidate == null
                    || candidate.name.StartsWith(MarkerPrefix, StringComparison.Ordinal))
                    continue;
                if (best == null || candidate.sortingOrder > best.sortingOrder)
                    best = candidate;
            }
            return best;
        }

        private static float ArtHeight(LiquidBottle bottle)
        {
            VesselProfile profile = bottle.profile;
            if (profile != null && profile.front != null)
                return Mathf.Max(0.0001f, profile.front.bounds.size.y);
            return Mathf.Max(0.0001f, bottle.InteriorBounds.height);
        }

        private void HideAll()
        {
            foreach (BottleVisuals set in visuals.Values) Hide(set);
        }

        private static void Hide(BottleVisuals set)
        {
            if (set == null) return;
            HideQuestions(set);
            set.QuestionsTilted = false;
            ResetLockMotions(set);
            HideCountBadge(set.Badge);
            HideWholeGlassDim(set);
            HideWholeLock(set);
        }

        private static void HideQuestions(BottleVisuals set)
        {
            if (set == null) return;
            for (int i = 0; i < set.Questions.Count; i++)
            {
                SpriteRenderer question = set.Questions[i];
                if (question == null) continue;
                question.enabled = false;
                question.sharedMaterial = set.QuestionMaterials[i];
            }
        }

        private static void HideWholeLock(BottleVisuals set)
        {
            if (set?.WholeLock != null) set.WholeLock.enabled = false;
        }

        private static void HideWholeGlassDim(BottleVisuals set)
        {
            if (set?.WholeInteriorDim != null)
                set.WholeInteriorDim.enabled = false;
            if (set?.WholeGlassDim != null) set.WholeGlassDim.enabled = false;
        }

        private void ResolveDependencies()
        {
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            if (controller == null && shelfView != null)
                controller = shelfView.Controller;
            if (controller == null)
                controller = GetComponent<BartenderLevelController>();
        }

        private void RebindIfNeeded()
        {
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            BartenderLevelController wanted = shelfView != null
                ? shelfView.Controller
                : controller;
            if (ReferenceEquals(wanted, controller)
                && ReferenceEquals(subscribedView, shelfView))
                return;

            Unsubscribe();
            ClearUnlockFeedback();
            HideAll();
            controller = wanted;
            Subscribe();
            RequestRefresh();
        }

        private void Subscribe()
        {
            if (subscribedController != controller)
            {
                if (subscribedController != null)
                {
                    subscribedController.LevelLoaded -= HandleLevelLoaded;
                    subscribedController.StateChanged -= HandleStateChanged;
                    subscribedController.BoardCommitted -= QueueUnlockFeedback;
                }
                subscribedController = controller;
                if (subscribedController != null)
                {
                    subscribedController.LevelLoaded += HandleLevelLoaded;
                    subscribedController.StateChanged += HandleStateChanged;
                    subscribedController.BoardCommitted += QueueUnlockFeedback;
                }
            }

            if (subscribedView == shelfView) return;
            if (subscribedView != null)
                subscribedView.PresentationChanged -= HandlePresentationChanged;
            subscribedView = shelfView;
            if (subscribedView != null)
                subscribedView.PresentationChanged += HandlePresentationChanged;
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.StateChanged -= HandleStateChanged;
                subscribedController.BoardCommitted -= QueueUnlockFeedback;
            }
            if (subscribedView != null)
                subscribedView.PresentationChanged -= HandlePresentationChanged;
            subscribedController = null;
            subscribedView = null;
        }
    }

    internal static class MinimalLockSprites
    {
        private const string QuestionPath =
            "Ui/Locks/Ui_HiddenLayer_Question_v2";
        private const string ClosedLockPath =
            "Ui/Locks/Ui_WholeGlassLock_Closed_v7";

        private static Sprite question;
        private static Sprite closedLock;
        private static Material layerLockMaterial;
        private static Material numberedLockMaterial;
        public static Material NumberedLockMaterial => numberedLockMaterial != null
            ? numberedLockMaterial : numberedLockMaterial = Resources.Load<Material>("Ui/Locks/NumberedLock");
        private static readonly Dictionary<Texture2D, Sprite> interiorMasks =
            new Dictionary<Texture2D, Sprite>();

        public static Sprite Question =>
            question != null ? question : question = Resources.Load<Sprite>(QuestionPath);

        public static Sprite ClosedLock =>
            closedLock != null
                ? closedLock
                : closedLock = Resources.Load<Sprite>(ClosedLockPath);

        public static Material LayerLockMaterial =>
            layerLockMaterial != null
                ? layerLockMaterial
                : layerLockMaterial = Resources.Load<Material>("Ui/Locks/LayerLockOutlined");

        public static Sprite InteriorMask(Texture2D texture)
        {
            if (texture == null || texture.width <= 0 || texture.height <= 0)
                return null;
            if (interiorMasks.TryGetValue(texture, out Sprite cached)
                && cached != null)
                return cached;

            Sprite sprite = Sprite.Create(
                texture, new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f), 1f, 0,
                SpriteMeshType.FullRect);
            sprite.name = texture.name + " Locked Interior View";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            interiorMasks[texture] = sprite;
            return sprite;
        }
    }
}
