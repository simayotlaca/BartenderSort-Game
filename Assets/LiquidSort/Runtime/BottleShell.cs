using UnityEngine;

namespace LiquidSort
{
    /// <summary>I draw the baked shell around LiquidBottle: halo (-2), contact shadow (-1), liquid (1), front glass (5) and thin FX (7).</summary>
    [ExecuteAlways]
    [RequireComponent(typeof(LiquidBottle))]
    [DisallowMultipleComponent]
    public sealed class BottleShell : MonoBehaviour
    {
        private const float ShadowFadeOutSeconds = 0.075f;
        private const float ShadowFadeInSeconds = 0.10f;

        [Tooltip("Glass wall thickness. The reference art uses 6.3% of the interior width.")]
        public float wallThickness = 0.063f;
        [Tooltip("Scene-level glass colours. Assign the shared theme on authored or pooled shells. Empty falls back to neutral defaults.")]
        public GlassVisualTheme theme;
        [Tooltip("Fallback contour material when the profile has none.")]
        public Material contourMaterial;
        public int frontOrder = 5;
        [Header("Thin glass FX")]
        [Tooltip("Fallback shared thin-FX material when a baked VesselProfile does not provide one.")]
        public Material thinGlassFxMaterial;
        [Tooltip("Resting strength of the profile's authored-alpha side light.")]
        [Range(0f, 1f)] public float thinFxIntensity = 0.24f;
        [Tooltip("Extra thin-FX strength while selected. The liquid centre is never part of this layer.")]
        [Range(0f, 1f)] public float thinFxSelectionBoost = 0.08f;
        [Tooltip("Per-vessel multiplier for the narrow visible-floor seam light. Zero is a diagnostic off switch; one uses the scene theme value.")]
        [Range(0f, 2f)] public float floorSeamLightScale = 1f;
        [Tooltip("Per-vessel multiplier for authored reflections on glass-only parts such as handles, stems, feet and the outer lip.")]
        [Range(0f, 2f)] public float accessoryLightScale = 1f;
        [Tooltip("Per-vessel multiplier for the profile's thin lower silhouette highlight.")]
        [Range(0f, 2f)] public float bottomRimLightScale = 1f;
        public int thinFxOrder = 7;

        [Header("Contact shadow")]
        // I add a contact shadow so the glass sits on the table instead of floating.
        public bool drawShadow = true;
        public int shadowOrder = -1;
        [Range(0f, 1f)] public float shadowStrength = 0.40f;

        /// <summary>0 at rest, 1 when picked up. Fades shelf contact and drives ThinGlassFX without rebuilding art.</summary>
        [System.NonSerialized] public float highlight;
        private bool shadowMotionSuppressed;
        private float shadowVisibilitySuppression;
        private int shadowFadeFrame = -1;

        private LiquidBottle bottle;
        private SpriteRenderer front;
        private SpriteRenderer thinGlassFx;
        private VesselPresentationSlots presentationSlots;
        private SpriteRenderer contactShadow;
        private SpriteRenderer softHalo;
        private bool built;
        private bool visualsDirty;
        private bool bakedContractErrorReported;
        private bool authoredHierarchyErrorReported;
        private bool staticBakedPlayModeFastPath;
        private VesselProfile fastPathProfile;
        private Sprite fastPathAuthoredFront;
        private Material fastPathFrontMaterial;
        private SpriteRenderer fastPathThinFxRenderer;
        private Sprite fastPathThinFxSprite;
        private Material fastPathThinFxMaterial;
        private bool fastPathThinFxEnabled;
        private int fastPathThinFxSortingLayerId;
        private MaterialPropertyBlock contourBlock;
        private MaterialPropertyBlock authoredFrontBlock;
        private MaterialPropertyBlock thinFxBlock;
        private static readonly int ContourDarkId = Shader.PropertyToID("_ContourDark");
        private static readonly int ContourLightId = Shader.PropertyToID("_ContourLight");
        private static readonly int LightAngleId = Shader.PropertyToID("_LightAngle");
        private static readonly int ContourSpecularId = Shader.PropertyToID("_SpecularColor");
        private static readonly int InteriorRectId = Shader.PropertyToID("_InteriorRect");
        private static readonly int FxKeyColorId = Shader.PropertyToID("_FxColor");
        private static readonly int FxFillColorId = Shader.PropertyToID("_FxColor2");
        private static readonly int FxSideStrengthId = Shader.PropertyToID("_SideStrength");
        private static readonly int FxBottomStrengthId = Shader.PropertyToID("_BottomStrength");
        private static readonly int ContourAccessoryFxId = Shader.PropertyToID("_AccessoryFx");
        private static readonly int ContourContactStrengthId = Shader.PropertyToID("_ContactStrength");
        private static readonly int ContourBottomRimStrengthId = Shader.PropertyToID("_BottomRimStrength");
        private static readonly int ContourRimHotspotStrengthId = Shader.PropertyToID("_RimHotspotStrength");
        private static readonly int ContourLiquidBounceColorId = Shader.PropertyToID("_LiquidBounceColor");
        private static readonly int ContourLiquidBounceStrengthId = Shader.PropertyToID("_LiquidBounceStrength");
        private static readonly int ContourPaintedToyStrengthId = Shader.PropertyToID("_PaintedToyStrength");
        private static readonly int ContourToyMidColorId = Shader.PropertyToID("_ToyMidColor");
        private static readonly int ContourToyFillColorId = Shader.PropertyToID("_ToyFillColor");
        private static readonly int BackRightInteriorClipId =
            Shader.PropertyToID("_RightInteriorClip");
        private static readonly int AuthoredSpriteUvRectId =
            Shader.PropertyToID("_SpriteUvRect");
        private static readonly int AuthoredSpriteLocalRectId =
            Shader.PropertyToID("_SpriteLocalRect");
        private static readonly int AuthoredMouthEllipseId =
            Shader.PropertyToID("_MouthEllipse");
        private static readonly int AuthoredWidthExpansionId =
            Shader.PropertyToID("_AuthoredWidthExpansion");
        private static readonly int AuthoredReflectionTrimId =
            Shader.PropertyToID("_AuthoredReflectionTrim");
        private static readonly int AuthoredInteractionHighlightId =
            Shader.PropertyToID("_InteractionHighlight");
        private static readonly int AuthoredGlassPartBackingId =
            Shader.PropertyToID("_GlassPartBacking");
        private static readonly int FxVisibleFloorYId = Shader.PropertyToID("_VisibleFloorY");
        private static readonly int FxVisibleBottomYId = Shader.PropertyToID("_VisibleBottomY");
        private static readonly int InnerWidthId = Shader.PropertyToID("_InnerWidth");
        private static readonly int AuthoredPassthroughId =
            Shader.PropertyToID("_AuthoredPassthrough");
        private static readonly int PearlModeId = Shader.PropertyToID("_PearlMode");
        private static readonly int PreserveAuthoredGeometryId =
            Shader.PropertyToID("_PreserveAuthoredGeometry");
        private static readonly int StaticBakedFrontId =
            Shader.PropertyToID("_StaticBakedFront");

        /// <summary>True only when the shader rebuilds and widens the glass. I match its authoredProcessing * (1 - preserveAuthoredGeometry) gate.</summary>
        private static bool AuthoredFrontIsReconstructed(Material authoredMaterial)
        {
            if (authoredMaterial == null) return false;
            if (IsStaticBakedFront(authoredMaterial)) return false;

            float Read(int id) =>
                authoredMaterial.HasProperty(id) ? authoredMaterial.GetFloat(id) : 0f;

            return Read(AuthoredPassthroughId) < 0.5f
                && Read(PearlModeId) < 0.5f
                && Read(PreserveAuthoredGeometryId) < 0.5f;
        }

        private static bool IsStaticBakedFront(Material material) =>
            material != null
            && material.HasProperty(StaticBakedFrontId)
            && material.GetFloat(StaticBakedFrontId) >= 0.5f;
        private static readonly int FxMaskTexId = Shader.PropertyToID("_MaskTex");
        private static readonly int FxMaskRectId = Shader.PropertyToID("_MaskRect");
        private static readonly int FxMaskReachId = Shader.PropertyToID("_MaskReach");
        private static readonly int FxUseMaskId = Shader.PropertyToID("_UseMask");
        private bool bounceApplied;
        private Color appliedBounceColor;
        private float appliedBounceStrength = -1f;
        private float appliedAuthoredHighlight = -1f;
        private BottleCompletionEffect activeCompletionEffect;
        private long nextCompletionPresentationId;
        private long activeCompletionPresentationId;

        /// <summary>Colours for this glass. Neutral defaults when no theme is assigned.</summary>
        private GlassVisualTheme.Settings Theme =>
            theme != null ? theme.settings : GlassVisualTheme.Settings.Default;
        private int builtSettingsHash;
        private int builtGeometryHash;
        private int appliedSortingHash;

        private void OnEnable()
        {
            // I retain pooled shell sprites; SettingsHash still catches profile or visual changes made while
            // hidden.
            if (!built)
            {
                builtSettingsHash = 0;
                builtGeometryHash = 0;
            }
            appliedSortingHash = 0;
            bounceApplied = false;
            appliedBounceStrength = -1f;
            appliedAuthoredHighlight = -1f;
            shadowMotionSuppressed = false;
            shadowVisibilitySuppression = 0f;
            shadowFadeFrame = -1;
            ResolveAuthoredShadowRenderers();
            ApplyShadowVisibility();
            // I restore draw orders on reuse because the idle fast path may skip the next Refresh.
            if (built) ApplySorting();
        }

        private void OnDisable()
        {
            highlight = 0f;
            shadowMotionSuppressed = false;
            shadowVisibilitySuppression = 0f;
            shadowFadeFrame = -1;
            appliedAuthoredHighlight = -1f;
            StopCompletionPresentation();
            // I keep baked shell children when the whole object is pooled with SetActive(false).
            if (Application.isPlaying && !gameObject.activeInHierarchy) return;

            // I clear this child when the component is disabled so OnEnable rebuilds its material, sprite and rect
            // together.
            DisableThinFx();
            if (softHalo != null) softHalo.enabled = false;
            if (contactShadow != null) contactShadow.enabled = false;
            built = false;
            staticBakedPlayModeFastPath = false;
        }

        private void OnValidate()
        {
            wallThickness = Mathf.Max(0.001f, wallThickness);
            floorSeamLightScale = Mathf.Clamp(floorSeamLightScale, 0f, 2f);
            accessoryLightScale = Mathf.Clamp(accessoryLightScale, 0f, 2f);
            bottomRimLightScale = Mathf.Clamp(bottomRimLightScale, 0f, 2f);
            built = false;
            visualsDirty = true;
            staticBakedPlayModeFastPath = false;
        }

        private void LateUpdate()
        {
            // I skip full checks for healthy baked fronts in play mode. Only shadow suppression and selection
            // alpha stay live; edit mode checks everything.
            if (!Application.isPlaying || !built || visualsDirty
                || !staticBakedPlayModeFastPath
                || StaticBakedContractChanged())
            {
                Refresh();
                return;
            }

            if (ShadowVisibilityNeedsUpdate()) ApplyShadowVisibility();
            ApplyThinFxHighlight();
        }

        /// <summary>I ensure shell layers exist without rebuilding cached art, so the shelf can prepare liquid and glass in the activation frame.</summary>
        public void Refresh()
        {
            LiquidBottle current = GetComponent<LiquidBottle>();
            int wantedHash = SettingsHash(current);
            if (!built || visualsDirty || wantedHash != builtSettingsHash
                || RenderersNeedRefresh(current))
                Build();

            ApplySorting();
            ApplyHighlight();
            UpdatePlayModeFastPathEligibility(current);
        }

        /// <summary>I apply authored draw orders without allocations or repainting.</summary>
        private void ApplySorting()
        {
            // I update orders only when they change so I do not undo LiquidBottle's pour sorting offset.
            int wanted = unchecked(((frontOrder * 397 + thinFxOrder) * 397
                                    + shadowOrder));
            if (wanted == appliedSortingHash) return;
            appliedSortingHash = wanted;

            if (front != null) front.sortingOrder = frontOrder;
            if (thinGlassFx != null) thinGlassFx.sortingOrder = thinFxOrder;
            if (softHalo != null) softHalo.sortingOrder = shadowOrder - 1;
            if (contactShadow != null) contactShadow.sortingOrder = shadowOrder;

            // I reset the bottle's relative-order cache after changing the base orders.
            GetComponent<LiquidBottle>()?.InvalidateRenderers();
        }

        /// <summary>I update liquid reflections, front state, shadows and the baked front's thin reflection pass.</summary>
        private void ApplyHighlight()
        {
            ApplyLiquidBounce();
            ApplyAuthoredFrontState();
            ApplyShadowVisibility();

            ApplyThinFxHighlight();
        }

        private void ApplyThinFxHighlight()
        {
            if (thinGlassFx != null && thinGlassFx.enabled)
            {
                // I use the scene theme's side light with 0.14 as a minimum so older shelf instances stay
                // readable.
                float baseThinAmount = Mathf.Max(thinFxIntensity,
                    Mathf.Clamp01(Theme.sideFxStrength * 0.55f));
                float thinAmount = Mathf.Clamp01(baseThinAmount
                    + Mathf.Clamp01(highlight) * thinFxSelectionBoost);
                Color thinWanted = new Color(1f, 1f, 1f, thinAmount);
                if (thinGlassFx.color != thinWanted) thinGlassFx.color = thinWanted;
            }
        }

        /// <summary>PourAnimator owns this flag until the source returns. I keep it out of the settings hash so visibility cannot regenerate shadows.</summary>
        internal void SetShadowMotionSuppressed(bool suppressed)
        {
            shadowMotionSuppressed = suppressed;
        }

        private void ApplyShadowVisibility()
        {
            float targetSuppression = ShadowVisibilityTarget();
            if (Application.isPlaying && shadowFadeFrame != Time.frameCount)
            {
                float duration = targetSuppression > shadowVisibilitySuppression
                    ? ShadowFadeOutSeconds
                    : ShadowFadeInSeconds;
                shadowVisibilitySuppression = Mathf.MoveTowards(
                    shadowVisibilitySuppression, targetSuppression,
                    Time.unscaledDeltaTime / Mathf.Max(0.001f, duration));
                shadowFadeFrame = Time.frameCount;
            }
            else if (!Application.isPlaying)
            {
                shadowVisibilitySuppression = targetSuppression;
            }

            float visibility = 1f - shadowVisibilitySuppression;
            GlassVisualTheme.Settings settings = Theme;
            ApplyShadowLayerVisibility(softHalo, settings.wideShadowColor,
                settings.wideShadowStrength, visibility);
            ApplyShadowLayerVisibility(contactShadow, settings.shadowColor,
                settings.shadowStrength, visibility);
        }

        private void ApplyShadowLayerVisibility(SpriteRenderer renderer,
                                                Color tint,
                                                float layerStrength,
                                                float visibility)
        {
            if (renderer == null || !renderer.enabled) return;
            tint.a = Mathf.Clamp01(shadowStrength * layerStrength * visibility);
            if (renderer.color != tint) renderer.color = tint;
        }

        private float ShadowVisibilityTarget() => Mathf.Max(
            Mathf.Clamp01(highlight), shadowMotionSuppressed ? 1f : 0f);

        private bool ShadowVisibilityNeedsUpdate() =>
            ((contactShadow != null && contactShadow.enabled)
             || (softHalo != null && softHalo.enabled))
            && Mathf.Abs(shadowVisibilitySuppression - ShadowVisibilityTarget()) > 0.001f;

        /// <summary>I restore the exact shelf pose before <paramref name="onCheckCue"/> opens delivery.</summary>
        public bool PlayCompletionPresentation(BottleCompletionEffect completionEffect,
                                               Transform motionRoot,
                                               Vector3 homePosition,
                                               Quaternion homeRotation,
                                               Vector3 homeLocalScale,
                                               Vector3 liftDirectionWorld,
                                               System.Action onBadgeCue,
                                               System.Action onCheckCue,
                                               System.Action onPresentationFinished)
        {
            if (!Application.isPlaying || completionEffect == null
                || nextCompletionPresentationId == long.MaxValue
                || completionEffect.IsPlaying)
                return false;
            if (bottle == null) bottle = GetComponent<LiquidBottle>();
            if (bottle == null) return false;

            BottleCompletionEffect requestedEffect = completionEffect;
            long presentationId = ++nextCompletionPresentationId;
            activeCompletionEffect = requestedEffect;
            activeCompletionPresentationId = presentationId;
            System.Action finish = () =>
            {
                if (activeCompletionPresentationId == presentationId)
                {
                    activeCompletionEffect = null;
                    activeCompletionPresentationId = 0L;
                }
                onPresentationFinished?.Invoke();
            };
            bool started = false;
            try
            {
                started = requestedEffect.Play(
                    bottle, this, motionRoot, homePosition, homeRotation,
                    homeLocalScale, liftDirectionWorld, onBadgeCue, onCheckCue,
                    finish);
                return started;
            }
            finally
            {
                // A failed Play can finish synchronously and its callback can reuse the same pooled effect.
                // Clear only this request's ownership, including exceptions before the effect takes ownership.
                if (!started && activeCompletionPresentationId == presentationId)
                {
                    activeCompletionEffect = null;
                    activeCompletionPresentationId = 0L;
                }
            }
        }

        /// <summary>Cancels only completion presentation; pour/reveal tweens are untouched.</summary>
        public void StopCompletionPresentation()
        {
            StopCompletionPresentation(true);
        }

        /// <summary>I cancel completion visuals and callbacks; level changes can skip restoring the old motion pose.</summary>
        public void StopCompletionPresentation(bool restoreMotion)
        {
            BottleCompletionEffect completionEffect = activeCompletionEffect;
            activeCompletionEffect = null;
            activeCompletionPresentationId = 0L;
            if (completionEffect != null) completionEffect.Stop(restoreMotion);
        }

        private void ApplyAuthoredFrontState()
        {
            if (front == null || bottle == null || front.sharedMaterial == null
                || !UsesAuthoredFront(ResolveFront(bottle))) return;
            if (IsStaticBakedFront(front.sharedMaterial)) return;

            float wantedHighlight = Mathf.Clamp01(highlight);
            if (Mathf.Abs(appliedAuthoredHighlight - wantedHighlight) < 0.001f)
                return;

            authoredFrontBlock ??= new MaterialPropertyBlock();
            front.GetPropertyBlock(authoredFrontBlock);
            authoredFrontBlock.SetFloat(
                AuthoredInteractionHighlightId, wantedHighlight);
            front.SetPropertyBlock(authoredFrontBlock);
            appliedAuthoredHighlight = wantedHighlight;
        }

        /// <summary>I send the bottom liquid colour to the contour each frame without rebuilding art.</summary>
        private void ApplyLiquidBounce()
        {
            if (front == null || bottle == null || front.sharedMaterial == null) return;
            if (UsesAuthoredFront(ResolveFront(bottle))) return;

            GlassVisualTheme.Settings settings = Theme;
            Color source = bottle.VisualBottomColor;
            float profileScale = bottle.Profiled
                ? bottle.profile.liquidBounceScale
                : 1f;
            float strength = source.a > 0.001f
                ? settings.liquidBounceStrength * profileScale
                    * bottle.VisualBottomPresence
                : 0f;
            Color bounce = strength > 0.001f
                ? LiquidPalette.CapFor(source)
                : Color.clear;
            bounce.a = 1f;

            if (bounceApplied && Nearly(appliedBounceColor, bounce)
                              && Mathf.Abs(appliedBounceStrength - strength) < 0.001f)
                return;

            contourBlock ??= new MaterialPropertyBlock();
            front.GetPropertyBlock(contourBlock);
            contourBlock.SetColor(ContourLiquidBounceColorId, bounce);
            contourBlock.SetFloat(ContourLiquidBounceStrengthId, strength);
            front.SetPropertyBlock(contourBlock);

            appliedBounceColor = bounce;
            appliedBounceStrength = strength;
            bounceApplied = true;
        }

        private static bool Nearly(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 0.001f && Mathf.Abs(a.g - b.g) < 0.001f
            && Mathf.Abs(a.b - b.b) < 0.001f && Mathf.Abs(a.a - b.a) < 0.001f;

        private void ConfigureAuthoredFrontGeometry(Sprite sprite, Rect liquidBounds)
        {
            if (sprite == null || authoredFrontBlock == null) return;

            Bounds spriteBounds = sprite.bounds;
            Vector2 localMin = spriteBounds.min;
            Vector2 localSize = spriteBounds.size;
            localSize.x = Mathf.Max(0.0001f, localSize.x);
            localSize.y = Mathf.Max(0.0001f, localSize.y);

            Vector2 uvMin = Vector2.zero;
            Vector2 uvMax = Vector2.one;
            Vector2[] spriteUvs = sprite.uv;
            if (spriteUvs != null && spriteUvs.Length > 0)
            {
                uvMin = spriteUvs[0];
                uvMax = spriteUvs[0];
                for (int i = 1; i < spriteUvs.Length; i++)
                {
                    uvMin = Vector2.Min(uvMin, spriteUvs[i]);
                    uvMax = Vector2.Max(uvMax, spriteUvs[i]);
                }
            }
            Vector2 uvSize = Vector2.Max(
                uvMax - uvMin, new Vector2(0.000001f, 0.000001f));

            authoredFrontBlock.SetVector(AuthoredSpriteUvRectId,
                new Vector4(uvMin.x, uvMin.y, uvSize.x, uvSize.y));
            authoredFrontBlock.SetVector(AuthoredSpriteLocalRectId,
                new Vector4(localMin.x, localMin.y, localSize.x, localSize.y));
            authoredFrontBlock.SetFloat(AuthoredWidthExpansionId,
                bottle.Profiled ? bottle.profile.AuthoredFrontWidthExpansion : 0f);
            authoredFrontBlock.SetFloat(AuthoredReflectionTrimId,
                bottle.Profiled ? bottle.profile.AuthoredReflectionTrim : 0f);
            authoredFrontBlock.SetVector(InteriorRectId, new Vector4(
                liquidBounds.xMin, liquidBounds.yMin,
                liquidBounds.xMax, liquidBounds.yMax));

            if (bottle.Profiled && bottle.profile.interiorMask != null)
            {
                Rect maskRect = bottle.profile.QuadRect;
                authoredFrontBlock.SetTexture(
                    FxMaskTexId, bottle.profile.interiorMask);
                authoredFrontBlock.SetVector(FxMaskRectId, new Vector4(
                    maskRect.xMin, maskRect.yMin,
                    maskRect.width, maskRect.height));
                authoredFrontBlock.SetFloat(FxUseMaskId, 1f);
            }
            else
            {
                authoredFrontBlock.SetTexture(FxMaskTexId, null);
                authoredFrontBlock.SetFloat(FxUseMaskId, 0f);
            }
            authoredFrontBlock.SetVector(BackRightInteriorClipId,
                bottle.Profiled && bottle.profile.clipRightInterior
                    ? new Vector4(1f,
                        bottle.profile.rightInteriorXAtY0,
                        bottle.profile.rightInteriorSlope, 0f)
                    : Vector4.zero);

            // I share the liquid's baked floor curve so the glass shader tints only the thick base below it.
            bool hasLiquidFloorCurve = bottle.Profiled
                && bottle.profile.HasLiquidFloorCurve;
            authoredFrontBlock.SetVector(
                LiquidSurfaceContract.LiquidFloorRangeId,
                hasLiquidFloorCurve
                    ? new Vector4(bottle.profile.liquidFloorXRange.x,
                        bottle.profile.liquidFloorXRange.y, 1f, 0f)
                    : Vector4.zero);
            if (hasLiquidFloorCurve)
            {
                authoredFrontBlock.SetFloatArray(
                    LiquidSurfaceContract.LiquidFloorSamplesId,
                    bottle.profile.liquidFloorSamples);
            }

            float mouthHalfWidth = Mathf.Max(0f, bottle.mouthHalfWidth);
            if (mouthHalfWidth <= 0.0001f)
            {
                authoredFrontBlock.SetVector(AuthoredMouthEllipseId, Vector4.zero);
                return;
            }

            float bulge = bottle.Profiled
                ? bottle.profile.surfaceBulge
                : bottle.surfaceBulge;
            float depthLimit = bottle.Profiled
                ? bottle.profile.maxCapDepth
                : bottle.maxCapDepth;
            float interiorHeight = bottle.Profiled
                ? bottle.profile.interiorBounds.height
                : liquidBounds.height;
            float mouthHalfDepth = Mathf.Min(
                mouthHalfWidth * 2f * bulge,
                Mathf.Max(0.0001f, interiorHeight) * depthLimit);

            Vector2 mouth01 = new Vector2(
                (bottle.mouthLocal.x - localMin.x) / localSize.x,
                (bottle.mouthLocal.y - localMin.y) / localSize.y);
            Vector2 mouthUv = uvMin + Vector2.Scale(mouth01, uvSize);
            Vector2 radiusUv = Vector2.Scale(new Vector2(
                mouthHalfWidth / localSize.x,
                Mathf.Max(0.0001f, mouthHalfDepth) / localSize.y), uvSize);
            authoredFrontBlock.SetVector(AuthoredMouthEllipseId,
                new Vector4(mouthUv.x, mouthUv.y, radiusUv.x, radiusUv.y));

            // I keep the measured liquid alignment in pearl mode too. The shell sends its exact stretch to the
            // liquid so both passes agree.
            float innerEdgePixels = 0f;
            Material authoredMaterial = Theme.authoredFrontMaterial;
            if (authoredMaterial != null && authoredMaterial.HasProperty(InnerWidthId))
                innerEdgePixels = Mathf.Max(0f, authoredMaterial.GetFloat(InnerWidthId));

            float authoredExpansion = bottle.Profiled
                ? bottle.profile.AuthoredFrontWidthExpansion
                : 0f;

            // I disable stretch for passthrough, pearl and preserved geometry, exactly like the glass shader.
            if (!AuthoredFrontIsReconstructed(authoredMaterial))
            {
                innerEdgePixels = 0f;
                authoredExpansion = 0f;
            }
            // I use the mouth as the stretch anchor when a rim ellipse exists, otherwise the sprite centre,
            // matching the shader.
            float anchorX = radiusUv.x > 1e-6f
                ? bottle.mouthLocal.x
                : localMin.x + localSize.x * 0.5f;

            bottle.SetGlassExpansion(new Vector4(
                anchorX, localSize.x, innerEdgePixels + authoredExpansion, 0f));
        }

        public void Build()
        {
            bottle = GetComponent<LiquidBottle>();
            if (bottle == null || !bottle.Profiled || bottle.profile.front == null
                || bottle.profile.interiorMask == null)
            {
                if (!bakedContractErrorReported)
                {
                    Debug.LogError(
                        $"{name}: BottleShell requires a baked VesselProfile with "
                      + "front and interiorMask assets. Runtime glass generation was "
                      + "removed intentionally.", this);
                    bakedContractErrorReported = true;
                }
                if (front != null) front.enabled = false;
                DisableThinFx();
                built = false;
                staticBakedPlayModeFastPath = false;
                return;
            }
            bakedContractErrorReported = false;

            int geometryHash = GeometryHash(bottle);
            if (geometryHash != builtGeometryHash)
            {
                // I invalidate liquid caches before a runtime profile swap because it does not call OnValidate.
                bottle.Invalidate();
            }

            Rect bounds = bottle.InteriorBounds;
            Sprite authoredFront = ResolveFront(bottle);
            ResolveFloorRange(bottle, bounds, out float opticalFloor, out float visibleBottom);
            float bottomInteriorInset = bottle.Profiled
                ? VesselPresentationMath.RoyalPixelsToLocal(
                    bottle.profile.bottomInteriorInsetPixels, bottle.profile)
                : 0f;
            float wall = wallThickness <= 1f ? wallThickness * bounds.width : wallThickness;
            Vector4 accessoryFx = Vector4.zero;
            float bottomRimStrength = 0f;
            Vector4 glassPartBacking = Vector4.zero;
            if (bottle.Profiled)
            {
                float partFeather = Mathf.Max(
                    0.005f, bottle.profile.accessoryGlassLightFeather);
                accessoryFx = new Vector4(
                    bottle.profile.handleGlassLight * accessoryLightScale,
                    bottle.profile.stemFootGlassLight * accessoryLightScale,
                    partFeather,
                    bottle.profile.stemFootToonStrength);
                bottomRimStrength =
                    bottle.profile.bottomRimGlassLight * bottomRimLightScale;
                glassPartBacking = new Vector4(
                    bottle.profile.stemFootGlassBacking,
                    bottle.profile.bottomGlassBacking,
                    partFeather, 0f);
            }

            GlassVisualTheme.Settings themeNow = Theme;
            ResolveAuthoredShadowRenderers();
            bool wantsContactShadow = drawShadow && shadowStrength > 0.001f
                                   && themeNow.shadowStrength > 0.001f;
            bool wantsSoftHalo = drawShadow && shadowStrength > 0.001f
                              && themeNow.wideShadowStrength > 0.001f;
            ConfigureAuthoredShadowLayer(
                softHalo, wantsSoftHalo, shadowOrder - 1);
            ConfigureAuthoredShadowLayer(
                contactShadow, wantsContactShadow, shadowOrder);

            front = ResolveAuthoredChild("FrontGlass", frontOrder, front);
            if (front == null)
            {
                DisableThinFx();
                built = false;
                staticBakedPlayModeFastPath = false;
                return;
            }
            Sprite nextFront = authoredFront;
            front.sprite = nextFront;

            // I draw the authored PNG as a white-tinted front plate. The property block stabilizes solid alpha
            // while keeping the cavity transparent.
            bool authoredFrontPassThrough = UsesAuthoredFront(authoredFront);
            if (authoredFrontPassThrough)
            {
                front.sharedMaterial = themeNow.authoredFrontMaterial;
                front.color = Color.white;
                if (IsStaticBakedFront(front.sharedMaterial))
                {
                    // I back the translucent feet slightly so orange shelves cannot turn silver glass beige;
                    // cavity and edge alpha stay unchanged.
                    authoredFrontBlock ??= new MaterialPropertyBlock();
                    authoredFrontBlock.Clear();
                    authoredFrontBlock.SetVector(InteriorRectId, new Vector4(
                        bounds.xMin, bounds.yMin, bounds.xMax, bounds.yMax));
                    authoredFrontBlock.SetFloat(FxVisibleBottomYId, visibleBottom);
                    authoredFrontBlock.SetVector(
                        AuthoredGlassPartBackingId, glassPartBacking);
                    front.SetPropertyBlock(authoredFrontBlock);
                    bottle.SetGlassExpansion(new Vector4(0f, 1f, 0f, 0f));
                    appliedAuthoredHighlight = 0f;
                }
                else
                {
                    authoredFrontBlock ??= new MaterialPropertyBlock();
                    authoredFrontBlock.Clear();
                    ConfigureAuthoredFrontGeometry(nextFront, bounds);
                    authoredFrontBlock.SetFloat(FxVisibleBottomYId, visibleBottom);
                    authoredFrontBlock.SetFloat(
                        LiquidSurfaceContract.BottomInteriorInsetId,
                        bottomInteriorInset);
                    authoredFrontBlock.SetFloat(
                        LiquidSurfaceContract.BottomInteriorFloorId,
                        bottle.Profiled ? bottle.profile.DrawnFloorLocal : 0f);
                    authoredFrontBlock.SetFloat(
                        AuthoredInteractionHighlightId, Mathf.Clamp01(highlight));
                    authoredFrontBlock.SetVector(
                        AuthoredGlassPartBackingId, glassPartBacking);
                    front.SetPropertyBlock(authoredFrontBlock);
                    appliedAuthoredHighlight = Mathf.Clamp01(highlight);
                }
                bounceApplied = false;
                appliedBounceStrength = -1f;
            }

            // I recolour strokes with a shared GPU material and per-renderer theme data, without cloning assets.
            Material contour = bottle.Profiled && bottle.profile.contourMaterial != null
                ? bottle.profile.contourMaterial
                : contourMaterial;
            if (!authoredFrontPassThrough && contour != null)
            {
                front.sharedMaterial = contour;
                contourBlock ??= new MaterialPropertyBlock();
                front.GetPropertyBlock(contourBlock);
                contourBlock.SetColor(ContourDarkId, themeNow.contourDark);
                contourBlock.SetColor(ContourLightId, themeNow.contourLight);
                contourBlock.SetFloat(LightAngleId, themeNow.lightDirection);
                contourBlock.SetColor(ContourSpecularId, themeNow.glassKeyLight);
                contourBlock.SetVector(InteriorRectId, new Vector4(
                    bounds.xMin, bounds.yMin, bounds.xMax, bounds.yMax));
                contourBlock.SetVector(ContourAccessoryFxId, accessoryFx);
                contourBlock.SetFloat(FxVisibleFloorYId, opticalFloor);
                contourBlock.SetFloat(FxVisibleBottomYId, visibleBottom);
                contourBlock.SetFloat(ContourContactStrengthId,
                    themeNow.bottomLensStrength * floorSeamLightScale);
                contourBlock.SetFloat(ContourRimHotspotStrengthId,
                    themeNow.rimHotspotStrength);
                // I always publish these values, including zero, because the shared material and property block
                // may retain old settings.
                contourBlock.SetFloat(ContourPaintedToyStrengthId,
                    themeNow.paintedToyStrength);
                contourBlock.SetColor(ContourToyMidColorId, themeNow.toyMidColor);
                contourBlock.SetColor(ContourToyFillColorId, themeNow.toyFillColor);
                contourBlock.SetFloat(ContourBottomRimStrengthId, bottomRimStrength);
                front.SetPropertyBlock(contourBlock);
            }

            // I reuse the front sprite's alpha for side and floor reflections, without generating a texture or
            // reading pixels back.
            if (TryGetThinFx(bottle, out Sprite thinSprite, out Material thinMaterial,
                    out Rect thinInteriorBounds))
            {
                thinGlassFx = ResolveAuthoredChild(
                    "ThinGlassFX", thinFxOrder, thinGlassFx);
                if (thinGlassFx == null)
                {
                    built = false;
                    staticBakedPlayModeFastPath = false;
                    return;
                }
                thinGlassFx.sprite = thinSprite;
                thinGlassFx.sharedMaterial = thinMaterial;

                thinFxBlock ??= new MaterialPropertyBlock();
                thinGlassFx.GetPropertyBlock(thinFxBlock);
                thinFxBlock.SetVector(InteriorRectId, new Vector4(
                    thinInteriorBounds.xMin, thinInteriorBounds.yMin,
                    thinInteriorBounds.xMax, thinInteriorBounds.yMax));
                thinFxBlock.SetColor(FxKeyColorId, themeNow.glassKeyLight);
                thinFxBlock.SetColor(FxFillColorId, themeNow.glassFillLight);
                thinFxBlock.SetFloat(FxSideStrengthId, themeNow.sideFxStrength);
                thinFxBlock.SetFloat(LightAngleId, themeNow.lightDirection);
                // I keep profile part lights separate from scene intensity so stems, feet and mug bases can be
                // tuned individually.
                thinFxBlock.SetVector(ContourAccessoryFxId, accessoryFx);
                thinFxBlock.SetFloat(ContourBottomRimStrengthId, bottomRimStrength);
                // GlassContour handles the seam recolour. ThinFX only adds side reflections to avoid a two-tone
                // base strip.
                thinFxBlock.SetFloat(FxBottomStrengthId, 0f);
                thinFxBlock.SetFloat(FxVisibleFloorYId, opticalFloor);
                thinFxBlock.SetFloat(FxVisibleBottomYId, visibleBottom);
                if (bottle.Profiled && bottle.profile.interiorMask != null)
                {
                    Rect maskRect = bottle.profile.QuadRect;
                    Texture2D maskTexture = bottle.profile.interiorMask;
                    float maskTexel = maskRect.width / Mathf.Max(1, maskTexture.width);
                    float reachLocal = Mathf.Max(wall * 1.15f, maskTexel * 2.5f);
                    thinFxBlock.SetTexture(FxMaskTexId, maskTexture);
                    thinFxBlock.SetVector(FxMaskRectId, new Vector4(
                        maskRect.xMin, maskRect.yMin, maskRect.width, maskRect.height));
                    thinFxBlock.SetVector(FxMaskReachId, new Vector4(
                        reachLocal / Mathf.Max(maskRect.width, 1e-4f),
                        reachLocal / Mathf.Max(maskRect.height, 1e-4f), 0f, 0f));
                    thinFxBlock.SetFloat(FxUseMaskId, 1f);
                }
                else
                {
                    thinFxBlock.SetFloat(FxUseMaskId, 0f);
                }
                thinGlassFx.SetPropertyBlock(thinFxBlock);
                ApplyHighlight();
            }
            else
            {
                DisableThinFx();
            }

            // New child renderers need a fresh sorting cache so the whole bottle lifts together.
            bottle.InvalidateRenderers();

            builtGeometryHash = geometryHash;
            builtSettingsHash = SettingsHash(bottle);
            built = true;
            visualsDirty = false;
            authoredHierarchyErrorReported = false;
            UpdatePlayModeFastPathEligibility(bottle);
        }

        private void UpdatePlayModeFastPathEligibility(LiquidBottle current)
        {
            Sprite authoredFront = ResolveFront(current);
            bool wantsThinFx = TryGetThinFx(current, out Sprite wantedThinFxSprite,
                out Material wantedThinFxMaterial, out _);
            int wantedLayer = current != null
                ? SortingLayer.NameToID(current.sortingLayer)
                : 0;
            bool thinFxReady = wantsThinFx
                ? thinGlassFx != null
                    && thinGlassFx.enabled
                    && thinGlassFx.sprite == wantedThinFxSprite
                    && thinGlassFx.sharedMaterial == wantedThinFxMaterial
                    && thinGlassFx.sortingLayerID == wantedLayer
                : thinGlassFx == null || !thinGlassFx.enabled;
            fastPathProfile = current != null ? current.profile : null;
            fastPathAuthoredFront = authoredFront;
            fastPathFrontMaterial = front != null ? front.sharedMaterial : null;
            fastPathThinFxRenderer = thinGlassFx;
            fastPathThinFxSprite = thinGlassFx != null ? thinGlassFx.sprite : null;
            fastPathThinFxMaterial = thinGlassFx != null
                ? thinGlassFx.sharedMaterial
                : null;
            fastPathThinFxEnabled = thinGlassFx != null && thinGlassFx.enabled;
            fastPathThinFxSortingLayerId = thinGlassFx != null
                ? thinGlassFx.sortingLayerID
                : 0;
            staticBakedPlayModeFastPath = Application.isPlaying
                && built
                && !visualsDirty
                && current != null
                && current.Profiled
                && front != null
                && front.enabled
                && front.sprite == authoredFront
                && UsesAuthoredFront(authoredFront)
                && IsStaticBakedFront(front.sharedMaterial)
                && thinFxReady;
        }

        private bool StaticBakedContractChanged()
        {
            ResolveAuthoredShadowRenderers();
            // I compare baked references to catch runtime profile swaps cheaply. Scalar edits still use public
            // Refresh.
            return bottle == null
                || bottle.profile != fastPathProfile
                || fastPathProfile == null
                || fastPathProfile.front != fastPathAuthoredFront
                || front == null
                || !front.enabled
                || front.sprite != fastPathAuthoredFront
                || front.sharedMaterial != fastPathFrontMaterial
                || !IsStaticBakedFront(fastPathFrontMaterial)
                || (fastPathThinFxEnabled && thinGlassFx == null)
                || thinGlassFx != fastPathThinFxRenderer
                || (fastPathThinFxRenderer != null
                    && (thinGlassFx.enabled != fastPathThinFxEnabled
                        || thinGlassFx.sprite != fastPathThinFxSprite
                        || thinGlassFx.sharedMaterial != fastPathThinFxMaterial
                        || thinGlassFx.sortingLayerID
                            != fastPathThinFxSortingLayerId))
                || AuthoredShadowLayerContractChanged(
                    contactShadow,
                    drawShadow && shadowStrength > 0.001f
                        && Theme.shadowStrength > 0.001f)
                || AuthoredShadowLayerContractChanged(
                    softHalo,
                    drawShadow && shadowStrength > 0.001f
                        && Theme.wideShadowStrength > 0.001f);
        }

        private bool AuthoredShadowLayerContractChanged(SpriteRenderer renderer,
                                                        bool wanted)
        {
            // Build cannot repair a missing authored slot; I mark it absent instead of rebuilding every frame.
            if (renderer == null) return false;
            bool shouldEnable = wanted && renderer.sprite != null;
            if (renderer.enabled != shouldEnable) return true;
            if (!shouldEnable) return false;
            int layer = bottle != null
                ? SortingLayer.NameToID(bottle.sortingLayer)
                : 0;
            return renderer.sortingLayerID != layer;
        }

        private void ResolveAuthoredShadowRenderers()
        {
            if (presentationSlots == null)
                presentationSlots = GetComponentInChildren<VesselPresentationSlots>(true);

            contactShadow = presentationSlots != null
                ? presentationSlots.ContactShadow
                : null;
            softHalo = presentationSlots != null
                ? presentationSlots.SoftHalo
                : null;
        }

        private void ConfigureAuthoredShadowLayer(SpriteRenderer renderer,
                                                  bool wanted,
                                                  int sortingOrder)
        {
            if (renderer == null) return;

            bool shouldEnable = wanted && renderer.sprite != null;
            renderer.enabled = shouldEnable;
            if (!shouldEnable) return;

            renderer.sortingLayerName = bottle.sortingLayer;
            renderer.sortingOrder = sortingOrder;
        }

        private SpriteRenderer ResolveAuthoredChild(
            string childName, int order, SpriteRenderer cached)
        {
            SpriteRenderer renderer = cached != null
                && cached.transform.parent == transform
                && cached.name == childName
                    ? cached
                    : transform.Find(childName)?.GetComponent<SpriteRenderer>();
            if (renderer == null)
            {
                if (!authoredHierarchyErrorReported)
                {
                    Debug.LogError(
                        $"{name}: authored '{childName}' child with SpriteRenderer "
                      + "is missing. BottleShell will not create hierarchy at runtime.",
                        this);
                    authoredHierarchyErrorReported = true;
                }
                return null;
            }

            Transform rendererTransform = renderer.transform;
            rendererTransform.localPosition = Vector3.zero;
            rendererTransform.localRotation = Quaternion.identity;
            rendererTransform.localScale = Vector3.one;
            renderer.sortingOrder = order;
            renderer.sortingLayerName = bottle.sortingLayer;
            renderer.enabled = true;
            return renderer;
        }

        private void DisableThinFx()
        {
            if (thinGlassFx == null)
            {
                Transform found = transform.Find("ThinGlassFX");
                if (found != null) thinGlassFx = found.GetComponent<SpriteRenderer>();
            }

            if (thinGlassFx == null) return;
            thinGlassFx.enabled = false;
            thinGlassFx.sprite = null;
            thinGlassFx.SetPropertyBlock(null);
        }

        private bool RenderersNeedRefresh(LiquidBottle current)
        {
            // PourAnimator owns draw-order changes. I only check baked references and renderer state here.
            if (current == null || !current.Profiled
                || current.profile.front == null
                || current.profile.interiorMask == null
                || front == null)
                return true;

            ResolveAuthoredShadowRenderers();
            Sprite authoredFront = ResolveFront(current);
            bool wantsAuthoredFront = UsesAuthoredFront(authoredFront);
            int layer = SortingLayer.NameToID(current.sortingLayer);

            if (!front.enabled || front.sprite != authoredFront
                || front.sortingLayerID != layer)
                return true;
            if (wantsAuthoredFront
                && (front.sharedMaterial != Theme.authoredFrontMaterial
                    || front.color != Color.white))
                return true;

            bool wantsThinFx = TryGetThinFx(current, out Sprite wantedThinSprite,
                out Material wantedThinMaterial, out _);
            if (wantsThinFx && (thinGlassFx == null || !thinGlassFx.enabled
                                || thinGlassFx.sprite != wantedThinSprite
                                || thinGlassFx.sharedMaterial != wantedThinMaterial
                                || thinGlassFx.sortingLayerID != layer))
                return true;
            if (!wantsThinFx && thinGlassFx != null && thinGlassFx.enabled)
                return true;

            bool wantsShadow = drawShadow && shadowStrength > 0.001f
                            && Theme.shadowStrength > 0.001f;
            bool wantsHalo = drawShadow && shadowStrength > 0.001f
                          && Theme.wideShadowStrength > 0.001f;
            return AuthoredShadowLayerContractChanged(contactShadow, wantsShadow)
                || AuthoredShadowLayerContractChanged(softHalo, wantsHalo);
        }

        private Sprite ResolveFront(LiquidBottle source)
        {
            return source != null && source.Profiled ? source.profile.front : null;
        }

        private bool UsesAuthoredFront(Sprite authoredFront)
        {
            GlassVisualTheme.Settings settings = Theme;
            return authoredFront != null
                && settings.preserveAuthoredFront
                && settings.authoredFrontMaterial != null;
        }

        private static void ResolveFloorRange(LiquidBottle source, Rect bounds,
            out float opticalFloor, out float visibleBottom)
        {
            opticalFloor = bounds.yMin;
            visibleBottom = opticalFloor;
            if (source == null) return;

            if (source.Profiled && source.profile.hasVisibleLiquidFloor)
                opticalFloor = source.profile.visibleLiquidFloor;
            if (source.Profiled
                && source.profile.visibleBottomLocal > LiquidBottle.Unmeasured + 1f)
                visibleBottom = source.profile.visibleBottomLocal;
        }

        private int SettingsHash(LiquidBottle source)
        {
            unchecked
            {
                int hash = GeometryHash(source);
                hash = hash * 31 + wallThickness.GetHashCode();
                hash = hash * 31 + ObjectId(thinGlassFxMaterial);
                hash = hash * 31 + floorSeamLightScale.GetHashCode();
                hash = hash * 31 + accessoryLightScale.GetHashCode();
                hash = hash * 31 + bottomRimLightScale.GetHashCode();
                hash = hash * 31 + ObjectId(source != null ? source.profile : null);
                if (source != null && source.Profiled)
                {
                    hash = hash * 31 + ObjectId(source.profile.front);
                    hash = hash * 31 + ObjectId(source.profile.interiorMask);
                    hash = hash * 31 + source.profile.QuadRect.GetHashCode();
                    if (source.profile.interiorMask != null)
                    {
                        hash = hash * 31 + source.profile.interiorMask.width;
                        hash = hash * 31 + source.profile.interiorMask.height;
                    }
                    hash = hash * 31 + ObjectId(source.profile.contourMaterial);
                    hash = hash * 31 + ObjectId(source.profile.thinGlassFxMaterial);
                    hash = hash * 31
                        + source.profile.AuthoredFrontEdgeWidthScale.GetHashCode();
                    hash = hash * 31
                        + source.profile.AuthoredFrontWidthExpansion.GetHashCode();
                    hash = hash * 31
                        + source.profile.AuthoredReflectionTrim.GetHashCode();
                    hash = hash * 31 + source.profile.handleGlassLight.GetHashCode();
                    hash = hash * 31 + source.profile.stemFootGlassLight.GetHashCode();
                    hash = hash * 31 + source.profile.stemFootGlassBacking.GetHashCode();
                    hash = hash * 31 + source.profile.stemFootToonStrength.GetHashCode();
                    hash = hash * 31 + source.profile.accessoryGlassLightFeather.GetHashCode();
                    hash = hash * 31 + source.profile.bottomRimGlassLight.GetHashCode();
                    hash = hash * 31 + source.profile.bottomGlassBacking.GetHashCode();
                    hash = hash * 31 + source.profile.liquidBounceScale.GetHashCode();
                    hash = hash * 31 + source.profile.clipRightInterior.GetHashCode();
                    hash = hash * 31 + source.profile.rightInteriorXAtY0.GetHashCode();
                    hash = hash * 31 + source.profile.rightInteriorSlope.GetHashCode();
                    hash = hash * 31 + VesselPresentationMath.RoyalPixelsToLocal(
                        source.profile.bottomInteriorInsetPixels,
                        source.profile).GetHashCode();
                    hash = hash * 31
                        + source.profile.interiorBounds.yMin.GetHashCode();
                }
                // I omit draw orders and panel-only settings from the rebuild hash. Shadow palette changes still
                // affect the shell.
                hash = hash * 31 + Theme.GlassHash();
                hash = hash * 31 + drawShadow.GetHashCode();
                hash = hash * 31 + shadowStrength.GetHashCode();
                hash = hash * 31 + (source != null && source.sortingLayer != null
                    ? source.sortingLayer.GetHashCode()
                    : 0);
                return hash;
            }
        }

        private static int GeometryHash(LiquidBottle source)
        {
            if (source == null) return 0;
            unchecked
            {
                int hash = source.GetInstanceID();
                hash = hash * 31 + ObjectId(source.profile);
                hash = hash * 31 + source.interiorWidth.GetHashCode();
                hash = hash * 31 + source.interiorHeight.GetHashCode();
                hash = hash * 31 + source.interiorBottom.GetHashCode();
                hash = hash * 31 + source.bottomCornerRadius.GetHashCode();
                hash = hash * 31 + source.topCornerRadius.GetHashCode();
                hash = hash * 31 + source.mouthLocal.GetHashCode();
                hash = hash * 31 + source.maskPixelsPerUnit.GetHashCode();
                hash = hash * 31 + ObjectId(source.maskSprite);
                Vector2[] custom = source.customInteriorPolygon;
                int customCount = custom != null ? custom.Length : 0;
                hash = hash * 31 + customCount;
                for (int i = 0; i < customCount; i++)
                    hash = hash * 31 + custom[i].GetHashCode();
                return hash;
            }
        }

        private static int ObjectId(Object value) => value != null ? value.GetInstanceID() : 0;

        private bool TryGetThinFx(LiquidBottle source, out Sprite sprite,
            out Material material, out Rect interiorBounds)
        {
            sprite = null;
            material = null;
            interiorBounds = default;
            if (source == null || !source.Profiled) return false;
            // I keep ThinFX enabled when side or profile-part lights are active; GlassContour owns the contact
            // correction.
            GlassVisualTheme.Settings settings = Theme;
            bool hasProfilePartLight =
                source.profile.handleGlassLight * accessoryLightScale > 0.001f
                    || source.profile.stemFootGlassLight * accessoryLightScale > 0.001f
                    || source.profile.bottomRimGlassLight * bottomRimLightScale > 0.001f;
            if (settings.sideFxStrength <= 0.001f && !hasProfilePartLight)
                return false;

            VesselProfile profile = source.profile;
            // ThinFX pulses the final front sprite's outer glass pixels without creating or swapping assets.
            material = profile.thinGlassFxMaterial != null
                ? profile.thinGlassFxMaterial
                : thinGlassFxMaterial;
            sprite = profile.front;
            interiorBounds = profile.interiorBounds;
            return sprite != null && material != null;
        }
    }
}
