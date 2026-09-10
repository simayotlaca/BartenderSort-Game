using UnityEngine;

namespace LiquidSort
{
    /// <summary>I store each liquid's body, cap and depth colours. Highlights and shadows shift hue while keeping saturation, with the same rule for unknown colours.</summary>
    public static class LiquidPalette
    {
        public const int Revision = 16;

        public struct Entry
        {
            /// <summary>The liquid's level-data name, so I can retune its colour without editing levels.</summary>
            public string name;
            public Color body;
            public Color cap;
            public Color shade;
        }

        public static readonly Entry[] Reference =
        {
            // I match the live BsPalette bodies explicitly so bright colours keep visible caps. Highlights and
            // shadows shift hue instead of mixing with white or grey.
            Pair("Royal Red",        0xE8453C, 0xFF765A, 0xE52B3D),
            Pair("Royal Orange",     0xF08020, 0xFFC13A, 0xEF4C16),
            Pair("Royal Yellow",     0xF5C51D, 0xFFEA55, 0xF09A13),
            Pair("Royal Green",      0x80A73D, 0xB8E54D, 0x42AD36),
            Pair("Royal Blue",       0x3E8EDE, 0x70E8F7, 0x2D63E8),
            Pair("Royal Navy",       0x2B30CE, 0x655BFF, 0x3337D8),
            Pair("Royal Pink",       0xCE1DA0, 0xFF51CA, 0xC2189E),

            Pair("Pink",             0xFC6FD8, 0xFF9BE5, 0xD84AB8),
            Pair("Purple",           0x5E1D8D, 0xA45BE0, 0x6D28B7),
            Pair("Wine",             0x6A0051, 0xC3379C, 0x8C0C73),
            Pair("Lime",             0x6FA400, 0xC4F23B, 0x4B9E1D),
            Pair("Sand",             0xADAB82, 0xE8DC89, 0x9E843F),
            Pair("Teal",             0x057A64, 0x31DDB3, 0x078B83),
            Pair("Orange",           0xE35800, 0xFFBF32, 0xED4810),
            Pair("Blue",             0x0098D7, 0x5CE8FA, 0x2364E8),

            // Additional art colours use the same saturated highlight/depth pairing.
            Pair("Candy Pink",       0xE8456E, 0xFF668A, 0xD52B68),
            Pair("Tangerine Orange", 0xF5A623, 0xFFDD54, 0xF16A16),
            Pair("Cyan",             0x3DAEEF, 0x74E9FA, 0x2D6FE8),
            Pair("Deep Teal",        0x1E7A63, 0x3ED8AD, 0x168C88),
            Pair("Lime Punch",       0x8ED12A, 0xC9F65A, 0x56B51E),
            Pair("Grape Pop",        0x8447E9, 0xBD73FF, 0x6836D9),
            Pair("Lemon Candy",      0xF3C928, 0xFFEC65, 0xEFA21A),

            // Exact BsPalette matches keep bright gameplay colours' top faces and body lighting visible.
            Pair("Gameplay Red",      0xE8453C, 0xFF765A, 0xE52B3D),
            Pair("Gameplay Orange",   0xF5893B, 0xFFD45D, 0xF05A18),
            Pair("Gameplay Yellow",   0xF7CE46, 0xFFEC70, 0xF2A91D),
            Pair("Gameplay Green",    0x5BC24B, 0x9DEB63, 0x2FA447),
            Pair("Gameplay Blue",     0x3E8EDE, 0x70E8F7, 0x2D63E8),
            Pair("Gameplay Purple",   0x7D32BF, 0xB961F1, 0x6731D2),
            Pair("Gameplay Pink",     0xEE6FA8, 0xFF91D2, 0xD43B9A)
        };

        /// <summary>Palette entry by name. Case and spacing are ignored.</summary>
        public static bool TryGet(string name, out Entry entry)
        {
            for (int i = 0; i < Reference.Length; i++)
            {
                if (!SameName(Reference[i].name, name)) continue;
                entry = Reference[i];
                return true;
            }
            entry = default;
            return false;
        }

        /// <summary>I prefer an authored cap colour, otherwise brighten only as far as the body colour has room.</summary>
        public static Color CapFor(Color body)
        {
            for (int i = 0; i < Reference.Length; i++)
                if (Same(Reference[i].body, body)) return Reference[i].cap;

            return Derive(body);
        }

        /// <summary>I use authored depth colours or a more saturated, darker fallback, without grey mixing.</summary>
        public static Color ShadeFor(Color body)
        {
            for (int i = 0; i < Reference.Length; i++)
                if (Same(Reference[i].body, body)) return Reference[i].shade;

            return DeriveShade(body);
        }

        /// <summary>I shift fallback highlight hue without lowering saturation and brighten only within the available headroom.</summary>
        public static Color Derive(Color body)
        {
            Color.RGBToHSV(body, out float hue, out float saturation, out float value);
            float shift = hue < 0.20f ? 0.035f
                : hue < 0.45f ? -0.030f
                : hue < 0.72f ? -0.035f
                : 0.020f;
            float liftedValue = Mathf.Lerp(value, 1f, 0.42f);
            Color result = Color.HSVToRGB(
                Mathf.Repeat(hue + shift, 1f),
                Mathf.Clamp01(saturation + 0.08f),
                liftedValue);
            result.a = body.a;
            return result;
        }

        private static Color DeriveShade(Color body)
        {
            Color.RGBToHSV(body, out float hue, out float saturation, out float value);
            float shift = hue < 0.20f ? -0.055f
                : hue < 0.45f ? 0.055f
                : hue < 0.72f ? 0.045f
                : 0.025f;
            // I use a saturated nearby hue for depth while keeping enough brightness for dark shelves.
            float jewelFloor = Mathf.Max(value, 0.82f);
            float depthValue = Mathf.Lerp(value, jewelFloor, 0.72f);
            Color result = Color.HSVToRGB(
                Mathf.Repeat(hue + shift, 1f),
                Mathf.Clamp01(saturation + 0.12f),
                depthValue);
            result.a = body.a;
            return result;
        }

        private static Entry Pair(string name, int body, int cap, int shade) =>
            new Entry
            {
                name = name,
                body = Hex(body),
                cap = Hex(cap),
                shade = Hex(shade)
            };

        private static bool SameName(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.Replace(" ", ""), b.Replace(" ", ""),
                System.StringComparison.OrdinalIgnoreCase);
        }

        private static Color Hex(int rgb) => new Color(
            ((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

        // I allow one-byte serialization differences without matching a neighbouring palette colour.
        private const float ChannelTolerance = 1.5f / 255f;
        private static bool Same(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < ChannelTolerance
            && Mathf.Abs(a.g - b.g) < ChannelTolerance
            && Mathf.Abs(a.b - b.b) < ChannelTolerance;
    }
}
