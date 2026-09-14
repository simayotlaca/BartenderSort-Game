using System;
using UnityEngine;
#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace LiquidSort.Levels
{
    public static class BartenderHaptics
    {
        private static bool warned;

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
            Debug.LogWarning("Haptics unavailable: " + exception.Message);
        }
    }
}
