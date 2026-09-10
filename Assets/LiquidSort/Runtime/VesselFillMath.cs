using System.Collections.Generic;
using UnityEngine;

namespace LiquidSort
{
    /// <summary>I preserve liquid area in bottle-local polygons, rotated into a liquid frame where waterlines are horizontal.</summary>
    public static class VesselFillMath
    {
        [System.ThreadStatic] private static List<Vector2> spillScratch;
        private static List<Vector2> scratch => spillScratch ??= new List<Vector2>(64);

        /// <summary>Rounded bottle interior. Points are counter clockwise, origin at the transform pivot.</summary>
        public static Vector2[] BottleInterior(
            float width, float height, float bottomY,
            float bottomRadius, float topRadius, int cornerSegments = 8)
        {
            float hw = Mathf.Max(0.01f, width * 0.5f);
            float top = bottomY + Mathf.Max(0.02f, height);
            bottomRadius = Mathf.Clamp(bottomRadius, 0f, hw);
            topRadius = Mathf.Clamp(topRadius, 0f, hw);
            cornerSegments = Mathf.Max(1, cornerSegments);

            var points = new List<Vector2>(cornerSegments * 4 + 8);

            // Bottom right corner, sweeping up from the flat bottom edge.
            AddArc(points, new Vector2(hw - bottomRadius, bottomY + bottomRadius),
                bottomRadius, -90f, 0f, cornerSegments);
            AddArc(points, new Vector2(hw - topRadius, top - topRadius),
                topRadius, 0f, 90f, cornerSegments);
            AddArc(points, new Vector2(-hw + topRadius, top - topRadius),
                topRadius, 90f, 180f, cornerSegments);
            AddArc(points, new Vector2(-hw + bottomRadius, bottomY + bottomRadius),
                bottomRadius, 180f, 270f, cornerSegments);

            return points.ToArray();
        }

        private static void AddArc(List<Vector2> into, Vector2 center, float radius,
            float fromDeg, float toDeg, int segments)
        {
            if (radius <= 0.0001f)
            {
                into.Add(center);
                return;
            }
            for (int i = 0; i <= segments; i++)
            {
                float a = Mathf.Deg2Rad * Mathf.Lerp(fromDeg, toDeg, i / (float)segments);
                into.Add(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius);
            }
        }

        public static void Rotate(IList<Vector2> source, float degrees, List<Vector2> result)
        {
            result.Clear();
            float rad = degrees * Mathf.Deg2Rad;
            float s = Mathf.Sin(rad);
            float c = Mathf.Cos(rad);
            for (int i = 0; i < source.Count; i++)
            {
                Vector2 p = source[i];
                result.Add(new Vector2(p.x * c - p.y * s, p.x * s + p.y * c));
            }
        }

        public static float Area(IList<Vector2> polygon)
        {
            if (polygon == null || polygon.Count < 3) return 0f;
            float sum = 0f;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
                sum += polygon[j].x * polygon[i].y - polygon[i].x * polygon[j].y;
            return Mathf.Abs(sum) * 0.5f;
        }

        public static void VerticalExtent(IList<Vector2> polygon, out float minY, out float maxY)
        {
            minY = float.MaxValue;
            maxY = float.MinValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                float y = polygon[i].y;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            if (polygon.Count == 0) { minY = 0f; maxY = 0f; }
        }

        /// <summary>Area of the part of the polygon that sits below the horizontal line y = level.</summary>
        public static float AreaBelow(IList<Vector2> polygon, float level)
        {
            if (polygon == null || polygon.Count < 3) return 0f;

            // Sutherland-Hodgman clipping against one half-plane keeps signed area exact even for concave
            // polygons.
            float sum = 0f;
            Vector2 previous = polygon[polygon.Count - 1];
            bool previousInside = previous.y <= level;
            Vector2 first = Vector2.zero;
            Vector2 last = Vector2.zero;
            bool started = false;

            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 current = polygon[i];
                bool currentInside = current.y <= level;

                if (currentInside != previousInside)
                {
                    float t = (level - previous.y) / (current.y - previous.y);
                    Vector2 crossing = new Vector2(previous.x + (current.x - previous.x) * t, level);
                    if (!started) { first = crossing; last = crossing; started = true; }
                    else { sum += last.x * crossing.y - crossing.x * last.y; last = crossing; }
                }

                if (currentInside)
                {
                    if (!started) { first = current; last = current; started = true; }
                    else { sum += last.x * current.y - current.x * last.y; last = current; }
                }

                previous = current;
                previousInside = currentInside;
            }

            if (!started) return 0f;
            sum += last.x * first.y - first.x * last.y;
            return Mathf.Abs(sum) * 0.5f;
        }

        /// <summary>The waterline with <paramref name="fraction"/> of the polygon area below it, preserving volume at any tilt.</summary>
        public static float LevelForFraction(IList<Vector2> polygon, float totalArea, float fraction)
        {
            VerticalExtent(polygon, out float low, out float high);
            fraction = Mathf.Clamp01(fraction);
            if (fraction <= 0.0001f) return low;
            if (fraction >= 0.9999f) return high;
            if (totalArea <= 0.0001f) return low;

            float wanted = totalArea * fraction;
            for (int i = 0; i < 28; i++)
            {
                float mid = (low + high) * 0.5f;
                if (AreaBelow(polygon, mid) < wanted) low = mid;
                else high = mid;
            }
            return (low + high) * 0.5f;
        }

        /// <summary>Horizontal span of the polygon at height <paramref name="level"/>.</summary>
        public static float HalfWidthAt(IList<Vector2> polygon, float level, out float centerX)
        {
            float min = float.MaxValue;
            float max = float.MinValue;
            Vector2 previous = polygon[polygon.Count - 1];
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 current = polygon[i];
                if ((previous.y > level) != (current.y > level))
                {
                    float t = (level - previous.y) / (current.y - previous.y);
                    float x = previous.x + (current.x - previous.x) * t;
                    if (x < min) min = x;
                    if (x > max) max = x;
                }
                previous = current;
            }

            if (min > max) { centerX = 0f; return 0.0001f; }
            centerX = (min + max) * 0.5f;
            return Mathf.Max(0.0001f, (max - min) * 0.5f);
        }

        /// <summary>I share the same brim-clearance calculation between baking and live tuning so both agree on the highest safe cap.</summary>
        public static float SurfaceCeiling(
            IList<Vector2> polygon, float minY, float maxY, float interiorHeight,
            float surfaceBulge, float maxCapDepth, float surfaceAllowance,
            float brimGapCaps, float brimHeadroom)
        {
            float span = Mathf.Max(0.001f, maxY - minY);
            float limit = Mathf.Min(interiorHeight * maxCapDepth, span * 0.45f);
            float depth = limit;

            // Cap depth and chord depend on each other, so I use the contract's three fixed-point passes.
            for (int i = 0; i < 3; i++)
            {
                float probe = Mathf.Clamp(maxY - depth, minY, maxY);
                float half = HalfWidthAt(polygon, probe, out _);
                depth = Mathf.Min(2f * half * surfaceBulge, limit);
            }

            // I reserve only the cap part outside the open-mouth allowance, then use the largest cap gap or
            // proportional headroom.
            float reserved = depth * (1f - Mathf.Clamp01(surfaceAllowance)) * 1.06f;
            float headroom = Mathf.Max(depth * brimGapCaps, reserved);
            headroom = Mathf.Max(headroom, interiorHeight * brimHeadroom);
            return Mathf.Max(minY, maxY - headroom);
        }

        /// <summary>The smallest absolute tilt in degrees where the waterline reaches the mouth and pouring begins.</summary>
        public static float SpillAngle(IList<Vector2> polygon, Vector2 mouthLocal, float fraction,
            float maxAngle = 130f, int steps = 26, float direction = 1f)
        {
            if (fraction <= 0.0001f) return maxAngle;

            // I reuse this list across calls to avoid allocations.
            scratch.Clear();
            List<Vector2> rotated = scratch;
            float area = Area(polygon);
            float low = 0f;
            float high = maxAngle;
            direction = direction < 0f ? -1f : 1f;

            for (int i = 0; i < steps; i++)
            {
                float mid = (low + high) * 0.5f;
                float signedAngle = mid * direction;
                Rotate(polygon, signedAngle, rotated);
                float level = LevelForFraction(rotated, area, fraction);

                float rad = signedAngle * Mathf.Deg2Rad;
                float mouthY = mouthLocal.x * Mathf.Sin(rad) + mouthLocal.y * Mathf.Cos(rad);

                if (level >= mouthY) high = mid;
                else low = mid;
            }
            return high;
        }
    }
}
