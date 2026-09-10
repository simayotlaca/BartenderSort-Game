using System;
using UnityEngine;
#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace LiquidSort.Levels
{
    /// <summary>
    /// Uses short iOS impact feedback and respects VibrationOn. Android support needs the androidjni module,
    /// native vibration path and VIBRATE permission.
    /// </summary>
    public static class BartenderHaptics
    {
        private static bool warned;

        /// <summary>One quiet, short tap for a rejected booster.</summary>
        public static void Light()
        {
            if (!BartenderSettingsStore.VibrationOn) return;

#if UNITY_IOS && !UNITY_EDITOR
            try { BartenderHapticsLight(); }
            catch (Exception exception) { WarnOnce(exception); }
#endif
        }

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void BartenderHapticsLight();
#endif

        private static void WarnOnce(Exception exception)
        {
            if (warned) return;
            warned = true;
            // Missing haptics should not stop the game. Warn only once.
            Debug.LogWarning("Haptik oynatılamadı, sessizce devam ediliyor: "
                           + exception.Message);
        }
    }
}
