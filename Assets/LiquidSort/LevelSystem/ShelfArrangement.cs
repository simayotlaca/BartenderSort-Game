using System.Collections.Generic;
using BartenderSort.Core;

namespace LiquidSort.Levels
{
    public static class ShelfArrangement
    {
        public const int MaximumAuthoredGlasses = 32;
        public const int MaximumBoardGlasses = 32;

        private const int MaximumRows = ShelfLayoutSolver.MaximumRowCount;
        private const int MaximumColumns = ShelfLayoutSolver.MaximumColumnsPerRow;
        private const int SameTypePenalty = 3;
        private const int BigScore = 3;
        // The level's own order stays unless the arrangement is clearly better, so small or hand-made boards
        // do not change for a marginal gain.
        private const int KeepMargin = 2;

        public sealed class Workspace
        {
            internal readonly int[] Scores = new int[MaximumAuthoredGlasses];
            internal readonly GlassType[] Types = new GlassType[MaximumAuthoredGlasses];
            internal readonly int[] ByScore = new int[MaximumAuthoredGlasses];
            internal readonly int[] RankOf = new int[MaximumAuthoredGlasses];
            internal readonly int[] RowCapacity = new int[MaximumRows];
            internal readonly int[] RowFill = new int[MaximumRows];
            internal readonly int[] RowMass = new int[MaximumRows];
            internal readonly int[] RowMembers = new int[MaximumRows * MaximumColumns];
            internal readonly int[] ArrangedSeats = new int[MaximumRows * MaximumColumns];
            internal readonly int[] Keys = new int[MaximumBoardGlasses];
        }

        public static int SizeScore(GlassType type)
        {
            switch (type)
            {
                case GlassType.Shot: return 1;
                case GlassType.Kadeh: return 2;
                case GlassType.Latte: return 3;
                case GlassType.Tumbler: return 4;
                case GlassType.Bira: return 5;
                default: return 3;
            }
        }

        public static void VisibleWidthInsets(GlassType type, out float left, out float right)
        {
            switch (type)
            {
                case GlassType.Shot: left = 0.0468f; right = 0.0468f; return;
                case GlassType.Kadeh: left = 0.0871f; right = 0.0871f; return;
                case GlassType.Latte: left = 0.0300f; right = 0.0284f; return;
                case GlassType.Tumbler: left = 0.0462f; right = 0.0489f; return;
                case GlassType.Bira: left = 0.0197f; right = 0.0177f; return;
                default: left = 0f; right = 0f; return;
            }
        }

        public static bool TryBuildSeatOrder(BsLevel level, int[] boardGlassIds, int boardCount,
                                             int[] seatOrder, Workspace workspace)
        {
            if (seatOrder == null || boardCount <= 0) return false;
            int identityCount = boardCount < seatOrder.Length ? boardCount : seatOrder.Length;
            for (int i = 0; i < identityCount; i++) seatOrder[i] = i;

            if (level == null || level.Glasses == null || workspace == null || boardGlassIds == null
                || boardCount > seatOrder.Length || boardCount > boardGlassIds.Length
                || boardCount > workspace.Keys.Length)
                return false;

            int authoredCount = level.Glasses.Count;
            if (!TryBuildAuthoredRanks(level.Glasses, level.ColumnsPerRow, workspace)) return false;

            int[] keys = workspace.Keys;
            for (int i = 0; i < boardCount; i++)
            {
                int id = boardGlassIds[i];
                keys[i] = id >= 0 && id < authoredCount
                    ? workspace.RankOf[id]
                    : authoredCount + i;
            }

            // Insertion sort by key; ties (impossible for distinct ids) keep board order.
            for (int i = 1; i < boardCount; i++)
            {
                int index = seatOrder[i];
                int key = keys[index];
                int j = i - 1;
                while (j >= 0 && keys[seatOrder[j]] > key)
                {
                    seatOrder[j + 1] = seatOrder[j];
                    j--;
                }
                seatOrder[j + 1] = index;
            }
            return true;
        }

        internal static bool TryBuildAuthoredRanks(List<GlassDef> glasses, int columnsPerRow,
                                                   Workspace workspace)
        {
            int count = glasses.Count;
            if (count <= 0 || count > MaximumAuthoredGlasses) return false;
            if (columnsPerRow <= 0 || columnsPerRow > MaximumColumns) return false;

            for (int id = 0; id < count; id++)
            {
                GlassDef glass = glasses[id];
                if (glass == null) return false;
                workspace.Types[id] = glass.Type;
                workspace.Scores[id] = SizeScore(glass.Type);
                workspace.RankOf[id] = id;
            }

            int mainCount = count < MaximumRows * columnsPerRow
                ? count
                : MaximumRows * columnsPerRow;
            int rowCount = (mainCount + columnsPerRow - 1) / columnsPerRow;
            if (rowCount < ShelfLayoutSolver.MinimumRowCount) rowCount = ShelfLayoutSolver.MinimumRowCount;
            if (rowCount > MaximumRows || mainCount > rowCount * MaximumColumns) return false;

            int basePerRow = mainCount / rowCount;
            int remainder = mainCount % rowCount;
            for (int row = 0; row < rowCount; row++)
                workspace.RowCapacity[row] = basePerRow + (row < remainder ? 1 : 0);

            // The level's own order, seated the way the solver seats it.
            for (int seat = 0; seat < mainCount; seat++) workspace.ArrangedSeats[seat] = seat;
            int authoredCost = Cost(workspace, workspace.ArrangedSeats, rowCount);

            if (!AssignRows(workspace, mainCount, rowCount)) return true;
            OrderInsideRows(workspace, rowCount);
            int arrangedCost = Cost(workspace, workspace.ArrangedSeats, rowCount);
            if (arrangedCost + KeepMargin > authoredCost) return true;

            for (int seat = 0; seat < mainCount; seat++)
                workspace.RankOf[workspace.ArrangedSeats[seat]] = seat;
            return true;
        }

        private static bool AssignRows(Workspace workspace, int mainCount, int rowCount)
        {
            int[] byScore = workspace.ByScore;
            for (int i = 0; i < mainCount; i++)
            {
                int id = i;
                int j = i - 1;
                while (j >= 0 && ScoresBefore(workspace, id, byScore[j]))
                {
                    byScore[j + 1] = byScore[j];
                    j--;
                }
                byScore[j + 1] = id;
            }

            for (int row = 0; row < rowCount; row++)
            {
                workspace.RowFill[row] = 0;
                workspace.RowMass[row] = 0;
            }

            for (int i = 0; i < mainCount; i++)
            {
                int id = byScore[i];
                int bestRow = -1;
                int bestLoad = int.MaxValue;
                for (int row = rowCount - 1; row >= 0; row--)
                {
                    if (workspace.RowFill[row] >= workspace.RowCapacity[row]) continue;
                    int load = workspace.RowMass[row]
                             + SameTypePenalty * SameTypeCount(workspace, row, workspace.Types[id]);
                    if (load < bestLoad)
                    {
                        bestLoad = load;
                        bestRow = row;
                    }
                }

                if (bestRow < 0) return false;
                int slot = bestRow * MaximumColumns + workspace.RowFill[bestRow];
                workspace.RowMembers[slot] = id;
                workspace.RowFill[bestRow]++;
                workspace.RowMass[bestRow] += workspace.Scores[id];
            }
            return true;
        }

        private static void OrderInsideRows(Workspace workspace, int rowCount)
        {
            int seat = 0;
            for (int row = 0; row < rowCount; row++)
            {
                int start = row * MaximumColumns;
                int fill = workspace.RowFill[row];
                int low = 0;
                int high = fill - 1;
                bool mirror = row % 2 == 1;
                for (int column = 0; column < fill; column++)
                {
                    int member = column % 2 == 0 ? low++ : high--;
                    int target = mirror ? seat + fill - 1 - column : seat + column;
                    workspace.ArrangedSeats[target] = workspace.RowMembers[start + member];
                }
                seat += fill;
            }
        }

        private static int Cost(Workspace workspace, int[] seats, int rowCount)
        {
            int minBig = int.MaxValue, maxBig = int.MinValue;
            int minMass = int.MaxValue, maxMass = int.MinValue;
            int bigNeighbours = 0, twinNeighbours = 0;
            int seat = 0;
            for (int row = 0; row < rowCount; row++)
            {
                int fill = workspace.RowCapacity[row];
                int big = 0, mass = 0;
                for (int column = 0; column < fill; column++)
                {
                    int id = seats[seat + column];
                    int score = workspace.Scores[id];
                    mass += score;
                    if (score >= BigScore) big++;
                    if (column == 0) continue;
                    int left = seats[seat + column - 1];
                    if (score >= BigScore && workspace.Scores[left] >= BigScore) bigNeighbours++;
                    if (workspace.Types[left] == workspace.Types[id]) twinNeighbours++;
                }
                if (big < minBig) minBig = big;
                if (big > maxBig) maxBig = big;
                if (mass < minMass) minMass = mass;
                if (mass > maxMass) maxMass = mass;
                seat += fill;
            }
            return 4 * (maxBig - minBig) + (maxMass - minMass) + 2 * bigNeighbours + twinNeighbours;
        }

        private static bool ScoresBefore(Workspace workspace, int a, int b)
        {
            int scoreA = workspace.Scores[a];
            int scoreB = workspace.Scores[b];
            return scoreA != scoreB ? scoreA > scoreB : a < b;
        }

        private static int SameTypeCount(Workspace workspace, int row, GlassType type)
        {
            int start = row * MaximumColumns;
            int total = 0;
            for (int i = 0; i < workspace.RowFill[row]; i++)
                if (workspace.Types[workspace.RowMembers[start + i]] == type) total++;
            return total;
        }
    }
}
