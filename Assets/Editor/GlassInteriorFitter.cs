using System.Collections.Generic;
using UnityEngine;

namespace LiquidSort
{
    /// <summary>I flood-fill the outline to find its largest enclosed pocket. This editor-only result feeds the baker.</summary>
    public static class GlassInteriorFitter
    {
        public struct Settings
        {
            [Tooltip("Alpha above which a pixel counts as glass line.")]
            public float alphaThreshold;
            [Tooltip("How far the liquid reaches into the glass stroke. 0 stops at the inner edge, 1 reaches the outer edge. 0.75 tucks it safely under the line.")]
            public float strokeFill;
            [Tooltip("Contour simplification tolerance, in source pixels.")]
            public float tolerancePixels;
            [Tooltip("Upper bound on polygon points. Fewer points means cheaper waterline math.")]
            public int maxPoints;
            [Tooltip("Top width / widest width above which the vessel is treated as open rimmed.")]
            public float wideMouthRatio;
            [Tooltip("Share of the widest clear span a row must carry before it counts as visible liquid.")]
            public float visibleBottomShare;
            [Tooltip("Share of the widest row a row must carry to count as the rim rather than the tip of the drawn ellipse.")]
            public float rimShare;
            [Tooltip("How far the DRAWN liquid tucks under the inner wall line, in source pixels. "
                   + "It only ever closes the antialiasing seam; a fixed few pixels can never "
                   + "reach the outer contour the way a share of the stroke can.")]
            public float strokeTuckPixels;
            [Tooltip("Point budget for the visual inner contour. It is rasterised once at bake "
                   + "time rather than walked per frame, so it can afford more fidelity than the "
                   + "volume polygon.")]
            public int renderMaxPoints;

            public static Settings Default => new Settings
            {
                alphaThreshold = 0.35f,
                strokeFill = 0.75f,
                tolerancePixels = 2.5f,
                maxPoints = 56,
                wideMouthRatio = 0.55f,
                visibleBottomShare = 0.47f,
                rimShare = 0.60f,
                strokeTuckPixels = 3f,
                renderMaxPoints = 96
            };
        }

        public sealed class Fit
        {
            public Vector2[] Polygon;
            public Rect Bounds;
            public Vector2 Mouth;
            public float MouthHalfWidth;
            public float VisibleBottom;
            public Vector2 LiquidFloorXRange;
            public float[] LiquidFloorSamples;
            /// <summary>I keep the drawn inner edge separate so fixing its shape does not change volume calculations.</summary>
            public Vector2[] RenderPolygon;
        }

        public static Fit FitSprite(Sprite glass, Settings settings)
        {
            if (glass == null || glass.texture == null) return null;

            Color32[] pixels;
            try
            {
                pixels = glass.texture.GetPixels32();
            }
            catch (UnityException)
            {
                Debug.LogError($"LiquidSort: '{glass.texture.name}' is not readable. Tick " +
                               "Read/Write on its import settings so the interior can be traced.");
                return null;
            }

            int width = glass.texture.width;
            int height = glass.texture.height;

            Rect rect = glass.rect;
            int x0 = Mathf.Clamp(Mathf.RoundToInt(rect.x), 0, width);
            int y0 = Mathf.Clamp(Mathf.RoundToInt(rect.y), 0, height);
            int w = Mathf.Clamp(Mathf.RoundToInt(rect.width), 1, width - x0);
            int h = Mathf.Clamp(Mathf.RoundToInt(rect.height), 1, height - y0);

            byte cut = (byte)Mathf.Clamp(Mathf.RoundToInt(settings.alphaThreshold * 255f), 1, 255);
            var ink = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    ink[y * w + x] = pixels[(y + y0) * width + (x + x0)].a >= cut;

            bool[] outside = FloodOutside(ink, w, h);
            bool[] region = LargestEnclosed(ink, outside, w, h, out int area);

            // I reject tiny pockets because they usually mean bad alpha data.
            if (region != null && area < w * h * 0.02f)
            {
                Debug.LogError($"LiquidSort: the largest enclosed area of '{glass.name}' is only " +
                               $"{area * 100f / (w * h):0.0}% of the sprite. Check that the outline " +
                               "is closed and that the texture is not compressed past recognition.");
                return null;
            }
            if (region == null)
            {
                Debug.LogError("LiquidSort: the artwork has no enclosed interior. " +
                               "The outline must be a closed shape.");
                return null;
            }

            // I save the inner outline before widening the volume region under the stroke.
            var renderRegion = (bool[])region.Clone();
            TuckUnderStroke(renderRegion, ink, w, h,
                Mathf.RoundToInt(Mathf.Max(0f, settings.strokeTuckPixels)));
            TrimBelowWaist(renderRegion, w, h);

            BleedIntoStroke(region, ink, w, h, settings.strokeFill);
            int trimmed = TrimBelowWaist(region, w, h);
            if (trimmed > 0)
                Debug.Log($"LiquidSort: trimmed {trimmed} rows of stem from the interior.");

            List<Vector2> contour = TraceOutline(region, w, h);
            if (contour == null || contour.Count < 3)
            {
                Debug.LogError("LiquidSort: could not trace the interior outline.");
                return null;
            }

            Smooth(contour, 3);
            List<Vector2> simple = SimplifyToBudget(contour, settings.tolerancePixels, settings.maxPoints);

            float ppu = glass.pixelsPerUnit;
            Vector2 pivot = glass.pivot;
            Vector2 ToLocal(Vector2 p) => new Vector2((p.x - pivot.x) / ppu, (p.y - pivot.y) / ppu);

            var polygon = new Vector2[simple.Count];
            for (int i = 0; i < simple.Count; i++) polygon[i] = ToLocal(simple[i]);

            var fit = new Fit { Polygon = polygon };
            fit.Bounds = PolygonBounds(polygon);
            fit.RenderPolygon = BuildPolygon(renderRegion, w, h,
                settings.tolerancePixels, settings.renderMaxPoints, ToLocal);

            // Wide rims pour from the side; narrow necks pour from the centre.
            float widest = WidestHalfWidth(region, w, h);
            float topHalf = RimRow(region, w, h, widest * settings.rimShare,
                out float topCentre, out int topRow);
            float topLocalY = (topRow - pivot.y) / ppu;
            float topLocalX = (topCentre - pivot.x) / ppu;

            // I count a row as visible only when enough clear pixels show between the walls.
            var clear = new int[h];
            int widestClear = 0;
            for (int y = 0; y < h; y++)
            {
                int min = int.MaxValue, max = int.MinValue;
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!region[i] || ink[i]) continue;
                    if (x < min) min = x;
                    if (x > max) max = x;
                }
                clear[y] = min > max ? 0 : max - min + 1;
                if (clear[y] > widestClear) widestClear = clear[y];
            }

            int openRow = -1;
            int needed = Mathf.RoundToInt(widestClear * settings.visibleBottomShare);
            for (int y = 0; y < h; y++)
            {
                if (clear[y] < needed) continue;
                openRow = y;
                break;
            }
            fit.VisibleBottom = openRow >= 0
                ? (openRow - pivot.y) / ppu
                : fit.Bounds.yMin;

            // I follow the inner base line using clear pixels, so liquid cannot enter solid glass.
            if (TrySampleLiquidFloor(ink, region, w, h, openRow, topRow,
                    out int floorXMin, out int floorXMax, out int[] floorRows))
            {
                fit.LiquidFloorXRange = new Vector2(
                    (floorXMin - pivot.x) / ppu,
                    (floorXMax - pivot.x) / ppu);
                fit.LiquidFloorSamples = new float[floorRows.Length];
                for (int i = 0; i < floorRows.Length; i++)
                {
                    // I tuck three pixels under the inner line to hide the seam, without reaching the outer base.
                    fit.LiquidFloorSamples[i] =
                        (Mathf.Max(0, floorRows[i] - 3) - pivot.y) / ppu;
                }
            }

            fit.Mouth = new Vector2(topLocalX, topLocalY);
            fit.MouthHalfWidth = widest > 0f && topHalf / widest >= settings.wideMouthRatio
                ? topHalf / ppu
                : 0f;

            return fit;
        }

        private static bool TrySampleLiquidFloor(bool[] ink, bool[] region,
            int w, int h, int openRow, int topRow,
            out int floorXMin, out int floorXMax, out int[] floorRows)
        {
            floorXMin = 0;
            floorXMax = 0;
            floorRows = null;
            if (openRow < 0 || topRow <= openRow + 2) return false;

            // I probe above the base to reach both walls, with less rise for shallow bowls.
            int wantedRise = Mathf.Max(4, Mathf.RoundToInt(h * 0.08f));
            int availableRise = Mathf.Max(4, (topRow - openRow) / 3);
            int probeRow = Mathf.Min(topRow - 1,
                openRow + Mathf.Min(wantedRise, availableRise));

            int bestStart = -1;
            int bestEnd = -1;
            int runStart = -1;
            for (int x = 0; x <= w; x++)
            {
                bool clear = x < w
                    && region[probeRow * w + x]
                    && !ink[probeRow * w + x];
                if (clear && runStart < 0)
                {
                    runStart = x;
                }
                else if (!clear && runStart >= 0)
                {
                    int runEnd = x - 1;
                    if (bestStart < 0 || runEnd - runStart > bestEnd - bestStart)
                    {
                        bestStart = runStart;
                        bestEnd = runEnd;
                    }
                    runStart = -1;
                }
            }

            if (bestStart < 0 || bestEnd - bestStart < 4) return false;

            var sampledRows = new int[VesselProfile.LiquidFloorSampleCount];
            for (int i = 0; i < sampledRows.Length; i++)
            {
                // I place more samples near the sharper wall and floor joins.
                float sampleT = i / (sampledRows.Length - 1f);
                float packedT = 0.5f - 0.5f * Mathf.Cos(Mathf.PI * sampleT);
                int x = Mathf.RoundToInt(Mathf.Lerp(bestStart, bestEnd, packedT));
                int boundary = -1;
                for (int y = probeRow; y >= 0; y--)
                {
                    if (!ink[y * w + x]) continue;
                    boundary = y;
                    break;
                }

                if (boundary < 0) return false;
                sampledRows[i] = boundary;
            }

            floorXMin = bestStart;
            floorXMax = bestEnd;
            floorRows = sampledRows;
            return true;
        }

        // Pixel helpers.

        /// <summary>4 connected fill of transparent pixels starting from the image border.</summary>
        private static bool[] FloodOutside(bool[] ink, int w, int h)
        {
            var outside = new bool[w * h];
            var stack = new Stack<int>(w * h / 8);

            void Seed(int index)
            {
                if (ink[index] || outside[index]) return;
                outside[index] = true;
                stack.Push(index);
            }

            for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
            for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }

            while (stack.Count > 0)
            {
                int index = stack.Pop();
                int x = index % w;
                int y = index / w;
                if (x > 0) Seed(index - 1);
                if (x < w - 1) Seed(index + 1);
                if (y > 0) Seed(index - w);
                if (y < h - 1) Seed(index + w);
            }
            return outside;
        }

        private static bool[] LargestEnclosed(bool[] ink, bool[] outside, int w, int h, out int bestArea)
        {
            var visited = new bool[w * h];
            var stack = new Stack<int>();
            var current = new List<int>();
            bool[] best = null;
            bestArea = 0;

            for (int start = 0; start < ink.Length; start++)
            {
                if (ink[start] || outside[start] || visited[start]) continue;

                current.Clear();
                stack.Push(start);
                visited[start] = true;

                while (stack.Count > 0)
                {
                    int index = stack.Pop();
                    current.Add(index);
                    int x = index % w;
                    int y = index / w;

                    void Push(int n)
                    {
                        if (ink[n] || outside[n] || visited[n]) return;
                        visited[n] = true;
                        stack.Push(n);
                    }

                    if (x > 0) Push(index - 1);
                    if (x < w - 1) Push(index + 1);
                    if (y > 0) Push(index - w);
                    if (y < h - 1) Push(index + w);
                }

                if (current.Count <= bestArea) continue;
                bestArea = current.Count;
                best = new bool[w * h];
                for (int i = 0; i < current.Count; i++) best[current[i]] = true;
            }
            return best;
        }

        /// <summary>I trace and smooth both contours the same way so their shapes stay aligned.</summary>
        private static Vector2[] BuildPolygon(bool[] region, int w, int h,
            float tolerancePixels, int maxPoints, System.Func<Vector2, Vector2> toLocal)
        {
            List<Vector2> contour = TraceOutline(region, w, h);
            if (contour == null || contour.Count < 3) return null;

            Smooth(contour, 3);
            List<Vector2> simple = SimplifyToBudget(contour, tolerancePixels,
                Mathf.Max(3, maxPoints));

            var polygon = new Vector2[simple.Count];
            for (int i = 0; i < simple.Count; i++) polygon[i] = toLocal(simple[i]);
            return polygon;
        }

        /// <summary>I overlap the stroke by a few pixels to close seams, stopping at its midpoint.</summary>
        private static void TuckUnderStroke(bool[] region, bool[] ink, int w, int h, int pixels)
        {
            if (pixels <= 0) return;

            var foreign = new bool[region.Length];
            for (int i = 0; i < region.Length; i++) foreign[i] = !ink[i] && !region[i];

            int[] fromRegion = DistanceThroughInk(region, ink, w, h);
            int[] fromForeign = DistanceThroughInk(foreign, ink, w, h);

            for (int i = 0; i < region.Length; i++)
            {
                if (!ink[i]) continue;

                int a = fromRegion[i];
                if (a == int.MaxValue || a > pixels) continue;

                int b = fromForeign[i];
                if (b != int.MaxValue && a > b) continue;

                region[i] = true;
            }
        }

        private static void BleedIntoStroke(bool[] region, bool[] ink, int w, int h, float fill)
        {
            fill = Mathf.Clamp01(fill);
            if (fill <= 0f) return;

            var foreign = new bool[region.Length];
            for (int i = 0; i < region.Length; i++) foreign[i] = !ink[i] && !region[i];

            int[] fromRegion = DistanceThroughInk(region, ink, w, h);
            int[] fromForeign = DistanceThroughInk(foreign, ink, w, h);

            for (int i = 0; i < region.Length; i++)
            {
                if (!ink[i]) continue;
                int a = fromRegion[i];
                if (a == int.MaxValue) continue;

                int b = fromForeign[i];
                float across = b == int.MaxValue ? 0f : a / (float)(a + b);
                if (across <= fill) region[i] = true;
            }
        }

        /// <summary>I cut at the upper waist so the glass stem does not count as liquid volume.</summary>
        private static int TrimBelowWaist(bool[] region, int w, int h)
        {
            var widths = new int[h];
            int widest = 0;
            int widestRow = -1;
            for (int y = 0; y < h; y++)
            {
                int min = int.MaxValue, max = int.MinValue;
                for (int x = 0; x < w; x++)
                {
                    if (!region[y * w + x]) continue;
                    if (x < min) min = x;
                    if (x > max) max = x;
                }
                widths[y] = min > max ? 0 : max - min + 1;
                if (widths[y] > widest) { widest = widths[y]; widestRow = y; }
            }
            if (widest <= 0 || widestRow <= 0) return 0;

            // Narrowest occupied row below the widest one.
            int narrowest = int.MaxValue;
            for (int y = 0; y < widestRow; y++)
                if (widths[y] > 0 && widths[y] < narrowest) narrowest = widths[y];
            if (narrowest == int.MaxValue) return 0;

            // A vessel with no stem never narrows much; leave those alone.
            if (narrowest > widest * 0.45f) return 0;

            int threshold = Mathf.Max(narrowest + 1, Mathf.RoundToInt(narrowest * 1.25f));
            int waist = -1;
            for (int y = widestRow - 1; y >= 0; y--)
            {
                if (widths[y] == 0 || widths[y] > threshold) continue;
                waist = y;
                break;
            }
            if (waist <= 0) return 0;

            // I skip this cut if it would remove nearly the whole bowl.
            int remaining = 0;
            for (int y = waist + 1; y < h; y++) remaining += widths[y];
            if (remaining < widest * 4) return 0;

            for (int y = 0; y <= waist; y++)
                for (int x = 0; x < w; x++)
                    region[y * w + x] = false;
            return waist + 1;
        }

        /// <summary>Breadth first distance from the seed set, travelling only through ink.</summary>
        private static int[] DistanceThroughInk(bool[] seeds, bool[] ink, int w, int h)
        {
            var distance = new int[seeds.Length];
            for (int i = 0; i < distance.Length; i++) distance[i] = int.MaxValue;

            var queue = new Queue<int>();
            for (int index = 0; index < seeds.Length; index++)
            {
                if (!seeds[index]) continue;
                int x = index % w;
                int y = index / w;

                void Seed(int n)
                {
                    if (!ink[n] || distance[n] != int.MaxValue) return;
                    distance[n] = 1;
                    queue.Enqueue(n);
                }

                if (x > 0) Seed(index - 1);
                if (x < w - 1) Seed(index + 1);
                if (y > 0) Seed(index - w);
                if (y < h - 1) Seed(index + w);
            }

            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                int next = distance[index] + 1;
                int x = index % w;
                int y = index / w;

                void Step(int n)
                {
                    if (!ink[n] || distance[n] <= next) return;
                    distance[n] = next;
                    queue.Enqueue(n);
                }

                if (x > 0) Step(index - 1);
                if (x < w - 1) Step(index + 1);
                if (y > 0) Step(index - w);
                if (y < h - 1) Step(index + w);
            }
            return distance;
        }

        // Contour helpers.

        private static readonly Vector2Int[] Neighbours =
        {
            new Vector2Int(-1, 0), new Vector2Int(-1, 1), new Vector2Int(0, 1), new Vector2Int(1, 1),
            new Vector2Int(1, 0), new Vector2Int(1, -1), new Vector2Int(0, -1), new Vector2Int(-1, -1)
        };

        /// <summary>Moore neighbourhood boundary trace of a filled binary region.</summary>
        private static List<Vector2> TraceOutline(bool[] region, int w, int h)
        {
            int start = -1;
            for (int i = 0; i < region.Length; i++)
                if (region[i]) { start = i; break; }
            if (start < 0) return null;

            var startPoint = new Vector2Int(start % w, start / w);
            var backtrack = new Vector2Int(startPoint.x - 1, startPoint.y);
            var point = startPoint;
            var contour = new List<Vector2> { new Vector2(startPoint.x + 0.5f, startPoint.y + 0.5f) };

            bool Filled(Vector2Int p) =>
                p.x >= 0 && p.y >= 0 && p.x < w && p.y < h && region[p.y * w + p.x];

            int guard = w * h * 4;
            while (guard-- > 0)
            {
                Vector2Int delta = backtrack - point;
                int entry = 0;
                for (int i = 0; i < Neighbours.Length; i++)
                    if (Neighbours[i] == delta) { entry = i; break; }

                bool moved = false;
                for (int k = 1; k <= Neighbours.Length; k++)
                {
                    int index = (entry + k) % Neighbours.Length;
                    Vector2Int candidate = point + Neighbours[index];
                    if (!Filled(candidate)) continue;

                    backtrack = point + Neighbours[(index - 1 + Neighbours.Length) % Neighbours.Length];
                    point = candidate;
                    moved = true;
                    break;
                }

                if (!moved) break;
                if (point == startPoint) break;
                contour.Add(new Vector2(point.x + 0.5f, point.y + 0.5f));
            }

            return contour;
        }

        /// <summary>I smooth the outline so the stem notch does not become a liquid spike.</summary>
        private static void Smooth(List<Vector2> contour, int passes)
        {
            int count = contour.Count;
            if (count < 8) return;

            var buffer = new Vector2[count];
            for (int pass = 0; pass < passes; pass++)
            {
                for (int i = 0; i < count; i++)
                {
                    Vector2 previous = contour[(i - 1 + count) % count];
                    Vector2 next = contour[(i + 1) % count];
                    buffer[i] = (previous + contour[i] * 2f + next) * 0.25f;
                }
                for (int i = 0; i < count; i++) contour[i] = buffer[i];
            }
        }

        private static List<Vector2> SimplifyToBudget(List<Vector2> contour, float tolerance, int maxPoints)
        {
            maxPoints = Mathf.Max(8, maxPoints);
            List<Vector2> result = SimplifyClosed(contour, tolerance);
            int guard = 0;
            while (result.Count > maxPoints && guard++ < 24)
            {
                tolerance *= 1.45f;
                result = SimplifyClosed(contour, tolerance);
            }
            return result;
        }

        private static List<Vector2> SimplifyClosed(List<Vector2> points, float tolerance)
        {
            // I split the loop at its widest pair so Douglas-Peucker can simplify two open chains.
            int anchor = 0;
            int opposite = 0;
            float best = -1f;
            for (int i = 1; i < points.Count; i++)
            {
                float d = (points[i] - points[0]).sqrMagnitude;
                if (d > best) { best = d; opposite = i; }
            }

            var first = points.GetRange(anchor, opposite - anchor + 1);
            var second = points.GetRange(opposite, points.Count - opposite);
            second.Add(points[0]);

            var result = new List<Vector2>();
            var partA = new List<Vector2>();
            var partB = new List<Vector2>();
            DouglasPeucker(first, 0, first.Count - 1, tolerance, partA);
            DouglasPeucker(second, 0, second.Count - 1, tolerance, partB);

            result.Add(first[0]);
            result.AddRange(partA);
            result.Add(second[0]);
            result.AddRange(partB);
            return result;
        }

        private static void DouglasPeucker(List<Vector2> points, int first, int last,
            float tolerance, List<Vector2> kept)
        {
            if (last <= first + 1) return;

            float worst = 0f;
            int worstIndex = -1;
            Vector2 a = points[first];
            Vector2 b = points[last];

            for (int i = first + 1; i < last; i++)
            {
                float d = PointToSegment(points[i], a, b);
                if (d > worst) { worst = d; worstIndex = i; }
            }

            if (worst <= tolerance || worstIndex < 0) return;

            DouglasPeucker(points, first, worstIndex, tolerance, kept);
            kept.Add(points[worstIndex]);
            DouglasPeucker(points, worstIndex, last, tolerance, kept);
        }

        private static float PointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lengthSquared = ab.sqrMagnitude;
            if (lengthSquared < 1e-8f) return Vector2.Distance(p, a);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lengthSquared);
            return Vector2.Distance(p, a + ab * t);
        }

        // Measurements.

        /// <summary>I use a wide enough top row as the rim, not the tiny tip of a drawn ellipse.</summary>
        private static float RimRow(bool[] region, int w, int h, float minimumHalfWidth,
            out float centre, out int row)
        {
            centre = 0f;
            row = 0;
            float fallbackHalf = 0f;
            bool haveFallback = false;

            for (int y = h - 1; y >= 0; y--)
            {
                int min = int.MaxValue;
                int max = int.MinValue;
                for (int x = 0; x < w; x++)
                {
                    if (!region[y * w + x]) continue;
                    if (x < min) min = x;
                    if (x > max) max = x;
                }
                if (min > max) continue;

                float half = (max - min) * 0.5f;
                if (!haveFallback)
                {
                    haveFallback = true;
                    fallbackHalf = half;
                    row = y;
                    centre = (min + max) * 0.5f + 0.5f;
                }
                if (half < minimumHalfWidth) continue;

                row = y;
                centre = (min + max) * 0.5f + 0.5f;
                return half;
            }
            return fallbackHalf;
        }

        private static float WidestHalfWidth(bool[] region, int w, int h)
        {
            float widest = 0f;
            for (int y = 0; y < h; y++)
            {
                int min = int.MaxValue;
                int max = int.MinValue;
                for (int x = 0; x < w; x++)
                {
                    if (!region[y * w + x]) continue;
                    if (x < min) min = x;
                    if (x > max) max = x;
                }
                if (min > max) continue;
                widest = Mathf.Max(widest, (max - min) * 0.5f);
            }
            return widest;
        }

        private static Rect PolygonBounds(Vector2[] polygon)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < polygon.Length; i++)
            {
                Vector2 p = polygon[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }
    }
}
