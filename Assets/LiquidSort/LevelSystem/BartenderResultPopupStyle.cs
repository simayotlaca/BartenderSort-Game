using BartenderSort.Core;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Visual treatment shared by the authored win and loss cards.
    /// </summary>
    public enum BartenderResultPopupStyle
    {
        Normal,
        Hard,
        VeryHard
    }

    /// <summary>
    /// Shares the campaign's difficulty pattern between result cards and the home button, using the same
    /// one-based level number.
    /// </summary>
    public static class BartenderResultPopupStyleResolver
    {
        public static BartenderResultPopupStyle Resolve(BsLevel level) =>
            Resolve(level != null ? level.Index : 1);

        public static BartenderResultPopupStyle Resolve(int levelIndex)
        {
            switch (levelIndex)
            {
                case 18:
                case 23:
                case 27:
                    return BartenderResultPopupStyle.VeryHard;

                case 4:
                case 6:
                case 10:
                case 15:
                case 20:
                case 26:
                case 29:
                    return BartenderResultPopupStyle.Hard;

                default:
                    return BartenderResultPopupStyle.Normal;
            }
        }
    }
}
