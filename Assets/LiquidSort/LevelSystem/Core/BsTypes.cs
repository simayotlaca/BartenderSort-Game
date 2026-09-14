using System;
using System.Collections.Generic;
using UnityEngine;

namespace BartenderSort.Core
{
    public enum GlassType
    {
        Shot = 0,      // 1 unit: shot glass
        Kadeh = 1,     // 2 units: cocktail glass
        Latte = 2,     // 3 units: mug
        Tumbler = 3,   // 4 units: tall glass
        Bira = 4,      // 5 units: barrel glass
    }

    public enum OrderKind
    {
        Set = 0,
        Layer = 1,
    }

    [Serializable]
    public struct Layer : IEquatable<Layer>
    {
        public int Color;
        public bool Hidden;

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

    public static class BsRules
    {
        public static readonly int[] CapacityTable = { 1, 2, 3, 4, 5 };

        public static int Capacity(GlassType t)
        {
            int i = (int)t;
            return i >= 0 && i < CapacityTable.Length ? CapacityTable[i] : 1;
        }

        public static readonly string[] GlassDisplayName =
        {
            "Shot", "Coupe", "Mug", "Highball", "Barrel Glass"
        };

        public static string DisplayName(GlassType t)
        {
            int i = (int)t;
            return i >= 0 && i < GlassDisplayName.Length ? GlassDisplayName[i] : t.ToString();
        }
    }
}
