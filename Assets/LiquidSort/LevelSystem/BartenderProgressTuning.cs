using BartenderSort.Core;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Shared production economy values. Editor tests use a separate save with normal defaults; adjust balances
    /// in Level Jumper and increase revision for a fresh test save.
    /// </summary>
    public static class BartenderProgressTuning
    {
        // Production economy
        public const int StartingCoins = 500;
        public const int MaximumLives = 5;
        public const int CoinsPerWin = 50;
        public const int PaidContinueCoinCost = 100;
        public const int FullLifeRefillCoinCost = 900;
        public const int UndoBoosterCoinCost = 300;
        public const int ExtraGlassBoosterCoinCost = 300;
        public const int ShuffleBoosterCoinCost = 300;
        public const GlassType PurchasedExtraGlassType = GlassType.Shot;

#if UNITY_EDITOR
        // When true, only the Editor uses a separate save. Set test coins, lives and level in Level Jumper.
        public const bool UseIsolatedEditorTestProfile = false;

        // Increase this to start a fresh test save without deleting the old one.
        public const int EditorTestProfileRevision = 1;
#endif

        internal static bool IsolatedEditorTestProfileEnabled
        {
            get
            {
#if UNITY_EDITOR
                return UseIsolatedEditorTestProfile;
#else
                return false;
#endif
            }
        }

        internal static string EditorTestSaveSuffix
        {
            get
            {
#if UNITY_EDITOR
                return EditorTestProfileRevision.ToString();
#else
                return "0";
#endif
            }
        }
    }
}
