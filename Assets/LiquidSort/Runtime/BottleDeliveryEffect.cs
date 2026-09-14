using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiquidSort
{
    [DefaultExecutionOrder(200)]
    [DisallowMultipleComponent]
    [AddComponentMenu("Liquid Sort/Glass Delivery Effect")]
    public sealed class BottleDeliveryEffect : MonoBehaviour
    {
        private const int StarCount = 2;

        [Header("Smoke-covered disappearance")]
        [Tooltip("Total delivery time in seconds. Changes apply to the next delivery.")]
        [SerializeField, Range(0.1f, 1f)] private float duration = 0.50f;
        [Tooltip("Seconds for the surrounding smoke to build before the glass starts fading.")]
        [SerializeField, Min(0f)] private float fadeDelay = 0.22f;
        [Tooltip("Short opacity transition concealed by the dense center of the delivery mist.")]
        [SerializeField, Min(0.02f)] private float fadeDuration = 0.14f;

        [Header("Two gold sparkles")]
        [Tooltip("Time when the first sparkle appears. The second follows shortly after.")]
        [SerializeField, Min(0f)] private float sparkleStart = 0.12f;
        [Tooltip("Sparkle radius relative to the glass width; automatically limited on short glasses.")]
        [SerializeField, Range(0.01f, 0.15f)] private float sparkleSize = 0.055f;
        [SerializeField, Range(0f, 1f)] private float sparkleOpacity = 0.90f;

        [Header("Prefab references")]
        [Tooltip("The authored Sparkles child. It is moved onto the delivered glass and scaled to its body.")]
        [SerializeField] private Transform sparkleRoot;
        [Tooltip("The two star sprites under Sparkles; colours come from their sprite. Code only moves, sizes and fades them.")]
        [SerializeField] private SpriteRenderer[] sparkleStars = new SpriteRenderer[StarCount];

        private struct PlaybackSettings
        {
            public float Duration, FadeDelay, FadeDuration;
            public float SparkleStart, SparkleSize, SparkleOpacity;
        }

        private PlaybackSettings playback;
        public bool IsPlaying => active;
        internal float SampledOpacity { get; private set; } = 1f;
        public float Duration => active ? playback.Duration : Valid(duration, 0.50f, 0.1f, 1f);

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
        private bool sparklesReady;
        private bool sparklesVisible;
        private Matrix4x4 placedGlassMatrix;
        private Matrix4x4 placedParentMatrix;
        private bool missingReferencesReported;

        private void OnValidate()
        {
            duration = Valid(duration, 0.50f, 0.1f, 1f);
            fadeDelay = Valid(fadeDelay, 0.22f, 0f, duration * 0.8f);
            fadeDuration = Valid(fadeDuration, 0.14f, 0.02f, duration - fadeDelay);
            sparkleStart = Valid(sparkleStart, 0.12f, 0f, duration * 0.85f);
            sparkleSize = Valid(sparkleSize, 0.055f, 0.01f, 0.15f);
            sparkleOpacity = Valid(sparkleOpacity, 0.90f, 0f, 1f);
        }

        private void OnDisable() => StopCurrentRun();

        private void OnDestroy() => StopCurrentRun();

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
            float seconds = Valid(duration, 0.50f, 0.1f, 1f);
            playback = new PlaybackSettings
            {
                Duration = seconds,
                FadeDelay = Valid(fadeDelay, 0.22f, 0f, seconds * 0.8f),
                FadeDuration = Valid(fadeDuration, 0.14f, 0.02f, seconds),
                SparkleStart = Valid(sparkleStart, 0.12f, 0f, seconds * 0.85f),
                SparkleSize = Valid(sparkleSize, 0.055f, 0.01f, 0.15f),
                SparkleOpacity = Valid(sparkleOpacity, 0.90f, 0f, 1f),
            };
            long startedRunId = ++nextRunId;
            activeRunId = startedRunId;
            active = true;
            SampledOpacity = 1f;

            try
            {
                CaptureGeometry(placementRenderer);
                CaptureOpacity();
                shell = bottle.GetComponent<BottleShell>();
                if (shell != null) shell.SetShadowMotionSuppressed(true);
                sparklesReady = EnsureSparkles();
                if (sparklesReady)
                {
                    int layer = placementRenderer != null
                        ? placementRenderer.sortingLayerID
                        : SortingLayer.NameToID(bottle.sortingLayer);
                    int order = Mathf.Min(32767, (placementRenderer != null
                        ? placementRenderer.sortingOrder : bottle.sortingOrder) + 10);
                    foreach (SpriteRenderer star in sparkleStars)
                    {
                        star.sortingLayerID = layer;
                        star.sortingOrder = order;
                    }
                    sparklesVisible = true;
                    SetSparklesVisible(false);
                    PlaceSparkles();
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
            float fadeEnd = Mathf.Min(Duration, playback.FadeDelay + playback.FadeDuration);
            SampledOpacity = 1f - Smooth(Mathf.InverseLerp(playback.FadeDelay, fadeEnd, time));
            motionRoot.localPosition = restLocalPosition;
            motionRoot.localRotation = restLocalRotation;
            motionRoot.localScale = restLocalScale;
            ApplyOpacity();
            StepSparkles(time);
        }

        internal void Stop(long runId)
        {
            if (OwnsRun(runId)) StopCurrentRun();
        }

        private void StopCurrentRun()
        {
            Transform ownedRoot = motionRoot;
            BottleShell ownedShell = shell;
            bool hideSparkles = sparklesReady;
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
            sparklesReady = false;

            try
            {
                RestoreOpacity();
                if (hideSparkles) SetSparklesVisible(false);
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
                    if (ownedShell != null) ownedShell.SetShadowMotionSuppressed(false);
                }
            }
        }

        private struct OpacityTarget
        {
            public Renderer Renderer;
            public SpriteRenderer Sprite;
            public Color RestColor;
            public float RestAlpha;
            public int AlphaProperty;
            public bool ShaderOpacity;
        }

        private static readonly int DeliveryOpacityId = Shader.PropertyToID("_DeliveryOpacity");
        private static readonly int AlphaId = Shader.PropertyToID("_Alpha");
        private readonly List<OpacityTarget> opacityTargets = new List<OpacityTarget>(24);
        private MaterialPropertyBlock opacityBlock;

        private void CaptureOpacity()
        {
            opacityTargets.Clear();
            opacityBlock ??= new MaterialPropertyBlock();
            foreach (Renderer renderer in bottle.GetComponentsInChildren<Renderer>(true))
            {
                Material material = renderer.sharedMaterial;
                bool shaderOpacity = material != null && material.HasProperty(DeliveryOpacityId);
                if (!shaderOpacity && renderer is SpriteRenderer sprite)
                    opacityTargets.Add(new OpacityTarget
                        { Renderer = renderer, Sprite = sprite, RestColor = sprite.color });
                else if (material != null && (shaderOpacity || material.HasProperty(AlphaId)))
                {
                    int property = shaderOpacity ? DeliveryOpacityId : AlphaId;
                    renderer.GetPropertyBlock(opacityBlock);
                    opacityTargets.Add(new OpacityTarget { Renderer = renderer,
                        Sprite = renderer as SpriteRenderer, ShaderOpacity = true, AlphaProperty = property,
                        RestAlpha = opacityBlock.HasFloat(property) ? opacityBlock.GetFloat(property)
                            : material.GetFloat(property) });
                }
            }
        }

        private void LateUpdate()
        {
            if (active) ApplyOpacity();
        }

        private void ApplyOpacity()
        {
            for (int i = 0; i < opacityTargets.Count; i++)
            {
                OpacityTarget part = opacityTargets[i];
                if (part.Renderer == null) continue;
                if (part.Sprite != null && !part.ShaderOpacity)
                {
                    Color color = part.RestColor;
                    color.a *= SampledOpacity;
                    part.Sprite.color = color;
                }
                else
                {
                    part.Renderer.GetPropertyBlock(opacityBlock);
                    opacityBlock.SetFloat(part.AlphaProperty, part.RestAlpha * SampledOpacity);
                    part.Renderer.SetPropertyBlock(opacityBlock);
                }
            }
        }

        private void RestoreOpacity()
        {
            for (int i = 0; i < opacityTargets.Count; i++)
            {
                OpacityTarget part = opacityTargets[i];
                if (part.Renderer == null) continue;
                if (part.Sprite != null && !part.ShaderOpacity) part.Sprite.color = part.RestColor;
                else
                {
                    part.Renderer.GetPropertyBlock(opacityBlock);
                    opacityBlock.SetFloat(part.AlphaProperty, part.RestAlpha);
                    part.Renderer.SetPropertyBlock(opacityBlock);
                }
            }
            opacityTargets.Clear();
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
            bool ready = sparkleRoot != null && sparkleRoot != transform && sparkleRoot.IsChildOf(transform)
                && sparkleRoot.gameObject.activeInHierarchy
                && sparkleStars != null && sparkleStars.Length == StarCount;
            for (int i = 0; ready && i < StarCount; i++)
                ready = sparkleStars[i] != null && sparkleStars[i].sprite != null
                    && sparkleStars[i].transform.parent == sparkleRoot;
            if (!ready)
            {
                if (!missingReferencesReported)
                {
                    missingReferencesReported = true;
                    Debug.LogWarning("Glass Delivery Effect needs its authored Sparkles root and two star sprites. "
                        + "The glass will still finish its exit.", this);
                }
                return false;
            }
            missingReferencesReported = false;
            return true;
        }

        // Moves the Sparkles root onto the glass art centre and scales it to the glass body, so star
        // positions below it are in body units. Redone only if the glass space or effect parent moves.
        private void PlaceSparkles()
        {
            Transform parent = sparkleRoot.parent;
            placedGlassMatrix = glassSpace.localToWorldMatrix;
            placedParentMatrix = parent.localToWorldMatrix;
            sparkleRoot.SetPositionAndRotation(
                glassSpace.TransformPoint(artCenterInGlassSpace), glassSpace.rotation);
            Vector3 glassScale = glassSpace.lossyScale;
            Vector3 parentScale = parent.lossyScale;
            sparkleRoot.localScale = new Vector3(
                Divide(bodyWidth * glassScale.x, parentScale.x),
                Divide(bodyHeight * glassScale.y, parentScale.y),
                Divide(glassScale.z, parentScale.z));
        }

        private void StepSparkles(float time)
        {
            if (!sparklesReady) return;
            if (time <= playback.SparkleStart || time >= Duration)
            {
                SetSparklesVisible(false);
                return;
            }
            if (glassSpace.localToWorldMatrix != placedGlassMatrix
                || sparkleRoot.parent.localToWorldMatrix != placedParentMatrix)
                PlaceSparkles();

            float radius = Mathf.Min(bodyWidth * playback.SparkleSize,
                bodyHeight * playback.SparkleSize * (0.035f / 0.055f));
            for (int star = 0; star < StarCount; star++)
            {
                float start = playback.SparkleStart
                    + (star == 0 ? 0f : Mathf.Min(0.02f, (Duration - playback.SparkleStart) * 0.12f));
                float progress = Mathf.InverseLerp(start, Duration, time);
                float opacity = Smooth(Mathf.Clamp01(progress / 0.22f))
                              * (1f - Smooth(Mathf.InverseLerp(0.35f, 1f, progress)));
                // The sprite's long tips are one local unit; undo the root's body scale to keep stars round.
                float size = radius * (star == 0 ? 1f : 0.78f)
                           * Mathf.Lerp(0.65f, 1f, Mathf.Sin(progress * Mathf.PI));
                SpriteRenderer sprite = sparkleStars[star];
                Transform starTransform = sprite.transform;
                starTransform.localPosition = new Vector3(star == 0 ? -0.23f : 0.24f,
                    (star == 0 ? 0.065f : 0.15f) + 0.025f * progress, 0f);
                starTransform.localScale = new Vector3(size / bodyWidth, size / bodyHeight, 1f);
                sprite.color = new Color(1f, 1f, 1f, opacity * playback.SparkleOpacity);
            }
            SetSparklesVisible(true);
        }

        private void SetSparklesVisible(bool visible)
        {
            if (sparklesVisible == visible || sparkleStars == null) return;
            sparklesVisible = visible;
            foreach (SpriteRenderer star in sparkleStars)
                if (star != null) star.enabled = visible;
        }

        private static float Divide(float value, float by) =>
            Mathf.Abs(by) > 1e-6f ? value / by : value;

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
