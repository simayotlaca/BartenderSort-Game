using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace BartenderSort.Core
{
    public struct BsMove
    {
        public bool IsDeliver;
        public int From;   // glass id
        public int To;     // Target glass id for pouring.

        public override string ToString() =>
            IsDeliver ? $"DELIVER #{From}" : $"POUR #{From} → #{To}";
    }

    public enum SolveOutcome
    {
        /// <summary>A solution was found.</summary>
        Solvable,
        /// <summary>Search finished with no solution.</summary>
        Unsolvable,
        /// <summary>Hit the node or time limit; the result is unknown.</summary>
        Inconclusive,
    }

    public class SolveResult
    {
        public SolveOutcome Outcome;
        public List<BsMove> Solution = new List<BsMove>();
        public int NodesVisited;
        public long ElapsedMs;

        public string Summary()
        {
            switch (Outcome)
            {
                case SolveOutcome.Solvable:
                    return $"SOLVABLE — {Solution.Count} moves, {NodesVisited} nodes, {ElapsedMs} ms";
                case SolveOutcome.Unsolvable:
                    return $"UNSOLVABLE — search complete, {NodesVisited} nodes, {ElapsedMs} ms";
                default:
                    return $"INCONCLUSIVE — search limit reached ({NodesVisited} nodes, {ElapsedMs} ms). Raise the limit or simplify the level.";
            }
        }
    }

    /// <summary>Checks whether a level is solvable using the free-pour rules.</summary>
    public static class BsSolver
    {
        public const int DefaultNodeBudget = 250_000;

        /// <summary>The depth limit only prevents stack overflow. Hitting it means unknown, not unsolvable.</summary>
        public const int DefaultMaxDepth = 2000;

        /// <summary>Zero means no time limit.</summary>
        public const int DefaultMaxMs = 0;

        public static SolveResult Solve(BsLevel level, int nodeBudget = DefaultNodeBudget,
                                        int maxDepth = DefaultMaxDepth, int maxMs = DefaultMaxMs)
        {
            var board = BsBoard.FromLevel(level);
            return Solve(board, nodeBudget, maxDepth, maxMs);
        }

        /// <summary>
        /// maxMs limits elapsed time; zero is unlimited. Hitting it returns unknown, since node count alone
        /// cannot keep gameplay within a frame budget.
        /// </summary>
        public static SolveResult Solve(BsBoard start, int nodeBudget = DefaultNodeBudget,
                                        int maxDepth = DefaultMaxDepth, int maxMs = DefaultMaxMs)
        {
            var res = new SolveResult();
            var sw = Stopwatch.StartNew();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var path = new List<BsMove>();
            int nodes = 0;
            bool budgetHit = false;
            bool depthHit = false;
            var deadline = new Deadline(sw, maxMs);

            bool found = Dfs(start, visited, path, ref nodes, nodeBudget, 0, maxDepth,
                             ref budgetHit, ref depthHit, deadline);

            sw.Stop();
            res.NodesVisited = nodes;
            res.ElapsedMs = sw.ElapsedMilliseconds;
            if (found)
            {
                res.Outcome = SolveOutcome.Solvable;
                res.Solution = new List<BsMove>(path);
            }
            else
            {
                // Only a complete search can prove no solution. Node or depth limits leave the result unknown.
                res.Outcome = (budgetHit || depthHit || deadline.Expired) ? SolveOutcome.Inconclusive
                                                                          : SolveOutcome.Unsolvable;
            }
            return res;
        }

        /// <summary>
        /// Starts a search the caller advances across frames. Clone the board now so later gameplay cannot
        /// change this search.
        /// </summary>
        public static IncrementalSearch BeginIncremental(
            BsBoard start, int nodeBudget = DefaultNodeBudget,
            int maxDepth = DefaultMaxDepth, int maxWorkMs = DefaultMaxMs)
        {
            if (start == null) throw new ArgumentNullException(nameof(start));
            return new IncrementalSearch(
                start.Clone(), nodeBudget, maxDepth, maxWorkMs, null);
        }

        /// <summary>Reuse the root key the caller already made for its cache lookup.</summary>
        internal static IncrementalSearch BeginIncremental(
            BsBoard start, int nodeBudget, int maxDepth, int maxWorkMs,
            string knownRootStateKey)
        {
            if (start == null) throw new ArgumentNullException(nameof(start));
            if (knownRootStateKey == null)
                throw new ArgumentNullException(nameof(knownRootStateKey));
            return new IncrementalSearch(
                start.Clone(), nodeBudget, maxDepth, maxWorkMs,
                knownRootStateKey);
        }

        /// <summary>
        /// Runs DFS with an explicit stack over one fixed board snapshot. It can pause across frames or cancel;
        /// ordering and unknown results match the synchronous solver.
        /// </summary>
        public sealed class IncrementalSearch
        {
            enum SearchPhase
            {
                Enter,
                Deliver,
                Pour,
            }

            struct SearchFrame
            {
                public BsBoard Board;
                public int Depth;
                public SearchPhase Phase;
                public int SourceIndex;
                public int TargetIndex;
                public bool HasIncomingMove;
                public string KnownStateKey;
            }

            readonly HashSet<string> _visited =
                new HashSet<string>(StringComparer.Ordinal);
            readonly List<BsMove> _path = new List<BsMove>();
            readonly List<SearchFrame> _stack = new List<SearchFrame>();
            readonly int _nodeBudget;
            readonly int _maxDepth;
            readonly int _maxWorkMs;

            int _nodes;
            long _elapsedTicks;
            bool _budgetHit;
            bool _depthHit;
            bool _cancelled;
            SolveResult _result;

            internal IncrementalSearch(BsBoard start, int nodeBudget,
                                       int maxDepth, int maxWorkMs,
                                       string knownRootStateKey)
            {
                _nodeBudget = nodeBudget;
                _maxDepth = maxDepth;
                _maxWorkMs = maxWorkMs;
                _stack.Add(new SearchFrame
                {
                    Board = start,
                    Depth = 0,
                    Phase = SearchPhase.Enter,
                    HasIncomingMove = false,
                    KnownStateKey = knownRootStateKey,
                });
            }

            public bool IsComplete => _result != null;
            public bool IsCancelled => _cancelled;
            public bool IsFinished => IsComplete || IsCancelled;
            public SolveResult Result => _result;
            public int NodesVisited => _nodes;

            /// <summary>
            /// Runs roughly maxSliceMs of search work, checking between key and clone operations. Returns true
            /// when finished or cancelled.
            /// </summary>
            public bool Step(int maxSliceMs = 1)
            {
                return StepCore(Math.Max(1, maxSliceMs), int.MaxValue);
            }

            /// <summary>
            /// Cancels a stale search and releases its clones and keys. A completed answer stays unchanged.
            /// </summary>
            public void Cancel()
            {
                if (IsComplete || _cancelled) return;
                _cancelled = true;
                ReleaseSearchState();
            }

            bool StepCore(int maxSliceMs, int maxOperations)
            {
                if (IsFinished) return true;

                var slice = Stopwatch.StartNew();
                int operations = 0;
                while (!IsFinished)
                {
                    // An empty stack proves the search finished, even if its last operation crossed the time
                    // limit.
                    if (_stack.Count == 0)
                    {
                        SolveOutcome exhausted = _budgetHit || _depthHit
                            ? SolveOutcome.Inconclusive
                            : SolveOutcome.Unsolvable;
                        return Finish(exhausted, slice);
                    }

                    if (operations > 0
                        && ((maxSliceMs > 0
                             && slice.ElapsedMilliseconds >= maxSliceMs)
                            || operations >= maxOperations))
                        break;

                    SolveOutcome? outcome = AdvanceOneOperation(
                        _elapsedTicks + slice.ElapsedTicks);
                    operations++;
                    if (outcome.HasValue) return Finish(outcome.Value, slice);
                }

                slice.Stop();
                _elapsedTicks += slice.ElapsedTicks;
                return IsFinished;
            }

            SolveOutcome? AdvanceOneOperation(long activeTicks)
            {
                int frameIndex = _stack.Count - 1;
                SearchFrame frame = _stack[frameIndex];
                switch (frame.Phase)
                {
                    case SearchPhase.Enter:
                        if (frame.Board.IsWin()) return SolveOutcome.Solvable;
                        if (frame.Depth >= _maxDepth)
                        {
                            _depthHit = true;
                            PopFrame();
                            return null;
                        }
                        if (_nodes >= _nodeBudget)
                        {
                            _budgetHit = true;
                            return SolveOutcome.Inconclusive;
                        }
                        // Check wins, depth and node limits before time, matching recursive DFS. A winning
                        // child must not become unknown at a frame boundary.
                        if (_maxWorkMs > 0
                            && Milliseconds(activeTicks) >= _maxWorkMs)
                            return SolveOutcome.Inconclusive;

                        _nodes++;
                        string stateKey = frame.KnownStateKey
                                       ?? frame.Board.StateKey();
                        if (!_visited.Add(stateKey))
                        {
                            PopFrame();
                            return null;
                        }

                        frame.Phase = SearchPhase.Deliver;
                        frame.SourceIndex = 0;
                        _stack[frameIndex] = frame;
                        return null;

                    case SearchPhase.Deliver:
                        return AdvanceDelivery(frameIndex, frame);

                    default:
                        return AdvancePour(frameIndex, frame);
                }
            }

            SolveOutcome? AdvanceDelivery(int frameIndex, SearchFrame frame)
            {
                if (frame.SourceIndex >= frame.Board.Glasses.Count)
                {
                    frame.Phase = SearchPhase.Pour;
                    frame.SourceIndex = 0;
                    frame.TargetIndex = 0;
                    _stack[frameIndex] = frame;
                    return null;
                }

                int sourceIndex = frame.SourceIndex++;
                _stack[frameIndex] = frame;
                RtGlass glass = frame.Board.Glasses[sourceIndex];
                if (frame.Board.MatchedSlot(glass) < 0) return null;

                BsBoard next = frame.Board.Clone();
                RtGlass nextGlass = next.GlassById(glass.Id);
                if (!next.Deliver(nextGlass, out _)) return null;

                PushFrame(next, frame.Depth + 1, new BsMove
                {
                    IsDeliver = true,
                    From = glass.Id,
                });
                return null;
            }

            SolveOutcome? AdvancePour(int frameIndex, SearchFrame frame)
            {
                int count = frame.Board.Glasses.Count;
                if (frame.SourceIndex >= count)
                {
                    PopFrame();
                    return null;
                }
                if (frame.TargetIndex >= count)
                {
                    frame.SourceIndex++;
                    frame.TargetIndex = 0;
                    _stack[frameIndex] = frame;
                    return null;
                }

                int sourceIndex = frame.SourceIndex;
                RtGlass source = frame.Board.Glasses[sourceIndex];
                if (source.IsEmpty)
                {
                    // Skip all targets for empty sources, matching recursive DFS.
                    frame.SourceIndex++;
                    frame.TargetIndex = 0;
                    _stack[frameIndex] = frame;
                    return null;
                }

                int targetIndex = frame.TargetIndex++;
                _stack[frameIndex] = frame;
                if (sourceIndex == targetIndex) return null;

                RtGlass target = frame.Board.Glasses[targetIndex];
                if (!frame.Board.CanPour(source, target).Success) return null;

                // Skip moving a whole glass into an identical empty glass; it is the same state.
                if (target.IsEmpty
                    && source.Layers.Count
                       == source.TopChainLength(frame.Board.Delivered)
                    && source.Type == target.Type)
                    return null;

                BsBoard next = frame.Board.Clone();
                RtGlass nextSource = next.GlassById(source.Id);
                RtGlass nextTarget = next.GlassById(target.Id);
                if (!next.Pour(nextSource, nextTarget).Success) return null;

                PushFrame(next, frame.Depth + 1, new BsMove
                {
                    IsDeliver = false,
                    From = source.Id,
                    To = target.Id,
                });
                return null;
            }

            void PushFrame(BsBoard board, int depth, BsMove move)
            {
                _path.Add(move);
                _stack.Add(new SearchFrame
                {
                    Board = board,
                    Depth = depth,
                    Phase = SearchPhase.Enter,
                    HasIncomingMove = true,
                });
            }

            void PopFrame()
            {
                int last = _stack.Count - 1;
                bool removeMove = _stack[last].HasIncomingMove;
                _stack.RemoveAt(last);
                if (removeMove) _path.RemoveAt(_path.Count - 1);
            }

            bool Finish(SolveOutcome outcome, Stopwatch slice)
            {
                slice.Stop();
                _elapsedTicks += slice.ElapsedTicks;
                var completed = new SolveResult
                {
                    Outcome = outcome,
                    NodesVisited = _nodes,
                    ElapsedMs = Milliseconds(_elapsedTicks),
                };
                if (outcome == SolveOutcome.Solvable)
                    completed.Solution = new List<BsMove>(_path);
                _result = completed;
                ReleaseSearchState();
                return true;
            }

            void ReleaseSearchState()
            {
                _stack.Clear();
                _path.Clear();
                _visited.Clear();
            }

            static long Milliseconds(long ticks)
            {
                return ticks <= 0
                    ? 0
                    : (long)((double)ticks * 1000d / Stopwatch.Frequency);
            }
        }

        /// <summary>Checks elapsed time at intervals to limit Stopwatch calls.</summary>
        sealed class Deadline
        {
            readonly Stopwatch _sw;
            readonly int _maxMs;
            readonly int _mask;
            int _counter;
            public bool Expired;
            public Deadline(Stopwatch sw, int maxMs)
            {
                _sw = sw; _maxMs = maxMs;
                // Check every node for tight budgets because cloning and cache growth can spike. Loose budgets
                // only need a check every 16 nodes.
                _mask = (maxMs > 0 && maxMs <= 100) ? 0 : 0x0F;
            }
            public bool Check()
            {
                if (_maxMs <= 0 || Expired) return Expired;
                // Check every 128 operations so expensive board clones cannot overshoot the time limit too
                // far.
                if ((++_counter & _mask) != 0) return false;
                if (_sw.ElapsedMilliseconds >= _maxMs) Expired = true;
                return Expired;
            }
        }

        static bool Dfs(BsBoard b, HashSet<string> visited, List<BsMove> path,
                        ref int nodes, int budget, int depth, int maxDepth,
                        ref bool budgetHit, ref bool depthHit, Deadline deadline)
        {
            if (b.IsWin()) return true;
            if (depth >= maxDepth) { depthHit = true; return false; }
            if (nodes >= budget) { budgetHit = true; return false; }
            if (deadline.Check()) return false;

            nodes++;
            var key = b.StateKey();
            // I keep a plain visited set and a high depth limit. If that limit is hit, return unknown; shallow
            // revisits might otherwise be skipped and falsely look unsolvable.
            if (!visited.Add(key)) return false;

            // Try deliveries first to shrink the board sooner.
            for (int i = 0; i < b.Glasses.Count; i++)
            {
                var g = b.Glasses[i];
                if (b.MatchedSlot(g) < 0) continue;

                var next = b.Clone();
                var ng = next.GlassById(g.Id);
                if (!next.Deliver(ng, out _)) continue;

                path.Add(new BsMove { IsDeliver = true, From = g.Id });
                if (Dfs(next, visited, path, ref nodes, budget, depth + 1, maxDepth,
                        ref budgetHit, ref depthHit, deadline)) return true;
                path.RemoveAt(path.Count - 1);
                if (budgetHit || deadline.Expired) return false;
            }

            // Try pours next.
            for (int i = 0; i < b.Glasses.Count; i++)
            {
                var src = b.Glasses[i];
                if (src.IsEmpty) continue;

                for (int j = 0; j < b.Glasses.Count; j++)
                {
                    if (i == j) continue;
                    var dst = b.Glasses[j];
                    if (!b.CanPour(src, dst).Success) continue;

                    // Skip moving a whole glass to an equivalent empty one; it only swaps their places.
                    if (dst.IsEmpty && src.Layers.Count == src.TopChainLength(b.Delivered) && src.Type == dst.Type)
                        continue;

                    var next = b.Clone();
                    var ns = next.GlassById(src.Id);
                    var nd = next.GlassById(dst.Id);
                    if (!next.Pour(ns, nd).Success) continue;

                    path.Add(new BsMove { IsDeliver = false, From = src.Id, To = dst.Id });
                    if (Dfs(next, visited, path, ref nodes, budget, depth + 1, maxDepth,
                            ref budgetHit, ref depthHit, deadline)) return true;
                    path.RemoveAt(path.Count - 1);
                    if (budgetHit || deadline.Expired) return false;
                }
            }

            return false;
        }
    }

    public enum IssueLevel { Info, Warning, Error }

    public struct ValidationIssue
    {
        public IssueLevel Level;
        public string Message;
        public static ValidationIssue Err(string m) => new ValidationIssue { Level = IssueLevel.Error, Message = m };
        public static ValidationIssue Warn(string m) => new ValidationIssue { Level = IssueLevel.Warning, Message = m };
        public static ValidationIssue Info(string m) => new ValidationIssue { Level = IssueLevel.Info, Message = m };
    }

    /// <summary>Checks liquid totals, capacity and solvability before accepting a level.</summary>
    public static class BsValidator
    {
        public static List<ValidationIssue> Validate(BsLevel level, BsPalette pal, bool runSolver, int nodeBudget = BsSolver.DefaultNodeBudget)
        {
            var issues = new List<ValidationIssue>();
            if (level == null)
            {
                issues.Add(ValidationIssue.Err("Missing level."));
                return issues;
            }

            if (level.Glasses.Count == 0)
                issues.Add(ValidationIssue.Err("The board has no glasses."));
            if (level.Orders.Count == 0)
                issues.Add(ValidationIssue.Err("The order deck is empty."));

            // Check glass capacity.
            for (int i = 0; i < level.Glasses.Count; i++)
            {
                var g = level.Glasses[i];
                if (g.Layers.Count > g.Capacity)
                    issues.Add(ValidationIssue.Err(
                        $"Glass #{i + 1} ({BsRules.DisplayName(g.Type)}) holds {g.Capacity} units but has {g.Layers.Count} layers."));
            }

            // Order contents must fill the requested glass.
            for (int i = 0; i < level.Orders.Count; i++)
            {
                var o = level.Orders[i];
                if (o.Contents.Count != o.Capacity)
                    issues.Add(ValidationIssue.Err(
                        $"Order #{i + 1} ({BsRules.DisplayName(o.Glass)}) needs {o.Capacity} units but defines {o.Contents.Count}."));
                if (o.TimeLimit > 0f && !level.AllowTimedOrders)
                    issues.Add(ValidationIssue.Err(
                        $"Order #{i + 1} has a timer, but timed orders are off. " +
                        "Set TimeLimit to zero or enable timed orders."));
            }

            // Check hidden-colour permission.
            bool anyHidden = false;
            foreach (var g in level.Glasses)
                foreach (var l in g.Layers)
                    if (l.Hidden) { anyHidden = true; break; }
            if (anyHidden && !level.AllowHiddenColors)
                issues.Add(ValidationIssue.Err("Hidden colours are disabled (L7+ feature). These board layers will stay visible."));

            // Each colour's board total must match its order total.
            var board = level.BoardColorTotals();
            var need = level.OrderColorTotals();
            var allColors = new HashSet<int>(board.Keys);
            allColors.UnionWith(need.Keys);
            bool conservationOk = true;
            foreach (var c in allColors)
            {
                board.TryGetValue(c, out var have);
                need.TryGetValue(c, out var want);
                if (have != want)
                {
                    conservationOk = false;
                    string cname = pal ? pal.NameAt(c) : ("Color " + c);
                    issues.Add(ValidationIssue.Err(
                        $"LIQUID MISMATCH — {cname}: board {have} units, orders need {want} units (difference {have - want:+#;-#;0})."));
                }
            }
            if (conservationOk && allColors.Count > 0)
                issues.Add(ValidationIssue.Info($"Liquid totals match — {level.TotalBoardUnits()} units on board, {level.TotalOrderUnits()} units needed."));

            // Check that the board has each requested glass type.
            var typeSupply = new Dictionary<GlassType, int>();
            foreach (var g in level.Glasses)
                typeSupply[g.Type] = typeSupply.TryGetValue(g.Type, out var v) ? v + 1 : 1;
            var typeDemand = new Dictionary<GlassType, int>();
            foreach (var o in level.Orders)
                typeDemand[o.Glass] = typeDemand.TryGetValue(o.Glass, out var v) ? v + 1 : 1;
            foreach (var kv in typeDemand)
            {
                typeSupply.TryGetValue(kv.Key, out var supply);
                if (supply < kv.Value)
                    issues.Add(ValidationIssue.Err(
                        $"{BsRules.DisplayName(kv.Key)} orders: {kv.Value}; board: {supply} {BsRules.DisplayName(kv.Key)} glasses. " +
                        "Each order needs its own glass because delivery removes it."));
            }

            // Check room to move liquid.
            int emptyCount = 0;
            foreach (var g in level.Glasses) if (g.Layers.Count == 0) emptyCount++;
            if (emptyCount == 0)
                issues.Add(ValidationIssue.Warn("No empty glasses. With no spare space, the level is likely blocked."));

            // Reject locks that can never open.
            int totalOrders = level.Orders.Count;
            for (int i = 0; i < level.Glasses.Count; i++)
            {
                var g = level.Glasses[i];
                if (g.UnlockAfter >= totalOrders && g.UnlockAfter > 0)
                    issues.Add(ValidationIssue.Err(
                        $"Glass #{i + 1} needs {g.UnlockAfter} deliveries to unlock, but the level has {totalOrders} orders — " +
                        "this glass can never unlock."));
                for (int k = 0; k < g.Layers.Count; k++)
                {
                    int lu = g.Layers[k].LockUntil;
                    if (lu > 0 && lu >= totalOrders)
                        issues.Add(ValidationIssue.Err(
                            $"Glass #{i + 1}, layer {k + 1} needs {lu} deliveries to unlock, but there are {totalOrders} orders — " +
                            "this layer can never unlock."));
                }
                // A full chained glass delays access to its liquid; this may be intentional.
                if (g.UnlockAfter > 0 && g.Layers.Count > 0)
                    issues.Add(ValidationIssue.Info(
                        $"Glass #{i + 1} is chained: its {g.Layers.Count} units stay locked until delivery {g.UnlockAfter}."));
            }

            // Flag orders that are already ready at the start.
            {
                var b0 = BsBoard.FromLevel(level);
                int pre = 0;
                foreach (var g in b0.Glasses)
                    if (b0.MatchedSlot(g) >= 0) pre++;
                if (pre > 0)
                    issues.Add(ValidationIssue.Err(
                        $"At startup, {pre} orders are already ready to deliver without a move. " +
                        "The shuffle was too light; regenerate the board."));
            }

            // Require a solvability check.
            if (runSolver)
            {
                bool hardError = false;
                foreach (var it in issues) if (it.Level == IssueLevel.Error) { hardError = true; break; }
                if (hardError)
                {
                    issues.Add(ValidationIssue.Info("Solver skipped — fix the errors above first."));
                }
                else
                {
                    var r = BsSolver.Solve(level, nodeBudget);
                    switch (r.Outcome)
                    {
                        case SolveOutcome.Solvable:
                            issues.Add(ValidationIssue.Info("Solver: " + r.Summary()));
                            break;
                        case SolveOutcome.Unsolvable:
                            issues.Add(ValidationIssue.Err("Solver: " + r.Summary()));
                            break;
                        default:
                            issues.Add(ValidationIssue.Warn("Solver: " + r.Summary()));
                            break;
                    }
                }
            }

            return issues;
        }

        public static bool HasError(List<ValidationIssue> issues)
        {
            foreach (var i in issues) if (i.Level == IssueLevel.Error) return true;
            return false;
        }
    }
}
