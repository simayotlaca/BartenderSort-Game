using BartenderSort.Core;
using TMPro;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Shows the level badge through its linked TMP label. Use <see cref="BsLevel.Index"/> for the number;
    /// campaign slots may shift when levels are inserted.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LevelBadgePresenter : MonoBehaviour
    {
        [Header("Rig references")]
        [Tooltip("Uses a component on this object if empty.")]
        [SerializeField] private BartenderLevelController controller;

        [Header("Text")]
        [SerializeField] private TMP_Text richLabel = null;
        [Tooltip("{0} is the level number.")]
        [SerializeField] private string format = "Level {0}";
        [Tooltip("Text shown when no level is loaded.")]
        [SerializeField] private string idleText = "BARTENDER";
        [Tooltip("Text shown after the campaign ends.")]
        [SerializeField] private string completedText = "COMPLETED";

        [Header("Visibility")]
        [Tooltip("Hide this badge when no level is loaded.")]
        [SerializeField] private GameObject badgeRoot = null;
        [SerializeField] private bool hideWhenUnloaded = false;

        private BartenderLevelController subscribedController;

        public string CurrentText { get; private set; } = "";

        private void Awake() => ResolveDependencies();

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
            Refresh();
        }

        private void OnDisable() => Unsubscribe();

        public void Refresh()
        {
            BsLevel level = controller != null ? controller.CurrentLevel : null;
            bool complete = controller != null
                            && controller.State == BartenderLevelState.CampaignComplete;

            CurrentText = level != null
                ? string.Format(format, level.Index)
                : (complete ? completedText : idleText);

            if (richLabel != null)
            {
                richLabel.text = CurrentText;
                richLabel.enabled = true;
            }
            if (badgeRoot != null && hideWhenUnloaded)
                badgeRoot.SetActive(level != null || complete);
        }

        private void ResolveDependencies()
        {
            if (controller == null) controller = GetComponent<BartenderLevelController>();
        }

        private void Subscribe()
        {
            if (subscribedController == controller) return;
            Unsubscribe();
            subscribedController = controller;
            if (subscribedController == null) return;
            subscribedController.LevelLoaded += HandleLevelLoaded;
            subscribedController.StateChanged += HandleStateChanged;
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.StateChanged -= HandleStateChanged;
            }
            subscribedController = null;
        }

        private void HandleLevelLoaded(BsLevel level) => Refresh();
        private void HandleStateChanged(BartenderLevelState state) => Refresh();
    }
}
