using UnityEngine;

namespace LiquidSort
{
    /// <summary>I float collider-free decorations on the level liquid surface. A second sprite shares transfer state but keeps its own idle phase.</summary>
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(LiquidBottle))]
    public sealed class VesselFloatingGarnish : MonoBehaviour
    {
        private static readonly int SurfaceWorldYId =
            Shader.PropertyToID("_SurfaceWorldY");
        private static readonly int LiquidColorId =
            Shader.PropertyToID("_LiquidColor");
        private static readonly int FoamFxStrengthId =
            Shader.PropertyToID("_FoamFxStrength");
        private static readonly int FoamBubbleStateId =
            Shader.PropertyToID("_FoamBubbleState");

        private const float FadeOutSeconds = 0.09f;
        private const float FadeInSeconds = 0.36f;
        private const float EnterHideDegrees = 10f;
        private const float ExitHideDegrees = 5f;
        private const float LegacyChordShare = 0.80f;
        private const float RimClearanceShare = 0.04f;
        private const float PourSinkShare = 0.10f;
        private const float SettleSeconds = 0.38f;
        private const float SettleLiftShare = 0.04f;
        private const float SettleRotation = 3.75f;
        private const float IdleFadeOutSeconds = 0.11f;
        private const float IdleFadeInSeconds = 0.26f;
        private const float MinimumIdlePeriod = 0.75f;
        // I separate the two ice silhouettes within one small footprint so they do not look like a rigid block.
        private const float PrimaryWidthShare = 0.74f;
        private const float SecondaryWidthShare = 0.64f;
        private const float PrimaryOffsetShare = -0.18f;
        private const float SecondaryOffsetShare = 0.21f;
        private const float SecondaryLiftShare = 0.08f;
        private const float PrimaryRestRotation = -4.5f;
        private const float SecondaryRestRotation = 6f;
        private const float LiquidLightWidthScale = 1.20f;
        private const float LiquidLightDepthRoyalPixels = 10f;
        private const float FoamBreathPeriod = 3.0f;
        private const float FoamBreathPeakRoyalPixels = 0.75f;
        private const float FoamBubbleDuration = 0.72f;
        private const float FoamBubbleMinimumDelay = 2.8f;
        private const float FoamBubbleMaximumDelay = 4.5f;
        private const float FoamBubbleReturnDelay = 1.2f;
        private const float TwoPi = Mathf.PI * 2f;

        [SerializeField] private VesselPresentationSlots slots;

        private LiquidBottle bottle;
        private SpriteRenderer primaryRenderer;
        private SpriteRenderer secondaryRenderer;
        private SpriteRenderer condensationRenderer;
        private MaterialPropertyBlock primaryBlock;
        private MaterialPropertyBlock secondaryBlock;
        private AppliedLiquidProperties primaryLiquidProperties;
        private AppliedLiquidProperties secondaryLiquidProperties;
        private Sprite configuredPrimarySprite;
        private Sprite configuredSecondarySprite;
        private Sprite configuredCondensationSprite;
        private Material configuredIceMaterial;
        private Material configuredCondensationMaterial;
        private Bounds primaryVisualBounds;
        private Bounds secondaryVisualBounds;
        private Bounds condensationVisualBounds;
        private string configuredSortingLayer;
        private int configuredIceSortingOrder = int.MinValue;
        private int configuredCondensationSortingOrder = int.MinValue;
        private float opacity;
        private float sinkAmount;
        private bool orderReady;
        private bool hidingForPour;
        private bool wasReserved;
        private float settleRemaining;
        private float settleSign = 1f;
        private float idlePhase;
        private float idleMotionWeight;
        private bool foamFxEnabled;
        private float foamBreathPhase;
        private float foamBubbleDelay;
        private float foamBubbleElapsed;
        private float foamBubbleStrength;
        private float foamBubbleProgress;
        private int foamBubbleCycle;
        private bool foamBubbleActive;
        private bool emptyPresentationSettled;

        private struct AppliedLiquidProperties
        {
            public bool valid;
            public SpriteRenderer renderer;
            public float surfaceWorldY;
            public Color liquidColor;
            public Vector4 foamBubbleState;
        }

        public static void Ensure(LiquidBottle owner)
        {
            if (!Application.isPlaying || owner == null) return;

            VesselFloatingGarnish presenter =
                owner.GetComponent<VesselFloatingGarnish>();
            bool wanted = owner.profile != null
                && owner.profile.floatingGarnish != null;
            if (!wanted)
            {
                if (presenter != null) presenter.SetPresentationEnabled(false);
                return;
            }

            if (presenter == null)
            {
                Debug.LogError(
                    $"{owner.name}: authored floating-garnish presenter is missing.",
                    owner);
                return;
            }
            presenter.Bind(owner);
        }

        /// <summary>The shelf owns order readiness. I start hidden so reused glasses cannot flash their previous garnish.</summary>
        public static void SetOrderReady(LiquidBottle owner, bool ready,
            bool immediate = false)
        {
            if (!Application.isPlaying || owner == null) return;

            VesselFloatingGarnish presenter =
                owner.GetComponent<VesselFloatingGarnish>();
            bool wanted = owner.profile != null
                && owner.profile.floatingGarnish != null;
            if (!wanted)
            {
                if (presenter != null)
                {
                    presenter.orderReady = false;
                    presenter.SetPresentationEnabled(false);
                }
                return;
            }

            // I keep unused pool resets allocation-free and enable presentation objects only when readiness
            // becomes positive.
            if (!ready && (presenter == null || !presenter.enabled))
            {
                if (presenter != null) presenter.orderReady = false;
                return;
            }

            if (presenter == null || !presenter.enabled)
            {
                Ensure(owner);
                presenter = owner.GetComponent<VesselFloatingGarnish>();
            }
            if (presenter == null) return;

            presenter.orderReady = ready;
            if (!ready && immediate)
                presenter.HideImmediately();
        }

        private void Awake() => bottle = GetComponent<LiquidBottle>();

        private void OnEnable()
        {
            if (bottle == null) bottle = GetComponent<LiquidBottle>();
            if (bottle != null && bottle.profile != null
                && bottle.profile.floatingGarnish != null)
                Bind(bottle);
        }

        private void Bind(LiquidBottle owner)
        {
            bottle = owner;
            enabled = true;
            opacity = 0f;
            sinkAmount = 0f;
            hidingForPour = false;
            wasReserved = owner.IsTransferReserved;
            settleRemaining = 0f;
            settleSign = (GetInstanceID() & 1) == 0 ? 1f : -1f;
            idlePhase = Mathf.Repeat(
                owner.GetInstanceID() * 0.754877666f, TwoPi);
            idleMotionWeight = 0f;
            foamBreathPhase = Mathf.Repeat(
                owner.GetInstanceID() * 0.56984029f, TwoPi);
            foamBubbleCycle = 0;
            foamBubbleActive = false;
            foamBubbleElapsed = 0f;
            foamBubbleStrength = 0f;
            foamBubbleProgress = 0f;
            foamBubbleDelay = InitialFoamBubbleDelay(owner.GetInstanceID());
            if (!ResolveAuthoredRenderers())
            {
                enabled = false;
                return;
            }
            ConfigureRenderers(true);
            ApplyPose(false, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
            emptyPresentationSettled = true;
        }

        private void SetPresentationEnabled(bool value)
        {
            enabled = value;
            SetRendererEnabled(primaryRenderer, value);
            SetRendererEnabled(secondaryRenderer, value);
            SetRendererEnabled(condensationRenderer, value);
            if (!value && bottle != null)
                bottle.SetFloatingGarnishCaustic(Vector4.zero);
        }

        private void HideImmediately()
        {
            opacity = 0f;
            sinkAmount = 0f;
            settleRemaining = 0f;
            idleMotionWeight = 0f;
            hidingForPour = false;
            ResetFoamWithoutSurface();
            HideRenderer(primaryRenderer);
            HideRenderer(secondaryRenderer);
            HideRenderer(condensationRenderer);
            if (bottle != null)
                bottle.SetFloatingGarnishCaustic(Vector4.zero);
            emptyPresentationSettled = true;
        }

        private void OnDisable()
        {
            if (bottle != null)
                bottle.SetFloatingGarnishCaustic(Vector4.zero);
        }

        private void LateUpdate()
        {
            VesselProfile profile = bottle != null ? bottle.profile : null;
            if (profile == null || profile.floatingGarnish == null)
            {
                SetPresentationEnabled(false);
                return;
            }

            if (!ResolveAuthoredRenderers())
            {
                SetPresentationEnabled(false);
                return;
            }
            bool rendererConfigurationChanged = ConfigureRenderers(false);
            float deltaTime = Time.unscaledDeltaTime;
            bool reserved = bottle.IsTransferReserved;

            // For empty hidden vessels, I advance only the idle clock and skip surface queries, pose math and
            // property-block writes.
            if (bottle.DisplayVolume <= 0.05f && EmptyMotionIsSettled())
            {
                wasReserved = reserved;
                hidingForPour = false;
                AdvanceIdlePhase(profile, deltaTime);
                ResetFoamWithoutSurface();
                if (rendererConfigurationChanged || !emptyPresentationSettled)
                    HideEmptyPresentation();
                emptyPresentationSettled = true;
                return;
            }

            emptyPresentationSettled = false;
            float halfWidth = 0f;
            bool hasSurface = bottle.DisplayVolume > 0.05f
                && bottle.TryGetContactSurfaceGeometry(
                    out _, out halfWidth, out _);
            UpdatePourHiding(profile, hasSurface);

            if (!wasReserved && reserved)
                settleRemaining = 0f;
            else if (wasReserved && !reserved && hasSurface)
                settleRemaining = SettleSeconds;
            wasReserved = reserved;

            float fillVisibility = Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(0.08f, 0.30f, bottle.DisplayVolume));
            float targetOpacity = orderReady && hasSurface && !hidingForPour
                ? fillVisibility
                : 0f;
            float fadeDuration = targetOpacity < opacity
                ? FadeOutSeconds
                : FadeInSeconds;
            opacity = Mathf.MoveTowards(opacity, targetOpacity,
                deltaTime / Mathf.Max(0.001f, fadeDuration));
            sinkAmount = Mathf.MoveTowards(sinkAmount,
                hidingForPour ? 1f : 0f,
                deltaTime / Mathf.Max(0.001f, fadeDuration));

            float primarySettleLift = 0f;
            float primarySettleRotation = 0f;
            float secondarySettleLift = 0f;
            float secondarySettleRotation = 0f;
            if (settleRemaining > 0f)
            {
                settleRemaining = Mathf.Max(0f, settleRemaining - deltaTime);
                float progress = 1f - settleRemaining / SettleSeconds;
                float pulse = Mathf.Sin(progress * Mathf.PI) * (1f - progress);
                primarySettleLift = pulse * SettleLiftShare;
                primarySettleRotation = pulse * SettleRotation * settleSign;
                secondarySettleLift = -pulse * SettleLiftShare * 0.45f;
                secondarySettleRotation = -pulse * SettleRotation
                    * settleSign * 0.78f;
            }

            UpdateIdleMotion(profile, hasSurface, reserved, deltaTime,
                out float primaryIdleLift, out float primaryIdleRotation,
                out float secondaryIdleLift, out float secondaryIdleRotation,
                out float idleHorizontalDrift);
            UpdateFoamMotion(profile, hasSurface, reserved, deltaTime,
                out float primaryHeightDelta);
            ApplyPose(hasSurface, halfWidth,
                primarySettleLift + primaryIdleLift,
                primarySettleRotation + primaryIdleRotation,
                secondarySettleLift + secondaryIdleLift,
                secondarySettleRotation + secondaryIdleRotation,
                idleHorizontalDrift, primaryHeightDelta);
            if (!hasSurface && EmptyMotionIsSettled())
                emptyPresentationSettled = true;
        }

        private void UpdateIdleMotion(VesselProfile profile, bool hasSurface,
            bool reserved, float deltaTime,
            out float primaryLift, out float primaryRotation,
            out float secondaryLift, out float secondaryRotation,
            out float horizontalDrift)
        {
            AdvanceIdlePhase(profile, deltaTime);

            // I use the vessel reservation for the whole transfer, including return and cancellation, instead of
            // duplicating state.
            bool canIdle = orderReady && hasSurface && !reserved
                && settleRemaining <= 0f;
            float targetWeight = canIdle ? 1f : 0f;
            float duration = targetWeight < idleMotionWeight
                ? IdleFadeOutSeconds
                : IdleFadeInSeconds;
            idleMotionWeight = Mathf.MoveTowards(idleMotionWeight, targetWeight,
                deltaTime / Mathf.Max(0.001f, duration));
            float weight = Mathf.SmoothStep(0f, 1f, idleMotionWeight);
            float bob = profile.floatingGarnishIdleBobShare * weight;
            float rock = profile.floatingGarnishIdleRockDegrees * weight;
            float drift = profile.floatingGarnishIdleDriftShare * weight;

            // Leaves sweep along a shallow arc; profiles without horizontal sweep keep the ice pair's sine bob.
            primaryLift = drift > 0.00001f
                ? Mathf.Cos(idlePhase * 2f) * bob
                : Mathf.Sin(idlePhase) * bob;
            primaryRotation = Mathf.Sin(idlePhase + Mathf.PI * 0.62f) * rock;
            horizontalDrift = Mathf.Sin(idlePhase) * drift;
            // I offset the two idle phases so each cube moves while their combined centre stays nearly still.
            secondaryLift = Mathf.Sin(idlePhase + Mathf.PI * 0.94f)
                * bob * 0.72f;
            secondaryRotation = Mathf.Sin(idlePhase + Mathf.PI * 1.47f)
                * rock * 0.78f;
        }

        private void AdvanceIdlePhase(VesselProfile profile, float deltaTime)
        {
            float period = Mathf.Max(MinimumIdlePeriod,
                profile.floatingGarnishIdleBobPeriod);
            idlePhase = Mathf.Repeat(
                idlePhase + deltaTime * TwoPi / period, TwoPi);
        }

        private bool EmptyMotionIsSettled()
        {
            return opacity <= 0f
                && sinkAmount <= 0f
                && settleRemaining <= 0f
                && idleMotionWeight <= 0f
                && !foamBubbleActive
                && foamBubbleStrength <= 0f
                && foamBubbleProgress <= 0f;
        }

        private void ResetFoamWithoutSurface()
        {
            foamBubbleStrength = 0f;
            foamBubbleProgress = 0f;
            if (!foamFxEnabled) return;

            foamBubbleActive = false;
            foamBubbleElapsed = 0f;
            foamBubbleDelay = Mathf.Max(
                foamBubbleDelay, FoamBubbleReturnDelay);
        }

        private void HideEmptyPresentation()
        {
            HideRenderer(primaryRenderer);
            HideRenderer(secondaryRenderer);
            HideRenderer(condensationRenderer);
            bottle.SetFloatingGarnishCaustic(Vector4.zero);
        }

        private void UpdateFoamMotion(VesselProfile profile, bool hasSurface,
            bool reserved, float deltaTime, out float primaryHeightDelta)
        {
            primaryHeightDelta = 0f;
            foamBubbleStrength = 0f;
            foamBubbleProgress = 0f;
            if (!foamFxEnabled) return;

            // I use the shared transfer reservation to settle foam before the normal tilt fade and sink.
            bool canAnimate = orderReady && hasSurface && !reserved
                && !hidingForPour && settleRemaining <= 0f;
            if (canAnimate)
            {
                foamBreathPhase = Mathf.Repeat(foamBreathPhase
                    + deltaTime * TwoPi / FoamBreathPeriod, TwoPi);
            }
            float breathWeight = Mathf.SmoothStep(0f, 1f, idleMotionWeight);
            primaryHeightDelta = Mathf.Sin(foamBreathPhase)
                * VesselPresentationMath.RoyalPixelsToLocal(
                    FoamBreathPeakRoyalPixels, profile)
                * breathWeight;
            if (!canAnimate)
            {
                foamBubbleActive = false;
                foamBubbleElapsed = 0f;
                foamBubbleDelay = Mathf.Max(
                    foamBubbleDelay, FoamBubbleReturnDelay);
                return;
            }

            if (!foamBubbleActive)
            {
                foamBubbleDelay -= deltaTime;
                if (foamBubbleDelay <= 0f)
                {
                    foamBubbleActive = true;
                    foamBubbleElapsed = 0f;
                }
                return;
            }

            foamBubbleElapsed += deltaTime;
            foamBubbleProgress = Mathf.Clamp01(
                foamBubbleElapsed / FoamBubbleDuration);
            float envelope = Mathf.Sin(foamBubbleProgress * Mathf.PI);
            foamBubbleStrength = envelope * envelope * breathWeight;
            if (foamBubbleElapsed < FoamBubbleDuration) return;

            foamBubbleActive = false;
            foamBubbleElapsed = 0f;
            foamBubbleStrength = 0f;
            foamBubbleProgress = 0f;
            foamBubbleDelay = NextFoamBubbleDelay();
        }

        private static float InitialFoamBubbleDelay(int instanceId)
        {
            float phase = Mathf.Repeat(instanceId * 0.381966011f, 1f);
            return Mathf.Lerp(1.5f, 3.1f, phase);
        }

        private float NextFoamBubbleDelay()
        {
            foamBubbleCycle++;
            float phase = Mathf.Repeat(
                GetInstanceID() * 0.618033989f
                + foamBubbleCycle * 0.381966011f, 1f);
            return Mathf.Lerp(
                FoamBubbleMinimumDelay, FoamBubbleMaximumDelay, phase);
        }

        private void UpdatePourHiding(VesselProfile profile, bool hasSurface)
        {
            if (!hasSurface || !bottle.IsTransferReserved
                || profile.mouthHalfWidth <= 0.001f)
            {
                hidingForPour = false;
                return;
            }

            Vector3 left = transform.TransformPoint(new Vector3(
                profile.mouthLocal.x - profile.mouthHalfWidth,
                profile.mouthLocal.y, 0f));
            Vector3 right = transform.TransformPoint(new Vector3(
                profile.mouthLocal.x + profile.mouthHalfWidth,
                profile.mouthLocal.y, 0f));
            float rimLength = Vector3.Distance(left, right);
            float verticalShare = rimLength > 0.0001f
                ? Mathf.Abs(right.y - left.y) / rimLength
                : 0f;
            float threshold = Mathf.Sin((hidingForPour
                ? ExitHideDegrees
                : EnterHideDegrees) * Mathf.Deg2Rad);
            hidingForPour = verticalShare >= threshold;
        }

        private bool ResolveAuthoredRenderers()
        {
            if (primaryRenderer != null && secondaryRenderer != null
                && condensationRenderer != null)
                return true;
            if (slots == null)
                slots = GetComponentInChildren<VesselPresentationSlots>(true);
            if (slots == null) return false;

            primaryRenderer = slots.FloatingGarnish;
            secondaryRenderer = slots.FloatingGarnishSecondary;
            condensationRenderer = slots.CondensationGarnish;
            return primaryRenderer != null && secondaryRenderer != null
                && condensationRenderer != null;
        }

        private bool ConfigureRenderers(bool force)
        {
            VesselProfile profile = bottle.profile;
            string wantedLayer = bottle.sortingLayer;
            int wantedIceOrder = profile.floatingGarnishSortingOrder;
            int wantedCondensationOrder = profile.condensationSortingOrder;
            bool changed = force
                || configuredPrimarySprite != profile.floatingGarnish
                || configuredSecondarySprite != profile.floatingGarnishSecondary
                || configuredCondensationSprite != profile.condensationGarnish
                || configuredIceMaterial != profile.floatingGarnishMaterial
                || configuredCondensationMaterial
                    != profile.condensationGarnishMaterial
                || configuredSortingLayer != wantedLayer
                || configuredIceSortingOrder != wantedIceOrder
                || configuredCondensationSortingOrder
                    != wantedCondensationOrder;
            if (!changed) return false;

            bool primaryChanged = configuredPrimarySprite
                != profile.floatingGarnish;
            bool secondaryChanged = configuredSecondarySprite
                != profile.floatingGarnishSecondary;
            bool condensationChanged = configuredCondensationSprite
                != profile.condensationGarnish;
            configuredPrimarySprite = profile.floatingGarnish;
            configuredSecondarySprite = profile.floatingGarnishSecondary;
            configuredCondensationSprite = profile.condensationGarnish;
            configuredIceMaterial = profile.floatingGarnishMaterial;
            configuredCondensationMaterial = profile.condensationGarnishMaterial;
            foamFxEnabled = configuredIceMaterial != null
                && configuredIceMaterial.HasProperty(FoamFxStrengthId)
                && configuredIceMaterial.GetFloat(FoamFxStrengthId) > 0.001f;
            if (!foamFxEnabled)
            {
                foamBubbleActive = false;
                foamBubbleStrength = 0f;
                foamBubbleProgress = 0f;
            }
            configuredSortingLayer = wantedLayer;
            configuredIceSortingOrder = wantedIceOrder;
            configuredCondensationSortingOrder = wantedCondensationOrder;

            if (primaryChanged || primaryVisualBounds.size.sqrMagnitude <= 0.0001f)
                primaryVisualBounds = ResolveVisualBounds(configuredPrimarySprite);
            if (secondaryChanged
                || secondaryVisualBounds.size.sqrMagnitude <= 0.0001f)
                secondaryVisualBounds = ResolveVisualBounds(configuredSecondarySprite);
            if (condensationChanged
                || condensationVisualBounds.size.sqrMagnitude <= 0.0001f)
                condensationVisualBounds = ResolveVisualBounds(
                    configuredCondensationSprite);

            bool hasSecondary = configuredSecondarySprite != null;
            ConfigureIceRenderer(primaryRenderer, configuredPrimarySprite,
                wantedLayer, wantedIceOrder + (hasSecondary ? 1 : 0));
            ConfigureIceRenderer(secondaryRenderer, configuredSecondarySprite,
                wantedLayer, wantedIceOrder);
            primaryLiquidProperties.valid = false;
            secondaryLiquidProperties.valid = false;

            condensationRenderer.sprite = configuredCondensationSprite;
            condensationRenderer.sharedMaterial = configuredCondensationMaterial;
            condensationRenderer.sortingLayerName = wantedLayer;
            condensationRenderer.sortingOrder = wantedCondensationOrder;
            condensationRenderer.maskInteraction = SpriteMaskInteraction.None;
            condensationRenderer.flipX = false;
            condensationRenderer.flipY = false;
            condensationRenderer.SetPropertyBlock(null);
            condensationRenderer.color = Color.clear;
            condensationRenderer.enabled = configuredCondensationSprite != null;
            bottle.InvalidateRenderers();
            return true;
        }

        private void ConfigureIceRenderer(SpriteRenderer renderer, Sprite sprite,
            string sortingLayer, int sortingOrder)
        {
            renderer.sprite = sprite;
            renderer.sharedMaterial = configuredIceMaterial;
            renderer.sortingLayerName = sortingLayer;
            renderer.sortingOrder = sortingOrder;
            renderer.maskInteraction = SpriteMaskInteraction.None;
            renderer.flipX = false;
            renderer.flipY = false;
            renderer.SetPropertyBlock(null);
            renderer.color = Color.clear;
            renderer.enabled = sprite != null;
        }

        private void ApplyPose(bool hasSurface, float surfaceHalfWidth,
            float primaryLift, float primaryRotation,
            float secondaryLift, float secondaryRotation,
            float horizontalDrift, float primaryHeightDelta)
        {
            if (primaryRenderer == null || primaryRenderer.sprite == null)
            {
                if (bottle != null)
                    bottle.SetFloatingGarnishCaustic(Vector4.zero);
                return;
            }
            if (!hasSurface)
            {
                HideRenderer(primaryRenderer);
                HideRenderer(secondaryRenderer);
                HideRenderer(condensationRenderer);
                bottle.SetFloatingGarnishCaustic(Vector4.zero);
                return;
            }

            VesselProfile profile = bottle.profile;
            bottle.TryGetContactSurfaceGeometry(
                out Vector2 surfaceCentre, out surfaceHalfWidth, out _);
            float interiorWidth = Mathf.Max(0.01f,
                profile.interiorBounds.width);
            float interiorHeight = Mathf.Max(0.01f,
                profile.interiorBounds.height);
            float chordShare = profile.floatingGarnishChordShare > 0.01f
                ? Mathf.Clamp(profile.floatingGarnishChordShare, 0.50f, 1f)
                : LegacyChordShare;
            float authoredWidth = interiorWidth
                * profile.floatingGarnishWidthShare;
            float chordWidth = surfaceHalfWidth * 2f * chordShare;
            float targetWidth = Mathf.Max(0.01f,
                Mathf.Min(authoredWidth, chordWidth));
            float visualOpacity = Mathf.SmoothStep(0f, 1f, opacity);
            float popScale = RevealScale(opacity);
            bool dual = secondaryRenderer != null
                && secondaryRenderer.sprite != null;

            float primaryWidth = targetWidth
                * (dual ? PrimaryWidthShare : 1f);
            float secondaryWidth = dual
                ? targetWidth * SecondaryWidthShare
                : 0f;
            Bounds primaryBounds = primaryVisualBounds.size.sqrMagnitude > 0.0001f
                ? primaryVisualBounds
                : primaryRenderer.sprite.bounds;
            float primaryScale = primaryWidth
                / Mathf.Max(0.001f, primaryBounds.size.x);
            float primaryHeight = primaryBounds.size.y
                * primaryScale * popScale;

            Bounds secondaryBounds = default;
            float secondaryScale = 0f;
            float secondaryHeight = 0f;
            if (dual)
            {
                secondaryBounds = secondaryVisualBounds.size.sqrMagnitude > 0.0001f
                    ? secondaryVisualBounds
                    : secondaryRenderer.sprite.bounds;
                secondaryScale = secondaryWidth
                    / Mathf.Max(0.001f, secondaryBounds.size.x);
                secondaryHeight = secondaryBounds.size.y
                    * secondaryScale * popScale;
            }

            float xOffset = interiorWidth
                * (profile.floatingGarnishHorizontalOffsetShare
                    + horizontalDrift);
            float horizontalRoom = Mathf.Max(0f,
                surfaceHalfWidth * chordShare - targetWidth * 0.5f);
            xOffset = Mathf.Clamp(xOffset, -horizontalRoom, horizontalRoom);
            float clusterX = surfaceCentre.x + xOffset;
            float primaryX = clusterX
                + (dual ? targetWidth * PrimaryOffsetShare : 0f);
            float secondaryX = clusterX + targetWidth * SecondaryOffsetShare;
            float sink = interiorHeight * PourSinkShare * sinkAmount;
            float primaryY = surfaceCentre.y
                + primaryHeight * profile.floatingGarnishSurfaceLiftShare
                - sink + interiorWidth * primaryLift;
            float secondaryY = surfaceCentre.y
                + secondaryHeight * profile.floatingGarnishSurfaceLiftShare
                + targetWidth * SecondaryLiftShare
                - sink + interiorWidth * secondaryLift;
            float rimCeiling = profile.mouthLocal.y
                - interiorWidth * RimClearanceShare;
            primaryY = Mathf.Min(primaryY,
                rimCeiling - primaryHeight * 0.5f);
            if (dual)
                secondaryY = Mathf.Min(secondaryY,
                    rimCeiling - secondaryHeight * 0.5f);

            // I keep the foam's lower edge planted and limit expansion to real brim headroom; shrinking stays
            // unrestricted.
            float primaryHeadroom = Mathf.Max(0f,
                rimCeiling - (primaryY + primaryHeight * 0.5f));
            float appliedHeightDelta = Mathf.Min(
                primaryHeightDelta, primaryHeadroom);
            float animatedPrimaryHeight = Mathf.Max(
                primaryHeight * 0.94f,
                primaryHeight + appliedHeightDelta);
            primaryY += (animatedPrimaryHeight - primaryHeight) * 0.5f;
            float primaryVerticalScale = primaryScale * popScale
                * animatedPrimaryHeight / Mathf.Max(0.001f, primaryHeight);

            PlaceGarnishRenderer(primaryRenderer, primaryBounds,
                new Vector2(primaryScale * popScale, primaryVerticalScale),
                new Vector2(primaryX, primaryY),
                primaryRotation + (dual ? PrimaryRestRotation : 0f));
            primaryRenderer.enabled = visualOpacity > 0.001f;
            if (dual)
            {
                PlaceGarnishRenderer(secondaryRenderer, secondaryBounds,
                    Vector2.one * (secondaryScale * popScale),
                    new Vector2(secondaryX, secondaryY),
                    secondaryRotation + SecondaryRestRotation);
                secondaryRenderer.enabled = visualOpacity > 0.001f;
            }
            else
            {
                HideRenderer(secondaryRenderer);
            }

            Color iceColor = new Color(1f, 1f, 1f,
                visualOpacity * profile.floatingGarnishOpacity);
            primaryRenderer.color = iceColor;
            if (dual) secondaryRenderer.color = iceColor;
            ApplyLiquidProperties(primaryRenderer, ref primaryBlock,
                ref primaryLiquidProperties, surfaceCentre, true);
            if (dual)
                ApplyLiquidProperties(secondaryRenderer, ref secondaryBlock,
                    ref secondaryLiquidProperties, surfaceCentre, false);

            // I publish one footprint for both cubes and let the liquid shader clip the reflection to the actual
            // surface and mask.
            float left = primaryX - primaryWidth * popScale * 0.5f;
            float right = primaryX + primaryWidth * popScale * 0.5f;
            if (dual)
            {
                left = Mathf.Min(left,
                    secondaryX - secondaryWidth * popScale * 0.5f);
                right = Mathf.Max(right,
                    secondaryX + secondaryWidth * popScale * 0.5f);
            }
            float causticHalfWidth = Mathf.Max(0.005f,
                (right - left) * LiquidLightWidthScale * 0.5f);
            float causticDepth = VesselPresentationMath.RoyalPixelsToLocal(
                LiquidLightDepthRoyalPixels, profile);
            float causticAmount = visualOpacity
                * profile.floatingGarnishOpacity
                * profile.floatingGarnishLiquidLightStrength;
            bottle.SetFloatingGarnishCaustic(new Vector4(
                (left + right) * 0.5f,
                causticHalfWidth,
                causticDepth,
                causticAmount));

            ApplyCondensationPose(profile, visualOpacity);
        }

        private void PlaceGarnishRenderer(SpriteRenderer renderer, Bounds bounds,
            Vector2 scale, Vector2 centre, float rotationDegrees)
        {
            Transform garnishTransform = renderer.transform;
            garnishTransform.localScale = new Vector3(scale.x, scale.y, 1f);
            Quaternion worldRotation = Quaternion.Euler(
                0f, 0f, rotationDegrees);
            garnishTransform.rotation = worldRotation;
            Vector3 visualCenterWorld = bottle.LiquidFrameToWorld(centre);
            Vector3 scaledBoundsCenter = Vector3.Scale(
                bounds.center, garnishTransform.lossyScale);
            garnishTransform.position = visualCenterWorld
                - worldRotation * scaledBoundsCenter;
        }

        private static float RevealScale(float progress)
        {
            float remaining = Mathf.Clamp01(progress) - 1f;
            float spring = 1f + 3.6f * remaining * remaining * remaining
                + 2.6f * remaining * remaining;
            return Mathf.LerpUnclamped(0.70f, 1f, spring);
        }

        private void ApplyLiquidProperties(SpriteRenderer renderer,
            ref MaterialPropertyBlock propertyBlock,
            ref AppliedLiquidProperties applied, Vector2 surfaceCentre,
            bool allowFoamBubble)
        {
            if (renderer == null || configuredIceMaterial == null) return;
            float surfaceWorldY = bottle.LiquidFrameToWorld(surfaceCentre).y;
            Color liquidColor = bottle.VisualTopColor;
            Vector4 foamBubbleState = allowFoamBubble && foamFxEnabled
                ? new Vector4(foamBubbleStrength, foamBubbleProgress, 0f, 0f)
                : Vector4.zero;
            if (applied.valid
                && applied.renderer == renderer
                && applied.surfaceWorldY == surfaceWorldY
                && applied.liquidColor.Equals(liquidColor)
                && applied.foamBubbleState.Equals(foamBubbleState))
                return;

            propertyBlock ??= new MaterialPropertyBlock();
            renderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetFloat(SurfaceWorldYId, surfaceWorldY);
            propertyBlock.SetColor(LiquidColorId, liquidColor);
            propertyBlock.SetVector(FoamBubbleStateId, foamBubbleState);
            renderer.SetPropertyBlock(propertyBlock);

            applied.valid = true;
            applied.renderer = renderer;
            applied.surfaceWorldY = surfaceWorldY;
            applied.liquidColor = liquidColor;
            applied.foamBubbleState = foamBubbleState;
        }

        private void ApplyCondensationPose(VesselProfile profile,
            float visibility)
        {
            if (condensationRenderer == null
                || condensationRenderer.sprite == null)
                return;

            Bounds bounds = condensationVisualBounds.size.sqrMagnitude > 0.0001f
                ? condensationVisualBounds
                : condensationRenderer.sprite.bounds;
            float interiorWidth = Mathf.Max(0.01f,
                profile.interiorBounds.width);
            float interiorHeight = Mathf.Max(0.01f,
                profile.interiorBounds.height);
            float targetWidth = interiorWidth
                * Mathf.Max(0.01f, profile.condensationWidthShare);
            float scale = targetWidth / Mathf.Max(0.001f, bounds.size.x);
            Vector2 anchor = profile.interiorBounds.center + new Vector2(
                interiorWidth * profile.condensationPositionShare.x,
                interiorHeight * profile.condensationPositionShare.y);

            Transform condensationTransform = condensationRenderer.transform;
            condensationTransform.localScale = Vector3.one * scale;
            condensationTransform.localRotation = Quaternion.identity;
            condensationTransform.localPosition = new Vector3(
                anchor.x - bounds.center.x * scale,
                anchor.y - bounds.center.y * scale,
                -0.01f);
            condensationRenderer.color = new Color(1f, 1f, 1f,
                visibility * profile.condensationOpacity);
            condensationRenderer.enabled = visibility > 0.001f;
        }

        private static void HideRenderer(SpriteRenderer renderer)
        {
            if (renderer == null) return;
            renderer.enabled = false;
            Color hidden = renderer.color;
            hidden.a = 0f;
            renderer.color = hidden;
        }

        private static void SetRendererEnabled(SpriteRenderer renderer,
            bool value)
        {
            if (renderer != null) renderer.enabled = value;
        }

        private static Bounds ResolveVisualBounds(Sprite sprite)
        {
            if (sprite == null) return default;
            Vector2[] vertices = sprite.vertices;
            if (vertices == null || vertices.Length == 0) return sprite.bounds;

            Vector3 min = new Vector3(vertices[0].x, vertices[0].y, 0f);
            Vector3 max = min;
            for (int i = 1; i < vertices.Length; i++)
            {
                Vector3 point = new Vector3(vertices[i].x, vertices[i].y, 0f);
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }

            var result = new Bounds();
            result.SetMinMax(min, max);
            return result;
        }
    }
}
