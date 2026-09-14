using UnityEngine;

namespace LiquidSort.Levels
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    public sealed class BsSafeArea : MonoBehaviour
    {
        [Tooltip("Fit top to safe area.")]
        public bool ApplyTop = true;
        [Tooltip("Fit bottom to safe area.")]
        public bool ApplyBottom = true;
        [Tooltip("Fit sides to safe area.")]
        public bool ApplySides = true;

        private RectTransform rectTransform;
        private Rect lastSafe;
        private int lastWidth;
        private int lastHeight;

        private void OnEnable()
        {
            rectTransform = (RectTransform)transform;
            lastWidth = lastHeight = 0;      // Force the first Apply.
            Apply();
        }

        private void Update()
        {
            if (Screen.width == lastWidth && Screen.height == lastHeight
                && Screen.safeArea == lastSafe) return;
            Apply();
        }

        private void Apply()
        {
            if (rectTransform == null) rectTransform = (RectTransform)transform;
            int w = Screen.width;
            int h = Screen.height;
            if (w <= 0 || h <= 0) return;          // The first frame may report zero.

            Rect safe = Screen.safeArea;
            lastSafe = safe;
            lastWidth = w;
            lastHeight = h;

            Vector2 min = safe.position;
            Vector2 max = safe.position + safe.size;
            min.x /= w; min.y /= h;
            max.x /= w; max.y /= h;

            if (!ApplySides) { min.x = 0f; max.x = 1f; }
            if (!ApplyBottom) min.y = 0f;
            if (!ApplyTop) max.y = 1f;

            if (min.x < 0f || min.y < 0f || max.x > 1f || max.y > 1f
                || max.x <= min.x || max.y <= min.y) return;

            rectTransform.anchorMin = min;
            rectTransform.anchorMax = max;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;
        }
    }
}
