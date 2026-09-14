using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    public sealed class TimeBoostFlightFxView : MonoBehaviour
    {
        [SerializeField] private Image glow = null;
        [SerializeField] private Image ring = null;
        [SerializeField] private Image token = null;
        [SerializeField] private Image label = null;
        [SerializeField] private Image[] burst = new Image[0];
        [SerializeField] private Image[] trail = new Image[0];

        private readonly Dictionary<Image, Vector3> restScales = new Dictionary<Image, Vector3>();

        public Image Glow => glow;
        public Image Ring => ring;
        public Image Token => token;
        public Image Label => label;
        public IReadOnlyList<Image> Burst => burst;
        public IReadOnlyList<Image> Trail => trail;

        public bool IsReady => token != null && burst != null && trail != null;

        private void Awake() => CaptureRestScales();

        public void Place(Image part, Vector2 position, float scale, float rotation, float alpha)
        {
            if (part == null) return;
            if (restScales.Count == 0) CaptureRestScales();
            if (!restScales.TryGetValue(part, out Vector3 rest)) rest = Vector3.one;
            RectTransform rect = part.rectTransform;
            rect.anchoredPosition = position;
            rect.localScale = new Vector3(rest.x * scale, rest.y * scale, rest.z);
            rect.localRotation = Quaternion.Euler(0f, 0f, rotation);
            part.canvasRenderer.SetAlpha(Mathf.Clamp01(alpha));
        }

        public static void Hide(Image part)
        {
            if (part != null) part.canvasRenderer.SetAlpha(0f);
        }

        public void HideAll()
        {
            Hide(glow);
            Hide(ring);
            Hide(token);
            Hide(label);
            for (int i = 0; i < burst.Length; i++) Hide(burst[i]);
            for (int i = 0; i < trail.Length; i++) Hide(trail[i]);
        }

        private void CaptureRestScales()
        {
            Capture(glow);
            Capture(ring);
            Capture(token);
            Capture(label);
            for (int i = 0; i < burst.Length; i++) Capture(burst[i]);
            for (int i = 0; i < trail.Length; i++) Capture(trail[i]);
        }

        private void Capture(Image part)
        {
            if (part != null && !restScales.ContainsKey(part))
                restScales[part] = part.rectTransform.localScale;
        }
    }
}
