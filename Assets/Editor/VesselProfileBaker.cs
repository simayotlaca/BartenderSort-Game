using System.Collections.Generic;
using LiquidSort;
using UnityEditor;
using UnityEngine;

/// <summary>I bake the glass outline, mask and lookup tables so the game can use them directly.</summary>
public static class VesselProfileBaker
{
    private const int AngleSteps = 97;    // 2.5 degrees apart over the tilt range
    private const int FillSteps = 65;
    private const int UprightSteps = 129;
    private const float MaxAngle = 120f;

    /// <summary>I rebuild masks from the saved polygons. This keeps the inner glass boundary unchanged.</summary>
    [MenuItem("Tools/LiquidSort/Rebake Interior Masks From Authored Polygons")]
    public static void RebakeMasksFromAuthoredPolygons()
    {
        string[] guids = AssetDatabase.FindAssets("t:VesselProfile");
        int done = 0;
        foreach (string guid in guids)
        {
            var profile = AssetDatabase.LoadAssetAtPath<VesselProfile>(
                AssetDatabase.GUIDToAssetPath(guid));
            if (profile == null || !profile.IsBaked) continue;
            BakeMask(profile);
            EditorUtility.SetDirty(profile);
            done++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"LiquidSort: re-cut {done} interior masks from the authored polygons. "
                + "No polygon and no volume field was written.");
    }

    [MenuItem("Tools/LiquidSort/Bake Selected Vessel Profiles %#b")]
    public static void BakeSelection()
    {
        var profiles = new List<VesselProfile>();
        foreach (Object o in Selection.objects)
            if (o is VesselProfile profile) profiles.Add(profile);

        if (profiles.Count == 0)
        {
            EditorUtility.DisplayDialog("Bake Vessel Profile",
                "Select one or more VesselProfile assets first.", "OK");
            return;
        }

        foreach (VesselProfile profile in profiles) Bake(profile);
        AssetDatabase.SaveAssets();
    }

    [MenuItem("Tools/LiquidSort/Bake Selected Vessel Profiles %#b", true)]
    private static bool BakeSelectionEnabled()
    {
        foreach (Object o in Selection.objects)
            if (o is VesselProfile) return true;
        return false;
    }

    public static bool Bake(VesselProfile profile)
    {
        if (profile == null) return false;
        if (profile.front == null)
        {
            Debug.LogError($"{profile.name}: no front sprite to draw or bake optical visibility from.", profile);
            return false;
        }

        Sprite traceSource = profile.traceSource != null ? profile.traceSource : profile.front;
        Sprite opticalSource = profile.front;
        string tracePath = AssetDatabase.GetAssetPath(traceSource);
        string opticalPath = AssetDatabase.GetAssetPath(opticalSource);
        bool sharedTexture = !string.IsNullOrEmpty(tracePath)
            && string.Equals(tracePath, opticalPath, System.StringComparison.Ordinal);
        var traceImporter = AssetImporter.GetAtPath(tracePath) as TextureImporter;
        var opticalImporter = sharedTexture
            ? traceImporter
            : AssetImporter.GetAtPath(opticalPath) as TextureImporter;
        bool restoreTraceUnreadable = traceImporter != null && !traceImporter.isReadable;
        bool restoreOpticalUnreadable = !sharedTexture
            && opticalImporter != null && !opticalImporter.isReadable;
        string traceName = traceSource.name;
        string opticalName = opticalSource.name;
        try
        {
            if (restoreTraceUnreadable)
            {
                traceImporter.isReadable = true;
                traceImporter.SaveAndReimport();
            }
            if (restoreOpticalUnreadable)
            {
                // I only toggle this importer when the sprites use different textures.
                opticalImporter.isReadable = true;
                opticalImporter.SaveAndReimport();
            }

            Sprite readableTrace = traceImporter != null
                ? LoadSprite(tracePath, traceName)
                : traceSource;
            Sprite readableOptical = opticalImporter != null
                ? LoadSprite(opticalPath, opticalName)
                : opticalSource;
            if (readableTrace == null || readableOptical == null)
            {
                Debug.LogError($"{profile.name}: could not reload trace '{traceName}' "
                    + $"and front '{opticalName}' for baking.", profile);
                return false;
            }
            return BakeReadable(profile, readableTrace, readableOptical);
        }
        finally
        {
            // I reacquire importers after reimport and remove the source textures' CPU copies.
            if (restoreOpticalUnreadable)
            {
                opticalImporter = AssetImporter.GetAtPath(opticalPath) as TextureImporter;
                if (opticalImporter != null)
                {
                    opticalImporter.isReadable = false;
                    opticalImporter.SaveAndReimport();
                }
            }
            if (restoreTraceUnreadable)
            {
                traceImporter = AssetImporter.GetAtPath(tracePath) as TextureImporter;
                if (traceImporter != null)
                {
                    traceImporter.isReadable = false;
                    traceImporter.SaveAndReimport();
                }
            }
        }
    }

    private static bool BakeReadable(VesselProfile profile, Sprite readableTrace,
        Sprite readableFront)
    {
        GlassInteriorFitter.Fit fit = GlassInteriorFitter.FitSprite(readableTrace,
            GlassInteriorFitter.Settings.Default);
        if (fit == null)
        {
            Debug.LogError($"{profile.name}: could not trace '{readableTrace.name}'.", profile);
            return false;
        }

        Vector2[] polygon = profile.clipRightInterior
            ? ClipRightInterior(fit.Polygon, profile.rightInteriorXAtY0,
                profile.rightInteriorSlope)
            : fit.Polygon;
        profile.interiorPolygon = polygon;
        // I keep the render contour intact; the volume clip would cut off its valid right wall.
        profile.interiorRenderPolygon = fit.RenderPolygon;
        profile.interiorBounds = PolygonBounds(polygon);
        profile.mouthLocal = fit.Mouth;
        profile.mouthHalfWidth = fit.MouthHalfWidth;
        BakeSupportLocal(profile, readableFront);
        profile.visibleBottomLocal = fit.VisibleBottom;
        profile.hasLiquidFloorCurve = profile.useLiquidFloorCurve
            && fit.LiquidFloorSamples != null
            && fit.LiquidFloorSamples.Length == VesselProfile.LiquidFloorSampleCount;
        profile.liquidFloorXRange = profile.hasLiquidFloorCurve
            ? fit.LiquidFloorXRange
            : default;
        profile.liquidFloorSamples = profile.hasLiquidFloorCurve
            ? fit.LiquidFloorSamples
            : null;
        profile.polygonArea = VesselFillMath.Area(polygon);

        BakeMask(profile);
        profile.upright = BakeUpright(profile, readableFront);
        profile.hasVisibleLiquidFloor = profile.upright.HasVisibleHeightMap;
        profile.visibleLiquidFloor = profile.hasVisibleLiquidFloor
            ? profile.upright.LevelAtVisibleHeight(0f)
            : profile.upright.floorY;
        profile.tilted = BakeTilted(profile);

        EditorUtility.SetDirty(profile);
        Debug.Log($"{profile.name}: baked {profile.interiorPolygon.Length} interior points, " +
                  $"{AngleSteps}x{FillSteps} tilt table, {UprightSteps} upright samples.", profile);
        return true;
    }

    /// <summary>I save the visible base and mouth centre so asymmetric glasses sit on their real base.</summary>
    private static void BakeSupportLocal(VesselProfile profile, Sprite front)
    {
        Texture2D texture = front != null ? front.texture : null;
        if (texture == null)
        {
            profile.hasSupportLocal = false;
            return;
        }

        Color32[] pixels = texture.GetPixels32();
        Rect rect = front.rect;
        int xStart = Mathf.Clamp(Mathf.FloorToInt(rect.xMin), 0, texture.width - 1);
        int xEnd = Mathf.Clamp(Mathf.CeilToInt(rect.xMax), 1, texture.width);
        int yStart = Mathf.Clamp(Mathf.FloorToInt(rect.yMin), 0, texture.height - 1);
        int yEnd = Mathf.Clamp(Mathf.CeilToInt(rect.yMax), 1, texture.height);
        int bottomPixel = yEnd;

        for (int y = yStart; y < yEnd; y++)
        {
            int row = y * texture.width;
            for (int x = xStart; x < xEnd; x++)
            {
                if (pixels[row + x].a < 8) continue;
                bottomPixel = y;
                break;
            }
            if (bottomPixel != yEnd) break;
        }

        if (bottomPixel == yEnd)
        {
            profile.hasSupportLocal = false;
            return;
        }

        float ppu = Mathf.Max(1f, front.pixelsPerUnit);
        float bottom = (bottomPixel - rect.yMin - front.pivot.y) / ppu;
        profile.supportLocal = new Vector2(profile.mouthLocal.x, bottom);
        profile.hasSupportLocal = true;
    }

    private static Sprite LoadSprite(string path, string spriteName)
    {
        Sprite readable = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (readable != null && readable.name == spriteName) return readable;

        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
        for (int i = 0; i < assets.Length; i++)
        {
            if (assets[i] is Sprite candidate && candidate.name == spriteName)
                return candidate;
        }
        return null;
    }

    /// <summary>I clip the handle out of the volume. Clean edge crossings avoid folded or zero-length edges.</summary>
    private static Vector2[] ClipRightInterior(Vector2[] source, float xAtY0, float slope)
    {
        var result = new List<Vector2>(source.Length + 2);
        Vector2 previous = source[source.Length - 1];
        float previousDistance = xAtY0 + slope * previous.y - previous.x;
        bool previousInside = previousDistance >= 0f;

        for (int i = 0; i < source.Length; i++)
        {
            Vector2 current = source[i];
            float currentDistance = xAtY0 + slope * current.y - current.x;
            bool currentInside = currentDistance >= 0f;

            if (currentInside != previousInside)
            {
                float denominator = previousDistance - currentDistance;
                float t = Mathf.Abs(denominator) > 1e-6f
                    ? Mathf.Clamp01(previousDistance / denominator)
                    : 0f;
                result.Add(Vector2.Lerp(previous, current, t));
            }
            if (currentInside) result.Add(current);

            previous = current;
            previousDistance = currentDistance;
            previousInside = currentInside;
        }
        return result.Count >= 3 ? result.ToArray() : source;
    }

    private static Rect PolygonBounds(Vector2[] polygon)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 p = polygon[i];
            minX = Mathf.Min(minX, p.x);
            minY = Mathf.Min(minY, p.y);
            maxX = Mathf.Max(maxX, p.x);
            maxY = Mathf.Max(maxY, p.y);
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static void BakeMask(VesselProfile profile)
    {
        string path = AssetDatabase.GetAssetPath(profile);
        if (profile.interiorMask != null)
        {
            Object.DestroyImmediate(profile.interiorMask, true);
            profile.interiorMask = null;
        }

        // I draw the mask from the inner wall but keep its existing texture alignment.
        Texture2D mask = CreateInteriorMask(
            profile.InteriorRenderPolygon, profile.QuadRect, 160f);
        mask.name = profile.name + " Interior Mask";
        mask.hideFlags = HideFlags.None;

        AssetDatabase.AddObjectToAsset(mask, path);
        profile.interiorMask = mask;
    }

    private static Texture2D CreateInteriorMask(
        Vector2[] polygon, Rect rect, float pixelsPerUnit)
    {
        int width = Mathf.Clamp(
            Mathf.RoundToInt(rect.width * pixelsPerUnit), 8, 2048);
        int height = Mathf.Clamp(
            Mathf.RoundToInt(rect.height * pixelsPerUnit), 8, 2048);
        var texture = new Texture2D(
            width, height, TextureFormat.RGBA32, false)
        {
            name = "LiquidInteriorMask",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave
        };

        var pixels = new Color32[width * height];
        float texel = rect.width / width;
        for (int y = 0; y < height; y++)
        {
            float localY = rect.yMin + (y + 0.5f) * rect.height / height;
            for (int x = 0; x < width; x++)
            {
                float localX = rect.xMin + (x + 0.5f) * texel;
                float distance = SignedDistance(
                    polygon, new Vector2(localX, localY));
                byte alpha = (byte)(
                    Mathf.Clamp01(0.5f - distance / texel) * 255f);
                pixels[y * width + x] =
                    new Color32(255, 255, 255, alpha);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private static float SignedDistance(Vector2[] polygon, Vector2 point)
    {
        float best = float.MaxValue;
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1;
             i < polygon.Length;
             j = i++)
        {
            Vector2 a = polygon[j];
            Vector2 b = polygon[i];
            best = Mathf.Min(best, DistanceToSegment(point, a, b));
            if ((b.y > point.y) != (a.y > point.y)
                && point.x < (a.x - b.x) * (point.y - b.y)
                    / (a.y - b.y) + b.x)
            {
                inside = !inside;
            }
        }

        return inside ? -best : best;
    }

    private static float DistanceToSegment(
        Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 segment = b - a;
        float lengthSquared = segment.sqrMagnitude;
        if (lengthSquared < 1e-8f)
            return Vector2.Distance(point, a);

        float t = Mathf.Clamp01(
            Vector2.Dot(point - a, segment) / lengthSquared);
        return Vector2.Distance(point, a + segment * t);
    }

    private static VesselProfile.UprightTable BakeUpright(VesselProfile profile,
        Sprite readableFront)
    {
        Vector2[] polygon = profile.interiorPolygon;
        float area = Mathf.Max(profile.polygonArea, 1e-5f);
        VesselFillMath.VerticalExtent(polygon, out float minY, out float maxY);

        var table = new VesselProfile.UprightTable
        {
            steps = UprightSteps,
            minY = minY,
            maxY = maxY,
            areaFraction = new float[UprightSteps],
            capHalfDepth = new float[UprightSteps],
            spillAngle = new float[UprightSteps]
        };

        for (int i = 0; i < UprightSteps; i++)
        {
            float level = Mathf.Lerp(minY, maxY, i / (float)(UprightSteps - 1));
            table.areaFraction[i] = Mathf.Clamp01(VesselFillMath.AreaBelow(polygon, level) / area);

            float half = VesselFillMath.HalfWidthAt(polygon, level, out _);
            table.capHalfDepth[i] = Mathf.Min(2f * half * profile.surfaceBulge,
                profile.interiorBounds.height * profile.maxCapDepth);
        }

        BakeVisibleHeightMap(profile, readableFront, table);

        table.ceilingY = VesselFillMath.SurfaceCeiling(
            polygon, minY, maxY, profile.interiorBounds.height,
            profile.surfaceBulge, profile.maxCapDepth, profile.surfaceAllowance,
            profile.brimGapCaps, profile.brimHeadroom);

        // I start the map at the inner outline floor so the bottom colour gets no extra height.
        table.floorY = minY;

        // I store the first pouring angle for each fill fraction.
        Vector2 spillMouth = profile.mouthHalfWidth > 0.0001f
            ? new Vector2(profile.mouthLocal.x - profile.mouthHalfWidth,
                profile.mouthLocal.y)
            : profile.mouthLocal;
        for (int i = 0; i < UprightSteps; i++)
        {
            float fill = i / (float)(UprightSteps - 1);
            table.spillAngle[i] = VesselFillMath.SpillAngle(polygon, spillMouth, fill, MaxAngle);
        }

        return table;
    }

    /// <summary>I bake visible height and its inverse within each row's width. The game only interpolates the tables.</summary>
    private static void BakeVisibleHeightMap(VesselProfile profile, Sprite front,
        VesselProfile.UprightTable table)
    {
        Texture2D texture = front != null ? front.texture : null;
        if (texture == null)
            throw new System.InvalidOperationException(profile.name
                + ": front art has no texture for the visibility bake");

        Color32[] pixels = texture.GetPixels32();
        Rect spriteRect = front.rect;
        Vector2 pivot = front.pivot;
        float ppu = Mathf.Max(1e-5f, front.pixelsPerUnit);
        int count = table.steps;
        var transmission = new float[count];
        var smoothed = new float[count];

        for (int i = 0; i < count; i++)
        {
            float level = Mathf.Lerp(table.minY, table.maxY,
                i / (float)(count - 1));
            float half = VesselFillMath.HalfWidthAt(profile.interiorPolygon,
                level, out float centre);
            if (half <= 1e-5f)
            {
                transmission[i] = 0f;
                continue;
            }

            // I use the middle 55% of each row to avoid the side strokes on both wide and narrow glasses.
            float coreHalf = half * 0.55f;
            int samples = Mathf.Clamp(
                Mathf.CeilToInt(coreHalf * 2f * ppu), 5, 96);
            float sum = 0f;
            for (int sample = 0; sample < samples; sample++)
            {
                float t = (sample + 0.5f) / samples;
                float localX = Mathf.Lerp(centre - coreHalf,
                    centre + coreHalf, t);
                float pixelX = spriteRect.x + pivot.x + localX * ppu;
                float pixelY = spriteRect.y + pivot.y + level * ppu;
                sum += 1f - SampleAlphaBilinear(pixels, texture.width,
                    texture.height, spriteRect, pixelX, pixelY);
            }
            transmission[i] = Mathf.Clamp01(sum / samples);
        }

        // I filter one noisy base row while keeping the height map monotonic.
        smoothed[0] = transmission[0];
        smoothed[count - 1] = transmission[count - 1];
        for (int i = 1; i < count - 1; i++)
            smoothed[i] = (transmission[i - 1] + 2f * transmission[i]
                + transmission[i + 1]) * 0.25f;

        table.visibleHeight = new float[count];
        float step = (table.maxY - table.minY) / (count - 1);
        for (int i = 1; i < count; i++)
            table.visibleHeight[i] = table.visibleHeight[i - 1]
                + step * (smoothed[i - 1] + smoothed[i]) * 0.5f;

        table.totalVisibleHeight = table.visibleHeight[count - 1];
        if (table.totalVisibleHeight <= 1e-5f)
            throw new System.InvalidOperationException(profile.name
                + ": authored front art leaves no visible liquid height");

        table.levelAtVisibleFraction = new float[count];
        int upper = 1;
        for (int i = 0; i < count; i++)
        {
            float target = table.totalVisibleHeight * i / (count - 1f);
            if (i == 0)
            {
                // I skip invisible base rows so they do not count toward the first unit.
                while (upper < count && table.visibleHeight[upper] <= 1e-6f)
                    upper++;
                table.levelAtVisibleFraction[0] = LevelAtIndex(table,
                    Mathf.Max(0, upper - 1));
                continue;
            }

            while (upper < count - 1 && table.visibleHeight[upper] < target)
                upper++;
            int lower = Mathf.Max(0, upper - 1);
            float lowVisible = table.visibleHeight[lower];
            float highVisible = table.visibleHeight[upper];
            float blend = highVisible > lowVisible + 1e-7f
                ? Mathf.Clamp01((target - lowVisible) / (highVisible - lowVisible))
                : 0f;
            table.levelAtVisibleFraction[i] = Mathf.Lerp(
                LevelAtIndex(table, lower), LevelAtIndex(table, upper), blend);
        }
    }

    private static float LevelAtIndex(VesselProfile.UprightTable table, int index) =>
        Mathf.Lerp(table.minY, table.maxY,
            Mathf.Clamp(index, 0, table.steps - 1) / (float)(table.steps - 1));

    private static float SampleAlphaBilinear(Color32[] pixels, int width, int height,
        Rect spriteRect, float x, float y)
    {
        float maxX = Mathf.Min(width - 1f, spriteRect.xMax - 1f);
        float maxY = Mathf.Min(height - 1f, spriteRect.yMax - 1f);
        x = Mathf.Clamp(x, Mathf.Max(0f, spriteRect.xMin), maxX);
        y = Mathf.Clamp(y, Mathf.Max(0f, spriteRect.yMin), maxY);

        int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, width - 1);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, height - 1);
        int x1 = Mathf.Min(x0 + 1, width - 1);
        int y1 = Mathf.Min(y0 + 1, height - 1);
        float tx = x - x0;
        float ty = y - y0;

        float a00 = pixels[y0 * width + x0].a / 255f;
        float a10 = pixels[y0 * width + x1].a / 255f;
        float a01 = pixels[y1 * width + x0].a / 255f;
        float a11 = pixels[y1 * width + x1].a / 255f;
        return Mathf.Lerp(Mathf.Lerp(a00, a10, tx),
            Mathf.Lerp(a01, a11, tx), ty);
    }

    private static VesselProfile.TiltTable BakeTilted(VesselProfile profile)
    {
        Vector2[] polygon = profile.interiorPolygon;
        float area = Mathf.Max(profile.polygonArea, 1e-5f);

        var table = new VesselProfile.TiltTable
        {
            angleSteps = AngleSteps,
            fillSteps = FillSteps,
            maxAngle = MaxAngle,
            level = new float[AngleSteps * FillSteps],
            centreX = new float[AngleSteps * FillSteps],
            halfChord = new float[AngleSteps * FillSteps],
            ceilingFill = new float[AngleSteps]
        };

        var rotated = new List<Vector2>(polygon.Length);
        for (int a = 0; a < AngleSteps; a++)
        {
            float angle = Mathf.Lerp(-MaxAngle, MaxAngle, a / (float)(AngleSteps - 1));
            VesselFillMath.Rotate(polygon, angle, rotated);
            VesselFillMath.VerticalExtent(rotated, out float lowY, out float highY);
            float ceilingLevel = VesselFillMath.SurfaceCeiling(
                rotated, lowY, highY, profile.interiorBounds.height,
                profile.surfaceBulge, profile.maxCapDepth, profile.surfaceAllowance,
                profile.brimGapCaps, profile.brimHeadroom);
            table.ceilingFill[a] = Mathf.Clamp01(VesselFillMath.AreaBelow(rotated, ceilingLevel) / area);

            for (int f = 0; f < FillSteps; f++)
            {
                float fraction = f / (float)(FillSteps - 1);
                float level = VesselFillMath.LevelForFraction(rotated, area, fraction);
                float half = VesselFillMath.HalfWidthAt(rotated, level, out float centre);

                int index = a * FillSteps + f;
                table.level[index] = level;
                table.centreX[index] = centre;
                table.halfChord[index] = half;
            }
        }

        return table;
    }
}
