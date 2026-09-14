using UnityEngine;

namespace LiquidSort.Levels
{
    public enum BartenderInputIntent
    {
        BackgroundTap,
        BottleTap,
        Pour,
        Delivery,
    }

    public struct BartenderInputRequest
    {
        public BartenderInputIntent Intent;
        public int PrimaryGlassId;
        public int SecondaryGlassId;
        public int SelectedGlassId;

        public static BartenderInputRequest Background(int selectedGlassId)
        {
            return new BartenderInputRequest
            {
                Intent = BartenderInputIntent.BackgroundTap,
                PrimaryGlassId = -1,
                SecondaryGlassId = -1,
                SelectedGlassId = selectedGlassId,
            };
        }

        public static BartenderInputRequest Bottle(int glassId, int selectedGlassId)
        {
            return new BartenderInputRequest
            {
                Intent = BartenderInputIntent.BottleTap,
                PrimaryGlassId = glassId,
                SecondaryGlassId = -1,
                SelectedGlassId = selectedGlassId,
            };
        }

        public static BartenderInputRequest Pour(int sourceGlassId, int targetGlassId)
        {
            return new BartenderInputRequest
            {
                Intent = BartenderInputIntent.Pour,
                PrimaryGlassId = sourceGlassId,
                SecondaryGlassId = targetGlassId,
                SelectedGlassId = sourceGlassId,
            };
        }

        public static BartenderInputRequest Delivery(int glassId)
        {
            return new BartenderInputRequest
            {
                Intent = BartenderInputIntent.Delivery,
                PrimaryGlassId = glassId,
                SecondaryGlassId = -1,
                SelectedGlassId = -1,
            };
        }
    }

    public interface IBartenderInputPolicy
    {
        bool Allows(BartenderInputRequest request, out string rejectionReason);
        void HandleRejected(BartenderInputRequest request, string rejectionReason);
    }

    public interface IBartenderAcceptedInputConsumer
    {
        bool TryConsume(BartenderInputRequest request);
    }

    public static class BartenderTutorialProgress
    {
        private const string Prefix = "LiquidSort.Bartender.Tutorial.";

        public static bool IsCompleted(string tutorialId, int version)
        {
            return PlayerPrefs.GetInt(Key(tutorialId, version), 0) != 0;
        }

        public static void Complete(string tutorialId, int version)
        {
            PlayerPrefs.SetInt(Key(tutorialId, version), 1);
            PlayerPrefs.Save();
        }

        public static void Reset(string tutorialId, int version)
        {
            PlayerPrefs.DeleteKey(Key(tutorialId, version));
            PlayerPrefs.Save();
        }

        private static string Key(string tutorialId, int version)
        {
            string safeId = string.IsNullOrWhiteSpace(tutorialId)
                ? "tutorial"
                : tutorialId.Trim();
            return Prefix + safeId + ".v" + Mathf.Max(1, version) + ".Completed";
        }
    }
}
