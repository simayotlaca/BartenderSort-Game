using DG.Tweening;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Wobbles a rejected glass and pulses a separate silhouette. Its own renderer leaves BottleShell
    /// materials and selection highlights alone.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(LiquidBottle))]
    public sealed class BartenderInvalidMoveFeedback : MonoBehaviour
    {
        private const string OverlayName = "InvalidMoveHighlight";

        [SerializeField] private VesselPresentationSlots slots;

        private LiquidBottle bottle;
        private SpriteRenderer overlay;
        private MaterialPropertyBlock overlayBlock;
        private Sequence activeSequence;
        private Transform drivenTransform;
        private Quaternion restLocalRotation = Quaternion.identity;
        private bool ownsRotation;

        public bool Playing => activeSequence != null;

        /// <summary>Restarts only this component's rejection tween. Other bottle tweens keep running.</summary>
        public void Play(Color tint, float highlightAlpha, float wobbleDegrees,
                         float duration, Transform motionRoot = null)
        {
            if (!isActiveAndEnabled || !gameObject.activeInHierarchy) return;

            Cancel(true);
            if (!TryPrepareOverlay(tint, highlightAlpha)) return;

            float total = Mathf.Max(0.08f, duration);
            float firstDuration = total * 0.18f;
            float secondDuration = total * 0.26f;
            float thirdDuration = total * 0.22f;
            float finalDuration = total - firstDuration - secondDuration - thirdDuration;

            drivenTransform = motionRoot != null ? motionRoot : transform;
            restLocalRotation = drivenTransform.localRotation;
            ownsRotation = true;
            Quaternion first = restLocalRotation
                * Quaternion.AngleAxis(wobbleDegrees, Vector3.forward);
            Quaternion second = restLocalRotation
                * Quaternion.AngleAxis(-wobbleDegrees * 0.82f, Vector3.forward);
            Quaternion third = restLocalRotation
                * Quaternion.AngleAxis(wobbleDegrees * 0.38f, Vector3.forward);

            Color clear = tint;
            clear.a = 0f;
            Color peak = tint;
            peak.a = Mathf.Clamp01(highlightAlpha * tint.a);
            overlay.color = clear;
            overlay.enabled = true;

            Sequence sequence = DOTween.Sequence()
                .SetTarget(this).SetUpdate(true).SetRecyclable(true);
            sequence.Append(drivenTransform.DOLocalRotateQuaternion(first, firstDuration)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            sequence.Append(drivenTransform.DOLocalRotateQuaternion(second, secondDuration)
                .SetEase(Ease.InOutSine).SetRecyclable(true));
            sequence.Append(drivenTransform.DOLocalRotateQuaternion(third, thirdDuration)
                .SetEase(Ease.InOutSine).SetRecyclable(true));
            sequence.Append(drivenTransform.DOLocalRotateQuaternion(
                    restLocalRotation, finalDuration)
                .SetEase(Ease.OutSine).SetRecyclable(true));

            float riseDuration = total * 0.22f;
            sequence.Insert(0f, overlay.DOColor(peak, riseDuration)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            sequence.Insert(riseDuration, overlay.DOColor(clear, total - riseDuration)
                .SetEase(Ease.InQuad).SetRecyclable(true));
            sequence.OnComplete(HandleCompleted);
            sequence.OnKill(HandleKilled);
            activeSequence = sequence;
        }

        /// <summary>
        /// Stops rejection. Pass false after shelf layout changes rotation so cleanup cannot restore the
        /// older pose.
        /// </summary>
        public void Cancel(bool restoreRotation)
        {
            Sequence sequence = activeSequence;
            activeSequence = null;
            if (sequence != null && sequence.IsActive()) sequence.Kill(false);

            if (restoreRotation && ownsRotation && drivenTransform != null)
                drivenTransform.localRotation = restLocalRotation;
            ownsRotation = false;
            drivenTransform = null;
            HideOverlay();
        }

        private void OnDisable() => Cancel(true);

        private void OnDestroy()
        {
            Sequence sequence = activeSequence;
            activeSequence = null;
            if (sequence != null && sequence.IsActive()) sequence.Kill(false);
        }

        private void HandleCompleted()
        {
            activeSequence = null;
            if (ownsRotation && drivenTransform != null)
                drivenTransform.localRotation = restLocalRotation;
            ownsRotation = false;
            drivenTransform = null;
            HideOverlay();
        }

        private void HandleKilled()
        {
            // Cancel clears ownership before Kill. External or Safe Mode kills still need to remove tilt
            // and glow here.
            if (activeSequence == null) return;
            activeSequence = null;
            if (ownsRotation && drivenTransform != null)
                drivenTransform.localRotation = restLocalRotation;
            ownsRotation = false;
            drivenTransform = null;
            HideOverlay();
        }

        private bool TryPrepareOverlay(Color tint, float highlightAlpha)
        {
            bottle ??= GetComponent<LiquidBottle>();
            SpriteRenderer source = FindSilhouetteRenderer();
            if (bottle == null || source == null || source.sprite == null) return false;

            if (!ResolveAuthoredOverlay()) return false;

            Transform overlayTransform = overlay.transform;
            Transform sourceTransform = source.transform;
            if (sourceTransform.parent == transform)
            {
                overlayTransform.localPosition = sourceTransform.localPosition;
                overlayTransform.localRotation = sourceTransform.localRotation;
                overlayTransform.localScale = sourceTransform.localScale;
            }
            else
            {
                overlayTransform.SetPositionAndRotation(
                    sourceTransform.position, sourceTransform.rotation);
                Vector3 parentScale = transform.lossyScale;
                Vector3 sourceScale = sourceTransform.lossyScale;
                overlayTransform.localScale = new Vector3(
                    SafeRatio(sourceScale.x, parentScale.x),
                    SafeRatio(sourceScale.y, parentScale.y),
                    SafeRatio(sourceScale.z, parentScale.z));
            }

            overlay.sprite = source.sprite;
            overlay.sharedMaterial = source.sharedMaterial;
            overlay.drawMode = source.drawMode;
            overlay.size = source.size;
            overlay.flipX = source.flipX;
            overlay.flipY = source.flipY;
            overlay.maskInteraction = source.maskInteraction;
            overlay.sortingLayerID = source.sortingLayerID;
            overlay.sortingOrder = HighestSortingOrder() + 2;

            overlayBlock ??= new MaterialPropertyBlock();
            source.GetPropertyBlock(overlayBlock);
            overlay.SetPropertyBlock(overlayBlock);

            Color clear = tint;
            clear.a = 0f;
            overlay.color = clear;
            overlay.enabled = highlightAlpha > 0.001f;
            return true;
        }

        private bool ResolveAuthoredOverlay()
        {
            if (overlay != null) return true;
            if (slots == null)
                slots = GetComponentInChildren<VesselPresentationSlots>(true);
            overlay = slots != null ? slots.InvalidMoveHighlight : null;
            return overlay != null;
        }

        private SpriteRenderer FindSilhouetteRenderer()
        {
            SpriteRenderer fallback = null;
            SpriteRenderer[] renderers = GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteRenderer candidate = renderers[i];
                if (candidate == null || candidate == overlay
                    || candidate.name == OverlayName || !candidate.enabled
                    || candidate.sprite == null)
                    continue;
                if (candidate.name == "FrontGlass") return candidate;
                if (fallback == null
                    && candidate.name != "Shadow"
                    && candidate.name != "ContactShadow"
                    && candidate.name != "SoftHalo")
                    fallback = candidate;
            }
            return fallback;
        }

        private int HighestSortingOrder()
        {
            int highest = 0;
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer candidate = renderers[i];
                if (candidate == null || candidate == overlay) continue;
                highest = Mathf.Max(highest, candidate.sortingOrder);
            }
            return Mathf.Min(32765, highest);
        }

        private void HideOverlay()
        {
            if (overlay == null) return;
            overlay.enabled = false;
            Color clear = overlay.color;
            clear.a = 0f;
            overlay.color = clear;
        }

        private static float SafeRatio(float numerator, float denominator) =>
            Mathf.Abs(denominator) > 0.0001f ? numerator / denominator : 1f;
    }
}
