using System;
using DG.Tweening;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiquidSort
{
    /// <summary>I lift the glass, play a floor ring, front/back helix and bubble tail, then restore its exact shelf pose before allowing delivery.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Liquid Sort/Glass Completion Effect")]
    public sealed class BottleCompletionEffect : MonoBehaviour
    {
        private const float LiftDurationSeconds = 0.12f;
        // The shader timeline is 1.78 s. I speed up playback instead of shortening _Duration so the final sparkle
        // cues still run.
        private const float VisualTimelineSeconds = 1.78f;
        private const float VisualPlaybackDurationSeconds = 0.96f;
        private const float SettleDurationSeconds = 0.18f;
        // Authored in RoyalGlassLab world units and scaled with the responsive shelf.
        private const float LiftRoyalWorldDistance = 0.30f;
        private const float LiftScale = 1.025f;
        private const string ShaderResource = "BottleCompletionSpiral";

        private static readonly int ProgressId = Shader.PropertyToID("_Progress");
        private static readonly int DurationId = Shader.PropertyToID("_Duration");
        private static readonly int TintId = Shader.PropertyToID("_Tint");
        private static readonly int FrontPassId = Shader.PropertyToID("_FrontPass");
        private static readonly int AspectId = Shader.PropertyToID("_Aspect");
        private static readonly int BodyBottomId = Shader.PropertyToID("_BodyBottom");
        private static readonly int BodyTopId = Shader.PropertyToID("_BodyTop");
        private static readonly int BodyRadiusId = Shader.PropertyToID("_BodyRadius");

        [Header("Spiral Layers")]
        [SerializeField] private MeshFilter backSpiralMesh;
        [SerializeField] private MeshRenderer backSpiralRenderer;
        [SerializeField] private MeshFilter frontSpiralMesh;
        [SerializeField] private MeshRenderer frontSpiralRenderer;

        private LiquidBottle bottle;
        private BottleShell shell;
        private Material material;
        private Mesh quad;
        private MaterialPropertyBlock backBlock;
        private MaterialPropertyBlock frontBlock;
        private Tween progressTween;
        private long nextRunId;
        private long activeRunId;
        private bool settling;
        private bool cleanupFailed;
        private Transform motionRoot;
        private Vector3 restLocalPosition;
        private Quaternion restLocalRotation = Quaternion.identity;
        private Vector3 restLocalScale = Vector3.one;
        private bool motionCaptured;
        private bool shadowSuppressed;
        private Action checkCue;
        private Action presentationFinishedCue;
        private bool shaderErrorLogged;
        private Color tint = new Color(0.52f, 0.92f, 1f, 1f);
        private float aspect = 0.42f;
        private float bodyBottom = 0.05f;
        private float bodyTop = 0.78f;
        private float bodyRadius = 0.18f;
        private Vector3 authoredLocalPosition;
        private Quaternion authoredLocalRotation = Quaternion.identity;
        private Vector3 authoredLocalScale = Vector3.one;
        private bool authoredPoseCaptured;

        public bool IsPlaying => activeRunId != 0L || settling;
        internal bool CanReuse => !IsPlaying && !cleanupFailed;
        public LiquidBottle TargetBottle => bottle;

        private void Awake()
        {
            CaptureAuthoredPose();
        }

        private void OnDisable() => Stop();

        private void OnDestroy()
        {
            Stop();
            ReleaseVisuals();
        }

        /// <summary>I start from the shelf's saved seat pose. The badge reveals on descent, and confirmation waits until the root is fully restored.</summary>
        public bool Play(LiquidBottle targetBottle,
                         BottleShell targetShell,
                         Transform presentationRoot,
                         Vector3 homePosition,
                         Quaternion homeRotation,
                         Vector3 homeLocalScale,
                         Vector3 liftDirectionWorld,
                         Action onBadgeCue = null,
                         Action onCheckCue = null,
                         Action onPresentationFinished = null)
        {
            if (!Application.isPlaying || !isActiveAndEnabled) return false;
            if (!CanReuse || nextRunId == long.MaxValue) return false;
            if (targetBottle == null || targetShell == null || !EnsureVisuals())
                return false;

            long runId = ++nextRunId;
            activeRunId = runId;
            bottle = targetBottle;
            shell = targetShell;
            checkCue = onCheckCue;
            presentationFinishedCue = onPresentationFinished;
            try
            {
                CaptureMotion(presentationRoot, homePosition, homeRotation,
                    homeLocalScale, liftDirectionWorld, out Vector3 liftedLocalPosition);
                FollowTargetPose();
                ConfigureGeometry();
                tint = ResolveTint();
                ConfigureSorting();
                backSpiralRenderer.enabled = false;
                frontSpiralRenderer.enabled = false;
                bottle.ClearCompletionEffect();

                Sequence sequence = DOTween.Sequence()
                    .SetUpdate(UpdateType.Late, true)
                    .SetRecyclable(true)
                    .SetTarget(this);
                if (sequence == null)
                {
                    SettlePresentation(runId, false, true, true);
                    return false;
                }
                // Own the sequence before adding children so startup failures can settle the partial run.
                progressTween = sequence;
                sequence.OnComplete(() => SettlePresentation(runId, true, true, false));
                sequence.OnKill(() => SettlePresentation(runId, false, true, false));
                sequence.OnUpdate(() =>
                {
                    if (OwnsRun(runId)) FollowTargetPose();
                });
                if (motionCaptured)
                {
                    sequence.Append(motionRoot
                        .DOLocalMove(liftedLocalPosition, LiftDurationSeconds)
                        .SetEase(Ease.OutCubic).SetRecyclable(true));
                    sequence.Join(motionRoot
                        .DOScale(restLocalScale * LiftScale, LiftDurationSeconds)
                        .SetEase(Ease.OutCubic).SetRecyclable(true));
                }
                else
                {
                    sequence.AppendInterval(LiftDurationSeconds);
                }
                sequence.AppendCallback(() =>
                {
                    if (OwnsRun(runId)) BeginVisuals();
                });
                sequence.Append(DOVirtual.Float(
                        0.001f, 1f, VisualPlaybackDurationSeconds,
                        value =>
                        {
                            if (OwnsRun(runId)) ApplyProgress(value);
                        })
                    .SetEase(Ease.Linear).SetRecyclable(true).SetTarget(this));
                if (onBadgeCue != null)
                    sequence.AppendCallback(() =>
                    {
                        if (OwnsRun(runId)) InvokeCue(onBadgeCue);
                    });
                if (motionCaptured)
                {
                    sequence.Append(motionRoot
                        .DOLocalMove(restLocalPosition, SettleDurationSeconds)
                        .SetEase(Ease.InOutSine).SetRecyclable(true));
                    sequence.Join(motionRoot
                        .DOLocalRotateQuaternion(restLocalRotation, SettleDurationSeconds)
                        .SetEase(Ease.InOutSine).SetRecyclable(true));
                    sequence.Join(motionRoot
                        .DOScale(restLocalScale, SettleDurationSeconds)
                        .SetEase(Ease.InOutSine).SetRecyclable(true));
                }
                else
                {
                    // Let the 180 ms badge pop complete before its hit area becomes live.
                    sequence.AppendInterval(SettleDurationSeconds);
                }
                return OwnsRun(runId);
            }
            catch (Exception exception)
            {
                SettlePresentation(runId, false, true, true);
                Debug.LogException(exception, this);
                return false;
            }
        }

        /// <summary>I stop only this effect's tween, leaving pour, contact and reveal tweens alone.</summary>
        public void Stop()
        {
            Stop(true);
        }

        /// <summary>I pass false on level/reuse cleanup so an old captured pose cannot overwrite a newly assigned shelf position.</summary>
        public void Stop(bool restoreMotion)
        {
            SettlePresentation(activeRunId, false, restoreMotion, true);
        }

        private void CaptureMotion(Transform presentationRoot,
                                   Vector3 homePosition,
                                   Quaternion homeRotation,
                                   Vector3 homeLocalScale,
                                   Vector3 liftDirectionWorld,
                                   out Vector3 liftedLocalPosition)
        {
            liftedLocalPosition = default;
            motionRoot = presentationRoot;
            motionCaptured = motionRoot != null
                          && motionRoot.gameObject.activeInHierarchy;
            if (!motionCaptured) return;

            // I use the shelf's saved pose so previous selection or pour motion cannot become the new home
            // position.
            motionRoot.SetPositionAndRotation(homePosition, homeRotation);
            motionRoot.localScale = homeLocalScale;
            restLocalPosition = motionRoot.localPosition;
            restLocalRotation = motionRoot.localRotation;
            restLocalScale = motionRoot.localScale;

            Vector3 direction = liftDirectionWorld.sqrMagnitude > 0.00000001f
                ? liftDirectionWorld.normalized
                : Vector3.up;
            float relativeScale = VesselPresentationMath.RelativeToRoyalReference(
                bottle.transform, bottle.profile);
            float worldDistance = VesselPresentationMath.ReferenceDistance(
                LiftRoyalWorldDistance, relativeScale);
            Vector3 worldOffset = direction * worldDistance;
            Vector3 localOffset = motionRoot.parent != null
                ? motionRoot.parent.InverseTransformVector(worldOffset)
                : worldOffset;
            liftedLocalPosition = restLocalPosition + localOffset;

            shell.SetShadowMotionSuppressed(true);
            shadowSuppressed = true;
        }

        private void BeginVisuals()
        {
            if (backSpiralRenderer != null) backSpiralRenderer.enabled = true;
            if (frontSpiralRenderer != null) frontSpiralRenderer.enabled = true;
            ApplyProgress(0.001f);
        }

        private bool EnsureVisuals()
        {
            if (backSpiralMesh == null || backSpiralRenderer == null
                || frontSpiralMesh == null || frontSpiralRenderer == null)
            {
                if (!shaderErrorLogged)
                {
                    Debug.LogError(
                        "Glass Completion Effect sahne bağlantıları eksik; Back Spiral ve Front Spiral alanlarını bağlayın.",
                        this);
                    shaderErrorLogged = true;
                }
                return false;
            }
            if (material != null && quad != null)
                return true;

            Shader shader = Resources.Load<Shader>(ShaderResource);
            if (shader == null) shader = Shader.Find("LiquidSort/BottleCompletionSpiral");
            if (shader == null)
            {
                if (!shaderErrorLogged)
                {
                    Debug.LogError(
                        "Bottle completion shader bulunamadı; helis efekti güvenli biçimde atlandı.",
                        this);
                    shaderErrorLogged = true;
                }
                return false;
            }

            material = new Material(shader)
            {
                name = "Bottle Completion Spiral (Runtime)",
                hideFlags = HideFlags.HideAndDontSave,
            };
            quad = CreateQuad();
            backSpiralMesh.sharedMesh = quad;
            frontSpiralMesh.sharedMesh = quad;
            backSpiralRenderer.sharedMaterial = material;
            frontSpiralRenderer.sharedMaterial = material;
            ConfigureRenderer(backSpiralRenderer);
            ConfigureRenderer(frontSpiralRenderer);
            return true;
        }

        private static void ConfigureRenderer(MeshRenderer renderer)
        {
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.enabled = false;
        }

        private static Mesh CreateQuad()
        {
            var mesh = new Mesh
            {
                name = "Bottle Completion Quad (Runtime)",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, 0f),
                    new Vector3( 0.5f, -0.5f, 0f),
                    new Vector3( 0.5f,  0.5f, 0f),
                    new Vector3(-0.5f,  0.5f, 0f),
                },
                uv = new[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(1f, 0f),
                    new Vector2(1f, 1f),
                    new Vector2(0f, 1f),
                },
                triangles = new[] { 0, 2, 1, 0, 3, 2 },
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        private void ConfigureGeometry()
        {
            Rect interior = bottle.InteriorBounds;
            float interiorHeight = Mathf.Max(interior.height, 0.05f);
            float mouthY = Mathf.Max(interior.yMax, bottle.mouthLocal.y);
            float bodyHeight = Mathf.Max(interiorHeight, mouthY - interior.yMin);
            float mouthWidth = Mathf.Max(0f, bottle.mouthHalfWidth * 2f);
            float bodyWidth = Mathf.Max(interior.width, mouthWidth, 0.05f);
            float centreX = mouthWidth > 0.001f
                ? bottle.mouthLocal.x
                : interior.center.x;

            // I widen only the completion quad and its transparent margin so the helix and aura cannot clip on
            // narrow glasses.
            float bottom = interior.yMin - bodyHeight * 0.14f;
            float top = mouthY + bodyHeight * 0.34f;
            float width = bodyWidth * 2.10f;
            float height = Mathf.Max(top - bottom, 0.05f);
            Vector3 position = new Vector3(centreX, (bottom + top) * 0.5f, 0f);
            Vector3 scale = new Vector3(width, height, 1f);

            backSpiralRenderer.transform.localPosition = position;
            backSpiralRenderer.transform.localRotation = Quaternion.identity;
            backSpiralRenderer.transform.localScale = scale;
            frontSpiralRenderer.transform.localPosition = position;
            frontSpiralRenderer.transform.localRotation = Quaternion.identity;
            frontSpiralRenderer.transform.localScale = scale;

            aspect = width / height;
            bodyBottom = Mathf.Clamp01((interior.yMin - bottom) / height);
            bodyTop = Mathf.Clamp01((mouthY - bottom) / height);
            // Radius uses shader height-normalized units; extra transparent quad space only prevents aura
            // clipping.
            bodyRadius = bodyWidth * 0.54f / height;
        }

        private void ConfigureSorting()
        {
            backSpiralRenderer.gameObject.layer = bottle.gameObject.layer;
            frontSpiralRenderer.gameObject.layer = bottle.gameObject.layer;
            string layer = bottle.sortingLayer;
            int liquidOrder = bottle.sortingOrder;
            int frontOrder = Mathf.Max(shell.frontOrder, shell.thinFxOrder) + 1;
            // I put the rear ribbon at order 3, between floating garnish (2) and front glass (5); liquid stays at
            // 1.
            int backOrder = liquidOrder + 1;
            if (bottle.profile != null)
                backOrder = Mathf.Max(backOrder,
                    bottle.profile.floatingGarnishSortingOrder + 1);
            if (shell.frontOrder > liquidOrder)
                backOrder = Mathf.Min(backOrder, shell.frontOrder - 1);
            backSpiralRenderer.sortingLayerName = layer;
            backSpiralRenderer.sortingOrder = backOrder;
            frontSpiralRenderer.sortingLayerName = layer;
            frontSpiralRenderer.sortingOrder = frontOrder;
        }

        private Color ResolveTint()
        {
            Color coolLight = new Color(0.52f, 0.92f, 1f, 1f);
            Color liquid = bottle.VisualTopColor;
            if (liquid.a <= 0.001f) liquid = bottle.VisualBottomColor;
            if (liquid.a <= 0.001f) return coolLight;

            Color.RGBToHSV(liquid, out float hue, out float saturation, out _);
            if (saturation <= 0.08f) return coolLight;

            // I keep the liquid hue and brighten its core with very little white; the shader adds the tiny hot
            // centre.
            Color liquidLight = Color.HSVToRGB(
                hue, Mathf.Clamp(saturation * 0.82f, 0.38f, 0.72f), 1f);
            Color resolved = Color.Lerp(liquidLight, Color.white, 0.08f);
            resolved.a = 1f;
            return resolved;
        }

        private void ApplyProgress(float value)
        {
            float progress = Mathf.Clamp01(value);
            ApplyBlock(backSpiralRenderer, ref backBlock, progress, 0f);
            ApplyBlock(frontSpiralRenderer, ref frontBlock, progress, 1f);
            if (bottle != null) bottle.SetCompletionEffect(progress, tint);
        }

        private void ApplyBlock(MeshRenderer renderer, ref MaterialPropertyBlock block,
                                float progress, float frontPass)
        {
            if (renderer == null) return;
            block ??= new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            block.SetFloat(ProgressId, progress);
            block.SetFloat(DurationId, VisualTimelineSeconds);
            block.SetColor(TintId, tint);
            block.SetFloat(FrontPassId, frontPass);
            block.SetFloat(AspectId, aspect);
            block.SetFloat(BodyBottomId, bodyBottom);
            block.SetFloat(BodyTopId, bodyTop);
            block.SetFloat(BodyRadiusId, bodyRadius);
            renderer.SetPropertyBlock(block);
        }

        private void InvokeCue(Action cue)
        {
            try
            {
                cue?.Invoke();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        private bool OwnsRun(long runId) =>
            runId != 0L && activeRunId == runId && !settling;

        private void SettlePresentation(long runId, bool completed,
                                        bool restoreMotion, bool killTween)
        {
            if (!OwnsRun(runId)) return;
            // Claim before Kill: OnKill, disable and an old recycled tween cannot finish this run twice.
            activeRunId = 0L;
            settling = true;
            Tween owned = progressTween;
            progressTween = null;
            Action check = checkCue;
            Action finished = presentationFinishedCue;
            checkCue = null;
            presentationFinishedCue = null;
            Exception failure = null;
            try
            {
                TryCleanup(() =>
                {
                    if (killTween && owned != null && owned.IsActive()) owned.Kill(false);
                }, ref failure);
                TryCleanup(() =>
                {
                    if (backSpiralRenderer != null) backSpiralRenderer.enabled = false;
                }, ref failure);
                TryCleanup(() =>
                {
                    if (frontSpiralRenderer != null) frontSpiralRenderer.enabled = false;
                }, ref failure);
                TryCleanup(() =>
                {
                    if (bottle != null) bottle.ClearCompletionEffect();
                }, ref failure);
                TryCleanup(() =>
                {
                    if (!restoreMotion || !motionCaptured || motionRoot == null) return;
                    motionRoot.localPosition = restLocalPosition;
                    motionRoot.localRotation = restLocalRotation;
                    motionRoot.localScale = restLocalScale;
                }, ref failure);
                TryCleanup(() =>
                {
                    if (shadowSuppressed && shell != null) shell.SetShadowMotionSuppressed(false);
                }, ref failure);
                TryCleanup(RestoreAuthoredPose, ref failure);
                // A killed or incompletely cleaned visual never emits the successful confirmation cue.
                if (completed && failure == null) InvokeCue(check);
            }
            finally
            {
                cleanupFailed = failure != null;
                motionCaptured = false;
                motionRoot = null;
                shadowSuppressed = false;
                ClearTarget();
                settling = false;
            }

            if (failure != null) Debug.LogException(failure, this);
            // State is completely detached before the terminal callback returns this object to its pool.
            // It may start a new run synchronously; do not mutate shared fields after invoking it.
            InvokeCue(finished);
        }

        private static void TryCleanup(Action cleanup, ref Exception failure)
        {
            try { cleanup(); }
            catch (Exception exception) { if (failure == null) failure = exception; }
        }

        private void CaptureAuthoredPose()
        {
            if (authoredPoseCaptured) return;
            authoredLocalPosition = transform.localPosition;
            authoredLocalRotation = transform.localRotation;
            authoredLocalScale = transform.localScale;
            authoredPoseCaptured = true;
        }

        private void FollowTargetPose()
        {
            if (bottle == null) return;
            Transform target = bottle.transform;
            transform.SetPositionAndRotation(target.position, target.rotation);

            Vector3 targetScale = target.lossyScale;
            Vector3 parentScale = transform.parent != null
                ? transform.parent.lossyScale
                : Vector3.one;
            transform.localScale = new Vector3(
                DivideScale(targetScale.x, parentScale.x),
                DivideScale(targetScale.y, parentScale.y),
                DivideScale(targetScale.z, parentScale.z));
        }

        private static float DivideScale(float value, float divisor) =>
            Mathf.Abs(divisor) > 0.00001f ? value / divisor : value;

        private void RestoreAuthoredPose()
        {
            if (!authoredPoseCaptured) return;
            transform.localPosition = authoredLocalPosition;
            transform.localRotation = authoredLocalRotation;
            transform.localScale = authoredLocalScale;
        }

        private void ClearTarget()
        {
            bottle = null;
            shell = null;
        }

        private void ReleaseVisuals()
        {
            if (backSpiralMesh != null && backSpiralMesh.sharedMesh == quad)
                backSpiralMesh.sharedMesh = null;
            if (frontSpiralMesh != null && frontSpiralMesh.sharedMesh == quad)
                frontSpiralMesh.sharedMesh = null;
            if (backSpiralRenderer != null
                && backSpiralRenderer.sharedMaterial == material)
                backSpiralRenderer.sharedMaterial = null;
            if (frontSpiralRenderer != null
                && frontSpiralRenderer.sharedMaterial == material)
                frontSpiralRenderer.sharedMaterial = null;
            if (material != null) DestroyOwned(material);
            if (quad != null) DestroyOwned(quad);
            material = null;
            quad = null;
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }
}
