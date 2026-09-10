using System;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>Presentation only. This cache can never award progress or currency.</summary>
    internal static class BartenderDailyOrdersPresentationStore
    {
        [Serializable]
        private sealed class SeenProgress
        {
            public long Day;
            public int Orders;
            public int Wins;
            public int Units;
        }

        private static string Key => "LiquidSort.Bartender.DailySeen.v1."
            + (BartenderProgressTuning.IsolatedEditorTestProfileEnabled
                ? "editor." + BartenderProgressTuning.EditorTestSaveSuffix : "player");

        internal static BartenderDailyOrdersSnapshot Load(BartenderDailyOrdersSnapshot actual)
        {
            SeenProgress seen = null;
            try
            {
                string json = PlayerPrefs.GetString(Key, string.Empty);
                if (!string.IsNullOrEmpty(json)) seen = JsonUtility.FromJson<SeenProgress>(json);
            }
            catch (Exception) { /* A missing visual cache simply reveals from zero. */ }
            if (seen == null || seen.Day != actual.UtcDayKey) seen = new SeenProgress();
            return new BartenderDailyOrdersSnapshot(actual.UtcDayKey,
                Math.Min(seen.Orders, actual.DeliveredOrders),
                Math.Min(seen.Wins, actual.WonLevels),
                Math.Min(seen.Units, actual.ServedUnits), false);
        }

        internal static void Save(long day, DailyOrderCardView[] cards)
        {
            if (day <= 0 || cards == null || cards.Length != 3
                || Array.Exists(cards, card => card == null)) return;
            try
            {
                PlayerPrefs.SetString(Key, JsonUtility.ToJson(new SeenProgress
                {
                    Day = day, Orders = cards[0].DisplayedProgress,
                    Wins = cards[1].DisplayedProgress, Units = cards[2].DisplayedProgress,
                }));
                PlayerPrefs.Save();
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Daily progress reveal could not be remembered: " + exception.Message);
            }
        }

#if UNITY_EDITOR
        internal static void EditorReset()
        {
            try
            {
                PlayerPrefs.DeleteKey(Key);
                PlayerPrefs.Save();
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Daily progress reveal could not be reset: " + exception.Message);
            }
        }
#endif
    }
}
