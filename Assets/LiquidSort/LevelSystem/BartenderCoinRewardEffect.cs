using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>Animates the win reward's rotating rays and drifting sparkles.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BartenderCoinRewardEffect : MonoBehaviour
    {
        [SerializeField] private RectTransform rays = null;
        [SerializeField] private RectTransform[] sparkles = null;

        [Header("Rays")]
        [SerializeField] private float rayDegreesPerSecond = 30f;

        [Header("Sparkles")]
        [SerializeField] private Vector2 maxWanderRadius = new Vector2(30f, 30f);
        [SerializeField] private float wanderSpeed = 0.3f;
        [SerializeField] private float maxSparkleSpinSpeed = 80f;
        [SerializeField] private float maxPulseAmount = 0.2f;
        [SerializeField] private float pulseSpeed = 2f;

        private SparkleState[] sparkleStates;
        private Quaternion raysStartRotation;
        private float elapsed;
        private bool cached;

        private struct SparkleState
        {
            public RectTransform Transform;
            public Vector2 StartPosition;
            public Vector3 StartScale;
            public Quaternion StartRotation;
            public float NoiseOffsetX;
            public float NoiseOffsetY;
            public float PulsePhase;
            public float SpeedMultiplier;
            public float SpinSpeed;
        }

        private void Awake() => CacheAuthoredState();

        private void OnEnable()
        {
            CacheAuthoredState();
            ResetVisuals();
        }

        private void OnDisable() => ResetVisuals();

        private void Update()
        {
            if (!cached) CacheAuthoredState();

            elapsed += Time.unscaledDeltaTime;
            if (rays != null)
                rays.localRotation = raysStartRotation
                                     * Quaternion.Euler(0f, 0f, -rayDegreesPerSecond * elapsed);

            if (sparkleStates == null) return;
            for (int i = 0; i < sparkleStates.Length; i++)
            {
                SparkleState state = sparkleStates[i];
                if (state.Transform == null) continue;

                float motionTime = elapsed * wanderSpeed * state.SpeedMultiplier;
                float wanderX = Mathf.PerlinNoise(state.NoiseOffsetX + motionTime, 0.173f) * 2f - 1f;
                float wanderY = Mathf.PerlinNoise(0.619f, state.NoiseOffsetY + motionTime) * 2f - 1f;
                state.Transform.anchoredPosition = state.StartPosition
                    + new Vector2(wanderX * maxWanderRadius.x, wanderY * maxWanderRadius.y);

                state.Transform.localRotation = state.StartRotation
                    * Quaternion.Euler(0f, 0f, state.SpinSpeed * elapsed);

                float pulse = Mathf.Sin(elapsed * pulseSpeed * state.SpeedMultiplier + state.PulsePhase);
                state.Transform.localScale = state.StartScale
                    + Vector3.one * (pulse * maxPulseAmount);
            }
        }

        private void CacheAuthoredState()
        {
            if (cached) return;

            raysStartRotation = rays != null ? rays.localRotation : Quaternion.identity;
            int count = sparkles != null ? sparkles.Length : 0;
            sparkleStates = new SparkleState[count];
            for (int i = 0; i < count; i++)
            {
                RectTransform sparkle = sparkles[i];
                float sequence = i + 1f;
                float signedDirection = (i & 1) == 0 ? -1f : 1f;
                sparkleStates[i] = new SparkleState
                {
                    Transform = sparkle,
                    StartPosition = sparkle != null ? sparkle.anchoredPosition : Vector2.zero,
                    StartScale = sparkle != null ? sparkle.localScale : Vector3.one,
                    StartRotation = sparkle != null ? sparkle.localRotation : Quaternion.identity,
                    NoiseOffsetX = 41.73f + sequence * 137.11f,
                    NoiseOffsetY = 83.29f + sequence * 211.37f,
                    PulsePhase = sequence * 1.618f,
                    SpeedMultiplier = Mathf.Lerp(0.78f, 1.26f, Mathf.Repeat(sequence * 0.417f, 1f)),
                    SpinSpeed = signedDirection * Mathf.Lerp(
                        maxSparkleSpinSpeed * 0.48f,
                        maxSparkleSpinSpeed,
                        Mathf.Repeat(sequence * 0.731f, 1f))
                };
            }

            cached = true;
        }

        private void ResetVisuals()
        {
            if (!cached) return;

            elapsed = 0f;
            if (rays != null) rays.localRotation = raysStartRotation;
            if (sparkleStates == null) return;
            for (int i = 0; i < sparkleStates.Length; i++)
            {
                SparkleState state = sparkleStates[i];
                if (state.Transform == null) continue;
                state.Transform.anchoredPosition = state.StartPosition;
                state.Transform.localScale = state.StartScale;
                state.Transform.localRotation = state.StartRotation;
            }
        }
    }
}
