using System;
using System.Collections.Generic;
using System.Globalization;
using BartenderSort.Core;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Recipe identity comes from the served drink: SET ignores layer order, LAYER keeps it. Keys include
    /// the recipe so catalogue changes preserve collected drinks.
    /// </summary>
    public static class BartenderRecipeKey
    {
        public static string From(OrderDef order)
        {
            if (order == null || !Enum.IsDefined(typeof(GlassType), order.Glass)
                || !Enum.IsDefined(typeof(OrderKind), order.Kind)
                || order.Contents == null || order.Contents.Count != order.Capacity)
                return string.Empty;
            var colors = new List<int>(order.Contents);
            if (colors.Exists(color => color < 0)) return string.Empty;
            if (order.Kind == OrderKind.Set) colors.Sort();
            return "r1:" + ((int)order.Glass).ToString(CultureInfo.InvariantCulture)
                + ":" + ((int)order.Kind).ToString(CultureInfo.InvariantCulture)
                + ":" + string.Join(",", colors.ConvertAll(
                    color => color.ToString(CultureInfo.InvariantCulture)));
        }

        public static bool TryDecode(string key, out OrderDef order)
        {
            order = null;
            if (string.IsNullOrEmpty(key) || key.Length > 128) return false;
            string[] parts = key.Split(':');
            if (parts.Length != 4 || parts[0] != "r1"
                || !TryInt(parts[1], out int glass)
                || !TryInt(parts[2], out int kind)
                || !Enum.IsDefined(typeof(GlassType), glass)
                || !Enum.IsDefined(typeof(OrderKind), kind)) return false;
            var candidate = new OrderDef { Glass = (GlassType)glass, Kind = (OrderKind)kind };
            string[] colors = parts[3].Split(',');
            if (colors.Length != candidate.Capacity) return false;
            foreach (string color in colors)
            {
                if (!TryInt(color, out int value) || value < 0) return false;
                candidate.Contents.Add(value);
            }
            if (!string.Equals(From(candidate), key, StringComparison.Ordinal)) return false;
            order = candidate;
            return true;
        }

        private static bool TryInt(string value, out int number) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>Immutable, permanent collection; daily reset never removes entries.</summary>
    public sealed class BartenderRecipeBookSnapshot
    {
        private readonly HashSet<string> unlocked;
        public IReadOnlyList<string> Keys { get; }
        public int Count => Keys.Count;

        internal BartenderRecipeBookSnapshot(List<string> keys)
        {
            string[] copy = keys?.ToArray() ?? Array.Empty<string>();
            Keys = Array.AsReadOnly(copy);
            unlocked = new HashSet<string>(copy, StringComparer.Ordinal);
        }

        public bool IsUnlocked(string key) => key != null && unlocked.Contains(key);
    }

    public sealed class BartenderRecipeBookEntry
    {
        public string Key { get; }
        public OrderDef Recipe => recipe.Clone();
        public int FirstLevel { get; }
        public int CampaignSlot { get; }
        public bool IsUnlocked { get; }
        private readonly OrderDef recipe;

        internal BartenderRecipeBookEntry(string key, OrderDef order, int firstLevel, bool unlocked,
            int campaignSlot = -1)
        {
            Key = key;
            recipe = order.Clone();
            FirstLevel = firstLevel;
            CampaignSlot = campaignSlot;
            IsUnlocked = unlocked;
        }
    }
}
