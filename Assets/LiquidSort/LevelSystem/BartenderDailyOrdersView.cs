using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>Connect the inactive popup under an active menu object. The presenter wires its buttons.</summary>
    [DisallowMultipleComponent]
    public sealed class BartenderDailyOrdersView : MonoBehaviour
    {
        [SerializeField] private Button sideButton;
        [SerializeField] private GameObject popupRoot;
        [SerializeField] private Button closeButton;
        [SerializeField] private Button backdropButton;
        [SerializeField] private Button actionButton;
        [SerializeField] private TextMeshProUGUI actionText;
        [SerializeField] private TextMeshProUGUI countdownText;
        [SerializeField] private TextMeshProUGUI sideTimerText;
        [SerializeField] private TextMeshProUGUI completedText;
        [SerializeField] private TextMeshProUGUI completionBonusText;
        [SerializeField] private TextMeshProUGUI feedbackText;
        [SerializeField] private BartenderDailyOrdersBadgeView badge;
        [Tooltip("In order: delivered orders, won levels, served units.")]
        [SerializeField] private DailyOrderCardView[] cards = new DailyOrderCardView[3];

        public bool IsOpen => popupRoot != null && popupRoot.activeSelf;
        internal GameObject SideEntry => sideButton != null ? sideButton.gameObject : null;
        internal DailyOrderCardView[] Cards => cards;
        internal RectTransform RewardSource => actionButton != null
            ? actionButton.transform as RectTransform : null;
        public bool IsReady => sideButton != null && popupRoot != null
            && closeButton != null && actionButton != null && actionText != null
            && countdownText != null && cards != null && cards.Length == 3
            && Array.TrueForAll(cards, card => card != null && card.IsReady);

        internal void Connect(BartenderDailyOrdersPresenter presenter)
        {
            sideButton.onClick.AddListener(presenter.Open);
            closeButton.onClick.AddListener(presenter.Close);
            if (backdropButton != null) backdropButton.onClick.AddListener(presenter.Close);
            actionButton.onClick.AddListener(presenter.HandleAction);
            SetVisible(false);
        }

        internal void Disconnect(BartenderDailyOrdersPresenter presenter)
        {
            if (sideButton != null) sideButton.onClick.RemoveListener(presenter.Open);
            if (closeButton != null) closeButton.onClick.RemoveListener(presenter.Close);
            if (backdropButton != null) backdropButton.onClick.RemoveListener(presenter.Close);
            if (actionButton != null) actionButton.onClick.RemoveListener(presenter.HandleAction);
        }

        public void SetVisible(bool visible)
        {
            if (popupRoot == null) return;
            popupRoot.SetActive(visible);
            if (visible) popupRoot.transform.SetAsLastSibling();
        }

        public void SetFeedback(string text)
        {
            if (feedbackText != null) feedbackText.text = text ?? string.Empty;
        }

        public void Render(BartenderDailyOrdersSnapshot snapshot, bool available)
        {
            TimeSpan remaining = snapshot.RemainingUntilReset(BartenderProgressService.DailyUtcNowTicks);
            string timer = string.Format("{0:00}:{1:00}:{2:00}",
                (int)remaining.TotalHours, remaining.Minutes, remaining.Seconds);
            if (countdownText != null) countdownText.text = timer;
            if (sideTimerText != null) sideTimerText.text = timer;
            if (completedText != null)
                completedText.text = snapshot.CompletedTaskCount + " / 3 COMPLETE";
            if (completionBonusText != null)
                completionBonusText.gameObject.SetActive(false);
            if (actionText != null) actionText.text = snapshot.RewardClaimed ? "COMPLETED" : snapshot.CanClaim
                ? $"CLAIM {BartenderDailyOrdersTuning.TotalRewardCoins} COINS" : "PLAY";
            if (actionButton != null) actionButton.interactable = available && !snapshot.RewardClaimed;
            if (sideButton != null) sideButton.interactable = available && !snapshot.RewardClaimed;
            if (badge != null) badge.Render(snapshot);
        }
    }
}
