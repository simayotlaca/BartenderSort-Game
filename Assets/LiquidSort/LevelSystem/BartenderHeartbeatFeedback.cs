using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Pulses the failure card with two smooth peaks at 15% and 45% of each beat, without extra tween
    /// dependencies.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderHeartbeatFeedback : MonoBehaviour
    {
        [SerializeField] private RectTransform target = null;
        [SerializeField, Min(1f)] private float beatsPerMinute = 50f;
        [SerializeField, Min(1f)] private float scaleMultiplier = 1.3f;

        private Vector3 restScale = Vector3.one;
        private float elapsed;
        private bool hasRestScale;

        private void OnEnable()
        {
            if (target == null) target = transform as RectTransform;
            CaptureRestScale();
            elapsed = 0f;
        }

        private void Update()
        {
            if (target == null || beatsPerMinute <= 0f) return;

            float duration = 60f / beatsPerMinute;
            elapsed = Mathf.Repeat(elapsed + Time.unscaledDeltaTime, duration);
            float pulse = EvaluateSourceCurve(elapsed / duration);
            target.localScale = restScale
                                * Mathf.LerpUnclamped(1f, scaleMultiplier, pulse);
        }

        private void OnDisable()
        {
            if (target != null && hasRestScale) target.localScale = restScale;
        }

        private void OnValidate()
        {
            beatsPerMinute = Mathf.Max(1f, beatsPerMinute);
            scaleMultiplier = Mathf.Max(1f, scaleMultiplier);
        }

        private void CaptureRestScale()
        {
            if (target == null) return;
            restScale = target.localScale;
            hasRestScale = true;
        }

        private static float EvaluateSourceCurve(float phase)
        {
            phase = Mathf.Repeat(phase, 1f);
            if (phase < 0.15f) return SmoothSegment(0f, 1f, phase / 0.15f);
            if (phase < 0.30f)
                return SmoothSegment(1f, 0f, (phase - 0.15f) / 0.15f);
            if (phase < 0.45f)
                return SmoothSegment(0f, 0.7f, (phase - 0.30f) / 0.15f);
            if (phase < 0.60f)
                return SmoothSegment(0.7f, 0f, (phase - 0.45f) / 0.15f);
            return 0f;
        }

        private static float SmoothSegment(float from, float to, float t)
        {
            t = Mathf.Clamp01(t);
            t = t * t * (3f - 2f * t);
            return Mathf.LerpUnclamped(from, to, t);
        }
    }
}
