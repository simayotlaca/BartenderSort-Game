using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiquidSort
{
    /// <summary>I draw the bottle's colour stack with waterlines from VesselFillMath, without simulating liquid.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class LiquidBottle : MonoBehaviour
    {
        public const int MaxBands = 8;

        // Cached once. Looking shader properties up by name every frame is slow.
        private static readonly int BandColorId = Shader.PropertyToID("_BandColor");
        private static readonly int BandCapId = Shader.PropertyToID("_BandCap");
        private static readonly int AngleId = Shader.PropertyToID("_Angle");
        private static readonly int RevealTurbulenceAmountId =
            Shader.PropertyToID("_RevealTurbulenceAmount");
        private static readonly int RevealTurbulenceLifeId =
            Shader.PropertyToID("_RevealTurbulenceLife");
        private static readonly int CompletionFxProgressId =
            Shader.PropertyToID("_CompletionFxProgress");
        private static readonly int CompletionFxColorId =
            Shader.PropertyToID("_CompletionFxColor");
        private static readonly int CapFlashId = Shader.PropertyToID("_CapFlash");
        private static readonly int MaskUvId = Shader.PropertyToID("_MaskUV");
        private static readonly int QuadSizeId = Shader.PropertyToID("_QuadSize");
        private static readonly int InteriorId = Shader.PropertyToID("_Interior");
        private static readonly int MaskTexId = Shader.PropertyToID("_MaskTex");
        public const float Unmeasured = -9999f;

        [Header("Interior shape (bottle local, pivot at the base)")]
        public float interiorWidth = 0.76f;
        public float interiorHeight = 2.00f;
        public float interiorBottom = 0.16f;
        public float bottomCornerRadius = 0.34f;
        public float topCornerRadius = 0.24f;
        public Vector2 mouthLocal = new Vector2(0f, 2.62f);
        [Tooltip("Exact interior shape. 3+ points override the rounded one.")]
        public Vector2[] customInteriorPolygon;
        [Tooltip("Half width of an open rim. 0 = normal bottle mouth.")]
        public float mouthHalfWidth;

        [Header("Vessel")]
        [Tooltip("Baked shape, art and tables for this glass. Nothing is traced or searched at runtime. Empty = use the fields below.")]
        public VesselProfile profile;

        [Header("Contents")]
        public int capacity = 4;
        [SerializeField] private List<Color> units = new List<Color>();

        [Header("Rendering")]
        public Sprite maskSprite;
        public Material liquidMaterial;
        public int sortingOrder = 1;
        public string sortingLayer = "Default";
        public float maskPixelsPerUnit = 160f;
        [Tooltip("Top face ellipse depth, as a fraction of the liquid width.")]
        [Range(0.02f, 0.20f)] public float surfaceBulge = 0.135f;   // from the reference art
        [Tooltip("Max cap depth as a fraction of interior height, so a wide bowl does not get a huge top face.")]
        [Range(0.01f, 0.30f)] public float maxCapDepth = 0.075f;
        [Tooltip("Height below which the drawing itself hides the liquid. -9999 = not measured yet.")]
        public float visibleBottomLocal = Unmeasured;
        [Tooltip("How much of the interior stays empty above the waterline when full. Measured from the back of the rim, so a wide glass needs a big value.")]
        [Range(0f, 0.50f)] public float brimHeadroom = 0.34f;
        [Tooltip("Gap from surface to brim, counted in top-face depths. Keeps different glasses consistent.")]
        [Range(0f, 8f)] public float brimGapCaps = 3.2f;
        [Tooltip("A full glass stops short of the brim so its top face is not clipped.")]
        [Range(0.50f, 1f)] public float maxFillFraction = 1f;
        [Tooltip("How much of the top face may rise into the open mouth. 1 = the whole back rim.")]
        [Range(0f, 1f)] public float surfaceAllowance = 0.8f;
        [Tooltip("1 = every unit the same height (reference look). 0 = every unit the same volume.")]
        [Range(0f, 1f)] public float evenBandHeights = 1f;
        [Tooltip("Curve on colour boundaries. 1 with depth 0.098 matches the reference; 0 gives straight bands.")]
        [Range(0f, 1f)] public float innerJunctionCurve = 1f;
        [Tooltip("How deep the arc between two colours sags, as a fraction of the chord. Reference: 14px on 143px.")]
        [Range(0f, 0.25f)] public float innerJunctionDepth = 0.098f;

        // I keep displayed volume separate from the real stack so pours can fill and drain smoothly.
        [SerializeField, HideInInspector] private float displayVolume = -1f;

        private Transform liquidRoot;
        private MeshRenderer liquidRenderer;
        private MeshFilter liquidFilter;
        private MaterialPropertyBlock block;
        private Mesh quad;

        private Vector2[] interiorPolygon;
        private readonly List<Vector2> rotatedPolygon = new List<Vector2>();
        private float polygonArea;
        private Rect quadRect;
        private bool quadRectValid;
        private Vector4 maskUv = new Vector4(0f, 0f, 1f, 1f);

        private readonly Vector4[] bandColors = new Vector4[MaxBands];
        private readonly Vector4[] bandCaps = new Vector4[MaxBands];
        private readonly Vector4[] bandShades = new Vector4[MaxBands];
        private readonly Vector4[] bandInfo = new Vector4[MaxBands];
        private readonly List<Color> groupColors = new List<Color>();
        private readonly List<int> groupTops = new List<int>();

        // I preview the incoming colour and count without changing the receiver's real stack.
        private object transferReservationOwner;
        private int transferReservationId;
        private Color receivePreviewColor;
        private int receivePreviewCount;
        private int modelVersion;

        private Renderer[] cachedRenderers;
        private int[] cachedBaseOrders;

        private float lastBuiltAngle = float.NaN;
        private float lastBuiltVolume = float.NaN;
        private int lastBuiltLookHash = int.MinValue;
        private int contentVersion;
        private int builtContentVersion = -1;
        private float surfaceLocalY;
        private float surfaceLocalX;
        private float surfaceFullChord;
        private Vector4 floatingGarnishCaustic;
        [System.NonSerialized, Range(0f, 1f)] public float revealTurbulenceAmount;
        [System.NonSerialized, Range(0f, 1f)] public float revealTurbulenceLife;
        private float completionFxProgress;
        private Color completionFxColor = Color.clear;
        private bool revealTurbulenceActive;
        private int revealUnitIndex = -1;
        private Color revealConcealedColor;
        private float revealColorProgress = 1f;
        private Material validatedContractMaterial;
        private Shader validatedContractShader;
        private bool liquidContractValid;
        private bool liquidContractErrorLogged;
        private bool liquidMaskContractErrorLogged;
        private bool authoredRendererErrorLogged;

        // I cache baked profiles to skip idle-frame hashing. Refresh() and edit mode still check everything.
        private bool runtimeRefreshRequested = true;
        private VesselProfile runtimeProfile;
        private bool runtimeWasProfiled;
        private Texture2D runtimeProfileMask;
        private Vector2[] runtimeProfilePolygon;
        private VesselProfile.TiltTable runtimeProfileTilted;
        private VesselProfile.UprightTable runtimeProfileUpright;
        private Rect runtimeProfileQuadRect;
        private int runtimeProfileCapacity;
        private int runtimeCapacity;

        /// <summary>True once a baked profile is driving this vessel.</summary>
        public bool Profiled => profile != null && profile.IsBaked;

        // I use the profile's look settings so each glass is configured once.
        private float Bulge => Profiled ? profile.surfaceBulge : surfaceBulge;
        private float CapDepth => Profiled ? profile.maxCapDepth : maxCapDepth;
        private float Headroom => Profiled ? profile.brimHeadroom : brimHeadroom;
        private float GapCaps => Profiled ? profile.brimGapCaps : brimGapCaps;
        private bool DerivesSurfaceCeiling =>
            Profiled && profile.deriveSurfaceCeilingAtRuntime;
        private float Allowance => Profiled ? profile.surfaceAllowance : surfaceAllowance;
        private float EvenBands => Profiled ? profile.evenBandHeights : evenBandHeights;
        private float JunctionCurve => Profiled ? profile.innerJunctionCurve : innerJunctionCurve;
        private float JunctionDepth => Profiled
            ? profile.innerJunctionDepth
            : innerJunctionDepth;
        private float InteriorHeight => Profiled ? profile.interiorBounds.height : interiorHeight;

        public int UnitCount => units.Count;
        public bool IsEmpty => units.Count == 0;
        public bool IsFull => units.Count >= capacity;
        public int FreeSpace => Mathf.Max(0, capacity - units.Count);
        public bool IsTransferReserved => transferReservationOwner != null;
        internal int ModelVersion => modelVersion;

        private int VisualUnitCount => units.Count + receivePreviewCount;

        public float DisplayVolume
        {
            get => displayVolume;
            set => displayVolume = Mathf.Clamp(value, 0f, capacity);
        }

        /// <summary>World position of the pour lip.</summary>
        public Vector3 MouthWorld => transform.TransformPoint(new Vector3(mouthLocal.x, mouthLocal.y, 0f));

        /// <summary>World height of the centre of the exposed liquid ellipse.</summary>
        public float SurfaceWorldY =>
            transform.position.y + surfaceLocalY * Mathf.Abs(transform.lossyScale.y);

        /// <summary>The last surface in gravity-aligned liquid space. Tilt is already included; do not rotate it again.</summary>
        public bool TryGetContactSurfaceGeometry(out Vector2 localCentre,
            out float halfWidth, out float halfDepth)
        {
            localCentre = new Vector2(surfaceLocalX, surfaceLocalY);
            halfWidth = ContactSurfaceHalfWidth;
            halfDepth = ContactSurfaceHalfDepth;
            return (displayVolume > 0.0001f || receivePreviewCount > 0)
                && surfaceFullChord > 0.0002f;
        }

        public Vector3 SurfaceWorldCenter =>
            LiquidFrameToWorld(new Vector2(surfaceLocalX, surfaceLocalY));
        public float SurfaceWorldHalfWidth =>
            ContactSurfaceHalfWidth * Mathf.Abs(transform.lossyScale.x);
        public float SurfaceWorldHalfDepth =>
            ContactSurfaceHalfDepth * Mathf.Abs(transform.lossyScale.y);

        /// <summary>The exposed far edge at the actual horizontal stream position.</summary>
        public float ContactWorldYAt(float worldX)
        {
            float halfWidth = Mathf.Max(0.0001f, SurfaceWorldHalfWidth);
            float across = Mathf.Clamp((worldX - SurfaceWorldCenter.x) / halfWidth, -1f, 1f);
            return SurfaceWorldY + SurfaceWorldHalfDepth
                * Mathf.Sqrt(Mathf.Max(0f, 1f - across * across));
        }

        /// <summary>Converts already gravity-aligned liquid coordinates to world space.</summary>
        public Vector3 LiquidFrameToWorld(Vector2 point)
        {
            Vector3 scale = transform.lossyScale;
            return transform.position + new Vector3(
                point.x * Mathf.Abs(scale.x), point.y * Mathf.Abs(scale.y), 0f);
        }

        public Vector2 WorldToLiquidFrame(Vector3 point)
        {
            Vector3 offset = point - transform.position;
            Vector3 scale = transform.lossyScale;
            return new Vector2(offset.x / Mathf.Max(0.0001f, Mathf.Abs(scale.x)),
                offset.y / Mathf.Max(0.0001f, Mathf.Abs(scale.y)));
        }

        private float ContactSurfaceHalfDepth =>
            Mathf.Min(surfaceFullChord * Mathf.Max(Bulge, 0.001f),
                Mathf.Max(0.005f, InteriorHeight * CapDepth))
            * Mathf.Clamp01(LiquidSurfaceContract.ExposedSurfaceScale(displayVolume, capacity));

        private float ContactSurfaceHalfWidth
        {
            get
            {
                float halfWidth = Mathf.Max(0.0001f, surfaceFullChord * 0.5f);
                Material material = liquidRenderer != null ? liquidRenderer.sharedMaterial : null;
                float inset = material != null
                    && material.HasProperty(LiquidSurfaceContract.CapWallInsetId)
                    ? Mathf.Max(0f, material.GetFloat(LiquidSurfaceContract.CapWallInsetId))
                      * RoyalUnitsPerPixel
                    : 0f;
                // Profiled glasses use the shader's fixed Royal-local inset. Others keep their geometric chord,
                // independent of camera size.
                return Mathf.Max(0.0001f, halfWidth - Mathf.Min(inset, halfWidth * 0.15f));
            }
        }

        /// <summary>Fraction of the interior the liquid is allowed to occupy.</summary>
        public float UsableFill =>
            Mathf.Clamp(Profiled ? profile.maxFillFraction : maxFillFraction, 0.5f, 1f);

        public Color TopColor => units.Count == 0 ? Color.clear : units[units.Count - 1];

        /// <summary>The visible top colour, including an incoming preview before the real stack changes.</summary>
        public Color VisualTopColor =>
            receivePreviewCount > 0 && displayVolume > units.Count + 0.001f
                ? receivePreviewColor
                : units.Count > 0 ? VisualUnitColor(units.Count - 1) : Color.clear;

        /// <summary>The colour touching the visible floor, including previews in empty receivers so base reflections appear during pouring.</summary>
        public Color VisualBottomColor
        {
            get
            {
                float volume = displayVolume >= 0f ? displayVolume : VisualUnitCount;
                if (volume <= 0.001f) return Color.clear;
                if (units.Count > 0) return VisualUnitColor(0);
                return receivePreviewCount > 0 ? receivePreviewColor : Color.clear;
            }
        }

        /// <summary>Soft presence used to fade the coloured glass bounce at empty/full transitions.</summary>
        public float VisualBottomPresence
        {
            get
            {
                float volume = displayVolume >= 0f ? displayVolume : VisualUnitCount;
                return Mathf.Clamp01(volume / 0.35f);
            }
        }

        /// <summary>How many identical units sit on top of the stack.</summary>
        public int TopRunLength
        {
            get
            {
                if (units.Count == 0) return 0;
                Color top = units[units.Count - 1];
                int run = 1;
                for (int i = units.Count - 2; i >= 0; i--)
                {
                    if (!Same(units[i], top)) break;
                    run++;
                }
                return run;
            }
        }

        public bool CanReceive(Color color)
        {
            if (IsFull) return false;
            return units.Count == 0 || Same(TopColor, color);
        }

        public void SetUnits(IEnumerable<Color> newUnits)
        {
            receivePreviewCount = 0;
            receivePreviewColor = Color.clear;
            ClearTransientMotion();
            units.Clear();
            if (newUnits != null)
            {
                foreach (Color color in newUnits)
                {
                    if (units.Count >= capacity) break;
                    units.Add(color);
                }
            }
            displayVolume = units.Count;
            contentVersion++;
            modelVersion++;
        }

        /// <summary>I lock the vessel itself so two animators cannot use it in overlapping transfers.</summary>
        internal bool TryReserveTransfer(object owner, int operationId)
        {
            if (owner == null || operationId == 0) return false;
            if (transferReservationOwner == null)
            {
                transferReservationOwner = owner;
                transferReservationId = operationId;
                // I publish the reservation even on idle frames so ambient bubbles stop during a pour.
                contentVersion++;
                return true;
            }
            return ReferenceEquals(transferReservationOwner, owner)
                   && transferReservationId == operationId;
        }

        internal void ReleaseTransferReservation(object owner, int operationId)
        {
            if (!ReferenceEquals(transferReservationOwner, owner)
                || transferReservationId != operationId)
                return;
            transferReservationOwner = null;
            transferReservationId = 0;
            contentVersion++;
        }

        /// <summary>I preview incoming units without exposing them to saves, solving or completion checks before commit.</summary>
        internal bool BeginReceivePreview(object owner, int operationId, Color color, int count)
        {
            if (!ReferenceEquals(transferReservationOwner, owner)
                || transferReservationId != operationId
                || receivePreviewCount != 0 || count <= 0
                || units.Count + count > capacity)
                return false;

            receivePreviewColor = color;
            receivePreviewCount = count;
            contentVersion++;
            return true;
        }

        internal void ClearReceivePreview(object owner, int operationId)
        {
            if (!ReferenceEquals(transferReservationOwner, owner)
                || transferReservationId != operationId || receivePreviewCount == 0)
                return;

            receivePreviewCount = 0;
            receivePreviewColor = Color.clear;
            contentVersion++;
        }

        /// <summary>I commit both stacks without yielding. Until then, only the source stack owns the units.</summary>
        internal bool TryCommitTransferTo(LiquidBottle target, object owner, int operationId,
            int expectedSourceVersion, int expectedTargetVersion, Color expectedColor,
            int count, bool requireMatchingColors)
        {
            if (this == null || !isActiveAndEnabled
                || target == null || !target.isActiveAndEnabled
                || target == this || count <= 0
                || !ReferenceEquals(transferReservationOwner, owner)
                || transferReservationId != operationId
                || !ReferenceEquals(target.transferReservationOwner, owner)
                || target.transferReservationId != operationId
                || modelVersion != expectedSourceVersion
                || target.modelVersion != expectedTargetVersion
                || target.receivePreviewCount != count
                || !Same(target.receivePreviewColor, expectedColor)
                || units.Count < count || target.units.Count + count > target.capacity)
                return false;

            for (int i = units.Count - count; i < units.Count; i++)
            {
                if (!Same(units[i], expectedColor)) return false;
            }

            if (requireMatchingColors && target.units.Count > 0
                && !Same(target.units[target.units.Count - 1], expectedColor))
                return false;

            // I grow storage before changing either stack so RemoveRange and Add cannot leave a partial transfer.
            int requiredTargetCapacity = target.units.Count + count;
            if (target.units.Capacity < requiredTargetCapacity)
                target.units.Capacity = requiredTargetCapacity;

            units.RemoveRange(units.Count - count, count);
            for (int i = 0; i < count; i++) target.units.Add(expectedColor);

            receivePreviewCount = 0;
            receivePreviewColor = Color.clear;
            target.receivePreviewCount = 0;
            target.receivePreviewColor = Color.clear;
            contentVersion++;
            target.contentVersion++;
            modelVersion++;
            target.modelVersion++;
            return true;
        }

        /// <summary>I capture the receiver's final chord, then restore its in-flight look before drawing. Contact X and width stay fixed for the impact.</summary>
        internal void CaptureReceiveImpactGeometry(float requestedLocalX,
            float settledVolume, out float localX, out float fullChord)
        {
            CaptureReceiveImpactGeometry(requestedLocalX, settledVolume,
                out localX, out fullChord, out _, out _);
        }

        /// <summary>Also captures the actual far-edge contact height at the latched impact X.</summary>
        internal void CaptureReceiveImpactGeometry(float requestedLocalX,
            float settledVolume, out float localX, out float fullChord,
            out float surfaceWorldY, out float contactWorldY)
        {
            float liveVolume = displayVolume;
            try
            {
                displayVolume = Mathf.Clamp(settledVolume, 0f, capacity);
                Refresh();

                fullChord = Mathf.Max(surfaceFullChord, 0.0002f);
                float halfChord = fullChord * 0.5f;
                localX = Mathf.Clamp(requestedLocalX,
                    surfaceLocalX - halfChord * 0.72f,
                    surfaceLocalX + halfChord * 0.72f);
                surfaceWorldY = SurfaceWorldY;
                contactWorldY = ContactWorldYAt(
                    LiquidFrameToWorld(new Vector2(localX, surfaceLocalY)).x);
            }
            finally
            {
                displayVolume = liveVolume;
                lastBuiltVolume = float.NaN;
                Refresh();
            }
        }

        /// <summary>I centre the hidden-colour reveal on the source's current band; stream contacts use their landing X.</summary>
        internal void SetRevealTurbulence(float amount, float normalizedLife)
        {
            float wantedAmount = Mathf.Clamp01(amount);
            float wantedLife = Mathf.Clamp01(normalizedLife);
            if (Mathf.Abs(revealTurbulenceAmount - wantedAmount) < 0.0001f
                && Mathf.Abs(revealTurbulenceLife - wantedLife) < 0.0001f)
                return;

            bool wasActive = revealTurbulenceActive;
            revealTurbulenceAmount = wantedAmount;
            revealTurbulenceLife = wantedLife;
            revealTurbulenceActive = wantedAmount > 0.0001f;
            // I publish the final zero when the effect stops so the last nonzero value cannot linger.
            if (wasActive && !revealTurbulenceActive) contentVersion++;
        }

        /// <summary>Clears only the hidden-colour reveal knot.</summary>
        internal void ClearRevealTurbulence()
        {
            revealTurbulenceAmount = 0f;
            revealTurbulenceLife = 1f;
            revealTurbulenceActive = false;
            revealUnitIndex = -1;
            revealColorProgress = 1f;
            contentVersion++;
        }

        /// <summary>Blends only the exposed unit's drawing; the committed stack remains authoritative.</summary>
        internal void BeginHiddenColorReveal(int unitIndex, Color concealedColor)
        {
            if (unitIndex < 0 || unitIndex >= units.Count) return;
            revealUnitIndex = unitIndex;
            revealConcealedColor = concealedColor;
            revealColorProgress = 0f;
            contentVersion++;
        }

        internal void SetHiddenColorRevealProgress(float progress)
        {
            float wanted = Mathf.Clamp01(progress);
            if (revealUnitIndex < 0 || Mathf.Approximately(wanted, revealColorProgress))
                return;
            revealColorProgress = wanted;
            contentVersion++;
        }

        /// <summary>I publish completion timing separately from ClearTransientMotion so SetUnits cannot cut off the effect.</summary>
        internal void SetCompletionEffect(float normalizedProgress, Color color)
        {
            float wanted = Mathf.Clamp01(normalizedProgress);
            Color wantedColor = color;
            wantedColor.a = 1f;
            if (Mathf.Abs(completionFxProgress - wanted) < 0.0001f
                && completionFxColor == wantedColor) return;
            completionFxProgress = wanted;
            completionFxColor = wantedColor;
            PublishCompletionEffectBlock();
        }

        internal void ClearCompletionEffect()
        {
            if (completionFxProgress <= 0.0001f
                && completionFxColor == Color.clear) return;
            completionFxProgress = 0f;
            completionFxColor = Color.clear;
            PublishCompletionEffectBlock();
        }

        private void PublishCompletionEffectBlock()
        {
            if (liquidRenderer == null)
            {
                runtimeRefreshRequested = true;
                return;
            }

            block ??= new MaterialPropertyBlock();
            liquidRenderer.GetPropertyBlock(block);
            block.SetFloat(CompletionFxProgressId,
                Mathf.Clamp01(completionFxProgress));
            block.SetColor(CompletionFxColorId, completionFxColor);
            liquidRenderer.SetPropertyBlock(block);
        }

        /// <summary>Clears presentation-only motion after a cancelled/reset transfer.</summary>
        internal void ClearTransientMotion()
        {
            ClearRevealTurbulence();
        }

        /// <summary>Pushes every renderer of this bottle in front of (or behind) the others.</summary>
        public void SetSortingOffset(int offset)
        {
            // I only change sortingOrder here, so lifting a pouring vessel cannot rebuild its art.
            CacheRenderers();
            for (int i = 0; i < cachedRenderers.Length; i++)
            {
                if (cachedRenderers[i] == null) continue;
                cachedRenderers[i].sortingOrder = cachedBaseOrders[i] + offset;
            }
        }

        internal void GetSortingSnapshot(out Renderer[] renderers, out int[] baseOrders)
        {
            CacheRenderers();
            renderers = cachedRenderers;
            baseOrders = cachedBaseOrders;
        }

        /// <summary>Clear the sorting cache after adding or removing a child renderer so the whole bottle lifts together.</summary>
        public void InvalidateRenderers() => cachedRenderers = null;

        /// <summary>I store the garnish reflection here so one owner updates the liquid property block and preserves it on full refreshes.</summary>
        internal void SetFloatingGarnishCaustic(Vector4 value)
        {
            if ((floatingGarnishCaustic - value).sqrMagnitude <= 1e-8f) return;
            floatingGarnishCaustic = value;
            if (liquidRenderer == null) return;

            block ??= new MaterialPropertyBlock();
            liquidRenderer.GetPropertyBlock(block);
            block.SetVector(LiquidSurfaceContract.FloatingGarnishCausticId,
                floatingGarnishCaustic);
            liquidRenderer.SetPropertyBlock(block);
        }

        private void CacheRenderers()
        {
            if (cachedRenderers != null && cachedRenderers.Length > 0 && cachedRenderers[0] != null) return;
            cachedRenderers = GetComponentsInChildren<Renderer>(true);
            cachedBaseOrders = new int[cachedRenderers.Length];
            for (int i = 0; i < cachedRenderers.Length; i++)
                cachedBaseOrders[i] = cachedRenderers[i].sortingOrder;
        }

        private void OnEnable()
        {
            if (displayVolume < 0f) displayVolume = units.Count;
            VesselRimGarnish.Ensure(this);
            VesselFloatingGarnish.Ensure(this);

            // I keep baked data while the pooled hierarchy is hidden. RuntimeRefreshNeeded still catches changes
            // made while inactive.
            bool resumesRetainedProfile = Application.isPlaying && Profiled
                && interiorPolygon != null && interiorPolygon.Length >= 3
                && liquidRenderer != null && liquidFilter != null
                && quad != null && quadRectValid
                && liquidFilter.sharedMesh == quad;
            if (!resumesRetainedProfile) Invalidate();
        }

        private void Start()
        {
            // Start handles scene objects when scene reload is off; pooled objects use OnEnable.
            VesselRimGarnish.Ensure(this);
            VesselFloatingGarnish.Ensure(this);
        }

        private void OnDisable()
        {
            // I retain baked data for pooling, but clean up if only the component is disabled. OnDestroy handles
            // final cleanup.
            if (Application.isPlaying && Profiled && !gameObject.activeInHierarchy)
                return;

            ReleaseQuad();
            runtimeRefreshRequested = true;
        }

        private void OnDestroy()
        {
            ReleaseQuad();
        }

        private void OnValidate()
        {
            capacity = Mathf.Clamp(capacity, 1, MaxBands);
            mouthHalfWidth = Mathf.Max(0f, mouthHalfWidth);
            surfaceBulge = Mathf.Clamp(surfaceBulge, 0.02f, 0.20f);
            maxCapDepth = Mathf.Clamp(maxCapDepth, 0.01f, 0.30f);
            brimHeadroom = Mathf.Clamp(brimHeadroom, 0f, 0.50f);
            maxFillFraction = Mathf.Clamp(maxFillFraction, 0.50f, 1f);
            innerJunctionCurve = Mathf.Clamp01(innerJunctionCurve);
            while (units.Count > capacity) units.RemoveAt(units.Count - 1);
            displayVolume = displayVolume < 0f
                ? units.Count
                : Mathf.Clamp(displayVolume, 0f, capacity);
            Invalidate();
        }

        /// <summary>Marks shape and content caches dirty. The rebuild itself happens in LateUpdate.</summary>
        public void Invalidate()
        {
            interiorPolygon = null;
            quadRectValid = false;
            lastBuiltAngle = float.NaN;
            lastBuiltVolume = float.NaN;
            lastBuiltLookHash = int.MinValue;
            builtContentVersion = -1;
            cachedRenderers = null;
            runtimeRefreshRequested = true;
        }

        private void LateUpdate() => RefreshIfNeeded();

        /// <summary>I skip settled baked vessels in play mode. Legacy/edit-mode vessels use the full check; runtime look changes can call Refresh.</summary>
        internal bool RefreshIfNeeded()
        {
            if (Application.isPlaying && Profiled && !RuntimeRefreshNeeded())
                return false;

            Refresh();
            return true;
        }

        /// <summary>I check the full look, rebuild changed waterlines and update the shader. Call this after editing runtime profile values.</summary>
        public void Refresh()
        {
            PrepareProfileContract();
            EnsurePolygon();
            if (!EnsureRenderer()) return;
            if (!EnsureLiquidRenderContract() || !EnsureLiquidMaskContract()) return;
            EnsureQuad();

            // I keep the waterline level. A velocity-driven spring would leave a surface tremor after pouring.
            float angle = NormalizeAngle(transform.eulerAngles.z);
            float volume = Mathf.Clamp(displayVolume, 0f, capacity);
            int lookHash = LiquidLookHash(volume);

            bool dirty = builtContentVersion != contentVersion
                         || !Mathf.Approximately(lastBuiltAngle, angle)
                         // I negate the comparison so NaN forces a rebuild; Abs(NaN - volume) > epsilon is false
                         // and would leave captured impact geometry stuck.
                         || !(Mathf.Abs(lastBuiltVolume - volume) <= 1e-4f)
                         || lastBuiltLookHash != lookHash
                         || revealTurbulenceActive;

            if (dirty)
            {
                BuildBands(angle, volume);
                lastBuiltAngle = angle;
                lastBuiltVolume = volume;
                lastBuiltLookHash = lookHash;
                builtContentVersion = contentVersion;
            }

            CaptureRuntimeContract();
        }

        /// <summary>I compare live state without allocations before calculating the full look hash.</summary>
        private bool RuntimeRefreshNeeded()
        {
            // LateUpdate already checked the baked profile, so I skip repeated table validation.
            if (runtimeRefreshRequested || ProfileContractChanged(true)) return true;
            if (interiorPolygon == null || interiorPolygon.Length < 3
                || liquidRenderer == null || liquidFilter == null
                || quad == null || !quadRectValid
                || liquidFilter.sharedMesh != quad)
                return true;

            Material wantedMaterial = profile.liquidMaterial != null
                ? profile.liquidMaterial
                : liquidMaterial;
            Material rendererMaterial = liquidRenderer.sharedMaterial;
            Shader rendererShader = rendererMaterial != null
                ? rendererMaterial.shader
                : null;
            if (rendererMaterial != wantedMaterial
                || validatedContractMaterial != rendererMaterial
                || validatedContractShader != rendererShader)
                return true;

            if (runtimeCapacity != capacity
                || builtContentVersion != contentVersion
                || lastBuiltLookHash == int.MinValue
                || revealTurbulenceActive)
                return true;

            float angle = NormalizeAngle(transform.eulerAngles.z);
            if (!Mathf.Approximately(lastBuiltAngle, angle)) return true;

            float volume = Mathf.Clamp(displayVolume, 0f, capacity);
            bool shouldRenderLiquid = VisualUnitCount > 0 && volume > 1e-4f;
            if (liquidRenderer.enabled != shouldRenderLiquid)
            {
                // I repair renderer changes through BuildBands, including previews in empty receivers.
                lastBuiltLookHash = int.MinValue;
                return true;
            }
            return !(Mathf.Abs(lastBuiltVolume - volume) <= 1e-4f);
        }

        /// <summary>A new profile or bake reference invalidates geometry. Edit mode checks scalar edits; runtime edits should call Refresh.</summary>
        private bool ProfileContractChanged(bool knownProfiled = false)
        {
            if (runtimeProfile != profile) return true;
            bool profiled = knownProfiled || Profiled;
            if (runtimeWasProfiled != profiled) return true;
            if (!profiled) return false;
            return runtimeProfileMask != profile.interiorMask
                   || runtimeProfilePolygon != profile.interiorPolygon
                   || runtimeProfileTilted != profile.tilted
                   || runtimeProfileUpright != profile.upright
                   || runtimeProfileQuadRect != profile.QuadRect
                   || runtimeProfileCapacity != profile.capacity;
        }

        private void PrepareProfileContract()
        {
            if (!ProfileContractChanged()) return;
            Invalidate();
            validatedContractMaterial = null;
            validatedContractShader = null;
            liquidContractValid = false;
            liquidContractErrorLogged = false;
        }

        private void CaptureRuntimeContract()
        {
            runtimeProfile = profile;
            runtimeCapacity = capacity;
            runtimeWasProfiled = Profiled;
            if (runtimeWasProfiled)
            {
                runtimeProfileMask = profile.interiorMask;
                runtimeProfilePolygon = profile.interiorPolygon;
                runtimeProfileTilted = profile.tilted;
                runtimeProfileUpright = profile.upright;
                runtimeProfileQuadRect = profile.QuadRect;
                runtimeProfileCapacity = profile.capacity;
            }
            else
            {
                runtimeProfileMask = null;
                runtimeProfilePolygon = null;
                runtimeProfileTilted = null;
                runtimeProfileUpright = null;
                runtimeProfileQuadRect = default;
                runtimeProfileCapacity = 0;
            }
            runtimeRefreshRequested = false;
        }

        /// <summary>I hash look settings and shared Royal rules here. Contents, angle and volume have separate checks.</summary>
        private int LiquidLookHash(float volume)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 397 + LiquidSurfaceContract.Revision;
                hash = hash * 397 + LiquidPalette.Revision;
                hash = hash * 397 + capacity;
                hash = hash * 397 + SurfaceScale(volume).GetHashCode();
                hash = hash * 397 + RoyalUnitsPerPixel.GetHashCode();
                hash = hash * 397 + Bulge.GetHashCode();
                hash = hash * 397 + CapDepth.GetHashCode();
                hash = hash * 397 + Headroom.GetHashCode();
                hash = hash * 397 + GapCaps.GetHashCode();
                hash = hash * 397 + (DerivesSurfaceCeiling ? 1 : 0);
                hash = hash * 397 + Allowance.GetHashCode();
                hash = hash * 397 + EvenBands.GetHashCode();
                hash = hash * 397 + JunctionCurve.GetHashCode();
                hash = hash * 397 + JunctionDepth.GetHashCode();
                hash = hash * 397 + UsableFill.GetHashCode();
                hash = hash * 397 + InteriorHeight.GetHashCode();

                if (Profiled)
                {
                    hash = hash * 397 + profile.GetInstanceID();
                    // I hash the mask reference so a visual rebake replaces the renderer's cached texture.
                    hash = hash * 397 + (profile.interiorMask != null
                        ? profile.interiorMask.GetInstanceID()
                        : 0);
                    hash = hash * 397 + (profile.clipRightInterior ? 1 : 0);
                    hash = hash * 397 + profile.rightInteriorXAtY0.GetHashCode();
                    hash = hash * 397 + profile.rightInteriorSlope.GetHashCode();
                    hash = hash * 397 + profile.bottomInteriorInsetPixels.GetHashCode();
                    // I hash the published render floor, which can change independently of the volume bounds.
                    hash = hash * 397 + profile.DrawnFloorLocal.GetHashCode();
                    hash = hash * 397 + (profile.HasLiquidFloorCurve ? 1 : 0);
                    hash = hash * 397 + profile.liquidFloorXRange.GetHashCode();
                    if (profile.HasLiquidFloorCurve)
                    {
                        for (int i = 0; i < profile.liquidFloorSamples.Length; i++)
                            hash = hash * 397
                                 + profile.liquidFloorSamples[i].GetHashCode();
                    }
                    hash = hash * 397 + profile.visibleLiquidFloor.GetHashCode();
                    hash = hash * 397 + (profile.hasVisibleLiquidFloor ? 1 : 0);
                    hash = hash * 397 + profile.upright.floorY.GetHashCode();
                    hash = hash * 397 + profile.upright.ceilingY.GetHashCode();
                    float ambientBubbleStrength =
                        profile.ambientRisingBubbleStrength;
                    hash = hash * 397 + ambientBubbleStrength.GetHashCode();
                    // I include the whole transfer reservation so ambient bubbles stop on the next refresh,
                    // including return and cancellation.
                    if (ambientBubbleStrength > 0.0001f)
                        hash = hash * 397 + (IsTransferReserved ? 1 : 0);
                }

                return hash;
            }
        }

        private void BuildBands(float angle, float volume)
        {
            if (liquidRenderer == null) return;

            // I sample just above the band threshold for an empty receiver's first contact, without changing
            // volume or showing liquid early.
            bool previewContactOnly = volume <= 1e-4f && receivePreviewCount > 0;
            float geometryVolume = previewContactOnly ? 2e-4f : volume;

            bool baked = Profiled;
            bool derivesSurfaceCeiling = baked && DerivesSurfaceCeiling;
            float minY, maxY;
            if (baked && !derivesSurfaceCeiling)
            {
                // I read the baked table directly; no rotation or measurement is needed here.
                minY = profile.upright.minY;
                maxY = profile.upright.maxY;
            }
            else
            {
                // I recompute only the live brim clamp from baked geometry. Clamp the angle to the table domain so
                // both results agree.
                float geometryAngle = baked
                    ? Mathf.Clamp(angle, -profile.tilted.maxAngle, profile.tilted.maxAngle)
                    : angle;
                VesselFillMath.Rotate(interiorPolygon, geometryAngle, rotatedPolygon);
                VesselFillMath.VerticalExtent(rotatedPolygon, out minY, out maxY);
            }

            GroupUnits();

            // I leave half a cap above the waterline so the brim cannot clip it. Clamp volume share, not height,
            // to keep the chord valid.
            float ceiling = derivesSurfaceCeiling || !baked
                ? SurfaceCeiling(rotatedPolygon, minY, maxY)
                : 0f;
            float ceilingFill = derivesSurfaceCeiling
                ? Mathf.Clamp01(VesselFillMath.AreaBelow(rotatedPolygon, ceiling)
                                / Mathf.Max(polygonArea, 1e-5f))
                : baked ? profile.tilted.CeilingFillAt(angle) : 1f;
            int bandCount = 0;
            float shownPrevious = 0f;
            surfaceLocalY = minY;

            for (int g = 0; g < groupTops.Count && bandCount < MaxBands; g++)
            {
                float shown = Mathf.Min(groupTops[g], geometryVolume);
                if (shown <= shownPrevious + 1e-4f) break;

                // Each unit keeps a fixed front edge so existing colours stay at the same height when covered.
                bool isSurface = shown >= geometryVolume - 1e-4f;
                float even = isSurface
                    ? SurfaceFraction(geometryVolume)
                    : JunctionFraction(shown);
                // The fixed-height branch already includes the fill cap. Apply it only to the volume branch to
                // avoid scaling twice.
                float volumeFraction = shown / capacity * UsableFill;
                float fraction = Mathf.Lerp(volumeFraction, even, EvenBands);

                float level, centerX, half;
                if (baked)
                {
                    profile.tilted.Sample(angle, Mathf.Min(fraction, ceilingFill),
                        out level, out centerX, out half);
                    if (derivesSurfaceCeiling
                        && (fraction >= ceilingFill || level >= ceiling))
                    {
                        // I replace an overshooting table chord with the exact ceiling chord so the cap stays
                        // below the brim.
                        level = ceiling;
                        half = VesselFillMath.HalfWidthAt(
                            rotatedPolygon, level, out centerX);
                    }
                }
                else
                {
                    level = VesselFillMath.LevelForFraction(rotatedPolygon, polygonArea, fraction);
                    level = Mathf.Min(level, ceiling);
                    half = VesselFillMath.HalfWidthAt(rotatedPolygon, level, out centerX);
                }

                // I clip visible chords at the real body wall to exclude handle holes. The wall uses the same
                // rotated frame during pours.
                if (baked)
                    ClampProfileRightInteriorSpan(
                        angle, level, ref centerX, ref half);

                Color c = groupColors[g];
                Color cap = LiquidPalette.CapFor(c);
                Color shade = LiquidPalette.ShadeFor(c);
                bandColors[bandCount] = new Vector4(c.r, c.g, c.b, 1f);
                bandCaps[bandCount] = new Vector4(cap.r, cap.g, cap.b, 1f);
                bandShades[bandCount] = new Vector4(
                    shade.r, shade.g, shade.b, 1f);
                bandInfo[bandCount] = new Vector4(level, centerX, half, 0f);
                bandCount++;

                surfaceLocalY = level;
                shownPrevious = shown;
                if (shown >= geometryVolume - 1e-4f) break;
            }

            for (int i = bandCount; i < MaxBands; i++)
            {
                bandColors[i] = Vector4.zero;
                bandCaps[i] = Vector4.zero;
                bandShades[i] = Vector4.zero;
                bandInfo[i] = Vector4.zero;
            }

            block ??= new MaterialPropertyBlock();
            liquidRenderer.GetPropertyBlock(block);
            block.SetVectorArray(BandColorId, bandColors);
            block.SetVectorArray(BandCapId, bandCaps);
            block.SetVectorArray(LiquidSurfaceContract.BandShadeId, bandShades);
            block.SetVectorArray(LiquidSurfaceContract.BandInfoId, bandInfo);
            block.SetFloat(LiquidSurfaceContract.BandCountId, bandCount);
            block.SetFloat(AngleId, angle * Mathf.Deg2Rad);
            block.SetFloat(LiquidSurfaceContract.BulgeId, Bulge);
            block.SetFloat(LiquidSurfaceContract.InnerCurveId, JunctionCurve);
            block.SetFloat(LiquidSurfaceContract.InnerBulgeId, JunctionDepth);
            block.SetFloat(LiquidSurfaceContract.SurfaceScaleId,
                LiquidSurfaceContract.ExposedSurfaceScale(volume, capacity));
            // I convert RoyalGlassLab pixels to vessel-local units so the liquid layout keeps the same proportions
            // at every scale.
            block.SetFloat(LiquidSurfaceContract.RoyalUnitsPerPixelId,
                RoyalUnitsPerPixel);
            block.SetFloat(LiquidSurfaceContract.BulgeMaxId,
                Mathf.Max(0.005f, InteriorHeight * CapDepth));
            // Publish the current cap geometry for the separate contact renderer.
            int surfaceBand = Mathf.Max(bandCount - 1, 0);
            float surfaceHalfChord = Mathf.Max(0.0001f, bandInfo[surfaceBand].z);
            surfaceLocalX = bandInfo[surfaceBand].y;
            surfaceFullChord = surfaceHalfChord * 2f;
            block.SetFloat(RevealTurbulenceAmountId,
                Mathf.Clamp01(revealTurbulenceAmount));
            block.SetFloat(RevealTurbulenceLifeId,
                Mathf.Clamp01(revealTurbulenceLife));
            block.SetFloat(CompletionFxProgressId,
                Mathf.Clamp01(completionFxProgress));
            block.SetColor(CompletionFxColorId, completionFxColor);
            block.SetVector(LiquidSurfaceContract.FloatingGarnishCausticId,
                floatingGarnishCaustic);
            float ambientBubbleStrength = Profiled && !IsTransferReserved
                ? Mathf.Clamp01(profile.ambientRisingBubbleStrength)
                : 0f;
            float ambientBubblePhase = Mathf.Repeat(
                GetInstanceID() * 0.754877666f, 1f);
            block.SetVector(LiquidSurfaceContract.AmbientBubbleId,
                new Vector4(
                    ambientBubbleStrength, ambientBubblePhase, 0f, 0f));
            // Contact colour remains local. Never wash the complete top face white.
            block.SetFloat(CapFlashId, 0f);
            block.SetVector(MaskUvId, maskUv);
            block.SetVector(QuadSizeId, new Vector4(quadRect.width, quadRect.height, 0f, 0f));
            block.SetVector(LiquidSurfaceContract.RightInteriorClipId,
                Profiled && profile.clipRightInterior
                    ? new Vector4(1f, profile.rightInteriorXAtY0,
                        profile.rightInteriorSlope, 0f)
                    : Vector4.zero);
            float bottomInteriorInset = Profiled
                ? VesselPresentationMath.RoyalPixelsToLocal(
                    profile.bottomInteriorInsetPixels, profile)
                : 0f;
            block.SetFloat(LiquidSurfaceContract.BottomInteriorInsetId,
                bottomInteriorInset);
            block.SetFloat(LiquidSurfaceContract.BottomInteriorFloorId,
                Profiled ? profile.DrawnFloorLocal : 0f);
            block.SetVector(LiquidSurfaceContract.GlassExpansionId, glassExpansion);
            bool hasLiquidFloorCurve = Profiled && profile.HasLiquidFloorCurve;
            block.SetVector(LiquidSurfaceContract.LiquidFloorRangeId,
                hasLiquidFloorCurve
                    ? new Vector4(profile.liquidFloorXRange.x,
                        profile.liquidFloorXRange.y, 1f, 0f)
                    : Vector4.zero);
            if (hasLiquidFloorCurve)
            {
                block.SetFloatArray(LiquidSurfaceContract.LiquidFloorSamplesId,
                    profile.liquidFloorSamples);
            }
            block.SetVector(InteriorId, new Vector4(
                Mathf.Max(0.01f, quadRect.width * 0.5f),
                Mathf.Max(0.01f, quadRect.height * 0.5f), 0f, 0f));

            block.SetTexture(MaskTexId, ResolveLiquidMask());
            liquidRenderer.SetPropertyBlock(block);

            liquidRenderer.enabled = bandCount > 0 && !previewContactOnly;
        }

        /// <summary>The highest waterline whose cap fits below the brim. A few passes settle the chord-dependent cap depth.</summary>
        private float SurfaceCeiling(IList<Vector2> polygon, float minY, float maxY)
        {
            return VesselFillMath.SurfaceCeiling(
                polygon, minY, maxY, InteriorHeight, Bulge, CapDepth,
                Allowance, GapCaps, Headroom);
        }

        /// <summary>I start fill mapping at the interior floor so the bottom colour cannot gain uncounted height.</summary>
        private float VisibleFloor(float low, float high) => Profiled ? profile.upright.floorY : low;

        /// <summary>I anchor the surface's front edge so a colour keeps its apparent height when its cap becomes exposed.</summary>
        private float SurfaceLevelUpright(float volume) =>
            WaterlineForFrontEdge(UnitFrontEdgeLevelUpright(volume), SurfaceScale(volume));

        /// <summary>I keep the authored cap depth even for one unit; volume only moves its front-edge waterline.</summary>
        private float SurfaceScale(float volume) =>
            LiquidSurfaceContract.ExposedSurfaceScale(volume, capacity);

        /// <summary>The vessel-local length of one RoyalGlassLab pixel. Zero leaves unprofiled bottles on the shader's derivative fallback.</summary>
        private float RoyalUnitsPerPixel => Profiled
            ? VesselPresentationMath.RoyalLocalUnitsPerPixel(profile)
            : 0f;

        /// <summary>The shell supplies (anchorX, spriteLocalWidth, designPixels, 0) for front-glass stretch. I reuse it so liquid and glass cannot disagree.</summary>
        private Vector4 glassExpansion = new Vector4(0f, 1f, 0f, 0f);

        internal void SetGlassExpansion(Vector4 expansion)
        {
            if (glassExpansion == expansion) return;
            glassExpansion = expansion;
            // I only republish the property block here; Invalidate would tear down geometry and renderer caches
            // during the shell build.
            lastBuiltLookHash = int.MinValue;
        }

        /// <summary>Volume below the surface as a fraction, so it represents the same amount of liquid at any tilt.</summary>
        private float SurfaceFraction(float volume) => AreaFraction(SurfaceLevelUpright(volume));

        /// <summary>Volume below <paramref name="units"/> cumulative units. It ignores total fill so adding colour cannot resize the bands below.</summary>
        private float JunctionFraction(float units)
        {
            if (units <= 1e-4f) return 0f;
            return AreaFraction(WaterlineForFrontEdge(
                UnitFrontEdgeLevelUpright(units), JunctionCurve));
        }

        /// <summary>Each unit index has a fixed viewer-facing edge for exposed and covered curves. Profiled glasses use baked optical heights so taper and opaque bases keep equal-looking bands.</summary>
        private float UnitFrontEdgeLevelUpright(float units)
        {
            float unitFraction = Mathf.Clamp01(units / Mathf.Max(1f, capacity));
            float fullCentre = EffectiveFullCentreUpright();

            if (Profiled && profile.upright.HasVisibleHeightMap)
            {
                VesselProfile.UprightTable table = profile.upright;
                float fullFrontEdge = fullCentre - table.CapHalfDepthAt(fullCentre);
                float visibleFloor = profile.hasVisibleLiquidFloor
                    ? profile.visibleLiquidFloor
                    : table.floorY;
                if (profile.HasLiquidFloorCurve)
                    visibleFloor = Mathf.Max(
                        visibleFloor, profile.LiquidFloorMinLocal);
                return table.LevelAtVisibleHeight(Mathf.Lerp(
                    table.VisibleHeightAt(visibleFloor),
                    table.VisibleHeightAt(fullFrontEdge), unitFraction));
            }

            float floor;
            if (Profiled)
            {
                floor = profile.upright.floorY;
                if (profile.HasLiquidFloorCurve)
                    floor = Mathf.Max(floor, profile.LiquidFloorMinLocal);
            }
            else
            {
                VesselFillMath.VerticalExtent(interiorPolygon, out float low, out float high);
                floor = VisibleFloor(low, high);
            }

            float fullCap = Profiled
                ? profile.upright.CapHalfDepthAt(fullCentre)
                : TopCapHalfDepth(interiorPolygon, fullCentre);
            return Mathf.Lerp(floor, fullCentre - fullCap, unitFraction);
        }

        private float EffectiveFullCentreUpright()
        {
            if (Profiled)
            {
                float ceiling = DerivesSurfaceCeiling
                    ? SurfaceCeiling(profile.interiorPolygon,
                        profile.upright.minY, profile.upright.maxY)
                    : profile.upright.ceilingY;
                float authoredFull = AreaFraction(ceiling);
                profile.tilted.Sample(0f, authoredFull * UsableFill,
                    out float level, out _, out _);
                return Mathf.Min(level, ceiling);
            }

            VesselFillMath.VerticalExtent(interiorPolygon, out float low, out float high);
            float fallbackCeiling = SurfaceCeiling(interiorPolygon, low, high);
            float fallbackFull = AreaFraction(fallbackCeiling);
            return VesselFillMath.LevelForFraction(interiorPolygon, polygonArea,
                fallbackFull * UsableFill);
        }

        /// <summary>I solve the waterline from its front edge with the shader's depth formula. Three fixed passes need no search or allocation.</summary>
        private float WaterlineForFrontEdge(float frontEdge, float curve)
        {
            float waterline = frontEdge;
            curve = Mathf.Clamp01(curve);
            for (int i = 0; i < 3; i++)
                waterline = frontEdge + SurfaceHalfDepthAt(waterline) * curve;
            return waterline;
        }

        private float SurfaceHalfDepthAt(float level)
        {
            if (Profiled && profile.clipRightInterior)
            {
                float half = VesselFillMath.HalfWidthAt(
                    profile.interiorPolygon, level, out float centerX);
                ClampProfileRightInteriorSpan(
                    0f, level, ref centerX, ref half);
                return Mathf.Min(
                    2f * half * Bulge, InteriorHeight * CapDepth);
            }
            if (Profiled) return profile.upright.CapHalfDepthAt(level);
            return TopCapHalfDepth(interiorPolygon, level);
        }

        /// <summary>I clip the liquid-frame chord to the real right body wall; the vessel-local half-plane also works at tilt.</summary>
        private void ClampProfileRightInteriorSpan(
            float angleDegrees, float level, ref float centerX, ref float halfWidth)
        {
            if (!Profiled || !profile.clipRightInterior || halfWidth <= 0.0001f)
                return;

            float radians = angleDegrees * Mathf.Deg2Rad;
            float sine = Mathf.Sin(radians);
            float cosine = Mathf.Cos(radians);
            float coefficientX = cosine + profile.rightInteriorSlope * sine;
            if (Mathf.Abs(coefficientX) <= 0.00001f)
                return;

            float coefficientY = sine - profile.rightInteriorSlope * cosine;
            float wallX = (profile.rightInteriorXAtY0 - coefficientY * level)
                          / coefficientX;
            float left = centerX - halfWidth;
            float right = centerX + halfWidth;

            if (coefficientX > 0f)
                right = Mathf.Min(right, wallX);
            else
                left = Mathf.Max(left, wallX);

            // A stale or malformed correction may never collapse valid liquid geometry.
            if (right <= left + 0.0002f)
                return;

            centerX = (left + right) * 0.5f;
            halfWidth = (right - left) * 0.5f;
        }

        /// <summary>Half depth of the surface ellipse at a given waterline.</summary>
        private float TopCapHalfDepth(IList<Vector2> polygon, float level)
        {
            float half = VesselFillMath.HalfWidthAt(polygon, level, out _);
            return Mathf.Min(2f * half * surfaceBulge, interiorHeight * maxCapDepth);
        }

        private float AreaFraction(float level) => Profiled
            ? profile.upright.AreaFractionAt(level)
            : Mathf.Clamp01(VesselFillMath.AreaBelow(interiorPolygon, level) / Mathf.Max(polygonArea, 1e-5f));

        private Color VisualUnitColor(int unitIndex)
        {
            Color color = units[unitIndex];
            return unitIndex == revealUnitIndex
                ? Color.Lerp(revealConcealedColor, color, revealColorProgress)
                : color;
        }

        private void GroupUnits()
        {
            groupColors.Clear();
            groupTops.Clear();
            int visualCount = VisualUnitCount;
            for (int i = 0; i < visualCount; i++)
            {
                Color color = i < units.Count ? VisualUnitColor(i) : receivePreviewColor;
                if (groupColors.Count > 0 && Same(groupColors[groupColors.Count - 1], color))
                    groupTops[groupTops.Count - 1] = i + 1;
                else
                {
                    groupColors.Add(color);
                    groupTops.Add(i + 1);
                }
            }
        }

        private void EnsurePolygon()
        {
            if (interiorPolygon != null && interiorPolygon.Length >= 3) return;

            // I use the baked shape so opening a level never needs sprite-pixel tracing.
            if (Profiled)
            {
                interiorPolygon = profile.interiorPolygon;
                polygonArea = profile.polygonArea;
                mouthLocal = profile.mouthLocal;
                mouthHalfWidth = profile.mouthHalfWidth;
                visibleBottomLocal = profile.visibleBottomLocal;
                capacity = Mathf.Clamp(profile.capacity, 1, MaxBands);
                return;
            }

            // Production uses baked profiles. Legacy bottles use an explicit polygon or stock capsule; pixel
            // tracing stays in VesselProfileBaker.
            if (customInteriorPolygon != null && customInteriorPolygon.Length >= 3)
                interiorPolygon = (Vector2[])customInteriorPolygon.Clone();
            else
                interiorPolygon = VesselFillMath.BottleInterior(
                    interiorWidth, interiorHeight, interiorBottom,
                    bottomCornerRadius, topCornerRadius, 8);
            polygonArea = VesselFillMath.Area(interiorPolygon);
        }

        private bool EnsureRenderer()
        {
            if (liquidRenderer != null && liquidFilter != null)
            {
                BindResolvedLiquidMaterial();
                authoredRendererErrorLogged = false;
                return true;
            }

            Transform found = transform.Find("Liquid");
            if (found == null)
            {
                // I accept a direct Liquid child or one under Liquid Layers so authored layer groups do not get a
                // duplicate renderer.
                Transform layers = transform.Find("Liquid Layers");
                if (layers != null)
                {
                    MeshRenderer[] renderers =
                        layers.GetComponentsInChildren<MeshRenderer>(true);
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        if (renderers[i].GetComponent<MeshFilter>() == null) continue;
                        found = renderers[i].transform;
                        break;
                    }
                }
            }
            if (found == null)
            {
                ReportMissingAuthoredRenderer(
                    "authored 'Liquid' child (or renderer under 'Liquid Layers')");
                return false;
            }
            liquidRoot = found;
            liquidRoot.localPosition = Vector3.zero;
            liquidRoot.localRotation = Quaternion.identity;
            liquidRoot.localScale = Vector3.one;

            liquidFilter = liquidRoot.GetComponent<MeshFilter>();
            liquidRenderer = liquidRoot.GetComponent<MeshRenderer>();
            if (liquidFilter == null || liquidRenderer == null)
            {
                ReportMissingAuthoredRenderer(
                    "MeshFilter and MeshRenderer on the authored liquid object");
                return false;
            }

            liquidRenderer.shadowCastingMode = ShadowCastingMode.Off;
            liquidRenderer.receiveShadows = false;
            liquidRenderer.lightProbeUsage = LightProbeUsage.Off;
            liquidRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            liquidRenderer.sortingLayerName = sortingLayer;
            liquidRenderer.sortingOrder = sortingOrder;

            // I prefer the profile material, then the bottle override. Missing material is an error; runtime
            // generation is unsupported.
            BindResolvedLiquidMaterial();
            authoredRendererErrorLogged = false;
            return true;
        }

        private void ReportMissingAuthoredRenderer(string missing)
        {
            if (authoredRendererErrorLogged) return;
            Debug.LogError(
                $"{name}: {missing} is missing. LiquidBottle will not create "
              + "hierarchy or renderer components at runtime.", this);
            authoredRendererErrorLogged = true;
        }

        private Material ResolveLiquidMaterial() =>
            Profiled && profile.liquidMaterial != null
                ? profile.liquidMaterial
                : liquidMaterial;

        private void BindResolvedLiquidMaterial()
        {
            if (liquidRenderer == null) return;
            Material resolved = ResolveLiquidMaterial();
            if (liquidRenderer.sharedMaterial == resolved) return;

            liquidRenderer.sharedMaterial = resolved;
            // A new material or shader needs fresh contract checks and a full property-block update.
            validatedContractMaterial = null;
            validatedContractShader = null;
            liquidContractValid = false;
            liquidContractErrorLogged = false;
            lastBuiltLookHash = int.MinValue;
        }

        private bool EnsureLiquidRenderContract()
        {
            Material material = liquidRenderer != null
                ? liquidRenderer.sharedMaterial
                : null;
            Shader shader = material != null ? material.shader : null;

            if (material == validatedContractMaterial
                && shader == validatedContractShader)
                return liquidContractValid;

            // I republish the full block after either reference changes, even if both shaders support the
            // contract.
            lastBuiltLookHash = int.MinValue;
            validatedContractMaterial = material;
            validatedContractShader = shader;
            liquidContractValid = LiquidSurfaceContract.TryValidate(
                material, out string reason);
            liquidContractErrorLogged = false;

            if (liquidContractValid) return true;

            if (liquidRenderer != null) liquidRenderer.enabled = false;
            if (!liquidContractErrorLogged)
            {
                Debug.LogError(
                    $"{name}: BottleLiquid render contract failed: {reason}. "
                    + "Liquid was hidden instead of drawing an incorrect full-depth "
                    + "surface.", this);
                liquidContractErrorLogged = true;
            }
            return false;
        }

        private Texture ResolveLiquidMask() => Profiled
            ? profile.interiorMask
            : maskSprite != null ? maskSprite.texture : null;

        /// <summary>Production profiles need a baked mask; legacy bottles may use an authored sprite. I never generate a missing mask at runtime.</summary>
        private bool EnsureLiquidMaskContract()
        {
            if (ResolveLiquidMask() != null)
            {
                liquidMaskContractErrorLogged = false;
                return true;
            }

            if (liquidRenderer != null) liquidRenderer.enabled = false;
            if (!liquidMaskContractErrorLogged)
            {
                string reason = Profiled
                    ? $"baked profile '{profile.name}' has no interiorMask"
                    : "no baked profile or authored maskSprite is assigned";
                Debug.LogError(
                    $"{name}: Liquid mask contract failed: {reason}. "
                    + "Liquid was hidden instead of generating a runtime mask.", this);
                liquidMaskContractErrorLogged = true;
            }
            return false;
        }

        private void EnsureQuad()
        {
            Rect wanted = ComputeQuadRect();
            bool meshStale = quad == null || !quadRectValid || quadRect != wanted
                             || liquidFilter.sharedMesh != quad;
            if (!meshStale) return;

            quadRect = wanted;
            quadRectValid = true;

            if (meshStale)
            {
                if (quad == null) quad = new Mesh { name = "LiquidQuad", hideFlags = HideFlags.DontSave };
                quad.Clear();
                quad.vertices = new[]
                {
                    new Vector3(quadRect.xMin, quadRect.yMin, 0f),
                    new Vector3(quadRect.xMax, quadRect.yMin, 0f),
                    new Vector3(quadRect.xMax, quadRect.yMax, 0f),
                    new Vector3(quadRect.xMin, quadRect.yMax, 0f)
                };
                quad.uv = new[]
                {
                    new Vector2(0f, 0f), new Vector2(1f, 0f),
                    new Vector2(1f, 1f), new Vector2(0f, 1f)
                };
                quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                quad.RecalculateBounds();
                liquidFilter.sharedMesh = quad;
            }

            if (Profiled)
            {
                maskUv = new Vector4(0f, 0f, 1f, 1f);
            }
            else if (maskSprite != null)
            {
                Rect tr = maskSprite.textureRect;
                Texture t = maskSprite.texture;
                maskUv = new Vector4(tr.x / t.width, tr.y / t.height, tr.width / t.width, tr.height / t.height);
            }
            else
            {
                maskUv = new Vector4(0f, 0f, 1f, 1f);
            }

            builtContentVersion = -1;
        }

        private Rect ComputeQuadRect()
        {
            if (Profiled) return profile.QuadRect;
            if (maskSprite != null)
            {
                Bounds b = maskSprite.bounds;
                return new Rect(b.min.x, b.min.y, b.size.x, b.size.y);
            }

            EnsurePolygon();
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < interiorPolygon.Length; i++)
            {
                Vector2 p = interiorPolygon[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }
            // Room for the part of the top face that rises above the interior outline.
            float pad = 0.02f + interiorHeight * maxCapDepth * Mathf.Clamp01(surfaceAllowance) * 1.15f;
            return new Rect(minX - pad, minY - pad, (maxX - minX) + pad * 2f, (maxY - minY) + pad * 2f);
        }

        private void ReleaseQuad()
        {
            if (quad == null) return;
            if (liquidFilter != null && liquidFilter.sharedMesh == quad)
                liquidFilter.sharedMesh = null;
            if (Application.isPlaying) Destroy(quad);
            else DestroyImmediate(quad);
            quad = null;
            quadRectValid = false;
        }

        /// <summary>Interior cross section in bottle local space. Drives both fill math and mask.</summary>
        public Vector2[] InteriorPolygon
        {
            get { EnsurePolygon(); return interiorPolygon; }
        }

        /// <summary>Local space bounds of the interior polygon, padded by a texel or two.</summary>
        public Rect InteriorBounds => ComputeQuadRect();

        /// <summary>The local centre and height of one capacity unit, using the shader's optical bounds so lock markers stay on their bands.</summary>
        public bool TryGetUnitVisualBand(int unitIndex, out Vector2 center,
                                         out float bandHeight)
        {
            center = default;
            bandHeight = 0f;
            EnsurePolygon();
            if (interiorPolygon == null || interiorPolygon.Length < 3
                || unitIndex < 0 || unitIndex >= capacity)
                return false;

            float lower = UnitFrontEdgeLevelUpright(unitIndex);
            float upper = UnitFrontEdgeLevelUpright(unitIndex + 1f);
            // These are already front-edge levels. Running WaterlineForFrontEdge again would shift markers into
            // the next band.
            float y = (lower + upper) * 0.5f;
            float halfWidth = VesselFillMath.HalfWidthAt(interiorPolygon, y,
                                                         out float centerX);
            ClampProfileRightInteriorSpan(0f, y, ref centerX, ref halfWidth);
            if (halfWidth <= 0.0001f) centerX = InteriorBounds.center.x;

            center = new Vector2(centerX, y);
            bandHeight = Mathf.Abs(upper - lower);
            return bandHeight > 0.0001f;
        }

        /// <summary>I anchor the lock belt to the full cavity, independent of fill. Handled glasses clip out the handle so the belt stays centred on the body.</summary>
        public bool TryGetWholeLockBandGeometry(float verticalOffset,
                                                out Vector2 center,
                                                out float columnHeight,
                                                out float bodyWidth)
        {
            return TryGetLockBandGeometry(true, verticalOffset, out center,
                                          out columnHeight, out bodyWidth);
        }

        private bool TryGetLockBandGeometry(bool useBodyCenter,
                                            float verticalOffset,
                                            out Vector2 center,
                                            out float columnHeight,
                                            out float bodyWidth)
        {
            center = default;
            columnHeight = 0f;
            bodyWidth = 0f;
            EnsurePolygon();
            if (interiorPolygon == null || interiorPolygon.Length < 3 || capacity < 1)
                return false;

            float lower = UnitFrontEdgeLevelUpright(0f);
            float upper = UnitFrontEdgeLevelUpright(capacity);
            float anchorY = useBodyCenter
                ? InteriorBounds.center.y
                : (lower + upper) * 0.5f;
            float y = Mathf.Clamp(anchorY + verticalOffset,
                                  Mathf.Min(lower, upper), Mathf.Max(lower, upper));
            float halfWidth = VesselFillMath.HalfWidthAt(interiorPolygon, y,
                                                         out float centerX);
            ClampProfileRightInteriorSpan(0f, y, ref centerX, ref halfWidth);
            if (halfWidth <= 0.0001f) centerX = InteriorBounds.center.x;

            center = new Vector2(centerX, y);
            columnHeight = Mathf.Abs(upper - lower);
            bodyWidth = halfWidth * 2f;
            return columnHeight > 0.0001f && bodyWidth > 0.0001f;
        }

        /// <summary>Local pour lip on the side facing a world-space target.</summary>
        public Vector2 PourMouthLocal(float targetWorldX)
        {
            if (mouthHalfWidth <= 0.0001f) return mouthLocal;

            // mouthHalfWidth is a radius around mouthLocal, which may be offset from the pivot on asymmetric
            // glasses.
            Vector2 left = new Vector2(mouthLocal.x - mouthHalfWidth, mouthLocal.y);
            Vector2 right = new Vector2(mouthLocal.x + mouthHalfWidth, mouthLocal.y);
            float leftDistance = Mathf.Abs(targetWorldX
                - transform.TransformPoint(left).x);
            float rightDistance = Mathf.Abs(targetWorldX
                - transform.TransformPoint(right).x);
            return leftDistance <= rightDistance ? left : right;
        }

        /// <summary>The tilt where the surface reaches the chosen lip. Its sign selects the correct side of an asymmetric vessel.</summary>
        internal float SpillAngle(Vector2 spillMouth, float signedTiltDirection)
        {
            EnsurePolygon();
            // I solve pours from the displayed fill fraction so any visual lift keeps the cap, lip and stream
            // connected.
            float fraction = Mathf.Lerp(
                Mathf.Clamp01(displayVolume / capacity) * UsableFill,
                SurfaceFraction(displayVolume), EvenBands);

            float direction = signedTiltDirection < 0f ? -1f : 1f;
            if (Profiled)
            {
                // The bake stores positive-angle/left-edge tilt. Opposite or offset rims use twelve signed table
                // reads, without allocations.
                if (direction > 0f && (mouthHalfWidth <= 0.0001f
                    || Mathf.Abs(mouthLocal.x) <= 0.0001f))
                    return profile.upright.SpillAngleFor(fraction);

                float low = 0f;
                float high = Mathf.Max(0.01f, profile.tilted.maxAngle);
                for (int i = 0; i < 12; i++)
                {
                    float magnitude = (low + high) * 0.5f;
                    float signedAngle = magnitude * direction;
                    profile.tilted.Sample(signedAngle, fraction,
                        out float level, out _, out _);
                    float radians = signedAngle * Mathf.Deg2Rad;
                    float mouthY = spillMouth.x * Mathf.Sin(radians)
                        + spillMouth.y * Mathf.Cos(radians);
                    if (level >= mouthY) high = magnitude;
                    else low = magnitude;
                }
                return high;
            }

            return VesselFillMath.SpillAngle(
                interiorPolygon, spillMouth, fraction, 130f, 26, direction);
        }

        private static float NormalizeAngle(float degrees)
        {
            degrees %= 360f;
            if (degrees > 180f) degrees -= 360f;
            if (degrees < -180f) degrees += 360f;
            return degrees;
        }

        public static bool Same(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.01f
                   && Mathf.Abs(a.g - b.g) < 0.01f
                   && Mathf.Abs(a.b - b.b) < 0.01f;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            EnsurePolygon();
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.8f);
            for (int i = 0, j = interiorPolygon.Length - 1; i < interiorPolygon.Length; j = i++)
            {
                Gizmos.DrawLine(
                    transform.TransformPoint(interiorPolygon[j]),
                    transform.TransformPoint(interiorPolygon[i]));
            }
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(MouthWorld, 0.06f);
        }
#endif
    }
}
