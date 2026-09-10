using System;
using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

namespace LiquidSort
{
    public enum PourPhase
    {
        Idle,
        Active,
        Flow,
    }

    public enum PourOutcome
    {
        Completed,
        Cancelled
    }

    /// <summary>I run the full pour and return on one DOTween timeline, then commit both stacks together.</summary>
    [DisallowMultipleComponent]
    public sealed class PourAnimator : MonoBehaviour
    {
        // Speed up the complete gesture together so carry, flow and settling stay in sync.
        private const float PlaybackSpeed = 1.2f;

        [Header("Timing")]
        public float moveTime = 0.28f;
        /// <summary>I keep measured emission times as constants so stale scene values cannot override them. One unit lasts 0.430 s, measured from 2.900 to 3.330 s.</summary>
        public const float ReferenceUnitTime = 0.43f;
        /// <summary>Extra emission time per unit. 0.31 keeps the receiver's rise near 0.301 s per unit; lower values make multi-unit pours faster.</summary>
        public const float ReferenceAdditionalUnitTime = 0.31f;
        /// <summary>Emission start to tip landing, receiver already holding liquid.</summary>
        public const float ReferenceFilledContactDelay = 0.19f;
        /// <summary>Emission start to tip landing, empty receiver.</summary>
        public const float ReferenceEmptyContactDelay = 0.217f;
        /// <summary>Pinch-off to last visible pixel, one unit: 3.330-3.415 s.</summary>
        public const float ReferenceSingleUnitTailTime = 0.09f;
        /// <summary>Same for a multi-unit transfer: 7.400-7.470 s.</summary>
        public const float ReferenceMultipleUnitTailTime = 0.07f;
        /// <summary>Surface collapse after the tail lands.</summary>
        public const float ReferenceSurfaceCollapseTime = 0.26f;
        /// <summary>Extra settle per additional transferred unit.</summary>
        public const float ReferenceAdditionalUnitSettleTime = 0.025f;

        // I keep shelf-return timing editable per scene because it controls layout motion.
        [Min(0.01f), Tooltip("Return overlaps the detached tail and the surface collapse.")]
        public float shelfReturnTime = 0.34f;

        [Header("Easing")]
        public Ease carryEase = Ease.InOutSine;
        public Ease tiltEase = Ease.InOutSine;
        public Ease returnEase = Ease.OutCubic;

        // I use a small swing because wide-rimmed glasses start pouring well before they reach horizontal.
        [Header("Pose")]
        [Tooltip("Neutral receiver clearance when no baked vessel pose is present.")]
        public float pourHeight = 0.81f;
        [Tooltip("Extra degrees past the angle at which the liquid first reaches the mouth.")]
        public float overTilt = 8f;
        public float maxTilt = 96f;
        [Tooltip("Height of the arc the vessel travels along on its way to the target, over the straight line.")]
        public float carryArc = 0.20f;
        [Tooltip("Reference keeps the vessel at its authored size while it is off the shelf.")]
        [Range(1f, 1.3f)] public float carryScale = 1f;
        public int frontSortingOffset = 60;

        // The reference carry lasts 0.263 s, with tilt starting about one-third in. The stream opens from roughly
        // 38 to 65 degrees.
        private const float ReferenceCarryTiltStart = 0.30f;
        private const float ReferenceStreamStartTilt = 38f;
        private const float ReferenceStreamEndTilt = 65f;
        // I ease only the start and end of flow to keep the total duration without abrupt volume steps.
        private const float ReferenceFlowRampFraction = 0.16f;
        // Stream width is 18-21% of the receiving glass's interior width in the reference.
        private const float ReferenceStreamShareOfReceiver = 0.20f;
        // Lip-to-fall-axis offset as a share of receiver width, matching the reference bend.
        private const float ReferenceLipSideShare = 0.09f;
        [Tooltip("Authored PourStream child used by this animator.")]
        public PourStream stream;
        [Tooltip("Authored PourContactEffect child used by this animator.")]
        [SerializeField] private PourContactEffect contactEffect;
        private bool authoredEffectsErrorReported;

        public PourPhase Phase { get; private set; } = PourPhase.Idle;
        public bool Busy => Phase != PourPhase.Idle;
        public int ActiveOperationId { get; private set; }
        public int ActiveAmount => ActiveOperationId != 0 ? activeAmount : 0;
        /// <summary>Horizontal transfer direction for a subtle stereo cue. Audio uses only the sign; zero means unavailable.</summary>
        public float ActiveHorizontalDirection
        {
            get
            {
                if (ActiveOperationId == 0 || activeTarget == null) return 0f;
                float delta = activeTarget.transform.position.x - activeHome.x;
                return Mathf.Abs(delta) < 0.0001f ? 0f : Mathf.Sign(delta);
            }
        }
        private readonly List<Action<int, PourOutcome>> pourFinishedListeners =
            new List<Action<int, PourOutcome>>(4);
        private readonly List<Action<int, PourOutcome>> notificationSnapshot =
            new List<Action<int, PourOutcome>>(4);

        public event Action<int, PourOutcome> PourFinished
        {
            add
            {
                if (value == null || pourFinishedListeners.Contains(value)) return;
                pourFinishedListeners.Add(value);
                if (notificationSnapshot.Capacity < pourFinishedListeners.Count)
                    notificationSnapshot.Capacity = pourFinishedListeners.Count;
            }
            remove
            {
                if (value != null) pourFinishedListeners.Remove(value);
            }
        }

        private Vector3 carryStartPosition;
        private Vector3 carryPreTiltPosition;
        private Vector3 carryControl;
        private Vector3 carryStartScale;
        private Vector3 carryEndScale;
        private Quaternion carryStartRotation;
        private Quaternion carryEndRotation;
        private Tween activeTween;
        private Vector2 emissionLip;
        private Vector3 mouthAnchor;
        private Vector3 carriedMouthOffset;
        private Vector3 returnStartPosition;
        private Quaternion returnStartRotation;
        private float sourceFrom, targetFrom;
        private float startTilt, homeLocalTilt, tiltSign;
        private float maximumPourTilt, extraPourTilt;
        private float bodyWidth, leadingWidth, referenceScale;
        private float carryDuration, emissionDuration, headDuration;
        private float tailDuration, collapseDuration, returnDuration;
        private bool streamStarted, emissionDetached, contactStarted;
        private bool activeTweenFinished;
        private bool activeTweenCancelled;
        private bool cancellationRequested;
        private bool completingOperation;

        // This animator owns its coroutine and transaction; boards only submit requests.
        private Coroutine activeRoutine;
        private int nextOperationId;
        private LiquidBottle activeSource;
        private LiquidBottle activeTarget;
        private BottleShell activeSourceShell;
        private Transform activeSourceTransform;
        private Renderer[] activeSourceRenderers;
        private int[] activeSourceBaseOrders;
        private int activeAmount;
        private Color activeColor;
        private bool requireMatchingColors;
        private bool targetPrepared;
        private int activeSourceModelVersion;
        private int activeTargetModelVersion;
        private Vector3 activeHome;
        private Quaternion activeHomeRotation;
        private Vector3 activeHomeScale;

        private static bool tweenPoolPrepared;

        /// <summary>I reset the init flag because disabling domain reload can leave it set after DOTween has shut down.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            tweenPoolPrepared = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void PrepareTweenPool()
        {
            if (tweenPoolPrepared) return;
            DOTween.Init();
            DOTween.SetTweensCapacity(200, 50);
            tweenPoolPrepared = true;
        }

        private void Awake()
        {
            ValidateAuthoredEffects();
        }

        private bool ValidateAuthoredEffects()
        {
            if (stream != null && contactEffect != null) return true;
            if (!authoredEffectsErrorReported)
            {
                authoredEffectsErrorReported = true;
                Debug.LogError(
                    "[PourAnimator] PourStream and PourContactEffect must be authored "
                    + "and assigned in the Inspector.", this);
            }
            return false;
        }

        /// <summary>I move the shelf placement parent so the LiquidBottle child keeps its baked local pose.</summary>
        public bool TryStartPour(LiquidBottle source, LiquidBottle target, int amount,
            Transform sourceMotionRoot, Vector3 homePosition, Quaternion homeRotation,
            Vector3 homeLocalScale, bool requireColorMatch = true)
        {
            return TryStartPourInternal(source, target, amount, sourceMotionRoot,
                homePosition, homeRotation, homeLocalScale, requireColorMatch);
        }

        private bool TryStartPourInternal(LiquidBottle source, LiquidBottle target, int amount,
            Transform sourceMotionRoot, Vector3 homePosition, Quaternion homeRotation,
            Vector3 homeLocalScale, bool requireColorMatch)
        {
            if (Busy || completingOperation || !isActiveAndEnabled
                || source == null || target == null
                || source == target || amount <= 0 || source.IsEmpty || target.IsFull)
                return false;

            if (!ValidateAuthoredEffects()
                || !source.isActiveAndEnabled || !target.isActiveAndEnabled
                || stream == null || !stream.isActiveAndEnabled
                || !stream.IsReady
                || contactEffect == null || !contactEffect.isActiveAndEnabled
                || !contactEffect.IsReady)
                return false;

            Transform resolvedMotionRoot = sourceMotionRoot != null
                ? sourceMotionRoot
                : source.transform;
            if (resolvedMotionRoot != source.transform
                && !source.transform.IsChildOf(resolvedMotionRoot)) return false;

            if (requireColorMatch && !target.CanReceive(source.TopColor)) return false;

            unchecked
            {
                nextOperationId++;
                if (nextOperationId == 0) nextOperationId = 1;
            }

            int operationId = nextOperationId;
            amount = Mathf.Min(amount, source.TopRunLength, target.FreeSpace);
            if (amount <= 0 || !source.TryReserveTransfer(this, operationId)) return false;
            if (!target.TryReserveTransfer(this, operationId))
            {
                source.ReleaseTransferReservation(this, operationId);
                return false;
            }

            cancellationRequested = false;
            targetPrepared = false;
            activeSource = source;
            activeTarget = target;
            activeSourceTransform = resolvedMotionRoot;
            activeAmount = amount;
            activeColor = source.TopColor;
            requireMatchingColors = requireColorMatch;
            activeSourceModelVersion = source.ModelVersion;
            activeTargetModelVersion = target.ModelVersion;
            activeHome = homePosition;
            activeHomeRotation = homeRotation;
            activeHomeScale = homeLocalScale;

            ActiveOperationId = operationId;
            Phase = PourPhase.Active;

            try
            {
                activeSourceShell = source.GetComponent<BottleShell>();
                if (activeSourceShell != null)
                    activeSourceShell.SetShadowMotionSuppressed(true);
                source.GetSortingSnapshot(out activeSourceRenderers, out activeSourceBaseOrders);
                source.SetSortingOffset(frontSortingOffset);
                Coroutine started = StartCoroutine(RunPourOperation(operationId));
                if (started == null || ActiveOperationId != operationId)
                {
                    if (started != null) StopCoroutine(started);
                    CompleteActiveOperation(operationId, PourOutcome.Cancelled);
                    return false;
                }
                activeRoutine = started;
                return true;
            }
            catch
            {
                CompleteActiveOperation(operationId, PourOutcome.Cancelled);
                throw;
            }
        }

        private IEnumerator RunPourOperation(int operationId)
        {
            bool completed = false;
            try
            {
                ConfigureTimeline();
                if (!activeTarget.BeginReceivePreview(this, operationId,
                        activeColor, activeAmount)) yield break;
                targetPrepared = true;

                float duration = carryDuration + emissionDuration
                    + Mathf.Max(returnDuration, tailDuration + collapseDuration);
                // The iterator only waits for DOTween. It never advances animation time.
                ArmTween(DOVirtual.Float(0f, duration, duration / PlaybackSpeed,
                        time => ApplyTimeline(operationId, time))
                    .SetEase(Ease.Linear).SetRecyclable(true).SetTarget(this), operationId);
                while (!activeTweenFinished)
                {
                    if (!OperationCanContinue(operationId)) yield break;
                    yield return null;
                }
                if (activeTweenCancelled || !OperationCanContinue(operationId)) yield break;

                if (!activeSource.TryCommitTransferTo(activeTarget, this, operationId,
                        activeSourceModelVersion, activeTargetModelVersion, activeColor,
                        activeAmount, requireMatchingColors)) yield break;
                completed = true;
            }
            finally
            {
                if (ActiveOperationId == operationId)
                    CompleteActiveOperation(operationId,
                        completed ? PourOutcome.Completed : PourOutcome.Cancelled);
            }
        }

        private void ConfigureTimeline()
        {
            Pose sourcePose = ResolvePose(activeSource);
            Pose targetPose = ResolvePose(activeTarget);
            float sourceScale = UniformScale(activeSource.transform);
            referenceScale = VesselPresentationMath.RelativeToRoyalReference(
                activeSource.transform, activeSource.profile);
            sourceFrom = activeSource.DisplayVolume;
            targetFrom = activeTarget.DisplayVolume;
            carryDuration = Mathf.Max(0.01f, moveTime);
            emissionDuration = Mathf.Max(0.05f,
                ReferenceUnitTime + (activeAmount - 1) * ReferenceAdditionalUnitTime);
            headDuration = Mathf.Clamp(targetFrom < 0.001f
                ? ReferenceEmptyContactDelay : ReferenceFilledContactDelay, 0.01f, emissionDuration * 0.90f);
            // Longer transfers use a shorter tail; travel distance does not change this authored timing.
            tailDuration = Mathf.Max(0.01f, Mathf.Lerp(ReferenceSingleUnitTailTime,
                ReferenceMultipleUnitTailTime, Mathf.Clamp01((activeAmount - 1) * 0.5f)));
            collapseDuration = ReferenceSurfaceCollapseTime
                + (activeAmount - 1) * ReferenceAdditionalUnitSettleTime;
            returnDuration = Mathf.Max(0.01f, shelfReturnTime);
            tiltSign = activeTarget.transform.position.x < activeSource.transform.position.x
                ? 1f : -1f;
            homeLocalTilt = SignedDegrees(activeSource.transform.localEulerAngles.z);
            maximumPourTilt = sourcePose.maximumTilt;
            extraPourTilt = sourcePose.extraTilt;
            emissionLip = activeSource.PourMouthLocal(activeTarget.transform.position.x);
            startTilt = Mathf.Min(maximumPourTilt,
                Mathf.Max(ReferenceStreamStartTilt,
                    activeSource.SpillAngle(emissionLip, tiltSign) + extraPourTilt));

            Vector3 worldLip = activeSource.transform.TransformPoint(emissionLip);
            carriedMouthOffset = Quaternion.Inverse(activeSourceTransform.rotation)
                * (worldLip - activeSourceTransform.position) * carryScale;
            // I offset the lip about one-tenth of the receiver width toward the source so PourStream can bend into
            // the fall axis.
            float lipSideOffset = tiltSign * ReferenceLipSideShare
                                  * ReceiverInteriorWidth(activeTarget);
            mouthAnchor = activeTarget.MouthWorld
                + Vector3.up
                * (targetPose.receiveClearance * Mathf.Abs(activeTarget.transform.lossyScale.y))
                + Vector3.right * lipSideOffset;

            carryStartPosition = activeSourceTransform.position;
            carryStartRotation = activeSourceTransform.rotation;
            carryStartScale = activeSourceTransform.localScale;
            carryEndScale = activeHomeScale * carryScale;
            carryEndRotation = TiltFromHome(activeHomeRotation,
                tiltSign * startTilt - homeLocalTilt);
            carryPreTiltPosition = mouthAnchor - carryEndRotation * carriedMouthOffset;
            float arc = sourcePose.carryArc * sourceScale
                + Mathf.Abs(carryPreTiltPosition.x - carryStartPosition.x) * 0.025f;
            carryControl = Vector3.Lerp(carryStartPosition, carryPreTiltPosition, 0.5f)
                + Vector3.up * arc * 2f;

            // I size the column to one-fifth of receiver width. The source's streamWidth/streamTipWidth ratio only
            // controls tip taper.
            bodyWidth = ReceiverInteriorWidth(activeTarget) * ReferenceStreamShareOfReceiver;
            float authoredTaper = sourcePose.streamWidth > 0.0001f
                ? Mathf.Clamp01(sourcePose.streamTipWidth / sourcePose.streamWidth)
                : 0.65f;
            leadingWidth = bodyWidth * authoredTaper;
            streamStarted = false;
            emissionDetached = false;
            contactStarted = false;
            contactEffect.Clear();
        }

        /// <summary>I update liquid geometry before sampling stream and contact from one absolute clock. This timeline is forward-only; latches and captured poses make backward scrubbing invalid.</summary>
        private void ApplyTimeline(int operationId, float time)
        {
            if (!OperationCanContinue(operationId)) return;
            if (time < carryDuration)
            {
                ApplyCarryPose(time / carryDuration);
                activeSource.RefreshIfNeeded();
                return;
            }

            float flowTime = time - carryDuration;
            float emitted = ReferenceFlowProgress(flowTime / emissionDuration);
            float arrivalEnd = emissionDuration + tailDuration;
            float arrival = Mathf.Clamp01((flowTime - headDuration)
                / (arrivalEnd - headDuration));
            float received = ReferenceFlowProgress(arrival);
            activeSource.DisplayVolume = sourceFrom - activeAmount * emitted;
            activeTarget.DisplayVolume = targetFrom + activeAmount * received;
            // I publish receiver volume before Begin or StopEmitting captures contact height. The dirty check
            // skips unchanged fills.
            activeTarget.RefreshIfNeeded();

            if (!emissionDetached)
            {
                ApplyEmissionPose(emitted);
                if (!streamStarted)
                {
                    stream.Begin(activeSource, activeTarget, activeColor,
                        bodyWidth, leadingWidth, referenceScale, emissionLip);
                    streamStarted = true;
                }
                if (flowTime >= emissionDuration)
                {
                    // I freeze the detached column at the final pour pose before advancing the return in the same
                    // frame.
                    returnStartPosition = activeSourceTransform.position;
                    returnStartRotation = activeSourceTransform.rotation;
                    // The receiver is already refreshed, so StopEmitting captures this frame's contact gap.
                    stream.StopEmitting();
                    emissionDetached = true;
                }
            }

            if (emissionDetached)
            {
                float back = Mathf.Clamp01((flowTime - emissionDuration) / returnDuration);
                float eased = DOVirtual.EasedValue(0f, 1f, back, returnEase);
                activeSourceTransform.SetPositionAndRotation(
                    Vector3.LerpUnclamped(returnStartPosition, activeHome, eased),
                    Quaternion.SlerpUnclamped(returnStartRotation, activeHomeRotation, eased));
                activeSourceTransform.localScale = Vector3.LerpUnclamped(
                    carryEndScale, activeHomeScale, eased);
                Phase = PourPhase.Active;
            }
            else Phase = PourPhase.Flow;

            activeSource.RefreshIfNeeded();
            float head = Mathf.Clamp01(flowTime / headDuration);
            float tail = Mathf.Clamp01((flowTime - emissionDuration) / tailDuration);
            // The leading tip moves nearly linearly; the renderer adds no extra time easing.
            stream.RenderFrame(head, tail, flowTime, !emissionDetached);

            if (flowTime >= headDuration)
            {
                if (!contactStarted)
                {
                    contactEffect.Begin(activeTarget, activeColor,
                        stream.FallX, targetFrom + activeAmount);
                    contactStarted = true;
                }
                float collapse = Mathf.Clamp01((flowTime - arrivalEnd) / collapseDuration);
                contactEffect.RenderFrame(arrival, tail, collapse);
            }
        }

        private void ApplyCarryPose(float progress)
        {
            float travel = DOVirtual.EasedValue(0f, 1f, Mathf.Clamp01(progress), carryEase);
            float inverse = 1f - travel;
            Vector3 position = inverse * inverse * carryStartPosition
                + 2f * inverse * travel * carryControl
                + travel * travel * carryPreTiltPosition;
            float rotationProgress = Mathf.InverseLerp(ReferenceCarryTiltStart, 1f, progress);
            float rotationEase = DOVirtual.EasedValue(0f, 1f, rotationProgress, tiltEase);
            activeSourceTransform.SetPositionAndRotation(position,
                Quaternion.SlerpUnclamped(carryStartRotation, carryEndRotation, rotationEase));
            activeSourceTransform.localScale = Vector3.LerpUnclamped(
                carryStartScale, carryEndScale, travel);
        }

        private void ApplyEmissionPose(float progress)
        {
            float referenceTilt = DOVirtual.EasedValue(startTilt,
                Mathf.Min(maximumPourTilt, Mathf.Max(startTilt, ReferenceStreamEndTilt)),
                progress, Ease.OutSine);
            float tilt = Mathf.Min(maximumPourTilt,
                Mathf.Max(referenceTilt,
                    activeSource.SpillAngle(emissionLip, tiltSign) + extraPourTilt));
            Quaternion rotation = TiltFromHome(activeHomeRotation,
                tiltSign * tilt - homeLocalTilt);
            activeSourceTransform.localScale = carryEndScale;
            activeSourceTransform.SetPositionAndRotation(
                mouthAnchor - rotation * carriedMouthOffset, rotation);
        }

        /// <summary>I integrate trapezoidal flow so the middle pours at a steady rate and only the ends taper.</summary>
        private static float ReferenceFlowProgress(float normalizedTime)
        {
            float t = Mathf.Clamp01(normalizedTime);
            float ramp = ReferenceFlowRampFraction;
            float area = 1f - ramp;
            if (t < ramp)
                return t * t / (2f * ramp * area);
            if (t > 1f - ramp)
            {
                float remaining = 1f - t;
                return 1f - remaining * remaining / (2f * ramp * area);
            }
            return (t - ramp * 0.5f) / area;
        }

        private void ArmTween(Tween tween, int operationId)
        {
            activeTween = tween;
            activeTweenFinished = tween == null;
            activeTweenCancelled = tween == null;
            if (tween == null) return;
            // I cache completion in a callback instead of polling a finished tween, since DOTween may reuse that
            // object.
            tween.OnComplete(() => MarkTweenCompleted(operationId));
            tween.OnKill(() => MarkTweenKilled(operationId));
        }

        private void MarkTweenCompleted(int operationId)
        {
            if (ActiveOperationId != operationId) return;
            activeTweenFinished = true;
            activeTween = null;
        }

        private void MarkTweenKilled(int operationId)
        {
            if (ActiveOperationId != operationId) return;
            // AutoKill after OnComplete is normal; only an earlier kill counts as cancellation.
            activeTween = null;
            if (!activeTweenFinished)
            {
                activeTweenCancelled = true;
                activeTweenFinished = true;
            }
        }

        private bool OperationCanContinue(int operationId)
        {
            return ActiveOperationId == operationId
                   && Phase != PourPhase.Idle
                   && !cancellationRequested
                   && isActiveAndEnabled
                   && activeSource != null && activeSource.isActiveAndEnabled
                   && activeSourceTransform != null
                   && activeTarget != null && activeTarget.isActiveAndEnabled
                   && stream != null && stream.isActiveAndEnabled
                   // After emission begins, the stream may be inactive only after its natural tail ends, never
                   // after Cancel.
                   && (!streamStarted || stream.Active || stream.TailCompleted)
                   && contactEffect != null && contactEffect.isActiveAndEnabled;
        }

        private void KillOwnedTween()
        {
            Tween owned = activeTween;
            activeTween = null;
            if (owned != null && owned.IsActive()) owned.Kill(false);
            activeTweenFinished = true;
        }

        /// <summary>I cancel the owned transfer and restore committed state immediately. Repeated reset, disable and teardown calls are safe.</summary>
        public bool CancelActivePour()
        {
            if (!Busy || completingOperation) return false;

            int operationId = ActiveOperationId;
            cancellationRequested = true;
            // Stopping a Unity coroutine may skip finally, so explicit cleanup owns cancellation and finally is
            // only a backup.
            CompleteActiveOperation(operationId, PourOutcome.Cancelled, true);
            return true;
        }

        private void CompleteActiveOperation(int operationId, PourOutcome outcome,
                                             bool stopRoutine = false)
        {
            if (operationId == 0 || ActiveOperationId != operationId || completingOperation) return;

            // Claim settlement before killing the tween/coroutine. A finally or a nested disable can only
            // observe an already-settled operation, and cannot interrupt the remaining releases.
            completingOperation = true;
            Coroutine ownedRoutine = activeRoutine;
            activeRoutine = null;
            ActiveOperationId = 0;
            Phase = PourPhase.Idle;
            Exception cleanupFailure = null;
            try
            {
                TryCleanup(KillOwnedTween, ref cleanupFailure);
                TryCleanup(() =>
                {
                    if (stopRoutine && ownedRoutine != null) StopCoroutine(ownedRoutine);
                }, ref cleanupFailure);
                TryCleanup(() =>
                {
                    if (contactEffect != null) contactEffect.Clear();
                }, ref cleanupFailure);
                TryCleanup(() =>
                {
                    if (stream != null) stream.Cancel();
                }, ref cleanupFailure);
                TryCleanup(() =>
                {
                    if (activeTarget == null) return;
                    if (targetPrepared) activeTarget.ClearReceivePreview(this, operationId);
                    activeTarget.DisplayVolume = activeTarget.UnitCount;
                    if (outcome == PourOutcome.Cancelled) activeTarget.ClearTransientMotion();
                }, ref cleanupFailure);
                TryCleanup(() =>
                {
                    if (activeTarget != null) activeTarget.ReleaseTransferReservation(this, operationId);
                }, ref cleanupFailure);
                TryCleanup(() =>
                {
                    if (activeSource == null) return;
                    activeSource.DisplayVolume = activeSource.UnitCount;
                    if (outcome == PourOutcome.Cancelled) activeSource.ClearTransientMotion();
                }, ref cleanupFailure);
                TryCleanup(() =>
                {
                    if (activeSource != null) activeSource.ReleaseTransferReservation(this, operationId);
                }, ref cleanupFailure);
                TryCleanup(() =>
                {
                    if (activeSourceTransform == null) return;
                    activeSourceTransform.SetPositionAndRotation(activeHome, activeHomeRotation);
                    activeSourceTransform.localScale = activeHomeScale;
                }, ref cleanupFailure);
                TryCleanup(() =>
                {
                    if (activeSourceShell != null) activeSourceShell.SetShadowMotionSuppressed(false);
                }, ref cleanupFailure);
                TryCleanup(RestoreSourceSorting, ref cleanupFailure);

                // A cleanup failure is a cancelled presentation, even if the liquid transfer committed.
                // The owning bridge still receives one result and reconciles the authoritative board.
                NotifyPourFinished(operationId,
                    cleanupFailure == null ? outcome : PourOutcome.Cancelled);
            }
            finally
            {
                streamStarted = false;
                emissionDetached = false;
                contactStarted = false;
                activeSource = null;
                activeTarget = null;
                activeSourceShell = null;
                activeSourceTransform = null;
                activeSourceRenderers = null;
                activeSourceBaseOrders = null;
                activeAmount = 0;
                activeColor = Color.clear;
                requireMatchingColors = true;
                targetPrepared = false;
                activeSourceModelVersion = 0;
                activeTargetModelVersion = 0;
                cancellationRequested = false;
                completingOperation = false;
            }
            if (cleanupFailure != null) Debug.LogException(cleanupFailure, this);
        }

        private static void TryCleanup(Action cleanup, ref Exception failure)
        {
            try { cleanup(); }
            catch (Exception exception) { if (failure == null) failure = exception; }
        }

        private void NotifyPourFinished(int operationId, PourOutcome outcome)
        {
            notificationSnapshot.Clear();
            for (int i = 0; i < pourFinishedListeners.Count; i++)
                notificationSnapshot.Add(pourFinishedListeners[i]);

            for (int i = 0; i < notificationSnapshot.Count; i++)
            {
                try
                {
                    notificationSnapshot[i](operationId, outcome);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                }
            }
            notificationSnapshot.Clear();
        }

        private void RestoreSourceSorting()
        {
            if (activeSourceRenderers == null || activeSourceBaseOrders == null) return;
            int count = Mathf.Min(activeSourceRenderers.Length, activeSourceBaseOrders.Length);
            for (int i = 0; i < count; i++)
            {
                if (activeSourceRenderers[i] != null)
                    activeSourceRenderers[i].sortingOrder = activeSourceBaseOrders[i];
            }
        }

        private Pose ResolvePose(LiquidBottle bottle)
        {
            if (bottle != null && bottle.Profiled && bottle.profile.pourPose.enabled)
            {
                VesselProfile.PourPose p = bottle.profile.pourPose;
                return new Pose(p.receiveClearance, p.carryArc, p.extraTilt,
                    p.maximumTilt, p.streamWidth, p.streamTipWidth);
            }

            // PourStream no longer owns authored width; keep the shared profile fallback.
            VesselProfile.PourPose fallback = VesselProfile.PourPose.Default;
            return new Pose(pourHeight, carryArc, overTilt, maxTilt,
                fallback.streamWidth, fallback.streamTipWidth);
        }

        /// <summary>The receiver's visible interior width in world units, used to size the falling column.</summary>
        private static float ReceiverInteriorWidth(LiquidBottle receiver)
        {
            float scale = VesselPresentationMath.PlanarWorldScale(receiver.transform);
            if (receiver.Profiled && receiver.profile.interiorBounds.width > 0.0001f)
                return receiver.profile.interiorBounds.width * scale;
            // Unprofiled bottles only provide a mouth radius, so I derive the fallback width from it.
            return Mathf.Max(0.02f, receiver.mouthHalfWidth * 2f) * scale;
        }

        private static float UniformScale(Transform value)
        {
            return VesselPresentationMath.PlanarWorldScale(value);
        }

        private static Quaternion TiltFromHome(Quaternion homeRotation, float degrees) =>
            homeRotation * Quaternion.AngleAxis(degrees, Vector3.forward);

        private static float SignedDegrees(float degrees)
        {
            degrees %= 360f;
            if (degrees > 180f) degrees -= 360f;
            if (degrees < -180f) degrees += 360f;
            return degrees;
        }

        private readonly struct Pose
        {
            public readonly float receiveClearance;
            public readonly float carryArc;
            public readonly float extraTilt;
            public readonly float maximumTilt;
            public readonly float streamWidth;
            public readonly float streamTipWidth;

            public Pose(float receiveClearance, float carryArc, float extraTilt,
                float maximumTilt, float streamWidth, float streamTipWidth)
            {
                this.receiveClearance = receiveClearance;
                this.carryArc = carryArc;
                this.extraTilt = extraTilt;
                this.maximumTilt = maximumTilt;
                this.streamWidth = streamWidth;
                this.streamTipWidth = streamTipWidth;
            }
        }

        private void OnDisable()
        {
            CancelActivePour();
            if (contactEffect != null) contactEffect.Clear();
        }
    }
}
