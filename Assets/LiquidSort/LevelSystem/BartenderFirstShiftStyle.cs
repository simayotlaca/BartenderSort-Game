using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>Art references shared by the authored First Shift overlay prefab.</summary>
    [CreateAssetMenu(menuName = "Bartender Sort/First Shift Style",
                    fileName = "BartenderFirstShiftStyle")]
    public sealed class BartenderFirstShiftStyle : ScriptableObject
    {
        [Header("Baked hippo badges")]
        public Sprite WelcomeHippo;
        public Sprite ThinkingHippo;
        public Sprite PointHippo;
        public Sprite CelebrateHippo;

        [Header("Timed order badges")]
        public Sprite TimedOrdersIntroHippo;
        public Sprite TimedOrdersCountdownHippo;
        public Sprite TimedOrdersAddTimeHippo;

        public Sprite Pose(BartenderFirstShiftPose pose)
        {
            switch (pose)
            {
                case BartenderFirstShiftPose.TimedOrdersIntro:
                    return TimedOrdersIntroHippo;
                case BartenderFirstShiftPose.TimedOrdersCountdown:
                    return TimedOrdersCountdownHippo;
                case BartenderFirstShiftPose.TimedOrdersAddTime:
                    return TimedOrdersAddTimeHippo;
                case BartenderFirstShiftPose.Thinking: return ThinkingHippo;
                case BartenderFirstShiftPose.Point: return PointHippo;
                case BartenderFirstShiftPose.Celebrate: return CelebrateHippo;
                default: return WelcomeHippo;
            }
        }
    }

    public enum BartenderFirstShiftPose
    {
        Welcome,
        Thinking,
        Point,
        Celebrate,
        TimedOrdersIntro,
        TimedOrdersCountdown,
        TimedOrdersAddTime,
    }
}
