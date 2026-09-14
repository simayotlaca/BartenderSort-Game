using UnityEngine;

namespace LiquidSort
{
    public static class LiquidSurfaceContract
    {
        public const int Revision = 15;
        public const string BulgeProperty = "_Bulge";
        public const string BulgeMaxProperty = "_BulgeMax";
        public const string InnerCurveProperty = "_InnerCurve";
        public const string InnerBulgeProperty = "_InnerBulge";
        public const string InnerMaxProperty = "_InnerMax";
        public const string SurfaceScaleProperty = "_SurfaceScale";
        public const string CapWallInsetProperty = "_CapWallInset";
        public const string RoyalUnitsPerPixelProperty = "_RoyalUnitsPerPixel";
        public const string GlassExpansionProperty = "_GlassExpansion";
        public const string RightInteriorClipProperty = "_RightInteriorClip";
        public const string BottomInteriorInsetProperty = "_BottomInteriorInset";
        public const string BottomInteriorFloorProperty = "_BottomInteriorFloor";
        public const string LiquidFloorRangeProperty = "_LiquidFloorRange";
        public const string LiquidFloorSamplesProperty = "_LiquidFloorSamples";
        public const string FloatingGarnishCausticProperty =
            "_FloatingGarnishCaustic";
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

        public static float ExposedSurfaceScale(float displayVolume, int capacity) =>
            Mathf.Clamp01(displayVolume / FirstUnitDepthRamp);

        private const float FirstUnitDepthRamp = 1f;

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
