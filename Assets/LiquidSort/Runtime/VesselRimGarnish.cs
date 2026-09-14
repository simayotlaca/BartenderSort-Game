using UnityEngine;

namespace LiquidSort
{
    public enum RimGarnishPourMode
    {
        HideWhilePouring = 0,
        MoveToSafeSide = 1
    }

    [DisallowMultipleComponent]
    [RequireComponent(typeof(LiquidBottle))]
    public sealed class VesselRimGarnish : MonoBehaviour
    {
        private const float EnterSafeSideDegrees = 9f;
        private const float ExitSafeSideDegrees = 5f;
        private const float IdleFadeOutSeconds = 0.12f;
        private const float IdleFadeInSeconds = 0.30f;

        [SerializeField] private VesselPresentationSlots slots;

        [Header("Authored rig")]
        [Tooltip("Root the garnish clips are sampled on.")]
        [SerializeField] private Transform rig;
        [Tooltip("Carries the garnish and its contact shadow; placed on one of the side points.")]
        [SerializeField] private Transform sideAnchor;
        [SerializeField] private Transform rightSide;
        [Tooltip("Only used by MoveToSafeSide.")]
        [SerializeField] private Transform leftSide;
        [SerializeField] private Transform swayPivot;

        [Header("Clips")]
        [Tooltip("Hidden at the start, resting at the end. Its length is the reveal duration.")]
        [SerializeField] private AnimationClip revealClip;
        [Tooltip("Optional. Resting at the start, tucked into the glass at the end.")]
        [SerializeField] private AnimationClip tuckClip;
        [Tooltip("Optional looping idle motion on the sway pivot.")]
        [SerializeField] private AnimationClip swayClip;

        [Header("Behaviour")]
        [SerializeField] private RimGarnishPourMode pourMode = RimGarnishPourMode.HideWhilePouring;
        [SerializeField, Min(0.01f)] private float hideSeconds = 0.06f;

        private LiquidBottle bottle;
        private SpriteRenderer garnishRenderer;
        private SpriteRenderer overlayRenderer;
        private SpriteRenderer contactShadowRenderer;
        private float displayedSide = 1f;
        private float higherSide = 1f;
        private float opacity;
        private float retractAmount;
        private float swayTime;
        private float swayWeight;
        private bool orderReady;
        private bool avoidingPourLip;
        private float sampledOpacity = -1f;
        private float sampledRetractAmount = -1f;

        private bool HasAuthoredRig => rig != null && sideAnchor != null
            && rightSide != null && revealClip != null;

        private float RevealSeconds => Mathf.Max(0.01f, revealClip.length);

        public static void Ensure(LiquidBottle owner)
        {
            if (!Application.isPlaying || owner == null) return;

            VesselRimGarnish presenter = owner.GetComponent<VesselRimGarnish>();
            if (presenter == null) return;
            if (!presenter.HasAuthoredRig)
            {
                presenter.DisablePresentation();
                return;
            }
            presenter.Bind(owner);
        }

        public static void SetOrderReady(LiquidBottle owner, bool ready,
            bool immediate = false)
        {
            if (!Application.isPlaying || owner == null) return;

            VesselRimGarnish presenter = owner.GetComponent<VesselRimGarnish>();
            if (presenter == null) return;
            if (!presenter.HasAuthoredRig)
            {
                presenter.orderReady = false;
                presenter.DisablePresentation();
                return;
            }

            if (!ready && !presenter.enabled)
            {
                presenter.orderReady = false;
                return;
            }

            if (!presenter.enabled) presenter.Bind(owner);
            presenter.orderReady = ready;
            if (!ready && immediate)
                presenter.HideImmediately();
        }

        private void Awake()
        {
            bottle = GetComponent<LiquidBottle>();
        }

        private void OnEnable()
        {
            if (bottle == null) bottle = GetComponent<LiquidBottle>();
            if (bottle != null && HasAuthoredRig) Bind(bottle);
        }

        private void Bind(LiquidBottle owner)
        {
            bottle = owner;
            enabled = true;
            displayedSide = 1f;
            higherSide = 1f;
            opacity = 0f;
            retractAmount = 0f;
            swayTime = swayClip != null
                ? Mathf.Repeat(owner.GetInstanceID() * 0.618034f, 1f) * swayClip.length
                : 0f;
            swayWeight = 0f;
            avoidingPourLip = false;
            if (!ResolveAuthoredRenderers())
            {
                enabled = false;
                return;
            }
            ApplyPresentationState();
        }

        private void DisablePresentation()
        {
            enabled = false;
            swayWeight = 0f;
            SetRenderersEnabled(false);
        }

        private void HideImmediately()
        {
            opacity = 0f;
            retractAmount = 0f;
            swayWeight = 0f;
            avoidingPourLip = false;
            ApplyPresentationState();
        }

        private void LateUpdate()
        {
            if (bottle == null || !HasAuthoredRig || !ResolveAuthoredRenderers())
            {
                DisablePresentation();
                return;
            }

            float previousOpacity = opacity;
            float previousRetractAmount = retractAmount;
            float previousSide = displayedSide;
            float previousSwayTime = swayTime;
            float previousSwayWeight = swayWeight;

            bool reserved = bottle.IsTransferReserved;
            UpdatePourLipAvoidance(reserved);
            float wantedSide = pourMode == RimGarnishPourMode.MoveToSafeSide
                && avoidingPourLip ? higherSide : 1f;

            float deltaTime = Time.unscaledDeltaTime;
            float hideStep = deltaTime / hideSeconds;
            float revealStep = deltaTime / RevealSeconds;
            if (!orderReady)
            {
                opacity = Mathf.MoveTowards(opacity, 0f, hideStep);
                retractAmount = Mathf.MoveTowards(retractAmount, 0f, hideStep);
            }
            else if (pourMode == RimGarnishPourMode.HideWhilePouring)
            {
                float target = avoidingPourLip ? 0f : 1f;
                float step = target < opacity ? hideStep : revealStep;
                opacity = Mathf.MoveTowards(opacity, target, step);
                retractAmount = Mathf.MoveTowards(retractAmount,
                    avoidingPourLip ? 1f : 0f, step);
            }
            else if (wantedSide != displayedSide)
            {
                opacity = Mathf.MoveTowards(opacity, 0f, hideStep);
            }
            else
            {
                opacity = Mathf.MoveTowards(opacity, 1f, revealStep);
            }
            if (opacity <= 0f) displayedSide = wantedSide;

            bool canIdle = orderReady && !reserved && swayClip != null
                && swayPivot != null;
            if (canIdle)
                swayTime = Mathf.Repeat(swayTime + deltaTime, swayClip.length);
            swayWeight = Mathf.MoveTowards(swayWeight, canIdle ? 1f : 0f,
                deltaTime / (canIdle ? IdleFadeInSeconds : IdleFadeOutSeconds));

            if (opacity <= 0f && previousOpacity <= 0f) return;
            bool changed = previousOpacity != opacity
                || previousRetractAmount != retractAmount
                || previousSide != displayedSide
                || previousSwayTime != swayTime
                || previousSwayWeight != swayWeight;
            if (changed) ApplyPresentationState();
        }

        private void UpdatePourLipAvoidance(bool reserved)
        {
            if (!reserved)
            {
                avoidingPourLip = false;
                return;
            }

            Vector3 rim = transform.TransformVector(Vector3.right);
            float rimLength = rim.magnitude;
            float verticalShare = rimLength > 0.0001f
                ? Mathf.Abs(rim.y) / rimLength
                : 0f;
            float limit = Mathf.Sin((avoidingPourLip
                ? ExitSafeSideDegrees
                : EnterSafeSideDegrees) * Mathf.Deg2Rad);
            avoidingPourLip = avoidingPourLip
                ? verticalShare > limit
                : verticalShare >= limit;
            higherSide = rim.y >= 0f ? 1f : -1f;
        }

        private bool ResolveAuthoredRenderers()
        {
            if (garnishRenderer != null) return true;
            if (slots == null)
                slots = GetComponentInChildren<VesselPresentationSlots>(true);
            if (slots == null) return false;

            garnishRenderer = slots.RimGarnish;
            overlayRenderer = slots.RimGarnishOverlay;
            contactShadowRenderer = slots.RimGarnishContactShadow;
            return garnishRenderer != null;
        }

        private void SetRenderersEnabled(bool value)
        {
            SetRendererEnabled(garnishRenderer, value);
            SetRendererEnabled(overlayRenderer, value);
            SetRendererEnabled(contactShadowRenderer, value);
        }

        private static void SetRendererEnabled(SpriteRenderer renderer, bool value)
        {
            if (renderer != null) renderer.enabled = value && renderer.sprite != null;
        }

        private void ApplyPresentationState()
        {
            bool visible = opacity > 0f;
            SetRenderersEnabled(visible);
            if (!visible)
            {
                sampledOpacity = -1f;
                sampledRetractAmount = -1f;
                return;
            }

            Transform side = displayedSide < 0f && leftSide != null ? leftSide : rightSide;
            sideAnchor.localPosition = side.localPosition;

            GameObject root = rig.gameObject;
            if (sampledOpacity != opacity)
            {
                revealClip.SampleAnimation(root, opacity * revealClip.length);
                sampledOpacity = opacity;
            }
            if (tuckClip != null && sampledRetractAmount != retractAmount)
            {
                tuckClip.SampleAnimation(root, retractAmount * tuckClip.length);
                sampledRetractAmount = retractAmount;
            }

            if (swayPivot == null) return;
            if (swayClip == null || swayWeight <= 0f)
            {
                swayPivot.localRotation = Quaternion.identity;
                return;
            }
            swayClip.SampleAnimation(root, swayTime);
            swayPivot.localRotation = Quaternion.SlerpUnclamped(Quaternion.identity,
                swayPivot.localRotation, Mathf.SmoothStep(0f, 1f, swayWeight));
        }
    }
}
