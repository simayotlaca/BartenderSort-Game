using UnityEngine;

namespace LiquidSort
{
    /// <summary>I pose rim garnishes without colliders or mask changes; straws retract before pouring. Shared-canvas back/front layers use one pose to cross the glass rim.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(LiquidBottle))]
    public sealed class VesselRimGarnish : MonoBehaviour
    {
        private const float DefaultWidthShare = 0.29f;
        private const float DefaultSideOffset = 0.78f;
        private const float DefaultLiftShare = 0.36f;
        private const float DefaultHeightShare = 1.05f;
        private const float DefaultInsertionShare = 0.78f;
        private const float DefaultRetractShare = 0.07f;
        private const int DefaultSortingOrder = 6;
        private const int DefaultOverlaySortingOrder = 6;
        private const float EnterSafeSideDegrees = 9f;
        private const float ExitSafeSideDegrees = 5f;
        private const float FadeOutSeconds = 0.06f;
        private const float FadeInSeconds = 0.36f;
        private const float UmbrellaSwayDegrees = 3.25f;
        private const float UmbrellaSwayPeriod = 2.8f;
        private const float IdleFadeOutSeconds = 0.12f;
        private const float IdleFadeInSeconds = 0.30f;
        private const float ClipContactWidthShare = 0.18f;
        private const float InsertedContactWidthShare = 0.105f;
        private const float ContactHeightToWidth = 0.22f;
        private const float ClipContactOpacity = 0.20f;
        private const float InsertedContactOpacity = 0.15f;

        private static readonly Color ContactShadowColor =
            new Color32(18, 45, 82, 255);

        [SerializeField] private VesselPresentationSlots slots;

        private LiquidBottle bottle;
        private SpriteRenderer garnishRenderer;
        private SpriteRenderer overlayRenderer;
        private SpriteRenderer contactShadowRenderer;
        private Sprite configuredSprite;
        private Sprite configuredOverlaySprite;
        private Sprite configuredContactShadowSprite;
        private string configuredSortingLayer;
        private int configuredSortingOrder = int.MinValue;
        private int configuredOverlaySortingOrder = int.MinValue;
        private int configuredContactSortingOrder = int.MinValue;
        private float displayedSide = 1f;
        private float requestedSide = 1f;
        private float opacity;
        private float retractAmount;
        private float idlePhase;
        private float idleMotionWeight;
        private float idleSwayDegrees;
        private bool orderReady;
        private bool avoidingPourLip;
        private VesselProfile posedProfile;
        private int posedProfileSignature;

        /// <summary>I bind the authored rim presenter without creating components or children at runtime.</summary>
        public static void Ensure(LiquidBottle owner)
        {
            if (!Application.isPlaying || owner == null) return;

            VesselRimGarnish presenter = owner.GetComponent<VesselRimGarnish>();
            bool wanted = owner.profile != null && owner.profile.rimGarnish != null;
            if (!wanted)
            {
                if (presenter != null) presenter.SetPresentationEnabled(false);
                return;
            }

            if (presenter == null)
            {
                Debug.LogError(
                    $"{owner.name}: authored rim-garnish presenter is missing.", owner);
                return;
            }
            presenter.Bind(owner);
        }

        /// <summary>I apply the shelf's order-ready state without rebinding active presenters, preserving live motion and hiding stale pooled garnishes.</summary>
        public static void SetOrderReady(LiquidBottle owner, bool ready,
            bool immediate = false)
        {
            if (!Application.isPlaying || owner == null) return;

            VesselRimGarnish presenter = owner.GetComponent<VesselRimGarnish>();
            bool wanted = owner.profile != null && owner.profile.rimGarnish != null;
            if (!wanted)
            {
                if (presenter != null)
                {
                    presenter.orderReady = false;
                    presenter.SetPresentationEnabled(false);
                }
                return;
            }

            // I keep pool teardown allocation-free and re-enable the presenter only for an order-ready reveal.
            if (!ready && (presenter == null || !presenter.enabled))
            {
                if (presenter != null) presenter.orderReady = false;
                return;
            }

            if (presenter == null || !presenter.enabled)
            {
                Ensure(owner);
                presenter = owner.GetComponent<VesselRimGarnish>();
            }
            if (presenter == null) return;

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
            if (bottle != null && bottle.profile != null && bottle.profile.rimGarnish != null)
                Bind(bottle);
        }

        private void Bind(LiquidBottle owner)
        {
            bottle = owner;
            enabled = true;
            displayedSide = 1f;
            requestedSide = 1f;
            opacity = 0f;
            retractAmount = 0f;
            idlePhase = Mathf.Repeat(
                owner.GetInstanceID() * 0.754877666f, Mathf.PI * 2f);
            idleMotionWeight = 0f;
            idleSwayDegrees = 0f;
            avoidingPourLip = false;
            if (!ResolveAuthoredRenderers())
            {
                enabled = false;
                return;
            }
            ConfigureRenderer(true);
            ApplyPose();
            posedProfile = owner.profile;
            posedProfileSignature = PoseSignature(owner.profile);
        }

        private void SetPresentationEnabled(bool value)
        {
            enabled = value;
            if (!value)
            {
                idleMotionWeight = 0f;
                idleSwayDegrees = 0f;
            }
            if (garnishRenderer != null) garnishRenderer.enabled = value;
            if (overlayRenderer != null)
                overlayRenderer.enabled = value && overlayRenderer.sprite != null;
            if (contactShadowRenderer != null)
                contactShadowRenderer.enabled = value
                    && contactShadowRenderer.sprite != null;
        }

        private void HideImmediately()
        {
            opacity = 0f;
            retractAmount = 0f;
            idleMotionWeight = 0f;
            idleSwayDegrees = 0f;
            avoidingPourLip = false;
            ApplyPose();
        }

        private void LateUpdate()
        {
            VesselProfile profile = bottle != null ? bottle.profile : null;
            if (profile == null || profile.rimGarnish == null)
            {
                SetPresentationEnabled(false);
                return;
            }

            bool profileReferenceChanged = posedProfile != profile;
            if (!ResolveAuthoredRenderers())
            {
                SetPresentationEnabled(false);
                return;
            }
            int profileSignature = PoseSignature(profile);
            bool profilePoseChanged = profileReferenceChanged
                || posedProfileSignature != profileSignature;
            bool rendererChanged = ConfigureRenderer(profilePoseChanged);
            posedProfile = profile;
            posedProfileSignature = profileSignature;

            float previousDisplayedSide = displayedSide;
            float previousRequestedSide = requestedSide;
            float previousOpacity = opacity;
            float previousRetractAmount = retractAmount;
            float previousIdleSwayDegrees = idleSwayDegrees;
            bool previousAvoidingPourLip = avoidingPourLip;
            requestedSide = RequestedSide(profile);

            float deltaTime = Time.unscaledDeltaTime;
            if (!orderReady)
            {
                opacity = Mathf.MoveTowards(opacity, 0f,
                    deltaTime / Mathf.Max(0.001f, FadeOutSeconds));
                retractAmount = Mathf.MoveTowards(retractAmount, 0f,
                    deltaTime / Mathf.Max(0.001f, FadeOutSeconds));
            }
            else if (profile.rimGarnishLayout == RimGarnishLayout.InsertedStraw)
            {
                UpdateInsertedStrawAnimation(deltaTime);
            }
            else if (Mathf.Sign(requestedSide) != Mathf.Sign(displayedSide))
            {
                opacity = Mathf.MoveTowards(opacity, 0f,
                    deltaTime / Mathf.Max(0.001f, FadeOutSeconds));
                if (opacity <= 0.001f)
                {
                    opacity = 0f;
                    displayedSide = requestedSide;
                }
            }
            else
            {
                opacity = Mathf.MoveTowards(opacity, 1f,
                    deltaTime / Mathf.Max(0.001f, FadeInSeconds));
            }
            UpdateIdleSway(profile, deltaTime);

            bool poseStateChanged = previousDisplayedSide != displayedSide
                || previousRequestedSide != requestedSide
                || previousOpacity != opacity
                || previousRetractAmount != retractAmount
                || previousIdleSwayDegrees != idleSwayDegrees
                || previousAvoidingPourLip != avoidingPourLip;
            if (!profilePoseChanged && !rendererChanged && !poseStateChanged)
                return;

            ApplyPose();
        }

        private void UpdateIdleSway(VesselProfile profile, float deltaTime)
        {
            // The layered umbrella pivots at the rim; ordinary straws and clipped fruit stay anchored.
            bool canIdle = orderReady && !bottle.IsTransferReserved
                && profile.rimGarnishLayout == RimGarnishLayout.InsertedStraw
                && profile.rimGarnishOverlay != null;
            if (canIdle)
                idlePhase = Mathf.Repeat(idlePhase
                    + deltaTime * Mathf.PI * 2f / UmbrellaSwayPeriod,
                    Mathf.PI * 2f);
            float targetWeight = canIdle ? 1f : 0f;
            float duration = canIdle ? IdleFadeInSeconds : IdleFadeOutSeconds;
            idleMotionWeight = Mathf.MoveTowards(idleMotionWeight, targetWeight,
                deltaTime / duration);
            idleSwayDegrees = Mathf.Sin(idlePhase) * UmbrellaSwayDegrees
                * Mathf.SmoothStep(0f, 1f, idleMotionWeight);
        }

        private void UpdateInsertedStrawAnimation(float deltaTime)
        {
            float target = avoidingPourLip ? 0f : 1f;
            float duration = target < opacity ? FadeOutSeconds : FadeInSeconds;
            opacity = Mathf.MoveTowards(opacity, target,
                deltaTime / Mathf.Max(0.001f, duration));
            retractAmount = Mathf.MoveTowards(retractAmount, avoidingPourLip ? 1f : 0f,
                deltaTime / Mathf.Max(0.001f, duration));

            // I switch sides only while hidden so bent straws cannot visibly snap through the liquid.
            if (opacity <= 0.001f)
            {
                opacity = 0f;
                displayedSide = requestedSide;
            }
            else if (!avoidingPourLip && retractAmount <= 0.001f)
            {
                displayedSide = requestedSide;
            }
        }

        private float RequestedSide(VesselProfile profile)
        {
            if (!bottle.IsTransferReserved || profile.mouthHalfWidth <= 0.001f)
            {
                avoidingPourLip = false;
                return 1f;
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
            float enter = Mathf.Sin(EnterSafeSideDegrees * Mathf.Deg2Rad);
            float exit = Mathf.Sin(ExitSafeSideDegrees * Mathf.Deg2Rad);

            if (avoidingPourLip)
                avoidingPourLip = verticalShare > exit;
            else
                avoidingPourLip = verticalShare >= enter;

            if (!avoidingPourLip) return 1f;
            return right.y >= left.y ? 1f : -1f;
        }

        private bool ResolveAuthoredRenderers()
        {
            if (garnishRenderer != null && overlayRenderer != null
                && contactShadowRenderer != null)
                return true;
            if (slots == null)
                slots = GetComponentInChildren<VesselPresentationSlots>(true);
            if (slots == null) return false;

            garnishRenderer = slots.RimGarnish;
            overlayRenderer = slots.RimGarnishOverlay;
            contactShadowRenderer = slots.RimGarnishContactShadow;
            return garnishRenderer != null && overlayRenderer != null
                && contactShadowRenderer != null;
        }

        private bool ConfigureRenderer(bool force)
        {
            VesselProfile profile = bottle.profile;
            int wantedOrder = profile.rimGarnishLayout == RimGarnishLayout.InsertedStraw
                ? profile.rimGarnishSortingOrder
                : profile.rimGarnishSortingOrder == 0
                ? DefaultSortingOrder
                : profile.rimGarnishSortingOrder;
            string wantedLayer = bottle.sortingLayer;
            int wantedOverlayOrder = profile.rimGarnishOverlaySortingOrder == 0
                ? DefaultOverlaySortingOrder
                : profile.rimGarnishOverlaySortingOrder;
            int wantedContactOrder = Mathf.Max(wantedOrder, wantedOverlayOrder);
            bool changed = force
                || configuredSprite != profile.rimGarnish
                || configuredSortingLayer != wantedLayer
                || configuredSortingOrder != wantedOrder
                || configuredOverlaySprite != profile.rimGarnishOverlay
                || configuredContactShadowSprite != profile.rimGarnishContactShadow
                || configuredOverlaySortingOrder != wantedOverlayOrder
                || configuredContactSortingOrder != wantedContactOrder;
            if (!changed) return false;

            configuredSprite = profile.rimGarnish;
            configuredSortingLayer = wantedLayer;
            configuredSortingOrder = wantedOrder;
            configuredOverlaySprite = profile.rimGarnishOverlay;
            configuredContactShadowSprite = profile.rimGarnishContactShadow;
            configuredOverlaySortingOrder = wantedOverlayOrder;
            configuredContactSortingOrder = wantedContactOrder;
            garnishRenderer.sprite = configuredSprite;
            garnishRenderer.sortingLayerName = configuredSortingLayer;
            garnishRenderer.sortingOrder = configuredSortingOrder;
            garnishRenderer.color = Color.white;
            garnishRenderer.enabled = true;
            ConfigureOverlayRenderer(profile, wantedLayer);
            ConfigureContactShadowRenderer(wantedLayer, wantedContactOrder);
            bottle.InvalidateRenderers();
            return true;
        }

        private static int PoseSignature(VesselProfile profile)
        {
            if (profile == null) return 0;

            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (int)profile.rimGarnishLayout;
                hash = hash * 31 + profile.interiorBounds.GetHashCode();
                hash = hash * 31 + profile.mouthLocal.GetHashCode();
                hash = hash * 31 + profile.mouthHalfWidth.GetHashCode();
                hash = hash * 31 + profile.rimGarnishWidthShare.GetHashCode();
                hash = hash * 31 + profile.rimGarnishSideOffset.GetHashCode();
                hash = hash * 31 + profile.rimGarnishLiftShare.GetHashCode();
                hash = hash * 31 + profile.rimGarnishHeightShare.GetHashCode();
                hash = hash * 31 + profile.rimGarnishInsertionShare.GetHashCode();
                hash = hash * 31 + profile.rimGarnishWidthScale.GetHashCode();
                hash = hash * 31 + profile.rimGarnishRotationDegrees.GetHashCode();
                hash = hash * 31 + profile.rimGarnishRetractShare.GetHashCode();
                return hash;
            }
        }

        private void ConfigureContactShadowRenderer(string wantedLayer,
            int wantedOrder)
        {
            if (contactShadowRenderer == null) return;

            contactShadowRenderer.sprite = configuredContactShadowSprite;
            contactShadowRenderer.sortingLayerName = wantedLayer;
            contactShadowRenderer.sortingOrder = wantedOrder;
            contactShadowRenderer.maskInteraction = SpriteMaskInteraction.None;
            contactShadowRenderer.flipX = false;
            contactShadowRenderer.flipY = false;
            contactShadowRenderer.color = Color.clear;
            contactShadowRenderer.enabled = configuredContactShadowSprite != null;
        }

        private void ConfigureOverlayRenderer(VesselProfile profile, string wantedLayer)
        {
            if (overlayRenderer == null) return;

            int wantedOrder = profile.rimGarnishOverlaySortingOrder == 0
                ? DefaultOverlaySortingOrder
                : profile.rimGarnishOverlaySortingOrder;
            configuredOverlaySprite = profile.rimGarnishOverlay;
            configuredOverlaySortingOrder = wantedOrder;
            overlayRenderer.sprite = configuredOverlaySprite;
            overlayRenderer.sortingLayerName = wantedLayer;
            overlayRenderer.sortingOrder = wantedOrder;
            overlayRenderer.color = Color.white;
            overlayRenderer.enabled = configuredOverlaySprite != null;
        }

        private void ApplyPose()
        {
            if (garnishRenderer == null || garnishRenderer.sprite == null) return;

            if (bottle.profile.rimGarnishLayout == RimGarnishLayout.InsertedStraw)
                ApplyInsertedStrawPose();
            else
                ApplyRimClipPose();

            ApplyContactShadowPose();
            SyncOverlayPose();
        }

        /// <summary>I use one shared 64x32 sprite for a tiny contact ellipse where the garnish meets the lip.</summary>
        private void ApplyContactShadowPose()
        {
            if (contactShadowRenderer == null
                || contactShadowRenderer.sprite == null)
                return;

            VesselProfile profile = bottle.profile;
            float interiorWidth = profile.interiorBounds.width > 0.001f
                ? profile.interiorBounds.width
                : profile.mouthHalfWidth * 2f;
            bool inserted = profile.rimGarnishLayout
                == RimGarnishLayout.InsertedStraw;
            float sideOffset = profile.rimGarnishSideOffset > 0.001f
                ? profile.rimGarnishSideOffset
                : DefaultSideOffset;
            float width = interiorWidth * (inserted
                ? InsertedContactWidthShare
                : ClipContactWidthShare);
            float height = width * ContactHeightToWidth;
            Bounds bounds = contactShadowRenderer.sprite.bounds;
            float scaleX = width / Mathf.Max(0.001f, bounds.size.x);
            float scaleY = height / Mathf.Max(0.001f, bounds.size.y);

            Transform contact = contactShadowRenderer.transform;
            contact.localPosition = new Vector3(
                profile.mouthLocal.x
                    + displayedSide * profile.mouthHalfWidth * sideOffset,
                profile.mouthLocal.y - height * 0.08f,
                0.01f);
            contact.localRotation = Quaternion.identity;
            contact.localScale = new Vector3(scaleX, scaleY, 1f);

            float alpha = Mathf.SmoothStep(0f, 1f, opacity) * (inserted
                ? InsertedContactOpacity
                : ClipContactOpacity);
            Color color = ContactShadowColor;
            color.a = alpha;
            contactShadowRenderer.color = color;
            contactShadowRenderer.enabled = alpha > 0.001f;
        }

        /// <summary>I copy the base sprite's transform to its shared-canvas overlay so both halves stay aligned.</summary>
        private void SyncOverlayPose()
        {
            if (overlayRenderer == null || overlayRenderer.sprite == null) return;

            Transform source = garnishRenderer.transform;
            Transform overlay = overlayRenderer.transform;
            overlay.localRotation = source.localRotation;
            overlay.localScale = source.localScale;
            Vector3 position = source.localPosition;
            overlay.localPosition = new Vector3(position.x, position.y, position.z - 0.01f);
            overlayRenderer.flipX = garnishRenderer.flipX;
            overlayRenderer.color = garnishRenderer.color;
        }

        private void ApplyRimClipPose()
        {
            VesselProfile profile = bottle.profile;

            float interiorWidth = profile.interiorBounds.width > 0.001f
                ? profile.interiorBounds.width
                : profile.mouthHalfWidth * 2f;
            float widthShare = profile.rimGarnishWidthShare > 0.001f
                ? profile.rimGarnishWidthShare
                : DefaultWidthShare;
            float sideOffset = profile.rimGarnishSideOffset > 0.001f
                ? profile.rimGarnishSideOffset
                : DefaultSideOffset;
            float liftShare = profile.rimGarnishLiftShare > 0.001f
                ? profile.rimGarnishLiftShare
                : DefaultLiftShare;
            float canvasWidth = Mathf.Max(0.01f, interiorWidth * widthShare);
            float scale = canvasWidth /
                Mathf.Max(0.001f, garnishRenderer.sprite.bounds.size.x);
            float visualOpacity = Mathf.SmoothStep(0f, 1f, opacity);
            float popScale = RevealScale(opacity);

            Transform garnishTransform = garnishRenderer.transform;
            garnishRenderer.flipX = false;
            garnishTransform.localPosition = new Vector3(
                profile.mouthLocal.x
                    + displayedSide * profile.mouthHalfWidth * sideOffset,
                profile.mouthLocal.y + canvasWidth * liftShare,
                0f);
            garnishTransform.localRotation = Quaternion.identity;
            garnishTransform.localScale = Vector3.one * scale * popScale;

            Color color = garnishRenderer.color;
            color.a = visualOpacity;
            garnishRenderer.color = color;
        }

        private void ApplyInsertedStrawPose()
        {
            VesselProfile profile = bottle.profile;
            Sprite sprite = garnishRenderer.sprite;
            float interiorHeight = profile.interiorBounds.height > 0.001f
                ? profile.interiorBounds.height
                : Mathf.Max(0.01f, profile.mouthHalfWidth * 2f);
            float heightShare = profile.rimGarnishHeightShare > 0.001f
                ? profile.rimGarnishHeightShare
                : DefaultHeightShare;
            float insertionShare = profile.rimGarnishInsertionShare > 0.001f
                ? profile.rimGarnishInsertionShare
                : DefaultInsertionShare;
            float widthScale = profile.rimGarnishWidthScale > 0.001f
                ? profile.rimGarnishWidthScale
                : 1f;
            float sideOffset = profile.rimGarnishSideOffset > 0.001f
                ? profile.rimGarnishSideOffset
                : DefaultSideOffset;
            float retractShare = profile.rimGarnishRetractShare > 0.001f
                ? profile.rimGarnishRetractShare
                : DefaultRetractShare;

            float visibleHeight = Mathf.Max(0.01f, interiorHeight * heightShare);
            float scale = visibleHeight / Mathf.Max(0.001f, sprite.bounds.size.y);
            float visualOpacity = Mathf.SmoothStep(0f, 1f, opacity);
            float popScale = RevealScale(opacity);
            float signedSide = Mathf.Sign(displayedSide);
            Vector3 localScale = new Vector3(
                scale * popScale * widthScale * signedSide,
                scale * popScale,
                1f);
            Quaternion localRotation = Quaternion.Euler(
                0f, 0f, profile.rimGarnishRotationDegrees * signedSide);

            // I use tight sprite bounds so transparent PNG padding cannot shift the pose.
            Bounds bounds = sprite.bounds;
            Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, 0f);
            Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, 0f);
            AccumulateCorner(bounds.min.x, bounds.min.y, localScale, localRotation,
                ref min, ref max);
            AccumulateCorner(bounds.min.x, bounds.max.y, localScale, localRotation,
                ref min, ref max);
            AccumulateCorner(bounds.max.x, bounds.min.y, localScale, localRotation,
                ref min, ref max);
            AccumulateCorner(bounds.max.x, bounds.max.y, localScale, localRotation,
                ref min, ref max);

            float anchorX = profile.mouthLocal.x
                + signedSide * profile.mouthHalfWidth * sideOffset;
            float restingBottom = profile.mouthLocal.y - visibleHeight * insertionShare;
            float tuckedBottom = restingBottom - visibleHeight * retractShare * retractAmount;
            Transform garnishTransform = garnishRenderer.transform;
            garnishRenderer.flipX = false;
            garnishTransform.localScale = localScale;
            Vector3 localPosition = new Vector3(
                anchorX - (min.x + max.x) * 0.5f,
                tuckedBottom - min.y,
                -0.01f);
            // Rotate the whole shared canvas about its rim contact so the canopy and shaft stay joined.
            Vector3 rimContact = new Vector3(anchorX, profile.mouthLocal.y, -0.01f);
            Quaternion swayRotation = Quaternion.Euler(0f, 0f, idleSwayDegrees);
            garnishTransform.localRotation = swayRotation * localRotation;
            garnishTransform.localPosition = rimContact
                + swayRotation * (localPosition - rimContact);

            Color color = garnishRenderer.color;
            color.a = visualOpacity;
            garnishRenderer.color = color;
        }

        private static float RevealScale(float progress)
        {
            // A quick expansion followed by a six-percent overshoot makes the order-ready reveal readable.
            float remaining = Mathf.Clamp01(progress) - 1f;
            float spring = 1f + 3.6f * remaining * remaining * remaining
                + 2.6f * remaining * remaining;
            return Mathf.LerpUnclamped(0.70f, 1f, spring);
        }

        private static void AccumulateCorner(float x, float y, Vector3 scale,
            Quaternion rotation, ref Vector3 min, ref Vector3 max)
        {
            Vector3 point = rotation * new Vector3(x * scale.x, y * scale.y, 0f);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
    }
}
