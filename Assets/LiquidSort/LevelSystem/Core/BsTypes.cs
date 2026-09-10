using System;
using System.Collections.Generic;
using UnityEngine;

namespace BartenderSort.Core
{
    /// <summary>Glass types; capacities come from <see cref="BsRules.Capacity"/>.</summary>
    public enum GlassType
    {
        Shot = 0,      // 1 unit: shot glass
        Kadeh = 1,     // 2 units: cocktail glass
        Latte = 2,     // 3 units: mug
        Tumbler = 3,   // 4 units: tall glass
        Bira = 4,      // 5 units: barrel glass
    }

    /// <summary>Order card type.</summary>
    public enum OrderKind
    {
        /// <summary>Easy order: the right colours in any layer order.</summary>
        Set = 0,
        /// <summary>Hard order: an exact bottom-to-top sequence.</summary>
        Layer = 1,
    }

    /// <summary>
    /// One fixed-volume liquid unit. <paramref name="Hidden"/> hides its colour until it reaches the top,
    /// from level 10 onward.
    /// </summary>
    [Serializable]
    public struct Layer : IEquatable<Layer>
    {
        public int Color;
        public bool Hidden;

        /// <summary>
        /// Deliveries needed to unlock this layer; zero means unlocked. Liquid can pour onto it, but it
        /// cannot pour out yet.
        /// </summary>
        public int LockUntil;

        public Layer(int color, bool hidden = false, int lockUntil = 0)
        {
            Color = color;
            Hidden = hidden;
            LockUntil = lockUntil;
        }

        public bool IsLocked(int delivered) => LockUntil > 0 && delivered < LockUntil;

        public bool Equals(Layer other) =>
            Color == other.Color && Hidden == other.Hidden && LockUntil == other.LockUntil;
        public override bool Equals(object obj) => obj is Layer l && Equals(l);
        public override int GetHashCode() => (Color * 397) ^ (Hidden ? 1 : 0) ^ (LockUntil << 8);
        public override string ToString() =>
            (Hidden ? "?" : Color.ToString()) + (LockUntil > 0 ? "L" + LockUntil : "");
    }

    /// <summary>Tunable rule values.</summary>
    public static class BsRules
    {
        /// <summary>Capacity for each <see cref="GlassType"/>, in enum order.</summary>
        public static readonly int[] CapacityTable = { 1, 2, 3, 4, 5 };

        public static int Capacity(GlassType t)
        {
            int i = (int)t;
            return i >= 0 && i < CapacityTable.Length ? CapacityTable[i] : 1;
        }

        /// <summary>
        /// Player-facing names follow the glass art. Keep enum names unchanged because level assets and
        /// filenames use them as saved keys.
        /// </summary>
        public static readonly string[] GlassDisplayName =
        {
            "Shot", "Coupe", "Mug", "Highball", "Barrel Glass"
        };

        public static string DisplayName(GlassType t)
        {
            int i = (int)t;
            return i >= 0 && i < GlassDisplayName.Length ? GlassDisplayName[i] : t.ToString();
        }

        public static IEnumerable<GlassType> AllGlassTypes
        {
            get
            {
                yield return GlassType.Shot;
                yield return GlassType.Kadeh;
                yield return GlassType.Latte;
                yield return GlassType.Tumbler;
                yield return GlassType.Bira;
            }
        }
    }
}
