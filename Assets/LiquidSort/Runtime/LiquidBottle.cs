using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiquidSort
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class LiquidBottle : MonoBehaviour
    {
        public const int MaxBands = 8;

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
        [Tooltip("Baked data for this glass.")]
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
        [Tooltip("Empty interior share above full liquid.")]
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

        [SerializeField, HideInInspector] private float displayVolume = -1f;

        private Transform liquidRoot;
        private MeshRenderer liquidRenderer;
        private MeshFilter liquidFilter;
        private MaterialPropertyBlock block;
        private Mesh quad;

        private Vector2[] interiorPolygon;
        private readonly List<Vector2> rotatedPolygon = new List<Vector2>();
        private readonly List<Vector2> bandProbePolygon = new List<Vector2>();
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
        // Presentation metadata is separate from colour and gameplay. Matching concealed colours must still
        // show their unit boundaries; ordinary matching liquids continue to merge into one band.
        private int lockedPresentationMask;

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

        public bool Profiled => profile != null && profile.IsBaked;

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

        public Vector3 MouthWorld => transform.TransformPoint(new Vector3(mouthLocal.x, mouthLocal.y, 0f));

        public float SurfaceWorldY =>
            transform.position.y + surfaceLocalY * Mathf.Abs(transform.lossyScale.y);

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

        public float ContactWorldYAt(float worldX)
        {
            float halfWidth = Mathf.Max(0.0001f, SurfaceWorldHalfWidth);
            float across = Mathf.Clamp((worldX - SurfaceWorldCenter.x) / halfWidth, -1f, 1f);
            return SurfaceWorldY + SurfaceWorldHalfDepth
                * Mathf.Sqrt(Mathf.Max(0f, 1f - across * across));
        }

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
                return Mathf.Max(0.0001f, halfWidth - Mathf.Min(inset, halfWidth * 0.15f));
            }
        }

        public float UsableFill =>
            Mathf.Clamp(Profiled ? profile.maxFillFraction : maxFillFraction, 0.5f, 1f);

        public Color TopColor => units.Count == 0 ? Color.clear : units[units.Count - 1];

        public Color VisualTopColor =>
            receivePreviewCount > 0 && displayVolume > units.Count + 0.001f
                ? receivePreviewColor
                : units.Count > 0 ? VisualUnitColor(units.Count - 1) : Color.clear;

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

        public float VisualBottomPresence
        {
            get
            {
                float volume = displayVolume >= 0f ? displayVolume : VisualUnitCount;
                return Mathf.Clamp01(volume / 0.35f);
            }
        }

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
            lockedPresentationMask = 0;
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

        internal void SetLockedPresentationMask(int unitMask)
        {
            int wanted = unitMask & ((1 << units.Count) - 1);
            if (lockedPresentationMask == wanted) return;
            lockedPresentationMask = wanted;
            contentVersion++;
        }

        private bool IsUnitPresentedLocked(int unitIndex) =>
            unitIndex >= 0 && unitIndex < units.Count
            && (lockedPresentationMask & (1 << unitIndex)) != 0;

        internal bool TryReserveTransfer(object owner, int operationId)
        {
            if (owner == null || operationId == 0) return false;
            if (transferReservationOwner == null)
            {
                transferReservationOwner = owner;
                transferReservationId = operationId;
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

            int requiredTargetCapacity = target.units.Count + count;
            if (target.units.Capacity < requiredTargetCapacity)
                target.units.Capacity = requiredTargetCapacity;

            units.RemoveRange(units.Count - count, count);
            lockedPresentationMask &= (1 << units.Count) - 1;
            target.lockedPresentationMask &= (1 << target.units.Count) - 1;
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

        internal void CaptureReceiveImpactGeometry(float requestedLocalX,
            float settledVolume, out float localX, out float fullChord)
        {
            CaptureReceiveImpactGeometry(requestedLocalX, settledVolume,
                out localX, out fullChord, out _, out _);
        }

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
            if (wasActive && !revealTurbulenceActive) contentVersion++;
        }

        internal void ClearRevealTurbulence()
        {
            revealTurbulenceAmount = 0f;
            revealTurbulenceLife = 1f;
            revealTurbulenceActive = false;
            revealUnitIndex = -1;
            revealColorProgress = 1f;
            contentVersion++;
        }

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

        internal void ClearTransientMotion()
        {
            ClearRevealTurbulence();
        }

        public void SetSortingOffset(int offset)
        {
            // Changes only draw order during a lift.
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

        public void InvalidateRenderers() => cachedRenderers = null;

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

            bool resumesRetainedProfile = Application.isPlaying && Profiled
                && interiorPolygon != null && interiorPolygon.Length >= 3
                && liquidRenderer != null && liquidFilter != null
                && quad != null && quadRectValid
                && liquidFilter.sharedMesh == quad;
            if (!resumesRetainedProfile) Invalidate();
        }

        private void Start()
        {
            VesselRimGarnish.Ensure(this);
            VesselFloatingGarnish.Ensure(this);
        }

        private void OnDisable()
        {
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

        internal bool RefreshIfNeeded()
        {
            if (Application.isPlaying && Profiled && !RuntimeRefreshNeeded())
                return false;

            Refresh();
            return true;
        }

        public void Refresh()
        {
            PrepareProfileContract();
            EnsurePolygon();
            if (!EnsureRenderer()) return;
            if (!EnsureLiquidRenderContract() || !EnsureLiquidMaskContract()) return;
            EnsureQuad();

            float angle = NormalizeAngle(transform.eulerAngles.z);
            float volume = Mathf.Clamp(displayVolume, 0f, capacity);
            int lookHash = LiquidLookHash(volume);

            bool dirty = builtContentVersion != contentVersion
                         || !Mathf.Approximately(lastBuiltAngle, angle)
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

        private bool RuntimeRefreshNeeded()
        {
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
                lastBuiltLookHash = int.MinValue;
                return true;
            }
            return !(Mathf.Abs(lastBuiltVolume - volume) <= 1e-4f);
        }

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
                    hash = hash * 397 + (profile.interiorMask != null
                        ? profile.interiorMask.GetInstanceID()
                        : 0);
                    hash = hash * 397 + (profile.clipRightInterior ? 1 : 0);
                    hash = hash * 397 + profile.rightInteriorXAtY0.GetHashCode();
                    hash = hash * 397 + profile.rightInteriorSlope.GetHashCode();
                    hash = hash * 397 + profile.bottomInteriorInsetPixels.GetHashCode();
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
                    if (ambientBubbleStrength > 0.0001f)
                        hash = hash * 397 + (IsTransferReserved ? 1 : 0);
                }

                return hash;
            }
        }

        private void BuildBands(float angle, float volume)
        {
            if (liquidRenderer == null) return;

            bool previewContactOnly = volume <= 1e-4f && receivePreviewCount > 0;
            float geometryVolume = previewContactOnly ? 2e-4f : volume;

            bool baked = Profiled;
            bool derivesSurfaceCeiling = baked && DerivesSurfaceCeiling;
            float minY, maxY;
            if (baked && !derivesSurfaceCeiling)
            {
                minY = profile.upright.minY;
                maxY = profile.upright.maxY;
            }
            else
            {
                float geometryAngle = baked
                    ? Mathf.Clamp(angle, -profile.tilted.maxAngle, profile.tilted.maxAngle)
                    : angle;
                VesselFillMath.Rotate(interiorPolygon, geometryAngle, rotatedPolygon);
                VesselFillMath.VerticalExtent(rotatedPolygon, out minY, out maxY);
            }

            GroupUnits();

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
                bandInfo[bandCount] = new Vector4(level, centerX, half,
                    IsUnitPresentedLocked(groupTops[g] - 1) ? 1f : 0f);
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
            block.SetFloat(LiquidSurfaceContract.RoyalUnitsPerPixelId,
                RoyalUnitsPerPixel);
            block.SetFloat(LiquidSurfaceContract.BulgeMaxId,
                Mathf.Max(0.005f, InteriorHeight * CapDepth));
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

        private float SurfaceCeiling(IList<Vector2> polygon, float minY, float maxY)
        {
            return VesselFillMath.SurfaceCeiling(
                polygon, minY, maxY, InteriorHeight, Bulge, CapDepth,
                Allowance, GapCaps, Headroom);
        }

        private float VisibleFloor(float low, float high) => Profiled ? profile.upright.floorY : low;

        private float SurfaceLevelUpright(float volume) =>
            WaterlineForFrontEdge(UnitFrontEdgeLevelUpright(volume), SurfaceScale(volume));

        private float SurfaceScale(float volume) =>
            LiquidSurfaceContract.ExposedSurfaceScale(volume, capacity);

        private float RoyalUnitsPerPixel => Profiled
            ? VesselPresentationMath.RoyalLocalUnitsPerPixel(profile)
            : 0f;

        private Vector4 glassExpansion = new Vector4(0f, 1f, 0f, 0f);

        internal void SetGlassExpansion(Vector4 expansion)
        {
            if (glassExpansion == expansion) return;
            glassExpansion = expansion;
            lastBuiltLookHash = int.MinValue;
        }

        private float SurfaceFraction(float volume) => AreaFraction(SurfaceLevelUpright(volume));

        private float JunctionFraction(float units)
        {
            if (units <= 1e-4f) return 0f;
            return AreaFraction(WaterlineForFrontEdge(
                UnitFrontEdgeLevelUpright(units), JunctionCurve));
        }

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
                if (groupColors.Count > 0
                    && !IsUnitPresentedLocked(i)
                    && !IsUnitPresentedLocked(i - 1)
                    && Same(groupColors[groupColors.Count - 1], color))
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

        public Vector2[] InteriorPolygon
        {
            get { EnsurePolygon(); return interiorPolygon; }
        }

        public Rect InteriorBounds => ComputeQuadRect();

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
            float y = (lower + upper) * 0.5f;
            float halfWidth = VesselFillMath.HalfWidthAt(interiorPolygon, y,
                                                         out float centerX);
            ClampProfileRightInteriorSpan(0f, y, ref centerX, ref halfWidth);
            if (halfWidth <= 0.0001f) centerX = InteriorBounds.center.x;

            center = new Vector2(centerX, y);
            bandHeight = Mathf.Abs(upper - lower);
            return bandHeight > 0.0001f;
        }

        public bool TryGetUnitBandAtTilt(int unitIndex, float angleDegrees,
                                         out Vector2 center, out float thickness,
                                         out float halfWidth)
        {
            center = default;
            thickness = 0f;
            halfWidth = 0f;
            EnsurePolygon();
            if (interiorPolygon == null || interiorPolygon.Length < 3
                || unitIndex < 0 || unitIndex >= capacity)
                return false;

            float angle = NormalizeAngle(angleDegrees);
            bool baked = Profiled;
            bool derivesSurfaceCeiling = baked && DerivesSurfaceCeiling;
            float geometryAngle = baked
                ? Mathf.Clamp(angle, -profile.tilted.maxAngle, profile.tilted.maxAngle)
                : angle;
            VesselFillMath.Rotate(interiorPolygon, geometryAngle, bandProbePolygon);
            VesselFillMath.VerticalExtent(bandProbePolygon, out float minY, out float maxY);
            float ceiling = derivesSurfaceCeiling || !baked
                ? SurfaceCeiling(bandProbePolygon, minY, maxY)
                : maxY;
            float ceilingFill = derivesSurfaceCeiling
                ? Mathf.Clamp01(VesselFillMath.AreaBelow(bandProbePolygon, ceiling)
                                / Mathf.Max(polygonArea, 1e-5f))
                : baked ? profile.tilted.CeilingFillAt(angle) : 1f;

            float lower = UnitBoundaryAtTilt(unitIndex, angle, ceiling, ceilingFill,
                                             derivesSurfaceCeiling);
            float upper = UnitBoundaryAtTilt(unitIndex + 1, angle, ceiling, ceilingFill,
                                             derivesSurfaceCeiling);
            thickness = upper - lower;
            if (thickness <= 0.0001f) return false;

            float middle = (lower + upper) * 0.5f;
            float middleHalf = VesselFillMath.HalfWidthAt(bandProbePolygon, middle,
                                                          out float middleX);
            ClampProfileRightInteriorSpan(angle, middle, ref middleX, ref middleHalf);
            if (middleHalf <= 0.0001f) return false;

            halfWidth = middleHalf;
            for (int side = -1; side <= 1; side += 2)
            {
                float y = middle + side * thickness * 0.44f;
                float half = VesselFillMath.HalfWidthAt(bandProbePolygon, y, out float centerX);
                ClampProfileRightInteriorSpan(angle, y, ref centerX, ref half);
                halfWidth = Mathf.Min(halfWidth, Mathf.Max(0f, half - Mathf.Abs(centerX - middleX)));
            }

            float radians = angle * Mathf.Deg2Rad;
            float sine = Mathf.Sin(radians);
            float cosine = Mathf.Cos(radians);
            center = new Vector2(middleX * cosine + middle * sine,
                                 -middleX * sine + middle * cosine);
            return true;
        }

        private float UnitBoundaryAtTilt(float units, float angle, float ceiling,
                                         float ceilingFill, bool derivesSurfaceCeiling)
        {
            float fraction = Mathf.Lerp(units / capacity * UsableFill,
                                        JunctionFraction(units), EvenBands);
            if (!Profiled)
                return Mathf.Min(VesselFillMath.LevelForFraction(
                    bandProbePolygon, polygonArea, fraction), ceiling);

            profile.tilted.Sample(angle, Mathf.Min(fraction, ceilingFill),
                                  out float level, out _, out _);
            return derivesSurfaceCeiling && (fraction >= ceilingFill || level >= ceiling)
                ? ceiling
                : level;
        }

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

        public Vector2 PourLipLocal(float signedTiltDirection)
        {
            if (mouthHalfWidth <= 0.0001f) return mouthLocal;

            Vector2 left = new Vector2(mouthLocal.x - mouthHalfWidth, mouthLocal.y);
            Vector2 right = new Vector2(mouthLocal.x + mouthHalfWidth, mouthLocal.y);
            bool leftIsWorldLeft = transform.TransformPoint(left).x
                                   <= transform.TransformPoint(right).x;
            return (signedTiltDirection > 0f) == leftIsWorldLeft ? left : right;
        }

        internal float SpillAngle(Vector2 spillMouth, float signedTiltDirection)
        {
            EnsurePolygon();
            float fraction = Mathf.Lerp(
                Mathf.Clamp01(displayVolume / capacity) * UsableFill,
                SurfaceFraction(displayVolume), EvenBands);

            float direction = signedTiltDirection < 0f ? -1f : 1f;
            if (Profiled)
            {
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
