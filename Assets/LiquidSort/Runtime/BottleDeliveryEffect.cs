using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiquidSort
{
    /// <summary>An authored, reusable delivery effect driven by the shelf's presentation clock.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Liquid Sort/Glass Delivery Effect")]
    public sealed class BottleDeliveryEffect : MonoBehaviour
    {
        private const int StarCount = 2;
        private const int VerticesPerStar = 9;

        [Header("Glass motion")]
        [Tooltip("Total delivery time in seconds. Changes apply to the next delivery.")]
        [SerializeField, Range(0.1f, 1f)] private float duration = 0.30f;
        [Tooltip("Seconds before the glass starts to shrink.")]
        [SerializeField, Min(0f)] private float shrinkDelay = 0.04f;
        [Tooltip("Rise as a fraction of this glass's visible height.")]
        [SerializeField, Range(0f, 0.3f)] private float liftHeightRatio = 0.10f;
        [Tooltip("Final uniform scale. Kept above zero so vessel geometry remains valid.")]
        [SerializeField, Range(0.005f, 0.1f)] private float minimumScale = 0.015f;

        [Header("Two gold sparkles")]
        [Tooltip("Time when the first sparkle appears. The second follows shortly after.")]
        [SerializeField, Min(0f)] private float sparkleStart = 0.12f;
        [Tooltip("Sparkle radius relative to the glass width; automatically limited on short glasses.")]
        [SerializeField, Range(0.01f, 0.15f)] private float sparkleSize = 0.055f;
        [SerializeField, Range(0f, 1f)] private float sparkleOpacity = 0.90f;
        [SerializeField] private Color sparkleCoreColor = new Color32(255, 246, 198, 255);
        [SerializeField] private Color sparkleTipColor = new Color32(255, 207, 94, 255);

        [Header("Prefab references")]
        [Tooltip("The authored Sparkles child. Only its runtime mesh is generated; the object stays in Hierarchy.")]
        [SerializeField] private MeshFilter sparkleFilter;
        [SerializeField] private MeshRenderer sparkleRenderer;

        private struct PlaybackSettings
        {
            public float Duration, ShrinkDelay, LiftHeightRatio, MinimumScale;
            public float SparkleStart, SparkleSize, SparkleOpacity;
            public Color CoreColor, TipColor;
        }

        private PlaybackSettings playback;
        public bool IsPlaying => active;
        public float Duration => active ? playback.Duration : Valid(duration, 0.30f, 0.1f, 1f);

        private readonly Vector3[] vertices = new Vector3[StarCount * VerticesPerStar];
        private readonly Color32[] colors = new Color32[StarCount * VerticesPerStar];
        private readonly Vector2[] uvs = new Vector2[StarCount * VerticesPerStar];
        private readonly int[] triangles = new int[StarCount * 8 * 3];

        private LiquidBottle bottle;
        private Transform motionRoot;
        private Transform glassSpace;
        private BottleShell shell;
        private Vector3 restLocalPosition;
        private Quaternion restLocalRotation;
        private Vector3 restLocalScale;
        private Vector3 artCenterInGlassSpace;
        private Vector3 artCenterInRoot;
        private float bodyWidth;
        private float bodyHeight;
        private bool active;
        private long nextRunId;
        private long activeRunId;
        private Mesh sparkleMesh;
        private MeshFilter meshOwner;
        private MeshFilter activeSparkleFilter;
        private MeshRenderer activeSparkleRenderer;
        private bool missingReferencesReported;

        private void OnValidate()
        {
            duration = Valid(duration, 0.30f, 0.1f, 1f);
            shrinkDelay = Valid(shrinkDelay, 0.04f, 0f, duration * 0.8f);
            liftHeightRatio = Valid(liftHeightRatio, 0.10f, 0f, 0.3f);
            minimumScale = Valid(minimumScale, 0.015f, 0.005f, 0.1f);
            sparkleStart = Valid(sparkleStart, 0.12f, 0f, duration * 0.85f);
            sparkleSize = Valid(sparkleSize, 0.055f, 0.01f, 0.15f);
            sparkleOpacity = Valid(sparkleOpacity, 0.90f, 0f, 1f);
        }

        private void OnDisable() => StopCurrentRun();

        private void OnDestroy()
        {
            try { StopCurrentRun(); }
            finally { ReleaseMesh(); }
        }

        internal bool Begin(LiquidBottle targetBottle, Transform targetMotionRoot,
                            Transform targetGlassSpace, SpriteRenderer placementRenderer,
                            out long runId)
        {
            runId = 0L;
            if (active || nextRunId == long.MaxValue || !isActiveAndEnabled || targetBottle == null
                || targetMotionRoot == null || targetGlassSpace == null
                || !targetBottle.transform.IsChildOf(targetMotionRoot)
                || targetGlassSpace.IsChildOf(targetMotionRoot)
                || transform.IsChildOf(targetMotionRoot)) return false;

            bottle = targetBottle;
            motionRoot = targetMotionRoot;
            glassSpace = targetGlassSpace;
            restLocalPosition = motionRoot.localPosition;
            restLocalRotation = motionRoot.localRotation;
            restLocalScale = motionRoot.localScale;
            float seconds = Valid(duration, 0.30f, 0.1f, 1f);
            playback = new PlaybackSettings
            {
                Duration = seconds,
                ShrinkDelay = Valid(shrinkDelay, 0.04f, 0f, seconds * 0.8f),
                LiftHeightRatio = Valid(liftHeightRatio, 0.10f, 0f, 0.3f),
                MinimumScale = Valid(minimumScale, 0.015f, 0.005f, 0.1f),
                SparkleStart = Valid(sparkleStart, 0.12f, 0f, seconds * 0.85f),
                SparkleSize = Valid(sparkleSize, 0.055f, 0.01f, 0.15f),
                SparkleOpacity = Valid(sparkleOpacity, 0.90f, 0f, 1f),
                CoreColor = sparkleCoreColor,
                TipColor = sparkleTipColor,
            };
            long startedRunId = ++nextRunId;
            activeRunId = startedRunId;
            active = true;

            try
            {
                CaptureGeometry(placementRenderer);
                shell = bottle.GetComponent<BottleShell>();
                if (shell != null) shell.SetShadowMotionSuppressed(true);
                if (EnsureSparkles())
                {
                    activeSparkleRenderer.sortingLayerID = placementRenderer != null
                        ? placementRenderer.sortingLayerID
                        : SortingLayer.NameToID(bottle.sortingLayer);
                    activeSparkleRenderer.sortingOrder = Mathf.Min(32767,
                        (placementRenderer != null
                            ? placementRenderer.sortingOrder : bottle.sortingOrder) + 10);
                    activeSparkleRenderer.enabled = false;
                }
                if (!OwnsRun(startedRunId)) return false;
                runId = startedRunId;
                return true;
            }
            catch (Exception exception)
            {
                Stop(startedRunId);
                Debug.LogException(exception, targetBottle);
                return false;
            }
        }

        internal bool OwnsRun(long runId) =>
            active && runId > 0L && activeRunId == runId;

        internal void Step(float elapsed, long runId)
        {
            if (!OwnsRun(runId)) return;
            if (bottle == null || motionRoot == null || glassSpace == null)
            {
                Stop(runId);
                return;
            }
            if (float.IsNaN(elapsed) || float.IsInfinity(elapsed)) return;

            float time = Mathf.Clamp(elapsed, 0f, Duration);
            float shrink = Smooth(Mathf.InverseLerp(playback.ShrinkDelay, Duration, time));
            float scale = Mathf.Lerp(1f, playback.MinimumScale, shrink);
            float lift = bodyHeight * playback.LiftHeightRatio * Smooth(time / Duration);
            motionRoot.localRotation = restLocalRotation;
            motionRoot.localScale = restLocalScale * scale;
            // Keep the drawing's centre anchored even when its imported pivot is well outside the glass.
            Vector3 wantedCenter = glassSpace.TransformPoint(
                artCenterInGlassSpace + Vector3.up * lift);
            motionRoot.position += wantedCenter - motionRoot.TransformPoint(artCenterInRoot);
            StepSparkles(time);
        }

        internal void Stop(long runId)
        {
            if (OwnsRun(runId)) StopCurrentRun();
        }

        private void StopCurrentRun()
        {
            // Detach the run before touching Unity objects; destruction cannot revive its captured pose.
            Transform ownedRoot = motionRoot;
            BottleShell ownedShell = shell;
            MeshRenderer ownedRenderer = activeSparkleRenderer;
            bool restorePose = active;
            Vector3 position = restLocalPosition;
            Quaternion rotation = restLocalRotation;
            Vector3 scale = restLocalScale;
            active = false;
            activeRunId = 0L;
            bottle = null;
            motionRoot = null;
            glassSpace = null;
            shell = null;
            activeSparkleFilter = null;
            activeSparkleRenderer = null;

            try
            {
                if (ownedRenderer != null) ownedRenderer.enabled = false;
            }
            finally
            {
                try
                {
                    if (restorePose && ownedRoot != null)
                    {
                        ownedRoot.localPosition = position;
                        ownedRoot.localRotation = rotation;
                        ownedRoot.localScale = scale;
                    }
                }
                finally
                {
                    // Delivery owns shadow suppression only while the seated glass is leaving.
                    if (ownedShell != null) ownedShell.SetShadowMotionSuppressed(false);
                }
            }
        }

        private void CaptureGeometry(SpriteRenderer placementRenderer)
        {
            Transform artSpace = bottle.transform;
            Rect body = bottle.profile != null
                ? bottle.profile.interiorBounds
                : new Rect(-bottle.interiorWidth * 0.5f, bottle.interiorBottom,
                    bottle.interiorWidth, bottle.interiorHeight);
            Bounds localBounds = new Bounds(body.center,
                new Vector3(Mathf.Max(0.001f, body.width),
                    Mathf.Max(0.001f, body.height), 0f));
            if (placementRenderer != null
                && placementRenderer.transform.IsChildOf(motionRoot))
            {
                artSpace = placementRenderer.transform;
                localBounds = placementRenderer.localBounds;
            }

            Vector3 centerWorld = artSpace.TransformPoint(localBounds.center);
            artCenterInGlassSpace = glassSpace.InverseTransformPoint(centerWorld);
            artCenterInRoot = motionRoot.InverseTransformPoint(centerWorld);
            Vector3 right = glassSpace.InverseTransformVector(
                artSpace.TransformVector(Vector3.right * localBounds.size.x));
            Vector3 up = glassSpace.InverseTransformVector(
                artSpace.TransformVector(Vector3.up * localBounds.size.y));
            bodyWidth = Mathf.Max(0.001f, Mathf.Abs(right.x) + Mathf.Abs(up.x));
            bodyHeight = Mathf.Max(0.001f, Mathf.Abs(right.y) + Mathf.Abs(up.y));
        }

        private bool EnsureSparkles()
        {
            bool ready = sparkleFilter != null && sparkleRenderer != null
                && sparkleFilter.gameObject == sparkleRenderer.gameObject
                && sparkleFilter.transform.IsChildOf(transform)
                && sparkleFilter.gameObject.activeInHierarchy
                && sparkleRenderer.sharedMaterial != null;
            if (!ready)
            {
                if (!missingReferencesReported)
                {
                    missingReferencesReported = true;
                    Debug.LogWarning("Glass Delivery Effect needs its authored Sparkles filter, renderer and material. "
                        + "The glass will still finish its exit.", this);
                }
                return false;
            }
            missingReferencesReported = false;
            activeSparkleFilter = sparkleFilter;
            activeSparkleRenderer = sparkleRenderer;
            if (meshOwner != null && meshOwner != activeSparkleFilter
                && meshOwner.sharedMesh == sparkleMesh)
                meshOwner.sharedMesh = null;
            meshOwner = activeSparkleFilter;
            if (sparkleMesh != null)
            {
                activeSparkleFilter.sharedMesh = sparkleMesh;
                return true;
            }
            for (int star = 0; star < StarCount; star++)
            {
                int first = star * VerticesPerStar;
                for (int point = 0; point < 8; point++)
                {
                    int triangle = (star * 8 + point) * 3;
                    triangles[triangle] = first;
                    triangles[triangle + 1] = first + point + 1;
                    triangles[triangle + 2] = first + (point + 1) % 8 + 1;
                }
            }
            sparkleMesh = new Mesh
            {
                name = "Delivery sparkle stars (Runtime)",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = vertices,
                colors32 = colors,
                uv = uvs,
                triangles = triangles,
            };
            sparkleMesh.MarkDynamic();
            activeSparkleFilter.sharedMesh = sparkleMesh;
            activeSparkleRenderer.shadowCastingMode = ShadowCastingMode.Off;
            activeSparkleRenderer.receiveShadows = false;
            activeSparkleRenderer.lightProbeUsage = LightProbeUsage.Off;
            activeSparkleRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            activeSparkleRenderer.enabled = false;
            return true;
        }

        private void StepSparkles(float time)
        {
            if (activeSparkleRenderer == null || sparkleMesh == null || activeSparkleFilter == null) return;
            if (time <= playback.SparkleStart || time >= Duration)
            {
                activeSparkleRenderer.enabled = false;
                return;
            }

            for (int star = 0; star < StarCount; star++)
            {
                float start = playback.SparkleStart
                    + (star == 0 ? 0f : Mathf.Min(0.02f, (Duration - playback.SparkleStart) * 0.12f));
                float progress = Mathf.InverseLerp(start, Duration, time);
                float opacity = Smooth(Mathf.Clamp01(progress / 0.22f))
                              * (1f - Smooth(Mathf.InverseLerp(0.35f, 1f, progress)));
                float radius = Mathf.Min(bodyWidth * playback.SparkleSize,
                    bodyHeight * playback.SparkleSize * (0.035f / 0.055f))
                             * (star == 0 ? 1f : 0.78f)
                             * Mathf.Lerp(0.65f, 1f, Mathf.Sin(progress * Mathf.PI));
                Vector3 center = artCenterInGlassSpace + new Vector3(
                    bodyWidth * (star == 0 ? -0.23f : 0.24f),
                    bodyHeight * ((star == 0 ? 0.065f : 0.15f) + 0.025f * progress),
                    0f);
                WriteStar(star, center, radius, opacity * playback.SparkleOpacity);
            }
            sparkleMesh.vertices = vertices;
            sparkleMesh.colors32 = colors;
            sparkleMesh.RecalculateBounds();
            activeSparkleRenderer.enabled = true;
        }

        private void WriteStar(int star, Vector3 center, float radius, float opacity)
        {
            int first = star * VerticesPerStar;
            Color core = playback.CoreColor;
            Color tip = playback.TipColor;
            core.a *= Mathf.Clamp01(opacity);
            tip.a *= Mathf.Clamp01(opacity);
            vertices[first] = ToSparkleSpace(center);
            colors[first] = core;
            for (int point = 0; point < 8; point++)
            {
                float angle = point * Mathf.PI * 0.25f;
                float reach = (point & 1) == 0 ? radius : radius * 0.25f;
                vertices[first + point + 1] = ToSparkleSpace(center + new Vector3(
                    Mathf.Cos(angle) * reach, Mathf.Sin(angle) * reach, 0f));
                colors[first + point + 1] = tip;
            }
        }

        // Preserve the prefab's authored hierarchy and transforms while following the delivered glass.
        private Vector3 ToSparkleSpace(Vector3 point) =>
            activeSparkleFilter.transform.InverseTransformPoint(glassSpace.TransformPoint(point));

        private void ReleaseMesh()
        {
            Mesh ownedMesh = sparkleMesh;
            MeshFilter ownedFilter = meshOwner;
            sparkleMesh = null;
            meshOwner = null;
            if (ownedMesh == null) return;
            try
            {
                if (ownedFilter != null && ownedFilter.sharedMesh == ownedMesh)
                    ownedFilter.sharedMesh = null;
            }
            finally
            {
                if (Application.isPlaying) Destroy(ownedMesh);
                else DestroyImmediate(ownedMesh);
            }
        }

        private static float Smooth(float value)
        {
            float t = Mathf.Clamp01(value);
            return t * t * (3f - 2f * t);
        }

        private static float Valid(float value, float fallback, float min, float max)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? Mathf.Clamp(fallback, min, max)
                : Mathf.Clamp(value, min, max);
        }
    }
}
