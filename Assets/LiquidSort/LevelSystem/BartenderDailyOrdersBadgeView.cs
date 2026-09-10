using TMPro;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>Prefab-owned art, driven only by committed daily progress.</summary>
    [DisallowMultipleComponent]
    public sealed class BartenderDailyOrdersBadgeView : MonoBehaviour
    {
        [SerializeField] private GameObject badgeRoot;
        [SerializeField] private TextMeshProUGUI remainingText;
        [SerializeField] private GameObject readyCheck;
        [SerializeField] private CanvasGroup readyGlow;
        private bool rewardReady;

        internal void Bind(GameObject badge, TextMeshProUGUI remaining,
            GameObject check, CanvasGroup glow)
        {
            badgeRoot = badge;
            remainingText = remaining;
            readyCheck = check;
            readyGlow = glow;
        }

        public void Render(BartenderDailyOrdersSnapshot snapshot)
        {
            rewardReady = snapshot.CanClaim;
            if (badgeRoot != null) badgeRoot.SetActive(!snapshot.RewardClaimed);
            if (remainingText != null)
            {
                remainingText.gameObject.SetActive(!snapshot.IsComplete);
                remainingText.text = (3 - snapshot.CompletedTaskCount).ToString();
            }
            if (readyCheck != null) readyCheck.SetActive(rewardReady);
            ApplyGlow();
        }

        private void Update() { if (rewardReady) ApplyGlow(); }
        private void OnDisable() { if (readyGlow != null) readyGlow.alpha = 0f; }

        private void ApplyGlow()
        {
            if (readyGlow == null) return;
            readyGlow.interactable = false;
            readyGlow.blocksRaycasts = false;
            readyGlow.alpha = rewardReady
                ? 0.12f + 0.12f * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 2.4f))
                : 0f;
        }
    }
}
