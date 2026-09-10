using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Keeps tutorial copy in one editable asset. Field defaults also supply the fallback when Resources is
    /// missing, so text stays available without duplicate definitions.
    /// </summary>
    [CreateAssetMenu(menuName = "Bartender/Tutorial Copy", fileName = "BartenderTutorialCopy")]
    public sealed class BartenderTutorialCopy : ScriptableObject
    {
        public const string ResourcePath = "Tutorials/BartenderTutorialCopy";

        private static BartenderTutorialCopy cached;

        [Header("First shift — steps")]
        [TextArea] public string DirectStart =
            "Make the red order!\nTap the red glass.";
        [TextArea] public string TapRedGlass =
            "Tap the glass with\n<size=115%>red on top.</size>";
        [TextArea] public string TapEmptyGlass =
            "Now tap the\n<size=125%>empty glass!</size>";
        [TextArea] public string TapFinishedDrink =
            "Perfect! Tap the\n<size=112%>finished drink.</size>";
        [TextArea] public string ShiftComplete = "First shift complete!";

        [Header("First shift — nudges when the player taps the wrong thing")]
        [TextArea] public string RejectTapGlassFirst = "Tap the glowing glass first.";
        [TextArea] public string RejectPourIntoEmpty =
            "Pour into the glowing empty shot glass.";
        [TextArea] public string RejectServeReadyDrink =
            "Tap the glowing ready drink to serve it.";

        [Header("Timed orders")]
        [TextArea] public string TimedOrdersCountdown =
            "These orders are timed!\nServe before zero.\n<size=75%>Tap to start.</size>";

        /// <summary>Returns the authored asset or an in-memory default. Never returns null.</summary>
        public static BartenderTutorialCopy Resolve()
        {
            if (cached != null) return cached;
            cached = Resources.Load<BartenderTutorialCopy>(ResourcePath);
            if (cached == null) cached = CreateInstance<BartenderTutorialCopy>();
            return cached;
        }

        /// <summary>First Shift lines in spoken order, shared with the editor preview.</summary>
        public string[] FirstShiftSequence => new[]
        {
            DirectStart, TapEmptyGlass, TapFinishedDrink, ShiftComplete,
        };
    }
}
