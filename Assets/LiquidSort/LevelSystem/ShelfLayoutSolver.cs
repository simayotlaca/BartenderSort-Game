using System;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>Spacing and scale settings for <see cref="ShelfLayoutSolver"/>.</summary>
    public readonly struct ShelfLayoutSettings
    {
        public float TwoAcrossColumnSpacing { get; }
        public float ThreeAcrossColumnSpacing { get; }
        public float CompactColumnSpacing { get; }
        public float TwoRowSpaciousGlassScale { get; }
        public float ThreeRowSpaciousGlassScale { get; }
        public float FourAcrossGlassScale { get; }
        public float FourAcrossThreeRowGlassScale { get; }
        public float OpticalSeatInset { get; }
        public float ThreeRowCompositionScale { get; }

        public ShelfLayoutSettings(
            float twoAcrossColumnSpacing,
            float threeAcrossColumnSpacing,
            float compactColumnSpacing,
            float twoRowSpaciousGlassScale,
            float threeRowSpaciousGlassScale,
            float fourAcrossGlassScale,
            float fourAcrossThreeRowGlassScale,
            float opticalSeatInset,
            float threeRowCompositionScale)
        {
            RequirePositive(twoAcrossColumnSpacing, nameof(twoAcrossColumnSpacing));
            RequirePositive(threeAcrossColumnSpacing, nameof(threeAcrossColumnSpacing));
            RequirePositive(compactColumnSpacing, nameof(compactColumnSpacing));
            RequirePositive(twoRowSpaciousGlassScale,
                nameof(twoRowSpaciousGlassScale));
            RequirePositive(threeRowSpaciousGlassScale,
                nameof(threeRowSpaciousGlassScale));
            RequirePositive(fourAcrossGlassScale, nameof(fourAcrossGlassScale));
            RequirePositive(fourAcrossThreeRowGlassScale,
                nameof(fourAcrossThreeRowGlassScale));
            RequireNonNegative(opticalSeatInset, nameof(opticalSeatInset));
            RequireFinite(threeRowCompositionScale,
                nameof(threeRowCompositionScale));

            TwoAcrossColumnSpacing = twoAcrossColumnSpacing;
            ThreeAcrossColumnSpacing = threeAcrossColumnSpacing;
            CompactColumnSpacing = compactColumnSpacing;
            TwoRowSpaciousGlassScale = twoRowSpaciousGlassScale;
            ThreeRowSpaciousGlassScale = threeRowSpaciousGlassScale;
            FourAcrossGlassScale = fourAcrossGlassScale;
            FourAcrossThreeRowGlassScale = fourAcrossThreeRowGlassScale;
            OpticalSeatInset = opticalSeatInset;
            ThreeRowCompositionScale = threeRowCompositionScale;
        }

        internal void Validate()
        {
            // default(ShelfLayoutSettings) skips the constructor, so check at solver entry too to reject
            // zero-sized layouts.
            RequirePositive(TwoAcrossColumnSpacing,
                nameof(TwoAcrossColumnSpacing));
            RequirePositive(ThreeAcrossColumnSpacing,
                nameof(ThreeAcrossColumnSpacing));
            RequirePositive(CompactColumnSpacing, nameof(CompactColumnSpacing));
            RequirePositive(TwoRowSpaciousGlassScale,
                nameof(TwoRowSpaciousGlassScale));
            RequirePositive(ThreeRowSpaciousGlassScale,
                nameof(ThreeRowSpaciousGlassScale));
            RequirePositive(FourAcrossGlassScale, nameof(FourAcrossGlassScale));
            RequirePositive(FourAcrossThreeRowGlassScale,
                nameof(FourAcrossThreeRowGlassScale));
            RequireNonNegative(OpticalSeatInset, nameof(OpticalSeatInset));
            RequireFinite(ThreeRowCompositionScale,
                nameof(ThreeRowCompositionScale));
        }

        private static void RequirePositive(float value, string parameterName)
        {
            RequireFinite(value, parameterName);
            if (value <= 0f)
                throw new ArgumentOutOfRangeException(parameterName, value,
                    "The value must be greater than zero.");
        }

        private static void RequireNonNegative(float value, string parameterName)
        {
            RequireFinite(value, parameterName);
            if (value < 0f)
                throw new ArgumentOutOfRangeException(parameterName, value,
                    "The value cannot be negative.");
        }

        private static void RequireFinite(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentException("The value must be finite.", parameterName);
        }
    }

    /// <summary>One solved shelf row, expressed entirely in layout-space units.</summary>
    public readonly struct ShelfRowLayout
    {
        public int ItemStart { get; }
        public int ItemCount { get; }
        public float ShelfCenterX { get; }
        public float SeatY { get; }
        public float FirstX { get; }
        public float Spacing { get; }

        internal ShelfRowLayout(int itemStart, int itemCount, float shelfCenterX,
                                float seatY, float firstX,
                                float spacing)
        {
            ItemStart = itemStart;
            ItemCount = itemCount;
            ShelfCenterX = shelfCenterX;
            SeatY = seatY;
            FirstX = firstX;
            Spacing = spacing;
        }

        public float XAt(int column)
        {
            if (column < 0 || column >= ItemCount)
                throw new ArgumentOutOfRangeException(nameof(column), column,
                    $"Column must be between 0 and {ItemCount - 1} for this row.");
            return FirstX + column * Spacing;
        }
    }

    /// <summary>Final shelf centre and surface coordinates, read by the view from scene markers.</summary>
    public readonly struct ShelfRowTarget
    {
        public float CenterX { get; }
        public float SurfaceY { get; }

        public ShelfRowTarget(float centerX, float surfaceY)
        {
            CenterX = centerX;
            SurfaceY = surfaceY;
        }
    }

    /// <summary>A two- or three-row layout. Inline row storage avoids allocations and fixes capacity at three.</summary>
    public readonly struct ShelfLayoutPlan
    {
        private readonly ShelfRowLayout row0;
        private readonly ShelfRowLayout row1;
        private readonly ShelfRowLayout row2;

        public int RowCount { get; }
        public float GlassScale { get; }

        internal ShelfLayoutPlan(int rowCount, float glassScale,
                                 ShelfRowLayout row0, ShelfRowLayout row1,
                                 ShelfRowLayout row2)
        {
            RowCount = rowCount;
            GlassScale = glassScale;
            this.row0 = row0;
            this.row1 = row1;
            this.row2 = row2;
        }

        public ShelfRowLayout RowAt(int row)
        {
            if (row < 0 || row >= RowCount)
                throw new ArgumentOutOfRangeException(nameof(row), row,
                    $"Row must be between 0 and {RowCount - 1} for this plan.");
            if (row == 0) return row0;
            return row == 1 ? row1 : row2;
        }
    }

    /// <summary>Shared shelf math for runtime and editor previews.</summary>
    public static class ShelfLayoutSolver
    {
        public const int MinimumRowCount = 2;
        public const int MaximumRowCount = 3;
        public const int MaximumColumnsPerRow = 4;
        public const int MaximumOverflowGlassCount = 3;
        // Leave edge room for four large glasses on the narrow top plank.
        public const float MaximumShelfWidthStep = 0.075f;

        public static int RequiredRowCount(int glassCount, int columnsPerRow)
        {
            if (glassCount < 0)
                throw new ArgumentOutOfRangeException(nameof(glassCount), glassCount,
                    "Glass count cannot be negative.");
            ValidateColumnsPerRow(columnsPerRow);

            int needed = glassCount / columnsPerRow
                       + (glassCount % columnsPerRow > 0 ? 1 : 0);
            return Math.Max(MinimumRowCount, needed);
        }

        /// <summary>Capacity of the three ordinary shelf rows for one level layout.</summary>
        public static int MainShelfCapacity(int columnsPerRow)
        {
            ValidateColumnsPerRow(columnsPerRow);
            return MaximumRowCount * columnsPerRow;
        }

        /// <summary>
        /// Counts glasses on the main shelf. Reject overflow beyond the short shelf's limit instead of
        /// hiding extra glasses.
        /// </summary>
        public static int MainShelfGlassCount(int totalGlassCount, int columnsPerRow)
        {
            int capacity = ValidateOverflowTotal(totalGlassCount, columnsPerRow);
            return Math.Min(totalGlassCount, capacity);
        }

        /// <summary>Number of glasses reserved for the short shelf above the main rack.</summary>
        public static int OverflowGlassCount(int totalGlassCount, int columnsPerRow)
        {
            int capacity = ValidateOverflowTotal(totalGlassCount, columnsPerRow);
            return Math.Max(0, totalGlassCount - capacity);
        }

        /// <summary>
        /// The bottom active plank stays full width; each higher row loses one step. Runtime and previews
        /// share this math.
        /// </summary>
        public static float ShelfWidthScale(int rowCount, int row, float widthStep)
        {
            ValidateRowCount(rowCount);
            ValidateRow(rowCount, row);
            ValidateShelfWidthStep(widthStep);

            int stepsFromBottom = rowCount - 1 - row;
            return 1f - widthStep * stepsFromBottom;
        }

        /// <summary>Gives the overflow shelf one more width step without adding a fourth solver row.</summary>
        public static float OverflowShelfWidthScale(float widthStep)
        {
            ValidateShelfWidthStep(widthStep);
            return 1f - widthStep * MaximumRowCount;
        }

        /// <summary>
        /// Moves a fixed-width post with its shorter plank while keeping the edge inset. Scaling its centre
        /// would crowd the rounded edge.
        /// </summary>
        public static float ShelfPostCenterX(float authoredPlankHalfWidth,
                                             float authoredPostCenterX,
                                             float shelfWidthScale)
        {
            RequireFinite(authoredPlankHalfWidth, nameof(authoredPlankHalfWidth));
            RequireFinite(authoredPostCenterX, nameof(authoredPostCenterX));
            RequireFinite(shelfWidthScale, nameof(shelfWidthScale));
            if (authoredPlankHalfWidth <= 0f)
                throw new ArgumentOutOfRangeException(nameof(authoredPlankHalfWidth),
                    authoredPlankHalfWidth, "Authored plank half-width must be positive.");
            if (authoredPostCenterX < 0f
                || authoredPostCenterX > authoredPlankHalfWidth)
                throw new ArgumentOutOfRangeException(nameof(authoredPostCenterX),
                    authoredPostCenterX,
                    "Post centre must lie inside the authored plank half-width.");
            if (shelfWidthScale <= 0f || shelfWidthScale > 1f)
                throw new ArgumentOutOfRangeException(nameof(shelfWidthScale),
                    shelfWidthScale, "Shelf width scale must be in the (0, 1] range.");

            float edgeInset = authoredPlankHalfWidth - authoredPostCenterX;
            float steppedCenter = authoredPlankHalfWidth * shelfWidthScale
                                - edgeInset;
            if (steppedCenter < 0f)
                throw new ArgumentOutOfRangeException(nameof(shelfWidthScale),
                    shelfWidthScale,
                    "Shelf width leaves no room for the authored post inset.");
            return steppedCenter;
        }

        /// <summary>
        /// Solves counts, spacing and scale around the authored shelf targets. Keep their final heights and
        /// add the responsive offset once.
        /// </summary>
        public static ShelfLayoutPlan SolveForAnchoredRows(
            int glassCount,
            int rowCount,
            ShelfRowTarget row0Target,
            ShelfRowTarget row1Target,
            ShelfRowTarget row2Target,
            ShelfLayoutSettings settings,
            float responsiveOffset = 0f)
        {
            if (glassCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(glassCount), glassCount,
                    "Glass count must be greater than zero when solving a layout.");
            ValidateRowCount(rowCount);
            int capacity = rowCount * MaximumColumnsPerRow;
            if (glassCount > capacity)
                throw new ArgumentOutOfRangeException(nameof(glassCount), glassCount,
                    $"A {rowCount}-row shelf layout can hold at most {capacity} glasses.");
            ValidateTarget(row0Target, nameof(row0Target));
            ValidateTarget(row1Target, nameof(row1Target));
            if (rowCount == MaximumRowCount)
                ValidateTarget(row2Target, nameof(row2Target));
            RequireFinite(responsiveOffset, nameof(responsiveOffset));
            settings.Validate();

            int basePerRow = glassCount / rowCount;
            int remainder = glassCount % rowCount;
            int busiestRowCount = basePerRow + (remainder > 0 ? 1 : 0);
            float compositionScale = CompositionScale(settings, rowCount);
            float glassScale = BoardGlassScale(settings, rowCount,
                busiestRowCount);
            float seatInset = SeatInset(settings, glassScale);

            int itemStart = 0;
            ShelfRowLayout row0 = SolveRow(0, row0Target);
            ShelfRowLayout row1 = SolveRow(1, row1Target);
            ShelfRowLayout row2 = rowCount == MaximumRowCount
                ? SolveRow(2, row2Target)
                : default;

            return new ShelfLayoutPlan(rowCount, glassScale, row0, row1, row2);

            ShelfRowLayout SolveRow(int row, ShelfRowTarget target)
            {
                int itemCount = basePerRow + (row < remainder ? 1 : 0);
                float spacing = ColumnSpacing(settings, itemCount)
                              * compositionScale;
                float surfaceY = target.SurfaceY + responsiveOffset;
                float firstX = target.CenterX
                             - 0.5f * spacing * (itemCount - 1);
                ShelfRowLayout solved = new ShelfRowLayout(itemStart, itemCount,
                    target.CenterX, surfaceY - seatInset, firstX, spacing);
                itemStart += itemCount;
                return solved;
            }
        }

        private static void ValidateTarget(ShelfRowTarget target, string parameterName)
        {
            RequireFinite(target.CenterX, parameterName + ".CenterX");
            RequireFinite(target.SurfaceY, parameterName + ".SurfaceY");
        }

        public static float CompositionScale(ShelfLayoutSettings settings,
                                             int rowCount)
        {
            ValidateRowCount(rowCount);
            settings.Validate();
            return rowCount >= MaximumRowCount
                ? Mathf.Clamp(settings.ThreeRowCompositionScale, 0.80f, 1f)
                : 1f;
        }

        public static float BoardGlassScale(ShelfLayoutSettings settings,
                                            int rowCount,
                                            int busiestRowColumns)
        {
            ValidateRowCount(rowCount);
            if (busiestRowColumns <= 0)
                throw new ArgumentOutOfRangeException(nameof(busiestRowColumns),
                    busiestRowColumns,
                    "The busiest row must contain at least one glass.");
            if (busiestRowColumns > MaximumColumnsPerRow)
                throw new ArgumentOutOfRangeException(nameof(busiestRowColumns),
                    busiestRowColumns,
                    $"The busiest row cannot exceed {MaximumColumnsPerRow} glasses.");
            settings.Validate();

            bool compactRow = busiestRowColumns >= 4;
            bool threeRowLayout = rowCount >= MaximumRowCount;
            float compactScale = threeRowLayout
                ? settings.FourAcrossThreeRowGlassScale
                : settings.FourAcrossGlassScale;
            float spaciousScale = threeRowLayout
                ? settings.ThreeRowSpaciousGlassScale
                : settings.TwoRowSpaciousGlassScale;
            return (compactRow ? compactScale : spaciousScale)
                 * CompositionScale(settings, rowCount);
        }

        public static float ColumnSpacing(ShelfLayoutSettings settings, int columns)
        {
            if (columns < 0)
                throw new ArgumentOutOfRangeException(nameof(columns), columns,
                    "Column count cannot be negative.");
            if (columns > MaximumColumnsPerRow)
                throw new ArgumentOutOfRangeException(nameof(columns), columns,
                    $"Column count cannot exceed {MaximumColumnsPerRow}.");
            settings.Validate();

            if (columns <= 1) return 0f;
            if (columns == 2) return settings.TwoAcrossColumnSpacing;
            return columns == 3
                ? settings.ThreeAcrossColumnSpacing
                : settings.CompactColumnSpacing;
        }

        public static float SeatInset(ShelfLayoutSettings settings,
                                      float boardGlassScale)
        {
            if (boardGlassScale <= 0f || float.IsNaN(boardGlassScale)
                || float.IsInfinity(boardGlassScale))
                throw new ArgumentOutOfRangeException(nameof(boardGlassScale),
                    boardGlassScale, "Board glass scale must be finite and positive.");
            settings.Validate();
            return LiquidSort.VesselPresentationMath.RescaleAuthoredLength(
                settings.OpticalSeatInset,
                settings.TwoRowSpaciousGlassScale,
                boardGlassScale);
        }

        private static void ValidateRowCount(int rowCount)
        {
            if (rowCount < MinimumRowCount || rowCount > MaximumRowCount)
                throw new ArgumentOutOfRangeException(nameof(rowCount), rowCount,
                    $"Shelf layouts support {MinimumRowCount} or {MaximumRowCount} rows.");
        }

        private static void ValidateColumnsPerRow(int columnsPerRow)
        {
            if (columnsPerRow <= 0)
                throw new ArgumentOutOfRangeException(nameof(columnsPerRow),
                    columnsPerRow, "Columns per row must be greater than zero.");
            if (columnsPerRow > MaximumColumnsPerRow)
                throw new ArgumentOutOfRangeException(nameof(columnsPerRow),
                    columnsPerRow,
                    $"Columns per row cannot exceed {MaximumColumnsPerRow}.");
        }

        private static int ValidateOverflowTotal(int totalGlassCount,
                                                 int columnsPerRow)
        {
            if (totalGlassCount < 0)
                throw new ArgumentOutOfRangeException(nameof(totalGlassCount),
                    totalGlassCount, "Total glass count cannot be negative.");

            int capacity = MainShelfCapacity(columnsPerRow);
            int maximum = capacity + MaximumOverflowGlassCount;
            if (totalGlassCount > maximum)
                throw new ArgumentOutOfRangeException(nameof(totalGlassCount),
                    totalGlassCount,
                    $"The main and overflow shelves can hold at most {maximum} glasses.");
            return capacity;
        }

        private static void ValidateShelfWidthStep(float widthStep)
        {
            RequireFinite(widthStep, nameof(widthStep));
            if (widthStep < 0f || widthStep > MaximumShelfWidthStep)
                throw new ArgumentOutOfRangeException(nameof(widthStep), widthStep,
                    $"Shelf width step must be between 0 and {MaximumShelfWidthStep}.");
        }

        private static void ValidateRow(int rowCount, int row)
        {
            if (row < 0 || row >= rowCount)
                throw new ArgumentOutOfRangeException(nameof(row), row,
                    $"Row must be between 0 and {rowCount - 1}.");
        }

        private static void RequireFinite(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentException("The value must be finite.", parameterName);
        }
    }
}
