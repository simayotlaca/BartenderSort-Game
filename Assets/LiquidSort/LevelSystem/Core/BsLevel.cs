using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace BartenderSort.Core
{
    /// <summary>Starting glass type and liquid layers, from bottom to top.</summary>
    [Serializable]
    public class GlassDef
    {
        public GlassType Type = GlassType.Tumbler;
        /// <summary>Bottom to top; length must fit the glass capacity.</summary>
        public List<Layer> Layers = new List<Layer>();

        /// <summary>
        /// Blocks pouring, filling and delivery until this many orders are served. Zero means no chain.
        /// </summary>
        public int UnlockAfter;

        public int Capacity => BsRules.Capacity(Type);
    }

    /// <summary>
    /// Order glass type and recipe. SET allows any colour order; LAYER requires the exact bottom-to-top
    /// sequence.
    /// </summary>
    [Serializable]
    public class OrderDef
    {
        /// <summary>
        /// Assigns a stable deck index when building the board. It survives clone/Undo so time bonuses find the
        /// same order, but is not serialized.
        /// </summary>
        [NonSerialized] public int RuntimeOrderIndex = -1;

        public OrderKind Kind = OrderKind.Set;
        public GlassType Glass = GlassType.Kadeh;
        /// <summary>SET ignores order; LAYER runs bottom to top.</summary>
        public List<int> Contents = new List<int>();

        /// <summary>Timed order from level 15 onward; zero means no timer.</summary>
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
                // Count colours and show each amount.
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

    /// <summary>Level data: glass types, starting liquids and order deck.</summary>
    [CreateAssetMenu(menuName = "Bartender Sort/Level", fileName = "Level_000")]
    public class BsLevel : ScriptableObject
    {
        [Header("Kimlik")]
        public int Index = 1;
        public string DisplayName = "";

        [Header("Board")]
        public List<GlassDef> Glasses = new List<GlassDef>();
        /// <summary>Number of board columns for the view.</summary>
        public int ColumnsPerRow = 4;

        [Header("Order deck")]
        public List<OrderDef> Orders = new List<OrderDef>();
        /// <summary>Number of open order slots; default is three.</summary>
        public int OrderSlots = 3;

        [Header("Level settings")]
        public bool AllowHiddenColors = false;   // L7+
        /// <summary>Enables timers when TimeLimit is above zero; used from level 15 onward.</summary>
        public bool AllowTimedOrders = false;

        [Header("Booster stock")]
        public int UndoCount = 99;
        public int ExtraGlassCount = 99;
        public int TimeBoostCount = 99;
        public int ShuffleCount = 99;

        [Header("Validation status")]
        public bool ValidatedSolvable = false;
        public string ValidationNote = "";

        /// <summary>Total liquid units on the board, by colour.</summary>
        public Dictionary<int, int> BoardColorTotals()
        {
            var d = new Dictionary<int, int>();
            foreach (var g in Glasses)
                foreach (var l in g.Layers)
                    d[l.Color] = d.TryGetValue(l.Color, out var v) ? v + 1 : 1;
            return d;
        }

        /// <summary>Total liquid required by the deck, by colour.</summary>
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
