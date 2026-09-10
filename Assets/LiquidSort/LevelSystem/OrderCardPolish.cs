using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Adds light card depth with existing Images under the card's CanvasGroup, so they share its visibility
    /// and lifecycle.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OrderCardPolish : MonoBehaviour
    {
        [Header("Subtle card depth")]
        [SerializeField] private Color shadowColor =
            new Color32(0x2D, 0x1B, 0x12, 0x38);
        [SerializeField] private Vector2 restingShadowOffset = new Vector2(0f, -1.25f);
        [SerializeField] private Vector2 liftedShadowOffset = new Vector2(0f, -3.25f);
        [SerializeField, Range(0f, 1f)] private float restingShadowStrength = 0.5f;
        [SerializeField, Range(0f, 1f)] private float liftedShadowStrength = 0.85f;
        [SerializeField, Min(0f)] private float surfaceLightInset = 5f;
        [SerializeField, Range(0f, 1.5f)] private float restingLightStrength = 0.92f;
        [SerializeField, Range(0f, 1.5f)] private float liftedLightStrength = 1f;

        [Header("Authored hierarchy")]
        [SerializeField] private RectTransform depthRoot;
        [SerializeField] private Image contactShadow;
        [SerializeField] private RectTransform lightRoot;
        [SerializeField] private Image lightMaskImage;
        [SerializeField] private Mask lightMask;
        [SerializeField] private OrderCardLightOverlay lightOverlay;

        private Image source;
        private float lift;
        private float sourceOpacity = 1f;
        private bool missingHierarchyReported;

        public float Lift => lift;

        /// <summary>Binds the card silhouette through Inspector references. Safe to call on every snapshot.</summary>
        public void Bind(Image cardBackground)
        {
            source = cardBackground;
            if (source == null) return;

            if (!HasAuthoredHierarchy())
            {
                if (!missingHierarchyReported)
                {
                    missingHierarchyReported = true;
                    Debug.LogError(
                        "OrderCardPolish authored depth/light referansları eksik veya "
                        + "yanlış parent altında.", this);
                }
                return;
            }
            RefreshFromSource();
            ApplyLift();
        }

        /// <summary>
        /// Zero rests the shadow on the rail; one moves it down for deals and exits. The gameplay root stays
        /// fixed.
        /// </summary>
        public void SetLift(float value)
        {
            float clamped = Mathf.Clamp01(value);
            if (Mathf.Approximately(lift, clamped)
                && contactShadow != null && lightOverlay != null)
                return;

            lift = clamped;
            ApplyLift();
        }

        /// <summary>Keeps an empty/fading slot from leaving a fully opaque shadow.</summary>
        public void SetSourceOpacity(float value)
        {
            float clamped = Mathf.Clamp01(value);
            if (Mathf.Approximately(sourceOpacity, clamped)) return;
            sourceOpacity = clamped;
            ApplyLift();
        }

        private bool HasAuthoredHierarchy()
        {
            Transform parent = source != null ? source.transform.parent : null;
            return parent != null
                   && depthRoot != null && depthRoot.parent == parent
                   && contactShadow != null && contactShadow.transform.parent == depthRoot
                   && lightRoot != null && lightRoot.parent == parent
                   && depthRoot.GetSiblingIndex() + 1 == source.transform.GetSiblingIndex()
                   && lightRoot.GetSiblingIndex() == source.transform.GetSiblingIndex() + 1
                   && lightMaskImage != null
                   && lightMaskImage.gameObject == lightRoot.gameObject
                   && lightMask != null && lightMask.gameObject == lightRoot.gameObject
                   && lightOverlay != null && lightOverlay.transform.parent == lightRoot;
        }

        private void RefreshFromSource()
        {
            if (source == null || depthRoot == null) return;

            RectTransform sourceRect = source.rectTransform;
            depthRoot.anchorMin = sourceRect.anchorMin;
            depthRoot.anchorMax = sourceRect.anchorMax;
            depthRoot.pivot = sourceRect.pivot;
            depthRoot.anchoredPosition3D = sourceRect.anchoredPosition3D;
            depthRoot.sizeDelta = sourceRect.sizeDelta;
            depthRoot.localRotation = sourceRect.localRotation;
            depthRoot.localScale = sourceRect.localScale;
            depthRoot.gameObject.layer = source.gameObject.layer;
            depthRoot.gameObject.SetActive(true);

            RefreshLayer(contactShadow);
            RefreshLightVisuals(sourceRect);
        }

        private void RefreshLightVisuals(RectTransform sourceRect)
        {
            if (lightRoot == null || lightMaskImage == null || source == null) return;

            lightRoot.anchorMin = sourceRect.anchorMin;
            lightRoot.anchorMax = sourceRect.anchorMax;
            lightRoot.pivot = sourceRect.pivot;
            lightRoot.anchoredPosition3D = sourceRect.anchoredPosition3D;
            lightRoot.sizeDelta = sourceRect.sizeDelta;
            lightRoot.localRotation = sourceRect.localRotation;
            lightRoot.localScale = sourceRect.localScale;
            lightRoot.gameObject.layer = source.gameObject.layer;
            lightRoot.gameObject.SetActive(source.isActiveAndEnabled);

            CopyImageShape(source, lightMaskImage);
            lightMaskImage.color = Color.white;
            lightMaskImage.material = null;
            lightMaskImage.raycastTarget = false;
            lightMaskImage.enabled = true;
            lightMask.enabled = true;
            lightMask.showMaskGraphic = false;

            if (lightOverlay != null)
            {
                RectTransform overlayRect = lightOverlay.rectTransform;
                overlayRect.anchorMin = Vector2.zero;
                overlayRect.anchorMax = Vector2.one;
                overlayRect.pivot = new Vector2(0.5f, 0.5f);
                overlayRect.offsetMin = Vector2.one * surfaceLightInset;
                overlayRect.offsetMax = -Vector2.one * surfaceLightInset;
                overlayRect.localRotation = Quaternion.identity;
                overlayRect.localScale = Vector3.one;
                lightOverlay.gameObject.layer = source.gameObject.layer;
                lightOverlay.color = Color.white;
                lightOverlay.material = null;
                lightOverlay.maskable = true;
                lightOverlay.raycastTarget = false;
                lightOverlay.gameObject.SetActive(true);
            }
        }

        private void RefreshLayer(Image image)
        {
            if (image == null || source == null) return;

            CopyImageShape(source, image);
            image.material = null;
            image.raycastTarget = false;
            image.enabled = source.enabled;
            image.gameObject.SetActive(true);
        }

        private static void CopyImageShape(Image from, Image to)
        {
            to.sprite = from.overrideSprite != null ? from.overrideSprite : from.sprite;
            to.type = from.type;
            to.preserveAspect = from.preserveAspect;
            to.fillCenter = from.fillCenter;
            to.fillMethod = from.fillMethod;
            to.fillAmount = from.fillAmount;
            to.fillClockwise = from.fillClockwise;
            to.fillOrigin = from.fillOrigin;
            to.useSpriteMesh = from.useSpriteMesh;
            to.pixelsPerUnitMultiplier = from.pixelsPerUnitMultiplier;
            to.maskable = from.maskable;
        }

        private void ApplyLift()
        {
            // Do not enlarge the coloured card sprite; its baked shading would look like a second card instead
            // of a shadow.
            ApplyLayer(contactShadow, shadowColor,
                Vector2.Lerp(restingShadowOffset, liftedShadowOffset, lift),
                sourceOpacity * Mathf.Lerp(
                    restingShadowStrength, liftedShadowStrength, lift));
            if (lightOverlay != null)
                lightOverlay.SetIntensity(sourceOpacity * Mathf.Lerp(
                    restingLightStrength, liftedLightStrength, lift));
        }

        private static void ApplyLayer(Image image, Color baseColor, Vector2 offset,
                                       float alphaScale)
        {
            if (image == null) return;

            RectTransform rect = image.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = offset;
            rect.sizeDelta = Vector2.zero;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;

            Color color = baseColor;
            color.a = Mathf.Clamp01(baseColor.a * alphaScale);
            image.color = color;
        }
    }
}
