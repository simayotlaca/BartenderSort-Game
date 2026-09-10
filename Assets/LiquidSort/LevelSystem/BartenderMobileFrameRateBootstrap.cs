using UnityEngine;

namespace LiquidSort.Levels
{
    internal static class BartenderMobileFrameRateBootstrap
    {
        private const int TargetFramesPerSecond = 60;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Apply()
        {
            if (Application.platform != RuntimePlatform.Android
                && Application.platform != RuntimePlatform.IPhonePlayer)
            {
                return;
            }

            Application.targetFrameRate = TargetFramesPerSecond;
        }
    }
}
