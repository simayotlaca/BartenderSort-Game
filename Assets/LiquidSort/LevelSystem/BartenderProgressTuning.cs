using BartenderSort.Core;

namespace LiquidSort.Levels
{
    public static class BartenderProgressTuning
    {
        public const int StartingCoins = 2500;
        public const int MaximumLives = 5;
        public const int CoinsPerWin = 50;
        public const int PaidContinueCoinCost = 900;
        public const int FullLifeRefillCoinCost = 900;
        public const int UndoBoosterCoinCost = 300;
        public const int ExtraGlassBoosterCoinCost = 300;
        public const int ShuffleBoosterCoinCost = 300;
        public const GlassType PurchasedExtraGlassType = GlassType.Shot;

#if UNITY_EDITOR
        public const bool UseIsolatedEditorTestProfile = false;

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
