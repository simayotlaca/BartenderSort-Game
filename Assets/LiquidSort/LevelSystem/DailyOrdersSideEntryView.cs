using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>Authored home-page Daily Orders board; the presenter only fills and animates these parts.</summary>
    [DisallowMultipleComponent]
    public sealed class DailyOrdersSideEntryView : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private Image artwork;
        [SerializeField] private Image backlight;
        [SerializeField] private BartenderDailyOrdersBadgeView badge;
        [Tooltip("Clipboard ticks in task order; tinted with the artwork while locked.")]
        [SerializeField] private Image[] taskChecks = new Image[3];
        [SerializeField] private Image timerPlate;
        [SerializeField] private TextMeshProUGUI timerText;
        [SerializeField] private TextMeshProUGUI resetHint;
        [SerializeField] private RectTransform lockOverlay;

        internal Button Button => button;
        internal Image Artwork => artwork;
        internal Image Backlight => backlight;
        internal BartenderDailyOrdersBadgeView Badge => badge;
        internal Image[] TaskChecks => taskChecks;
        internal Image TimerPlate => timerPlate;
        internal TextMeshProUGUI TimerText => timerText;
        internal TextMeshProUGUI ResetHint => resetHint;
        internal RectTransform LockOverlay => lockOverlay;

        internal bool IsReady => button != null && artwork != null && badge != null && timerText != null;
    }
}
