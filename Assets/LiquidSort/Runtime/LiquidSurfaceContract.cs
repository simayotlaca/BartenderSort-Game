using UnityEngine;

namespace LiquidSort
{
    /// <summary>I keep liquid property names and volume rules together so C# and BottleLiquid.shader stay in sync.</summary>
    public static class LiquidSurfaceContract
    {
        // Bump this when a property's meaning changes so pooled renderers refresh old property blocks after
        // reloads.
        public const int Revision = 14;
        public const string ShaderName = "LiquidSort/BottleLiquid";
        public const string BulgeProperty = "_Bulge";
        public const string BulgeMaxProperty = "_BulgeMax";
        public const string InnerCurveProperty = "_InnerCurve";
        public const string InnerBulgeProperty = "_InnerBulge";
        public const string InnerMaxProperty = "_InnerMax";
        public const string SurfaceScaleProperty = "_SurfaceScale";
        public const string CapWallInsetProperty = "_CapWallInset";
        // Vessel-local units per Royal reference pixel, keeping pixel-authored insets proportional at every board
        // and device size.
        public const string RoyalUnitsPerPixelProperty = "_RoyalUnitsPerPixel";
        // I share the front glass's horizontal stretch with the liquid to avoid wall gaps: (anchorX,
        // spriteLocalWidth, designPixels, unused).
        public const string GlassExpansionProperty = "_GlassExpansion";
        public const string RightInteriorClipProperty = "_RightInteriorClip";
        public const string BottomInteriorInsetProperty = "_BottomInteriorInset";
        public const string BottomInteriorFloorProperty = "_BottomInteriorFloor";
        public const string LiquidFloorRangeProperty = "_LiquidFloorRange";
        public const string LiquidFloorSamplesProperty = "_LiquidFloorSamples";
        // (centreX, halfWidth, depth, amount) in gravity-aligned liquid space. The garnish supplies it; the liquid
        // shader clips it to the live fill and mask.
        public const string FloatingGarnishCausticProperty =
            "_FloatingGarnishCaustic";
        // (strength, stable instance phase, unused, unused). The shader owns the path and clips it to the
        // waterline, floor and mask.
        public const string AmbientBubbleProperty = "_AmbientBubbleParams";

        public static readonly int BulgeId = Shader.PropertyToID(BulgeProperty);
        public static readonly int BulgeMaxId = Shader.PropertyToID(BulgeMaxProperty);
        public static readonly int InnerCurveId = Shader.PropertyToID(InnerCurveProperty);
        public static readonly int InnerBulgeId = Shader.PropertyToID(InnerBulgeProperty);
        public static readonly int BandInfoId = Shader.PropertyToID("_BandInfo");
        public static readonly int BandShadeId = Shader.PropertyToID("_BandShade");
        public static readonly int BandCountId = Shader.PropertyToID("_BandCount");
        public static readonly int SurfaceScaleId =
            Shader.PropertyToID(SurfaceScaleProperty);
        public static readonly int CapWallInsetId =
            Shader.PropertyToID(CapWallInsetProperty);
        public static readonly int RoyalUnitsPerPixelId =
            Shader.PropertyToID(RoyalUnitsPerPixelProperty);
        public static readonly int GlassExpansionId =
            Shader.PropertyToID(GlassExpansionProperty);
        public static readonly int RightInteriorClipId =
            Shader.PropertyToID(RightInteriorClipProperty);
        public static readonly int BottomInteriorInsetId =
            Shader.PropertyToID(BottomInteriorInsetProperty);
        public static readonly int BottomInteriorFloorId =
            Shader.PropertyToID(BottomInteriorFloorProperty);
        public static readonly int LiquidFloorRangeId =
            Shader.PropertyToID(LiquidFloorRangeProperty);
        public static readonly int LiquidFloorSamplesId =
            Shader.PropertyToID(LiquidFloorSamplesProperty);
        public static readonly int FloatingGarnishCausticId =
            Shader.PropertyToID(FloatingGarnishCausticProperty);
        public static readonly int AmbientBubbleId =
            Shader.PropertyToID(AmbientBubbleProperty);

        private static readonly string[] RequiredMaterialProperties =
        {
            "_MaskTex",
            BulgeProperty,
            BulgeMaxProperty,
            InnerCurveProperty,
            InnerBulgeProperty,
            InnerMaxProperty,
            SurfaceScaleProperty,
            CapWallInsetProperty,
            // RoyalUnitsPerPixelProperty is optional because zero uses shader derivatives. Requiring it would
            // freeze legacy materials that already support the fallback.
            "_RevealTurbulenceAmount",
            "_RevealTurbulenceLife",
            "_CapFlash",
            "_QuadSize",
            RightInteriorClipProperty,
            BottomInteriorInsetProperty,
            BottomInteriorFloorProperty,
            LiquidFloorRangeProperty,
            FloatingGarnishCausticProperty
        };

        /// <summary>I grow cap depth over the first fractional unit so the first drop cannot create a full ellipse at once. Every settled whole-unit fill keeps the authored depth.</summary>
        public static float ExposedSurfaceScale(float displayVolume, int capacity) =>
            Mathf.Clamp01(displayVolume / FirstUnitDepthRamp);

        /// <summary>Volume over which the exposed ellipse reaches its authored depth.</summary>
        private const float FirstUnitDepthRamp = 1f;

        /// <summary>I reject replacement materials that do not support the liquid contract so they cannot reinterpret cap depth silently.</summary>
        public static bool TryValidate(Material material, out string reason)
        {
            if (material == null)
            {
                reason = "liquid material is missing";
                return false;
            }

            if (material.shader == null)
            {
                reason = $"material '{material.name}' has no shader";
                return false;
            }

            for (int i = 0; i < RequiredMaterialProperties.Length; i++)
            {
                string property = RequiredMaterialProperties[i];
                if (material.HasProperty(Shader.PropertyToID(property))) continue;

                reason = $"material '{material.name}' using shader "
                       + $"'{material.shader.name}' does not expose {property}";
                return false;
            }

            reason = null;
            return true;
        }
    }
}
