using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>Updates the prefab's coin label with live balance data. The prefab owns its art and layout.</summary>
    [DisallowMultipleComponent]
    public sealed class BartenderCoinBalanceView : MonoBehaviour
    {
        /// <summary>Which point in the reward flow the badge represents.</summary>
        public enum BalanceMoment
        {
            Current,
            BeforeRoundReward,
        }

        [Tooltip("Authored balance label in this prefab.")]
        [SerializeField] private Text countLabel;
        [Tooltip("Which point in the reward flow this badge represents.")]
        [SerializeField] private BalanceMoment moment = BalanceMoment.Current;

        private bool subscribed;
        private int shownValue = int.MinValue;

        public bool IsReady => countLabel != null;

        public void SetMoment(BalanceMoment value)
        {
            moment = value;
            shownValue = int.MinValue;
            Refresh();
        }

        private void OnEnable()
        {
            Subscribe();
            Refresh();
        }

        private void OnDisable() => Unsubscribe();

        private void OnDestroy() => Unsubscribe();

        private void Subscribe()
        {
            if (subscribed) return;
            BartenderProgressService.CoinsChanged += HandleCoinsChanged;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed) return;
            BartenderProgressService.CoinsChanged -= HandleCoinsChanged;
            subscribed = false;
        }

        private void HandleCoinsChanged(int coins) => Refresh();

        /// <summary>Refreshes the authored label from the progress service.</summary>
        public void Refresh()
        {
            if (countLabel == null) return;
            int value = ResolveValue();
            if (value == shownValue) return;
            shownValue = value;
            countLabel.text = value.ToString();
        }

        private int ResolveValue()
        {
            int coins = Mathf.Max(0, BartenderProgressService.Coins);
            if (moment != BalanceMoment.BeforeRoundReward) return coins;
            return Mathf.Max(0, coins - BartenderProgressService.WinCoinReward);
        }
    }
}
