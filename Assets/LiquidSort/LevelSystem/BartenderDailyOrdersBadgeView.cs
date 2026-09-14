using TMPro;
using UnityEngine;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    public sealed class BartenderDailyOrdersBadgeView : MonoBehaviour
    {
        [SerializeField] private GameObject badgeRoot;
        [SerializeField] private TextMeshProUGUI remainingText;
        [SerializeField] private GameObject readyCheck;
        [SerializeField] private CanvasGroup readyGlow;
        [SerializeField] private RectTransform[] taskChecks;
        private bool rewardReady;
        private bool hasSnapshot;
        private BartenderDailyOrdersSnapshot snapshot;
        private long displayedDay;
        private int displayedMask;
        private int fromMask;
        private int targetMask;
        private bool revealing;
        private float revealStarted;
        private float glowStarted = float.NegativeInfinity;
        private bool glowShown;
        private int appliedState = -1;
        private static readonly string[] RemainingLabels = { "3", "2", "1", "0" };
        private static long sessionDay = -1L;
        private static int sessionMask;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSession() { sessionDay = -1L; sessionMask = 0; }

        public void Render(BartenderDailyOrdersSnapshot snapshot)
        {
            bool hadSnapshot = hasSnapshot;
            this.snapshot = snapshot;
            hasSnapshot = true;
            if (!gameObject.activeInHierarchy) return;
            rewardReady = snapshot.CanClaim;
            if (badgeRoot != null) badgeRoot.SetActive(!snapshot.RewardClaimed);
            int nextMask = CompletionMask(snapshot);
            if (revealing && snapshot.UtcDayKey == displayedDay && nextMask == targetMask
                && !snapshot.RewardClaimed) return;
            int previous = hadSnapshot && displayedDay == snapshot.UtcDayKey ? targetMask
                : sessionDay == snapshot.UtcDayKey ? sessionMask : nextMask;
            bool canReveal = Application.isPlaying && isActiveAndEnabled
                && !snapshot.RewardClaimed && nextMask != previous && (previous & ~nextMask) == 0;
            displayedDay = snapshot.UtcDayKey;
            fromMask = previous;
            targetMask = nextMask;
            bool wasRevealing = revealing;
            revealing = canReveal;
            revealStarted = Time.unscaledTime;
            if (canReveal && snapshot.CanClaim) glowStarted = revealStarted + .12f * CountBits(nextMask & ~previous);
            else if (!snapshot.CanClaim) glowStarted = float.NegativeInfinity;
            ApplyMask(canReveal ? previous : nextMask, canReveal || wasRevealing);
            if (!canReveal) RememberShown();
            ApplyGlow();
        }

        private static int CompletionMask(BartenderDailyOrdersSnapshot value) =>
            (value.DeliveredOrders >= BartenderDailyOrdersTuning.DeliveredOrderTarget ? 1 : 0)
            | (value.WonLevels >= BartenderDailyOrdersTuning.WonLevelTarget ? 2 : 0)
            | (value.ServedUnits >= BartenderDailyOrdersTuning.ServedUnitTarget ? 4 : 0);

        private static int CountBits(int mask) => (mask & 1) + ((mask >> 1) & 1) + ((mask >> 2) & 1);

        // Writes the badge only when what it shows changes; force also resets reveal scales.
        private void ApplyMask(int mask, bool force = false)
        {
            displayedMask = mask;
            int state = mask | (rewardReady ? 8 : 0) | (snapshot.RewardClaimed ? 16 : 0);
            if (!force && state == appliedState) return;
            appliedState = state;
            if (remainingText != null)
            {
                remainingText.gameObject.SetActive(mask != 7 && !snapshot.RewardClaimed);
                remainingText.text = RemainingLabels[CountBits(mask)];
                remainingText.rectTransform.localScale = Vector3.one;
            }
            if (readyCheck != null) readyCheck.SetActive(rewardReady && mask == 7);
            if (taskChecks == null) return;
            for (int i = 0; i < taskChecks.Length && i < 3; i++)
            {
                if (taskChecks[i] == null) continue;
                taskChecks[i].gameObject.SetActive((mask & (1 << i)) != 0);
                taskChecks[i].localScale = Vector3.one;
            }
        }

        private void OnEnable() { if (hasSnapshot) Render(snapshot); }

        private void Update()
        {
            if (revealing)
            {
                float elapsed = Time.unscaledTime - revealStarted;
                int visible = fromMask;
                int order = 0;
                float lastStampAge = elapsed;
                for (int i = 0; i < 3; i++)
                {
                    int bit = 1 << i;
                    if ((targetMask & ~fromMask & bit) == 0) continue;
                    float age = elapsed - order++ * .12f;
                    if (age < 0f) continue;
                    visible |= bit;
                    lastStampAge = age;
                }
                ApplyMask(visible);
                order = 0;
                for (int i = 0; i < 3; i++)
                {
                    if ((targetMask & ~fromMask & (1 << i)) == 0) continue;
                    float t = Mathf.Clamp01((elapsed - order++ * .12f) / .32f);
                    float q = t - 1f;
                    float scale = 1f + 2.70158f * q * q * q + 1.70158f * q * q;
                    if (taskChecks != null && i < taskChecks.Length && taskChecks[i] != null)
                        taskChecks[i].localScale = Vector3.one * Mathf.Max(0f, scale);
                }
                if (remainingText != null)
                    remainingText.rectTransform.localScale = Vector3.one
                        * (1f + .16f * Mathf.Sin(Mathf.Clamp01(lastStampAge / .30f) * Mathf.PI));
                if (elapsed >= .34f + .12f * (order - 1))
                {
                    revealing = false;
                    ApplyMask(targetMask, true);
                    RememberShown();
                }
            }
            if (glowShown || (rewardReady && Time.unscaledTime - glowStarted < .8f)) ApplyGlow();
        }

        private void OnDisable()
        {
            if (hasSnapshot)
            {
                revealing = false;
                ApplyMask(targetMask, true);
                RememberShown();
            }
            glowStarted = float.NegativeInfinity;
            glowShown = false;
            if (readyGlow != null) readyGlow.alpha = 0f;
        }

        private void RememberShown()
        {
            if (!Application.isPlaying || !hasSnapshot) return;
            sessionDay = displayedDay;
            sessionMask = displayedMask;
        }

        // The glow is one 0.8 s pulse; outside it the authored alpha stays at 0 and nothing is written.
        private void ApplyGlow()
        {
            if (readyGlow == null) return;
            float age = Time.unscaledTime - glowStarted;
            bool pulsing = rewardReady && age >= 0f && age < .8f;
            if (!pulsing && !glowShown) return;
            readyGlow.alpha = pulsing ? .18f * Mathf.Sin(age / .8f * Mathf.PI) : 0f;
            glowShown = pulsing;
        }
    }
}
