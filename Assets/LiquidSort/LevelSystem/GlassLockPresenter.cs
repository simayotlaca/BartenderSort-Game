using System;
using System.Collections.Generic;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Shows hidden liquid with question marks, delivery-locked layers with padlocks, and locked glasses with
    /// one lock and dimming. Layer and glass data still own the rules.
    /// </summary>
    [DefaultExecutionOrder(900)]
    [DisallowMultipleComponent]
    [AddComponentMenu("Liquid Sort/Gameplay/Glass Lock Presenter")]
    public sealed class GlassLockPresenter : MonoBehaviour
    {
        private sealed class BottleVisuals
        {
            public LiquidBottle Bottle;
            public readonly List<SpriteRenderer> Questions =
                new List<SpriteRenderer>(LiquidBottle.MaxBands);
            public readonly List<Material> QuestionMaterials =
                new List<Material>(LiquidBottle.MaxBands);
            public SpriteRenderer WholeInteriorDim;
            public SpriteRenderer WholeGlassDim;
            public SpriteRenderer WholeLock;
            public int GlassId = int.MinValue;
        }

        private const string MarkerPrefix = "SimpleLock_";

        // Draw questions above the glass front for consistent opacity. Lock plates share that layer only when
        // questions are hidden; locks stay on top.
        private const int QuestionOrder = 9;
        private const int WholeInteriorDimOrder = 9;
        private const int WholeGlassDimOrder = 10;
        private const int WholeLockOrder = 11;

        // Convert one screen-space question size through each profile so all marks share the same shelf
        // footprint.
        private const float QuestionRoyalHeightPixels = 48f;
        private const float UnprofiledQuestionHeightShare = 0.125f;
        private const float QuestionVisibleHeightShare = 0.80f;
        private const float LayerLockRoyalHeightPixels = 56f;
        private const float LayerMarkerGapRoyalPixels = 8f;
        private const float WholeLockDiameterShare = 0.235f;
        private const float WholeLockMaxColumnHeightShare = 0.42f;
        private const float WholeLockMaxBodyWidthShare = 0.58f;
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

        /// <summary>
        /// Reports ownership of lock feedback without an unlock tween, preventing MechanicRevealPresenter from
        /// adding its old hop effect.
        /// </summary>
        internal bool WillAnimateUnlock(RtGlass glass, int beforeDelivered,
                                        int afterDelivered)
        {
            if (!isActiveAndEnabled || glass == null) return false;
            if (glass.IsChained(beforeDelivered)
                && !glass.IsChained(afterDelivered))
                return true;

            for (int i = 0; i < glass.Layers.Count; i++)
            {
                Layer layer = glass.Layers[i];
                if (layer.IsLocked(beforeDelivered)
                    && !layer.IsLocked(afterDelivered))
                    return true;
            }
            return false;
        }

        internal static bool LayerNeedsQuestion(Layer layer, int delivered) =>
            layer.Hidden && !layer.IsLocked(delivered);

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
            HideAll();
        }

        private void LateUpdate()
        {
            RebindIfNeeded();
            if (refreshPending && shelfView != null && shelfView.Ready
                && !shelfView.SeatAnimationPlaying
                && !shelfView.SynchronizationDeferred)
                RequestRefresh();
        }

        private void HandlePresentationChanged() => RequestRefresh();

        private void HandleLevelLoaded(BsLevel _)
        {
            HideAll();
            refreshPending = true;
        }

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state != BartenderLevelState.Unloaded
                && state != BartenderLevelState.CampaignComplete)
                return;

            refreshPending = false;
            HideAll();
        }

        private void RequestRefresh()
        {
            if (shelfView != null
                && (shelfView.SeatAnimationPlaying
                    || shelfView.SynchronizationDeferred))
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

                BottleVisuals set = GetAuthoredVisuals(bottle);
                if (set == null) continue;
                if (set.GlassId != glass.Id)
                {
                    Hide(set);
                    set.GlassId = glass.Id;
                }

                touched.Add(bottle);
                if (glass.IsChained(delivered))
                {
                    HideQuestions(set);
                    RefreshWholeGlassDim(set);
                    RefreshWholeLock(set);
                }
                else
                {
                    HideWholeGlassDim(set);
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
            float artHeight = ArtHeight(set.Bottle);
            for (int layerIndex = 0; layerIndex < glass.Layers.Count; layerIndex++)
            {
                Layer layer = glass.Layers[layerIndex];
                bool locked = layer.IsLocked(delivered);
                if (!locked && !LayerNeedsQuestion(layer, delivered)) continue;
                if (!set.Bottle.TryGetUnitVisualBand(
                        layerIndex, out Vector2 center, out float bandHeight))
                    continue;

                SpriteRenderer question = GetAuthoredQuestion(set, layerIndex);
                if (question == null) continue;
                float wantedVisibleHeight = set.Bottle.profile != null
                    ? VesselPresentationMath.RoyalPixelsToLocal(
                        locked ? LayerLockRoyalHeightPixels : QuestionRoyalHeightPixels,
                        set.Bottle.profile)
                    : artHeight * UnprofiledQuestionHeightShare;
                // Thin cocktail bands must not shrink the lock below its readable reference size.
                // Keep the unit's centre so the marker still identifies the locked layer.
                if (locked)
                    wantedVisibleHeight = FitLayerLockBesideMarkers(
                        set.Bottle, glass, delivered, layerIndex, center.y, bandHeight,
                        wantedVisibleHeight);
                Material lockMaterial = locked ? MinimalLockSprites.LayerLockMaterial : null;
                question.sharedMaterial = lockMaterial != null
                    ? lockMaterial
                    : set.QuestionMaterials[layerIndex];
                // A delivery threshold must read as a lock even after its covering liquid is poured away.
                // The same authored layer slot becomes a question only if a hidden colour remains after unlock.
                PlaceSprite(question,
                    locked ? MinimalLockSprites.ClosedLock : MinimalLockSprites.Question,
                    center, wantedVisibleHeight,
                    locked ? WholeLockVisibleHeightShare : QuestionVisibleHeightShare);
            }
        }

        private static float FitLayerLockBesideMarkers(
            LiquidBottle bottle, RtGlass glass, int delivered, int layerIndex,
            float centerY, float bandHeight, float wantedHeight)
        {
            // Crowded markers may use the previous compact size, but never disappear or shrink further.
            float minimumHeight = Mathf.Min(wantedHeight, bandHeight * 0.72f);
            float gap = bottle.profile != null
                ? VesselPresentationMath.RoyalPixelsToLocal(LayerMarkerGapRoyalPixels, bottle.profile)
                : ArtHeight(bottle) * 0.02f;
            for (int direction = -1; direction <= 1; direction += 2)
            {
                int neighborIndex = layerIndex + direction;
                if (neighborIndex < 0 || neighborIndex >= glass.Layers.Count) continue;
                Layer neighbor = glass.Layers[neighborIndex];
                bool neighborLocked = neighbor.IsLocked(delivered);
                if (!neighborLocked && !LayerNeedsQuestion(neighbor, delivered)) continue;
                if (!bottle.TryGetUnitVisualBand(neighborIndex, out Vector2 neighborCenter, out _))
                    continue;

                float available = Mathf.Max(0f, Mathf.Abs(neighborCenter.y - centerY) - gap);
                // Two locks share the available space equally. An existing question keeps its size.
                if (!neighborLocked)
                {
                    float questionHeight = bottle.profile != null
                        ? VesselPresentationMath.RoyalPixelsToLocal(QuestionRoyalHeightPixels, bottle.profile)
                        : ArtHeight(bottle) * UnprofiledQuestionHeightShare;
                    available = Mathf.Max(0f, 2f * available - questionHeight);
                }
                wantedHeight = Mathf.Min(wantedHeight, available);
            }
            return Mathf.Max(minimumHeight, wantedHeight);
        }

        private void RefreshWholeLock(BottleVisuals set)
        {
            float artHeight = ArtHeight(set.Bottle);
            float verticalOffset = artHeight * WholeLockOffsetShare;
            if (!set.Bottle.TryGetWholeLockBandGeometry(
                    verticalOffset, out Vector2 center, out float columnHeight,
                    out float bodyWidth))
            {
                Rect bounds = set.Bottle.InteriorBounds;
                center = bounds.center + Vector2.up * verticalOffset;
                columnHeight = bounds.height;
                bodyWidth = bounds.width;
            }

            float wantedVisibleHeight = Mathf.Min(
                artHeight * WholeLockDiameterShare,
                Mathf.Min(columnHeight * WholeLockMaxColumnHeightShare,
                          bodyWidth * WholeLockMaxBodyWidthShare));
            PlaceSprite(set.WholeLock, MinimalLockSprites.ClosedLock, center,
                        wantedVisibleHeight, WholeLockVisibleHeightShare);
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
                set.Questions.Add(ConfigureAuthoredRenderer(
                    bottle, questions[i], sortingLayerId, QuestionOrder));
            }
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

            // Use a separate plate with the default sprite material so its tint works even when BottleShell's
            // custom shader ignores renderer RGB.
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
                }
                subscribedController = controller;
                if (subscribedController != null)
                {
                    subscribedController.LevelLoaded += HandleLevelLoaded;
                    subscribedController.StateChanged += HandleStateChanged;
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
