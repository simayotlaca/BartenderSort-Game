using UnityEngine;

namespace BartenderSort.Core
{
    internal readonly struct BsHomeRewardStackMotionSample
    {
        internal Vector2 Position { get; }
        internal float Scale { get; }

        internal BsHomeRewardStackMotionSample(
            Vector2 position,
            float scale)
        {
            Position = position;
            Scale = scale;
        }
    }

    internal static class BsHomeRewardStackMotion
    {
        internal const float CollectStartProgress = 0.39f;

        internal static float ResolveIconProgress(
            float elapsedTime,
            float scatterDuration,
            float holdDuration,
            float flightDuration,
            float emissionStagger,
            float collectStagger,
            int iconIndex)
        {
            float safeElapsed = Mathf.Max(0f, elapsedTime);
            float safeScatter = Mathf.Max(0.0001f, scatterDuration);
            float safeHold = Mathf.Max(0f, holdDuration);
            float safeFlight = Mathf.Max(0.0001f, flightDuration);
            int safeIndex = Mathf.Max(0, iconIndex);
            float emissionTime = safeIndex * Mathf.Max(0f, emissionStagger);
            float localElapsed = safeElapsed - emissionTime;

            if (localElapsed <= 0f) return 0f;
            if (localElapsed < safeScatter)
            {
                float scatter = Mathf.Clamp01(localElapsed / safeScatter);
                return Mathf.Lerp(0f, CollectStartProgress, scatter);
            }

            float collectStart = Mathf.Max(
                emissionTime + safeScatter,
                safeScatter + safeHold + safeIndex * Mathf.Max(0f, collectStagger));
            if (safeElapsed <= collectStart) return CollectStartProgress;

            float collect = Mathf.Clamp01(
                (safeElapsed - collectStart) / safeFlight);
            return Mathf.Lerp(CollectStartProgress, 1f, collect);
        }

        internal static BsHomeRewardStackMotionSample Sample(
            Vector2 source,
            Vector2 liveTarget,
            Vector2 iconOffset,
            float normalizedProgress)
        {
            float progress = Mathf.Clamp01(normalizedProgress);
            float spread;
            float travel;
            float scale;

            if (progress <= CollectStartProgress)
            {
                float scatter = Mathf.Clamp01(progress / CollectStartProgress);
                float easedScatter = 1f - Mathf.Pow(1f - scatter, 3f);
                spread = easedScatter + 0.08f * Mathf.Sin(scatter * Mathf.PI);
                travel = 0f;
                scale = easedScatter + 0.24f * Mathf.Sin(scatter * Mathf.PI);
            }
            else
            {
                float collect = Mathf.InverseLerp(
                    CollectStartProgress, 1f, progress);
                travel = collect * collect * (2f - collect);
                spread = 1f - travel;
                scale = 1f - Smooth01(Mathf.InverseLerp(0.78f, 1f, collect));
            }

            Vector2 pathPosition = Vector2.LerpUnclamped(
                source, liveTarget, travel);
            Vector2 position = pathPosition + iconOffset * spread;
            Vector2 direction = liveTarget - source;
            float distance = direction.magnitude;
            if (distance > 0.001f && travel > 0f && travel < 1f)
            {
                Vector2 normal = new Vector2(-direction.y, direction.x) / distance;
                float arcHeight = Mathf.Min(distance * 0.18f, iconOffset.magnitude * 0.85f);
                float arcSide = iconOffset.x < 0f ? -1f : 1f;
                position += normal * (arcHeight * arcSide * 4f * travel * (1f - travel));
            }
            return new BsHomeRewardStackMotionSample(
                position,
                Mathf.Max(0f, scale));
        }

        private static float Smooth01(float value)
        {
            float t = Mathf.Clamp01(value);
            return t * t * (3f - 2f * t);
        }
    }
}
