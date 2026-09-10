using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace BartenderSort.Core
{
    /// <summary>Runtime glass with layers ordered bottom to top.</summary>
    public class RtGlass
    {
        public GlassType Type;
        public List<Layer> Layers = new List<Layer>();
        /// <summary>Stable scene id for view bindings and Undo.</summary>
        public int Id;
        /// <summary>Locks the glass until this many orders are delivered.</summary>
        public int UnlockAfter;

        public int Capacity => BsRules.Capacity(Type);
        public int Free => Capacity - Layers.Count;
        public bool IsEmpty => Layers.Count == 0;

        /// <summary>True while the chain is locked.</summary>
        public bool IsChained(int delivered) => UnlockAfter > 0 && delivered < UnlockAfter;

        /// <summary>Deliveries left before the chain unlocks.</summary>
        public int ChainRemaining(int delivered) => Math.Max(0, UnlockAfter - delivered);

        public RtGlass Clone() => new RtGlass
        { Type = Type, Id = Id, UnlockAfter = UnlockAfter, Layers = new List<Layer>(Layers) };

        /// <summary>
        /// Counts matching layers from the top. A locked layer stops the run; a locked top cannot pour.
        /// </summary>
        public int TopChainLength(int delivered)
        {
            if (Layers.Count == 0) return 0;
            int top = Layers.Count - 1;
            if (Layers[top].IsLocked(delivered)) return 0;   // Locked layers cannot pour.
            int color = Layers[top].Color;
            int n = 1;
            for (int i = top - 1; i >= 0; i--)
            {
                // Hidden layers stop the run until their colour is revealed.
                if (Layers[i].Hidden || Layers[i].Color != color) break;
                if (Layers[i].IsLocked(delivered)) break;    // Locked layers stop the run.
                n++;
            }
            return n;
        }

        public bool HasLocked(int delivered)
        {
            for (int i = 0; i < Layers.Count; i++)
                if (Layers[i].IsLocked(delivered)) return true;
            return false;
        }

        public bool HasHidden()
        {
            for (int i = 0; i < Layers.Count; i++)
                if (Layers[i].Hidden) return true;
            return false;
        }

        /// <summary>Reveals a hidden layer when it reaches the top.</summary>
        public bool RevealTop()
        {
            if (Layers.Count == 0) return false;
            int top = Layers.Count - 1;
            if (!Layers[top].Hidden) return false;
            var l = Layers[top];
            l.Hidden = false;
            Layers[top] = l;
            return true;
        }
    }

    public struct PourResult
    {
        public bool Success;
        public int Amount;
        public string Reason;

        public static PourResult Fail(string reason) => new PourResult { Success = false, Reason = reason };
    }

    /// <summary>Serializable glass state used by the durable active-round snapshot.</summary>
    [Serializable]
    public sealed class BsGlassSnapshot
    {
        public GlassType Type;
        public List<Layer> Layers = new List<Layer>();
        public int Id;
        public int UnlockAfter;
    }

    /// <summary>
    /// Saved rule state. Slots keep deck indices so restore can reconnect the exact authored orders.
    /// </summary>
    [Serializable]
    public sealed class BsBoardSnapshot
    {
        public const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        public List<BsGlassSnapshot> Glasses = new List<BsGlassSnapshot>();
        public int[] SlotOrderIndices = Array.Empty<int>();
        public int DeckIndex;
        public int Delivered;
        public int NextGlassId;
        public int TotalOrders;
        public bool TimedOrdersEnabled;
    }

    /// <summary>Shared game rules for gameplay and the Editor solver, with no visual dependencies.</summary>
    public class BsBoard
    {
        sealed class OrderReferenceComparer : IEqualityComparer<OrderDef>
        {
            public static readonly OrderReferenceComparer Instance = new OrderReferenceComparer();

            OrderReferenceComparer() { }

            public bool Equals(OrderDef x, OrderDef y) => ReferenceEquals(x, y);
            public int GetHashCode(OrderDef obj) => RuntimeHelpers.GetHashCode(obj);
        }

        public List<RtGlass> Glasses = new List<RtGlass>();
        /// <summary>Open order slots; null means empty.</summary>
        public OrderDef[] Slots;
        /// <summary>Index of the next order in the deck.</summary>
        public int DeckIndex;
        public int Delivered;
        /// <summary>
        /// TimeLimit alone does not enable timers. The copied level feature flag controls deadline creation.
        /// </summary>
        public bool TimedOrdersEnabled { get; private set; }

        readonly List<OrderDef> _deck = new List<OrderDef>();
        int _nextGlassId;

#if UNITY_EDITOR
        internal int TotalOrders => _deck.Count;
        internal IReadOnlyList<OrderDef> Deck => _deck;
#endif

        public static BsBoard FromLevel(BsLevel level)
        {
            if (level == null) throw new ArgumentNullException(nameof(level));
            return FromDefinition(
                level.Glasses,
                level.Orders,
                level.OrderSlots,
                level.AllowTimedOrders,
                level.AllowHiddenColors);
        }

        /// <summary>
        /// Builds a board without Unity objects. Clone all input collections and elements so later authoring
        /// changes cannot alter the live board.
        /// </summary>
        public static BsBoard FromDefinition(
            IReadOnlyList<GlassDef> glasses,
            IReadOnlyList<OrderDef> orders,
            int orderSlots,
            bool timedOrdersEnabled,
            bool hiddenColorsEnabled = true)
        {
            if (glasses == null) throw new ArgumentNullException(nameof(glasses));
            if (orders == null) throw new ArgumentNullException(nameof(orders));

            var b = new BsBoard { TimedOrdersEnabled = timedOrdersEnabled };
            b.Slots = new OrderDef[Math.Max(1, orderSlots)];
            for (int glassIndex = 0; glassIndex < glasses.Count; glassIndex++)
            {
                GlassDef g = glasses[glassIndex]
                    ?? throw new ArgumentException(
                        "A glass definition cannot be null.", nameof(glasses));
                var rt = new RtGlass { Type = g.Type, Id = b._nextGlassId++, UnlockAfter = g.UnlockAfter };
                int cap = BsRules.Capacity(g.Type);
                for (int i = 0; i < g.Layers.Count && i < cap; i++)
                {
                    Layer layer = g.Layers[i];
                    // Ignore Hidden on each layer when the level disables hidden colours.
                    if (!hiddenColorsEnabled) layer.Hidden = false;
                    rt.Layers.Add(layer);
                }
                b.Glasses.Add(rt);
            }
            for (int orderIndex = 0; orderIndex < orders.Count; orderIndex++)
            {
                OrderDef source = orders[orderIndex]
                    ?? throw new ArgumentException(
                        "An order definition cannot be null.", nameof(orders));
                OrderDef order = source.Clone();
                order.RuntimeOrderIndex = orderIndex;
                b._deck.Add(order);
            }
            b.RefillSlots();
            b.RevealAllTops();
            return b;
        }

        public BsBoard Clone()
        {
            var orderClones = new Dictionary<OrderDef, OrderDef>(OrderReferenceComparer.Instance);

            OrderDef CloneOrder(OrderDef source)
            {
                if (source == null) return null;
                if (orderClones.TryGetValue(source, out OrderDef clone)) return clone;

                clone = source.Clone();
                orderClones.Add(source, clone);
                return clone;
            }

            var b = new BsBoard
            {
                Slots = new OrderDef[Slots.Length],
                DeckIndex = DeckIndex,
                Delivered = Delivered,
                TimedOrdersEnabled = TimedOrdersEnabled,
                _nextGlassId = _nextGlassId
            };
            foreach (var g in Glasses) b.Glasses.Add(g.Clone());
            foreach (var order in _deck) b._deck.Add(CloneOrder(order));
            for (int i = 0; i < Slots.Length; i++) b.Slots[i] = CloneOrder(Slots[i]);
            return b;
        }

        /// <summary>Captures every mutable rule field without sharing live collections.</summary>
        public BsBoardSnapshot CaptureSnapshot()
        {
            var snapshot = new BsBoardSnapshot
            {
                SlotOrderIndices = new int[Slots?.Length ?? 0],
                DeckIndex = DeckIndex,
                Delivered = Delivered,
                NextGlassId = _nextGlassId,
                TotalOrders = _deck.Count,
                TimedOrdersEnabled = TimedOrdersEnabled,
            };

            for (int i = 0; i < snapshot.SlotOrderIndices.Length; i++)
                snapshot.SlotOrderIndices[i] = Slots[i]?.RuntimeOrderIndex ?? -1;
            for (int i = 0; i < Glasses.Count; i++)
            {
                RtGlass glass = Glasses[i];
                snapshot.Glasses.Add(new BsGlassSnapshot
                {
                    Type = glass.Type,
                    Id = glass.Id,
                    UnlockAfter = glass.UnlockAfter,
                    Layers = new List<Layer>(glass.Layers),
                });
            }
            return snapshot;
        }

        /// <summary>
        /// Restores the snapshot using the current deck's order objects. Timer ownership depends on those exact
        /// references.
        /// </summary>
        public static bool TryRestore(BsLevel level, BsBoardSnapshot snapshot,
                                      out BsBoard restored, out string error)
        {
            restored = null;
            error = null;
            if (level == null || snapshot == null
                || snapshot.Version != BsBoardSnapshot.CurrentVersion)
            {
                error = "The board snapshot version is invalid";
                return false;
            }

            BsBoard candidate;
            try { candidate = FromLevel(level); }
            catch (Exception exception)
            {
                error = "The authored board could not be rebuilt: " + exception.Message;
                return false;
            }

            if (snapshot.Glasses == null || snapshot.SlotOrderIndices == null
                || snapshot.SlotOrderIndices.Length != candidate.Slots.Length
                || snapshot.TotalOrders != candidate._deck.Count
                || snapshot.TimedOrdersEnabled != candidate.TimedOrdersEnabled)
            {
                error = "The board snapshot no longer matches the level";
                return false;
            }
            if (snapshot.DeckIndex < 0 || snapshot.DeckIndex > candidate._deck.Count
                || snapshot.Delivered < 0 || snapshot.Delivered > snapshot.DeckIndex
                || snapshot.NextGlassId < 0)
            {
                error = "The board counters are invalid";
                return false;
            }

            var usedOrderIndices = new HashSet<int>();
            int occupiedSlots = 0;
            for (int slot = 0; slot < snapshot.SlotOrderIndices.Length; slot++)
            {
                int orderIndex = snapshot.SlotOrderIndices[slot];
                if (orderIndex == -1) continue;
                if (orderIndex < -1)
                {
                    error = "The board order slots are invalid";
                    return false;
                }
                if (orderIndex >= snapshot.DeckIndex
                    || !usedOrderIndices.Add(orderIndex))
                {
                    error = "The board order slots are invalid";
                    return false;
                }
                occupiedSlots++;
            }
            if (snapshot.Delivered != snapshot.DeckIndex - occupiedSlots)
            {
                error = "The delivered-order count is inconsistent";
                return false;
            }

            var usedGlassIds = new HashSet<int>();
            int greatestGlassId = -1;
            for (int i = 0; i < snapshot.Glasses.Count; i++)
            {
                BsGlassSnapshot glass = snapshot.Glasses[i];
                int typeIndex = glass == null ? -1 : (int)glass.Type;
                if (glass == null || glass.Layers == null || glass.Id < 0
                    || glass.UnlockAfter < 0 || !usedGlassIds.Add(glass.Id)
                    || typeIndex < 0 || typeIndex >= BsRules.CapacityTable.Length
                    || glass.Layers.Count > BsRules.Capacity(glass.Type))
                {
                    error = "The board glass state is invalid";
                    return false;
                }
                for (int layerIndex = 0; layerIndex < glass.Layers.Count; layerIndex++)
                {
                    Layer layer = glass.Layers[layerIndex];
                    if (layer.Color < 0 || layer.LockUntil < 0)
                    {
                        error = "The board layer state is invalid";
                        return false;
                    }
                }
                greatestGlassId = Math.Max(greatestGlassId, glass.Id);
            }
            if (snapshot.NextGlassId <= greatestGlassId
                || snapshot.NextGlassId < candidate._nextGlassId
                || snapshot.NextGlassId == int.MaxValue)
            {
                error = "The next glass identifier is invalid";
                return false;
            }

            candidate.Glasses.Clear();
            for (int i = 0; i < snapshot.Glasses.Count; i++)
            {
                BsGlassSnapshot saved = snapshot.Glasses[i];
                candidate.Glasses.Add(new RtGlass
                {
                    Type = saved.Type,
                    Id = saved.Id,
                    UnlockAfter = saved.UnlockAfter,
                    Layers = new List<Layer>(saved.Layers),
                });
            }
            candidate.DeckIndex = snapshot.DeckIndex;
            candidate.Delivered = snapshot.Delivered;
            candidate._nextGlassId = snapshot.NextGlassId;
            for (int slot = 0; slot < candidate.Slots.Length; slot++)
            {
                int orderIndex = snapshot.SlotOrderIndices[slot];
                candidate.Slots[slot] = orderIndex < 0
                    ? null
                    : candidate._deck[orderIndex];
            }

            restored = candidate;
            return true;
        }

        public void RevealAllTops()
        {
            foreach (var g in Glasses) g.RevealTop();
        }

        /// <summary>
        /// Packs cards left and adds new orders on the right. The available order set stays the same, and slot
        /// order gives a stable state key.
        /// </summary>
        public void RefillSlots()
        {
            int write = 0;
            for (int i = 0; i < Slots.Length; i++)
            {
                if (Slots[i] == null) continue;
                OrderDef card = Slots[i];
                Slots[i] = null;
                Slots[write++] = card;
            }
            for (int i = write; i < Slots.Length; i++)
            {
                if (DeckIndex >= _deck.Count) break;
                Slots[i] = _deck[DeckIndex++];
            }
        }

        public RtGlass GlassById(int id)
        {
            foreach (var g in Glasses) if (g.Id == id) return g;
            return null;
        }

        /// <summary>
        /// Checks whether the shuffle booster can change this glass. Every input adapter uses this same domain
        /// rule.
        /// </summary>
        public bool IsShuffleTarget(RtGlass glass)
        {
            if (glass == null || glass.Layers == null || glass.Layers.Count < 2)
                return false;
            if (glass.IsChained(Delivered) || glass.HasLocked(Delivered)
                || glass.HasHidden())
                return false;
            if (MatchedSlot(glass) >= 0) return false;

            int firstColor = glass.Layers[0].Color;
            for (int i = 1; i < glass.Layers.Count; i++)
                if (glass.Layers[i].Color != firstColor)
                    return HasLegalShufflePermutation(glass);
            return false;
        }

        /// <summary>
        /// Offers glasses only if a shuffle changes visible colour order and avoids failure. Check at most 120
        /// permutations and stop at the first valid one.
        /// </summary>
        private bool HasLegalShufflePermutation(RtGlass target)
        {
            int count = target.Layers.Count;
            var before = new Layer[count];
            target.Layers.CopyTo(before);
            var working = (Layer[])before.Clone();
            try
            {
                return HasLegalShufflePermutation(target, before, working, 0);
            }
            finally
            {
                WriteShuffleLayers(target, before);
            }
        }

        private bool HasLegalShufflePermutation(RtGlass target, Layer[] before,
                                                Layer[] working, int index)
        {
            if (index >= working.Length)
            {
                if (ShuffleLayerColorsEqual(before, working)) return false;
                WriteShuffleLayers(target, working);
                return !IsFail();
            }

            for (int candidate = index; candidate < working.Length; candidate++)
            {
                bool duplicate = false;
                for (int seen = index; seen < candidate; seen++)
                {
                    // Changing only same-colour metadata does not count as a visible paid shuffle.
                    if (working[seen].Color != working[candidate].Color) continue;
                    duplicate = true;
                    break;
                }
                if (duplicate) continue;

                SwapShuffleLayers(working, index, candidate);
                bool legal = HasLegalShufflePermutation(target, before, working, index + 1);
                SwapShuffleLayers(working, index, candidate);
                if (legal) return true;
            }
            return false;
        }

        private static void WriteShuffleLayers(RtGlass target, Layer[] layers)
        {
            for (int i = 0; i < layers.Length; i++) target.Layers[i] = layers[i];
        }

        private static bool ShuffleLayerColorsEqual(Layer[] first, Layer[] second)
        {
            if (first == null || second == null || first.Length != second.Length)
                return false;
            for (int i = 0; i < first.Length; i++)
                if (first[i].Color != second[i].Color) return false;
            return true;
        }

        private static void SwapShuffleLayers(Layer[] layers, int first, int second)
        {
            if (first == second) return;
            Layer value = layers[first];
            layers[first] = layers[second];
            layers[second] = value;
        }

        /// <summary>True if at least one glass can be shuffled.</summary>
        public bool HasShuffleTarget()
        {
            for (int i = 0; i < Glasses.Count; i++)
                if (IsShuffleTarget(Glasses[i])) return true;
            return false;
        }

        /// <summary>
        /// Clears and fills the caller's buffer with valid target ids so UI does not duplicate the shuffle
        /// rules.
        /// </summary>
        public int CollectShuffleTargetIds(List<int> destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            for (int i = 0; i < Glasses.Count; i++)
            {
                RtGlass glass = Glasses[i];
                if (IsShuffleTarget(glass)) destination.Add(glass.Id);
            }
            return destination.Count;
        }

        /// <summary>
        /// Pours the top run of matching colours as far as space allows. Any colour can go onto any other, even
        /// in a completed glass.
        /// </summary>
        public PourResult CanPour(RtGlass src, RtGlass dst)
        {
            if (src == null || dst == null) return PourResult.Fail("Invalid glass");
            if (ReferenceEquals(src, dst)) return PourResult.Fail("Source and target are the same");
            if (src.IsChained(Delivered))
                return PourResult.Fail($"Glass is chained — {src.ChainRemaining(Delivered)} deliveries remain");
            if (dst.IsChained(Delivered))
                return PourResult.Fail($"Target is chained — {dst.ChainRemaining(Delivered)} deliveries remain");
            if (src.IsEmpty) return PourResult.Fail("Source is empty");
            if (dst.Free <= 0) return PourResult.Fail("Target has no space");
            // Completed glasses can still pour in the domain. Input may treat a tap as delivery, but other
            // adapters use the same free-pour rule.

            int chain = src.TopChainLength(Delivered);
            if (chain <= 0)
                return src.Layers[src.Layers.Count - 1].IsLocked(Delivered)
                    ? PourResult.Fail("The top layer is locked")
                    : PourResult.Fail("There is no layer to pour");

            int amount = Math.Min(chain, dst.Free);
            return new PourResult
            {
                Success = true,
                Amount = amount
            };
        }

        public PourResult Pour(RtGlass src, RtGlass dst)
        {
            var r = CanPour(src, dst);
            if (!r.Success) return r;

            for (int i = 0; i < r.Amount; i++)
            {
                var l = src.Layers[src.Layers.Count - 1];
                src.Layers.RemoveAt(src.Layers.Count - 1);
                dst.Layers.Add(new Layer(l.Color, false));
            }
            src.RevealTop();   // Reveal the hidden colour.
            return r;
        }

        public bool AnyPourAvailable()
        {
            for (int i = 0; i < Glasses.Count; i++)
                for (int j = 0; j < Glasses.Count; j++)
                {
                    if (i == j) continue;
                    if (CanPour(Glasses[i], Glasses[j]).Success) return true;
                }
            return false;
        }

        /// <summary>Checks whether the glass contents match the order.</summary>
        public static bool Matches(RtGlass g, OrderDef o)
        {
            if (g == null) return false;
            // MatchedSlot checks locks using Delivered. Keep this path allocation-free because CanPour calls
            // it often.
            return MatchesLayers(g.Type, g.Layers, o);
        }

        /// <summary>
        /// Returns the leftmost matching slot, or -1. The buffer is thread-local because background searches
        /// also use it.
        /// </summary>
        [ThreadStatic] static List<int> _matchScratchTs;
        static List<int> MatchScratch => _matchScratchTs ?? (_matchScratchTs = new List<int>(8));

        /// <summary>
        /// Checks hypothetical layers against an order without cloning a board or allocating in the solver
        /// loop.
        /// </summary>
        public static bool MatchesLayers(GlassType type, List<Layer> layers, OrderDef o)
        {
            if (o == null || layers == null) return false;
            if (type != o.Glass) return false;
            if (layers.Count != o.Contents.Count) return false;
            for (int i = 0; i < layers.Count; i++)
                if (layers[i].Hidden) return false;

            if (o.Kind == OrderKind.Layer)
            {
                for (int i = 0; i < o.Contents.Count; i++)
                    if (layers[i].Color != o.Contents[i]) return false;
                return true;
            }

            // SET ignores order; remove matches from the small list.
            MatchScratch.Clear();
            MatchScratch.AddRange(o.Contents);
            for (int i = 0; i < layers.Count; i++)
            {
                int idx = MatchScratch.IndexOf(layers[i].Color);
                if (idx < 0) return false;
                MatchScratch.RemoveAt(idx);
            }
            return MatchScratch.Count == 0;
        }

        public int MatchedSlot(RtGlass g)
        {
            if (g == null) return -1;
            // Glass and layer locks block delivery too, even if the contents already match an order.
            if (g.IsChained(Delivered) || g.HasLocked(Delivered)) return -1;
            for (int i = 0; i < Slots.Length; i++)
                if (Slots[i] != null && Matches(g, Slots[i])) return i;
            return -1;
        }

        public bool AnyDeliverable()
        {
            foreach (var g in Glasses) if (MatchedSlot(g) >= 0) return true;
            return false;
        }

        /// <summary>
        /// Consumes the matching order, removes the glass and refills the slot if possible. Presentation hides
        /// the scene glass.
        /// </summary>
        public bool Deliver(RtGlass g, out int slotIndex)
        {
            slotIndex = MatchedSlot(g);
            if (slotIndex < 0) return false;
            Slots[slotIndex] = null;
            Glasses.Remove(g);          // Remove the glass from the board.
            Delivered++;
            RefillSlots();
            return true;
        }

        /// <summary>Win when the deck and all open slots are empty.</summary>
        public bool IsWin()
        {
            if (DeckIndex < _deck.Count) return false;
            foreach (var s in Slots) if (s != null) return false;
            return true;
        }

        /// <summary>Fail when no glass can pour or be delivered.</summary>
        public bool IsFail()
        {
            if (IsWin()) return false;
            return !AnyDeliverable() && !AnyPourAvailable();
        }

        /// <summary>Adds an empty glass to help escape a dead end.</summary>
        public RtGlass AddEmptyGlass(GlassType type)
        {
            var g = new RtGlass { Type = type, Id = _nextGlassId++ };
            Glasses.Add(g);
            return g;
        }

        /// <summary>
        /// Sorts glasses by type and contents so equivalent board arrangements share one search key.
        /// </summary>
        public string StateKey()
        {
            var parts = new List<string>(Glasses.Count);
            var sb = new StringBuilder();
            foreach (var g in Glasses)
            {
                sb.Clear();
                sb.Append((int)g.Type).Append('/').Append(g.UnlockAfter).Append(':');
                foreach (var l in g.Layers)
                {
                    // Include hidden layers' real colours in the key so identical-looking boards with
                    // different futures cannot share a cached result.
                    if (l.Hidden) sb.Append('?');
                    sb.Append(l.Color);
                    if (l.LockUntil > 0) sb.Append('L').Append(l.LockUntil);
                    sb.Append(',');
                }
                parts.Add(sb.ToString());
            }
            parts.Sort(StringComparer.Ordinal);

            sb.Clear();
            // Delivered belongs in the state because it controls locks.
            sb.Append(DeckIndex).Append('#').Append(Delivered).Append('|');
            foreach (var s in Slots)
                sb.Append(s == null ? "-" : (((int)s.Glass) + "/" + (int)s.Kind + "/" + string.Join(".", s.Contents))).Append(';');
            sb.Append('|');
            foreach (var p in parts) sb.Append(p).Append('#');
            return sb.ToString();
        }
    }
}
