using UnityEngine;

namespace LiquidSort
{
    /// <summary>I keep scene glass colours separate from the drawing so one asset works on different backgrounds. Puzzle liquid colours stay independent of the theme.</summary>
    [CreateAssetMenu(menuName = "Liquid Sort/Glass Visual Theme", fileName = "GlassVisualTheme")]
    public sealed class GlassVisualTheme : ScriptableObject
    {
        [System.Serializable]
        public struct Settings
        {
            [Header("Authored glass art")]
            [Tooltip("Draws a profiled vessel's Front sprite through the assigned material instead of the contour shader. The material keeps the authored detail and may restyle its colour/opacity.")]
            public bool preserveAuthoredFront;
            [Tooltip("Serialized sprite material used by the authored-front path. It may restyle colour and opacity while preserving the source sprite's fine detail.")]
            public Material authoredFrontMaterial;

            [Header("Contour")]
            [Tooltip("Thin outer line. The darker of the two, and what gives the glass its edge.")]
            public Color contourDark;
            [Tooltip("Thin edge highlight, on the side the light comes from.")]
            public Color contourLight;
            [Tooltip("Degrees. 0 = from the right, 90 = from above.")]
            [Range(0f, 360f)] public float lightDirection;

            [Header("Fake glass FX")]
            [Tooltip("Cool-white key reflection used by the lit side contour and bottom lens.")]
            public Color glassKeyLight;
            [Tooltip("Cooler fill reflection used on the opposite side contour.")]
            public Color glassFillLight;
            [Tooltip("Strength of the two thin side highlights drawn only on authored glass pixels.")]
            [Range(0f, 1f)] public float sideFxStrength;
            [Tooltip("Warm upper-left mouth hotspot, restricted to authored rim pixels.")]
            [Range(0f, 1f)] public float rimHotspotStrength;
            [Tooltip("Strength of the narrow glass lens immediately under the interior floor.")]
            [Range(0f, 1f)] public float bottomLensStrength;
            [Tooltip("How strongly the bottom liquid colour is reflected into the nearby glass base.")]
            [Range(0f, 1f)] public float liquidBounceStrength;

            [Header("Painted toy glass")]
            [Tooltip("Replaces continuous realistic ramps with a few hand-directed toy/acrylic light regions. Zero preserves the normal glass look.")]
            [Range(0f, 1f)] public float paintedToyStrength;
            [Tooltip("Saturated middle colour used to flatten solid toy-glass parts such as a mug handle.")]
            public Color toyMidColor;
            [Tooltip("Cool cyan used only for the narrow opposite rim and outer handle band.")]
            public Color toyFillColor;

            [Header("Contact shadow")]
            [Tooltip("Compact warm oval directly beneath the authored support footprint.")]
            public Color shadowColor;
            [Range(0f, 1f)] public float shadowStrength;
            [Tooltip("Faint plum/navy halo behind the compact contact oval.")]
            public Color wideShadowColor;
            [Range(0f, 1f)] public float wideShadowStrength;

            /// <summary>Neutral values, deliberately not tuned to any one background.</summary>
            public static Settings Default => new Settings
            {
                preserveAuthoredFront = false,
                authoredFrontMaterial = null,
                // I use cobalt edges and cyan highlights so the same glass palette reads on light and dark
                // backgrounds.
                contourDark = new Color(0.035f, 0.125f, 0.300f),
                contourLight = new Color(0.430f, 0.770f, 1.000f),
                lightDirection = 120f,
                glassKeyLight = new Color(0.76f, 0.94f, 1f, 1f),
                glassFillLight = new Color(0.24f, 0.60f, 1f, 1f),
                sideFxStrength = 0.58f,
                rimHotspotStrength = 0f,
                // I lift only painted glass at the measured seam, keeping the liquid shader and gameplay colours
                // unchanged.
                bottomLensStrength = 0.64f,
                liquidBounceStrength = 0f,
                paintedToyStrength = 0f,
                toyMidColor = new Color(0.31f, 0.47f, 0.59f, 1f),
                toyFillColor = new Color(0.27f, 0.89f, 0.96f, 1f),
                shadowColor = Color.black,
                shadowStrength = 0.36f,
                wideShadowColor = Color.black,
                wideShadowStrength = 0f
            };

            /// <summary>Only values that change an individual vessel.</summary>
            public int GlassHash()
            {
                unchecked
                {
                    int hash = preserveAuthoredFront.GetHashCode();
                    hash = hash * 31 + (authoredFrontMaterial != null
                        ? authoredFrontMaterial.GetInstanceID()
                        : 0);
                    hash = hash * 31 + contourDark.GetHashCode();
                    hash = hash * 31 + contourLight.GetHashCode();
                    hash = hash * 31 + lightDirection.GetHashCode();
                    hash = hash * 31 + glassKeyLight.GetHashCode();
                    hash = hash * 31 + glassFillLight.GetHashCode();
                    hash = hash * 31 + sideFxStrength.GetHashCode();
                    hash = hash * 31 + rimHotspotStrength.GetHashCode();
                    hash = hash * 31 + bottomLensStrength.GetHashCode();
                    hash = hash * 31 + liquidBounceStrength.GetHashCode();
                    hash = hash * 31 + paintedToyStrength.GetHashCode();
                    hash = hash * 31 + toyMidColor.GetHashCode();
                    hash = hash * 31 + toyFillColor.GetHashCode();
                    hash = hash * 31 + shadowColor.GetHashCode();
                    hash = hash * 31 + shadowStrength.GetHashCode();
                    hash = hash * 31 + wideShadowColor.GetHashCode();
                    hash = hash * 31 + wideShadowStrength.GetHashCode();
                    return hash;
                }
            }
        }

        public Settings settings = Settings.Default;
    }
}
