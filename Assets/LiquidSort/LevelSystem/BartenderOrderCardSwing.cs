using DG.Tweening;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Tilts a dealt card through <see cref="Transform.localRotation"/> only. <see cref="OrderCardView"/>
    /// owns position, so swing never fights its layout resets.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderOrderCardSwing : MonoBehaviour
    {
        [Tooltip("Opening tilt in degrees. The card slides in from the right, so it starts "
               + "leaning and settles upright.")]
        [SerializeField, Range(0f, 15f)] private float amplitude = 5.5f;
        [Tooltip("Settle time. Short enough that the card is upright well before the "
               + "shelf below it finishes filling.")]
        [SerializeField, Min(0.05f)] private float duration = 0.34f;
        [Tooltip("Half-swings before the card is upright. Two reads as a hang, more reads "
               + "as a pendulum toy.")]
        [SerializeField, Range(1, 5)] private int halfSwings = 2;
        [Tooltip("How fast the tilt decays. Higher settles sooner inside the same duration.")]
        [SerializeField, Range(1f, 8f)] private float decay = 3.4f;

        private Tween swing;

        private void OnDisable() => Stop();

        /// <summary>Stops the old swing before starting another so re-dealt cards cannot stack tilts.</summary>
        public void Play()
        {
            Stop();
            if (!isActiveAndEnabled || amplitude <= 0f) return;

            swing = DOVirtual.Float(0f, 1f, duration, Apply)
                .SetTarget(this)
                .SetEase(Ease.Linear)
                .SetUpdate(true)
                .SetRecyclable(true)
                .OnKill(() => swing = null)
                // Completion and interruption resolve to the same authored pose: upright.
                .OnComplete(ResetPose);
        }

        /// <summary>Ends the swing and restores the upright pose.</summary>
        public void Stop()
        {
            if (swing != null && swing.IsActive()) swing.Kill();
            swing = null;
            ResetPose();
        }

        private void Apply(float normalized)
        {
            transform.localRotation = Quaternion.Euler(0f, 0f, AngleAt(normalized));
        }

        private void ResetPose()
        {
            transform.localRotation = Quaternion.identity;
        }

        /// <summary>
        /// A damped cosine settles through <see cref="halfSwings"/> reversals to upright. This static curve
        /// can be checked without a scene.
        /// </summary>
        internal static float AngleAt(float normalized, float amplitude, int halfSwings,
                                      float decay)
        {
            float clamped = Mathf.Clamp01(normalized);
            return amplitude
                 * Mathf.Exp(-decay * clamped)
                 * Mathf.Cos(halfSwings * Mathf.PI * clamped);
        }

        private float AngleAt(float normalized)
            => AngleAt(normalized, amplitude, halfSwings, decay);
    }
}
