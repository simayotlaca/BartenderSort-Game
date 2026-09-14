using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace BartenderSort.Core
{
    [Serializable]
    public class GlassDef
    {
        public GlassType Type = GlassType.Tumbler;
        public List<Layer> Layers = new List<Layer>();

        public int UnlockAfter;

        public int Capacity => BsRules.Capacity(Type);
    }

    [Serializable]
    public class OrderDef
    {
        [NonSerialized] public int RuntimeOrderIndex = -1;

        public OrderKind Kind = OrderKind.Set;
        public GlassType Glass = GlassType.Kadeh;
        public List<int> Contents = new List<int>();

        public float TimeLimit = 0f;

        public int Capacity => BsRules.Capacity(Glass);

        public OrderDef Clone()
        {
            return new OrderDef
            {
                Kind = Kind,
                Glass = Glass,
                Contents = new List<int>(Contents),
                TimeLimit = TimeLimit,
                RuntimeOrderIndex = RuntimeOrderIndex
            };
        }

        public string Describe(BsPalette pal)
        {
            var sb = new StringBuilder();
            sb.Append(BsRules.DisplayName(Glass)).Append(": ");
            if (Kind == OrderKind.Set)
            {
                var counts = new Dictionary<int, int>();
                foreach (var c in Contents)
                    counts[c] = counts.TryGetValue(c, out var v) ? v + 1 : 1;
                bool first = true;
                foreach (var kv in counts)
                {
                    if (!first) sb.Append(" + ");
                    sb.Append(kv.Value).Append("x ").Append(pal ? pal.NameAt(kv.Key) : kv.Key.ToString());
                    first = false;
                }
            }
            else
            {
                for (int i = 0; i < Contents.Count; i++)
                {
                    if (i > 0) sb.Append(" > ");
                    sb.Append(pal ? pal.NameAt(Contents[i]) : Contents[i].ToString());
                }
            }
            return sb.ToString();
        }
    }

    [CreateAssetMenu(menuName = "Bartender Sort/Level", fileName = "Level_000")]
    public class BsLevel : ScriptableObject
    {
        [Header("Kimlik")]
        public int Index = 1;
        public string DisplayName = "";

        [Header("Board")]
        public List<GlassDef> Glasses = new List<GlassDef>();
        public int ColumnsPerRow = 4;

        [Header("Order deck")]
        public List<OrderDef> Orders = new List<OrderDef>();
        public int OrderSlots = 3;

        [Header("Level settings")]
        public bool AllowHiddenColors = false;   // L7+
        public bool AllowTimedOrders = false;

        [Header("Booster stock")]
        public int UndoCount = 99;
        public int ExtraGlassCount = 99;
        public int TimeBoostCount = 99;
        public int ShuffleCount = 99;

        [Header("Validation status")]
        public bool ValidatedSolvable = false;
        public string ValidationNote = "";

        public Dictionary<int, int> BoardColorTotals()
        {
            var d = new Dictionary<int, int>();
            foreach (var g in Glasses)
                foreach (var l in g.Layers)
                    d[l.Color] = d.TryGetValue(l.Color, out var v) ? v + 1 : 1;
            return d;
        }

        public Dictionary<int, int> OrderColorTotals()
        {
            var d = new Dictionary<int, int>();
            foreach (var o in Orders)
                foreach (var c in o.Contents)
                    d[c] = d.TryGetValue(c, out var v) ? v + 1 : 1;
            return d;
        }

        public int TotalBoardUnits()
        {
            int n = 0;
            foreach (var g in Glasses) n += g.Layers.Count;
            return n;
        }

        public int TotalOrderUnits()
        {
            int n = 0;
            foreach (var o in Orders) n += o.Contents.Count;
            return n;
        }
    }
}
