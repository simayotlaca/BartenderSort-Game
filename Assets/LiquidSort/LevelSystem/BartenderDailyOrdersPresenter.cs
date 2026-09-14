using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    public sealed class BartenderDailyOrdersPresenter : MonoBehaviour
    {
        // Both pieces are authored prefabs; this presenter only places, fills and animates them.
        private const string SideEntryPrefabPath = "Ui/DailyOrders/DailyOrdersSideEntry";
        private const string PopupPrefabPath = "Ui/DailyOrders/DailyOrdersPopup";
        private const string IntroductionPrefabPath = "Ui/DailyOrders/DailyOrdersIntroduction";
        private const string IntroductionId = "daily_orders";
        private const int IntroductionVersion = 2;
        private const int UnlockAtLevel = 3;
        private const string CountdownTextFormat =
            "<mspace=0.62em>{0:00}</mspace><color=#FFCD36>:</color>"
            + "<mspace=0.62em>{1:00}</mspace><color=#FFCD36>:</color>"
            + "<mspace=0.62em>{2:00}</mspace>";

        private static readonly Color Cream = new Color32(255, 245, 213, 255);
        private static readonly Color Mint = new Color32(168, 255, 180, 255);
        private static readonly Color LockedTint = new Color(0.62f, 0.6f, 0.68f, 1f);
        private static readonly Color AvailableBacklight = new Color(1f, 0.86f, 0.48f, 0.22f);
        private static readonly Color ReadyBacklight = new Color(1f, 0.86f, 0.48f, 0.42f);
        private const float PanelDisplayScale = 1.1f;
        private const float IntroductionVolume = 0.85f;
        private const float ClaimCloseDelay = 0.7f;
        private const float ClaimVolume = 0.85f;
        private const float AutoOpenDelay = 0.6f;
        private const float AutoOpenCheckInterval = 0.25f;

        private BartenderMainMenuPresenter menu;
        private DailyOrdersSideEntryView side;
        private BartenderDailyOrdersView popup;
        private Sprite playActionSprite;
        private bool subscribed;
        private float nextTimerRefresh;
        private long presentedDay;
        private bool rememberAfterReveal;
        private float nextActionAllowedAt;
        private long displayedActionDay;
        private bool displayedActionIsClaim;
        private bool claimPending;
        private int presentationRevision;
        private Vector3 sideLockRestScale = Vector3.one;   // the prefab's authored scale; the pop animates to it
        private bool sideLocked;
        private bool sideLockShown;
        private int shownHomeMode = -1;
        private long shownHomeDay;
        private bool actionPulsing;
        private bool closing;
        private bool rewardHandoff;
        private Tween claimCloseTween;
        private float autoOpenAt = -1f;
        private float nextAutoOpenCheck;
        private long autoOpenedDay = -1L;
        private int autoOpenedMask;
        private Tween autoOpenNudge;
        private BartenderDailyOrdersSnapshot latestSnapshot;
        private bool hasLatestSnapshot;
        private bool introductionSeen;
        private RectTransform introduction;
        private TextMeshProUGUI introductionText;
        private BartenderTutorialFocusScrim introductionScrim;
        public bool IsOpen => popup != null && popup.gameObject.activeSelf;

        private DailyOrderCardView[] Cards => popup != null ? popup.Cards : Array.Empty<DailyOrderCardView>();
        private GameObject SideEntry => side != null ? side.gameObject : null;
        private RectTransform ActionTransform => popup != null ? popup.ActionButton.transform as RectTransform : null;
        private static bool IsUnlocked =>
            BartenderProgressService.NextUnlockedCampaignSlot + 1 >= UnlockAtLevel;

        internal static void Ensure(BartenderMainMenuPresenter owner)
        {
            if (owner == null) return;
            BartenderDailyOrdersPresenter presenter =
                owner.GetComponent<BartenderDailyOrdersPresenter>();
            if (presenter == null)
                presenter = owner.gameObject.AddComponent<BartenderDailyOrdersPresenter>();
            presenter.Bind(owner);
        }

        private void Bind(BartenderMainMenuPresenter owner)
        {
            if (menu == owner && popup != null) return;
            menu = owner;
            introductionSeen = BartenderTutorialProgress.IsCompleted(IntroductionId, IntroductionVersion);
            if (!PlaceUi()) return;
            if (isActiveAndEnabled) Subscribe();
            Refresh(BartenderProgressService.DailyOrders);
            RefreshIntroduction();
        }

        private void OnEnable()
        {
            Subscribe();
            if (popup != null)
                Refresh(BartenderProgressService.DailyOrders);
        }

        private void OnDisable()
        {
            ++presentationRevision;
            Unsubscribe();
            HideIntroduction();
            StopAutoOpenNudge();
            autoOpenAt = -1f;
            SetOverlayVisible(false, false);
        }

        private void OnDestroy()
        {
            Unsubscribe();
            HideIntroduction();
        }

        private void Update()
        {
            RefreshIntroduction();
            UpdateAutoOpen();
            if (IsOpen && !rewardHandoff && (menu == null || !menu.CanUseHomeFeatures))
            {
                SetOverlayVisible(false, false);
                return;
            }

            if (IsOpen && Input.GetKeyDown(KeyCode.Escape))
                CloseAnimated();

            if (IsOpen && rememberAfterReveal
                && Array.TrueForAll(Cards, card => card != null && !card.IsAnimating))
                RememberProgress();

            if (Time.unscaledTime < nextTimerRefresh) return;
            nextTimerRefresh = Time.unscaledTime + 1f;
            if (popup != null)
                Refresh(BartenderProgressService.DailyOrders);
        }

        private void Subscribe()
        {
            if (subscribed || menu == null) return;
            BartenderProgressService.DailyOrdersChanged += Refresh;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed) return;
            BartenderProgressService.DailyOrdersChanged -= Refresh;
            subscribed = false;
        }

        private bool PlaceUi()
        {
            if (menu == null) return false;
            if (popup != null) return true;
            DailyOrdersSideEntryView sidePrefab = Resources.Load<DailyOrdersSideEntryView>(SideEntryPrefabPath);
            BartenderDailyOrdersView popupPrefab = Resources.Load<BartenderDailyOrdersView>(PopupPrefabPath);
            if (sidePrefab == null || popupPrefab == null || !sidePrefab.IsReady || !popupPrefab.IsReady)
            {
                Debug.LogError("Daily Orders prefabs are missing or incomplete under Resources/Ui/DailyOrders.", this);
                return false;
            }

            Transform home = FindNamed(menu.transform, "Home Page");
            Transform sideParent = home != null
                ? FindNamed(home, "10_CustomSafeAreaRoot_iPhone")
                : null;
            if (sideParent == null) sideParent = home != null ? home : menu.transform;
            side = Instantiate(sidePrefab, sideParent, false);
            side.name = sidePrefab.name;
            side.Button.onClick.AddListener(Open);
            if (side.LockOverlay != null) sideLockRestScale = side.LockOverlay.localScale;

            Canvas canvas = menu.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.rootCanvas != null) canvas = canvas.rootCanvas;
            popup = Instantiate(popupPrefab, canvas != null ? canvas.transform : menu.transform, false);
            popup.name = popupPrefab.name;
            popup.gameObject.SetActive(false);
            if (popup.BackdropButton != null) popup.BackdropButton.onClick.AddListener(CloseAnimated);
            popup.CloseButton.onClick.AddListener(CloseAnimated);
            popup.ActionButton.onClick.AddListener(HandleAction);
            playActionSprite = popup.ActionImage.sprite;
            if (popup.CompletionBonusText != null)
                popup.CompletionBonusText.text =
                    $"<color=#FFDC68>+{BartenderDailyOrdersTuning.CompletionBonus} completion bonus</color>\n"
                    + $"{BartenderDailyOrdersTuning.TotalRewardCoins} coins total";
            return true;
        }

        public void Open()
        {
            if (!IsUnlocked || IsOpen || menu == null || !menu.CanUseHomeFeatures
                || claimPending || menu.MenuSavePending
                || !BartenderProgressService.IsAvailable
                || popup == null) return;
            BartenderDailyOrdersSnapshot snapshot = BartenderProgressService.DailyOrders;
            if (snapshot.RewardClaimed)
            {
                Refresh(snapshot);
                return;
            }
            menu.PrepareDailyOrdersPresentation();
            HideIntroduction();
            SetFeedback(string.Empty);
            SetOverlayVisible(true, true);
            if (IsOpen && !introductionSeen)
            {
                introductionSeen = true;
                BartenderTutorialProgress.Complete(IntroductionId, IntroductionVersion);
            }
            presentedDay = snapshot.UtcDayKey;
            ApplyTasks(BartenderDailyOrdersPresentationStore.Load(snapshot), false);
            Refresh(snapshot);
            rememberAfterReveal = true;
        }

        public void Close() => CloseAnimated();

        public async void HandleAction()
        {
            if (!IsOpen || menu == null || !menu.CanUseHomeFeatures
                || claimPending || menu.MenuSavePending
                || Time.unscaledTime < nextActionAllowedAt) return;
            // A double tap on CLAIM must not become a PLAY tap after the first save.
            nextActionAllowedAt = Time.unscaledTime + 0.35f;
            nextTimerRefresh = nextActionAllowedAt;
            long expectedDay = displayedActionDay;
            bool intendedClaim = displayedActionIsClaim;
            BartenderDailyOrdersSnapshot snapshot =
                BartenderProgressService.DailyOrders;
            if (menu.MenuSavePending)
            {
                Refresh(snapshot);
                return;
            }
            if (snapshot.RewardClaimed)
            {
                Refresh(snapshot);
                return;
            }
            if (snapshot.UtcDayKey != expectedDay || snapshot.CanClaim != intendedClaim)
            {
                Refresh(snapshot);
                SetFeedback(string.Empty);
                return;
            }
            if (intendedClaim)
            {
                int revision = presentationRevision;
                BartenderMainMenuPresenter owner = menu;
                claimPending = true;
                // No saving/error text on the panel; the claim button is disabled while it saves.
                SetFeedback(string.Empty);
                Refresh(snapshot);
                BartenderSaveResult result;
                try
                {
                    result = await owner.ClaimDailyOrdersForPresentationAsync(
                        expectedDay, RewardSourceViewport());
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                    result = new BartenderSaveResult(false, "The daily reward could not be saved");
                }
                finally
                {
                    if (this != null)
                    {
                        claimPending = false;
                        if (isActiveAndEnabled)
                        {
                            Refresh(BartenderProgressService.DailyOrders);
                            if (!IsOpen || menu != owner || revision != presentationRevision)
                                SetFeedback(string.Empty);
                        }
                    }
                }
                if (this == null || !isActiveAndEnabled || !IsOpen
                    || menu != owner || revision != presentationRevision) return;
                if (result.Succeeded)
                {
                    SetFeedback(string.Empty);
                    Refresh(BartenderProgressService.DailyOrders);
                    CelebrateClaim();
                }
                else
                {
                    Refresh(BartenderProgressService.DailyOrders);
                    SetFeedback(string.Empty);
                }
                return;
            }

            SetOverlayVisible(false, true);
            menu?.StartGameFromDailyOrders();
        }

        private void SetOverlayVisible(bool visible, bool playSound)
        {
            if (IsOpen == visible) return;
            ++presentationRevision;
            closing = false;
            if (!visible)
            {
                RememberProgress();
                rewardHandoff = false;
                claimCloseTween?.Kill();
                claimCloseTween = null;
                nextTimerRefresh = 0f;
            }
            if (popup != null)
            {
                popup.gameObject.SetActive(visible);
                if (visible)
                {
                    popup.transform.SetAsLastSibling();
                    PlayOpenTransition();
                }
                else SetActionPulse(false);
            }
            if (!visible) menu?.PresentDailyOrdersReward();
            if (!playSound) return;
            BsAudio.Instance?.Play(
                visible ? BsSfx.PopupOpen : BsSfx.PopupClose, 0.8f);
        }

        private void Refresh(BartenderDailyOrdersSnapshot snapshot)
        {
            latestSnapshot = snapshot;
            hasLatestSnapshot = true;
            if (introductionText != null) introductionText.text = IntroductionCopy(snapshot);
            displayedActionDay = snapshot.UtcDayKey;
            displayedActionIsClaim = snapshot.CanClaim;
            bool available = BartenderProgressService.IsAvailable
                && !claimPending && (menu == null || !menu.MenuSavePending)
                && Time.unscaledTime >= nextActionAllowedAt;
            long nowTicks = BartenderProgressService.DailyUtcNowTicks;
            if (side != null) side.Badge.Render(snapshot);
            UpdateSideStatus(snapshot, available, nowTicks);
            // The closed popup keeps its last values; Open() refreshes it before it is shown.
            if (!IsOpen) return;

            bool sameDay = presentedDay == snapshot.UtcDayKey;
            presentedDay = snapshot.UtcDayKey;
            ApplyTasks(snapshot, sameDay);
            if (Array.Exists(Cards, card => card != null && card.IsAnimating))
                rememberAfterReveal = true;
            TimeSpan remaining = snapshot.RemainingUntilReset(nowTicks);
            int hours = Mathf.FloorToInt((float)remaining.TotalHours);
            if (popup.CountdownText != null)
                popup.CountdownText.text = string.Format(
                    CountdownTextFormat, hours, remaining.Minutes, remaining.Seconds);
            if (popup.CompletedText != null)
                popup.CompletedText.text = "<color=#FFDC68>" + snapshot.CompletedTaskCount
                    + " / 3</color> complete";

            RefreshActionVisual(snapshot);
            popup.ActionButton.interactable = available && !closing && !snapshot.RewardClaimed;
            SetActionPulse(snapshot.CanClaim && available && !closing);
        }

        private void RefreshActionVisual(BartenderDailyOrdersSnapshot snapshot)
        {
            bool canClaim = snapshot.CanClaim && !snapshot.RewardClaimed;
            // The approved artwork includes its lettering. Keep a text-free frame
            // available so future reward tuning cannot display an outdated amount.
            Sprite claimArtwork = popup.ClaimArtwork;
            Sprite claimFrame = popup.ClaimFrame;
            bool useClaimArtwork = canClaim && claimArtwork != null
                && BartenderDailyOrdersTuning.TotalRewardCoins == 500;
            popup.ActionImage.sprite = useClaimArtwork ? claimArtwork
                : canClaim && claimFrame != null ? claimFrame : playActionSprite;
            TextMeshProUGUI actionLabel = popup.ActionLabel;
            actionLabel.gameObject.SetActive(!useClaimArtwork);
            actionLabel.text = snapshot.RewardClaimed ? "COMPLETED" : canClaim
                ? $"CLAIM\n{BartenderDailyOrdersTuning.TotalRewardCoins} COINS" : "PLAY";
            actionLabel.fontSize = snapshot.RewardClaimed ? 30f : canClaim ? 32f : 42f;
        }

        private void RefreshIntroduction()
        {
            GameObject entry = SideEntry;
            if (menu == null || entry == null) return;
            bool ready = menu.CanShowHomeIntroduction && !IsOpen;
            bool visible = IsUnlocked && (introductionSeen || ready);
            if (entry.activeSelf != visible) entry.SetActive(visible);
            if (introduction != null)
            {
                if (!visible || !ready) HideIntroduction();
                return;
            }
            if (!visible || !ready || introductionSeen) return;
            if (!BartenderProgressService.IsAvailable || BartenderProgressService.DailyOrders.RewardClaimed)
                return;
            // The prefab root is the focus scrim; its first child is the callout.
            BartenderTutorialFocusScrim prefab =
                Resources.Load<BartenderTutorialFocusScrim>(IntroductionPrefabPath);
            if (prefab == null || prefab.transform.childCount == 0)
            {
                // Keep the entry usable if its optional hint asset is missing; retry next menu visit.
                introductionSeen = true;
                Debug.LogWarning("Daily Orders introduction prefab is missing.", this);
                return;
            }
            Canvas rootCanvas = entry.GetComponentInParent<Canvas>().rootCanvas;
            introductionScrim = Instantiate(prefab, rootCanvas.transform, false);
            introductionScrim.name = prefab.name;
            introduction = introductionScrim.transform.GetChild(0) as RectTransform;
            introductionScrim.Focus(entry.transform as RectTransform, introduction);
            introductionText = introduction.GetComponentInChildren<TextMeshProUGUI>(true);
            introductionText.text = IntroductionCopy(BartenderProgressService.DailyOrders);
            introduction.localScale = Vector3.one * 0.88f;
            introduction.DOScale(Vector3.one, 0.3f).SetEase(Ease.OutBack)
                .SetUpdate(true).SetRecyclable(true);
            BsAudio.Instance?.Play(BsSfx.DailyRewardHint, IntroductionVolume);
        }

        private void UpdateAutoOpen()
        {
            if (autoOpenNudge != null) return;
            GameObject entry = SideEntry;
            bool calm = hasLatestSnapshot && menu != null && introductionSeen && introduction == null
                && !IsOpen && !closing && !claimPending
                && entry != null && entry.activeInHierarchy
                && menu.CanShowHomeIntroduction && BartenderProgressService.IsAvailable;
            if (!calm)
            {
                autoOpenAt = -1f;
                return;
            }
            if (Time.unscaledTime < nextAutoOpenCheck) return;
            nextAutoOpenCheck = Time.unscaledTime + AutoOpenCheckInterval;

            BartenderDailyOrdersSnapshot actual = latestSnapshot;
            int fresh = CompletionMask(actual)
                & ~CompletionMask(BartenderDailyOrdersPresentationStore.Load(actual));
            if (autoOpenedDay == actual.UtcDayKey) fresh &= ~autoOpenedMask;
            if (actual.RewardClaimed || fresh == 0)
            {
                autoOpenAt = -1f;
                return;
            }
            if (autoOpenAt < 0f)
            {
                autoOpenAt = Time.unscaledTime + AutoOpenDelay;
                return;
            }
            if (Time.unscaledTime < autoOpenAt) return;

            autoOpenAt = -1f;
            long day = actual.UtcDayKey;
            Transform board = entry.transform;
            Vector3 rest = board.localScale;
            autoOpenNudge = board.DOScale(rest * 1.1f, 0.14f)
                .SetEase(Ease.OutCubic)
                .SetLoops(2, LoopType.Yoyo)
                .SetUpdate(true)
                .SetLink(entry)
                .OnComplete(() =>
                {
                    autoOpenNudge = null;
                    board.localScale = rest;
                    Open();
                    if (!IsOpen) return;
                    autoOpenedMask = (autoOpenedDay == day ? autoOpenedMask : 0) | fresh;
                    autoOpenedDay = day;
                })
                .OnKill(() =>
                {
                    autoOpenNudge = null;
                    if (board != null) board.localScale = rest;
                });
        }

        private void StopAutoOpenNudge()
        {
            Tween nudge = autoOpenNudge;
            autoOpenNudge = null;
            if (nudge != null && nudge.IsActive()) nudge.Kill(false);
        }

        private static int CompletionMask(BartenderDailyOrdersSnapshot snapshot) =>
            (snapshot.DeliveredOrders >= BartenderDailyOrdersTuning.DeliveredOrderTarget ? 1 : 0)
            | (snapshot.WonLevels >= BartenderDailyOrdersTuning.WonLevelTarget ? 2 : 0)
            | (snapshot.ServedUnits >= BartenderDailyOrdersTuning.ServedUnitTarget ? 4 : 0);

        private static string IntroductionCopy(BartenderDailyOrdersSnapshot snapshot) =>
            snapshot.CanClaim ? "Your daily reward is ready!\nTap the board to collect."
            : "Daily Orders unlocked!\nTap the board to open.";

        private void HideIntroduction()
        {
            if (introduction != null) introduction.DOKill(false);
            if (introductionScrim != null)
            {
                introductionScrim.gameObject.SetActive(false);
                Destroy(introductionScrim.gameObject);
            }
            introduction = null;
            introductionText = null;
            introductionScrim = null;
        }

        private void ApplyTasks(BartenderDailyOrdersSnapshot snapshot, bool animate)
        {
            ApplyTask(0, "Deliver 5 orders", snapshot.DeliveredOrders,
                BartenderDailyOrdersTuning.DeliveredOrderTarget, animate);
            ApplyTask(1, "Win 2 levels", snapshot.WonLevels,
                BartenderDailyOrdersTuning.WonLevelTarget, animate);
            ApplyTask(2, "Serve 10 units", snapshot.ServedUnits,
                BartenderDailyOrdersTuning.ServedUnitTarget, animate);
        }

        private void ApplyTask(int index, string title, int value, int target, bool animate)
        {
            DailyOrderCardView[] cards = Cards;
            if (index < 0 || index >= cards.Length || cards[index] == null) return;
            if (animate)
                cards[index].AnimateProgress(title, value, target,
                    BartenderDailyOrdersTuning.RewardPerTask, index * 0.10f);
            else cards[index].SetProgress(title, value, target,
                BartenderDailyOrdersTuning.RewardPerTask);
        }

        private void SetFeedback(string text)
        {
            TextMeshProUGUI feedback = popup != null ? popup.FeedbackText : null;
            if (feedback == null) return;
            feedback.rectTransform.DOKill();
            feedback.rectTransform.localScale = Vector3.one;
            feedback.color = Cream;
            feedback.text = text ?? string.Empty;
        }

        private void SetActionPulse(bool on)
        {
            RectTransform actionTransform = ActionTransform;
            if (actionTransform == null || actionPulsing == on) return;
            actionPulsing = on;
            actionTransform.DOKill();
            actionTransform.localScale = Vector3.one;
            if (!on) return;
            actionTransform.DOScale(1.04f, 0.6f)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo)
                .SetUpdate(true)
                .SetLink(actionTransform.gameObject);
        }

        private Vector2 RewardSourceViewport()
        {
            RectTransform source = ActionTransform;
            if (source == null && popup != null) source = popup.Panel;
            if (source == null) return new Vector2(0.5f, 0.4f);
            Canvas canvas = source.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera : null;
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera,
                source.TransformPoint(source.rect.center));
            return new Vector2(Mathf.Clamp01(screen.x / Mathf.Max(1, Screen.width)),
                Mathf.Clamp01(screen.y / Mathf.Max(1, Screen.height)));
        }

        private void CelebrateClaim()
        {
            SetActionPulse(false);
            popup.ActionButton.interactable = false;
            SetFeedback("+" + BartenderDailyOrdersTuning.TotalRewardCoins + " COINS COLLECTED!");
            TextMeshProUGUI feedback = popup.FeedbackText;
            if (feedback != null)
            {
                feedback.color = Mint;
                RectTransform rect = feedback.rectTransform;
                rect.DOKill();
                rect.localScale = Vector3.one * 0.6f;
                rect.DOScale(1f, 0.34f)
                    .SetEase(Ease.OutBack)
                    .SetUpdate(true)
                    .SetLink(rect.gameObject);
            }
            RectTransform actionTransform = ActionTransform;
            actionTransform.DOKill();
            actionTransform.localScale = Vector3.one;
            actionTransform.DOScale(1.12f, 0.12f)
                .SetEase(Ease.OutCubic)
                .SetLoops(2, LoopType.Yoyo)
                .SetUpdate(true)
                .SetLink(actionTransform.gameObject);
            BsAudio.Instance?.Play(BsSfx.WinCoinReward, ClaimVolume);
            if (menu == null) return;

            rewardHandoff = true;
            menu.PresentDailyOrdersReward();
            claimCloseTween?.Kill();
            claimCloseTween = DOVirtual.DelayedCall(ClaimCloseDelay, () =>
                {
                    claimCloseTween = null;
                    CloseAnimated();
                }, true)
                .SetLink(gameObject);
        }

        private void CloseAnimated()
        {
            if (!IsOpen || closing || claimPending || (menu != null && menu.MenuSavePending)) return;
            CanvasGroup overlayGroup = popup.Group;
            RectTransform panelTransform = popup.Panel;
            closing = true;
            SetActionPulse(false);
            overlayGroup.DOKill();
            panelTransform.DOKill();
            overlayGroup.interactable = false;
            overlayGroup.blocksRaycasts = true;
            panelTransform.DOScale(PanelDisplayScale * 0.975f, 0.30f)
                .SetEase(Ease.InOutSine)
                .SetUpdate(true)
                .SetLink(popup.gameObject);
            DOTween.To(() => overlayGroup.alpha, a => overlayGroup.alpha = a, 0f, 0.30f)
                .SetEase(Ease.InOutSine)
                .SetUpdate(true)
                .SetTarget(overlayGroup)
                .SetLink(popup.gameObject)
                .OnComplete(() => SetOverlayVisible(false, !rewardHandoff));
        }

        private void PlayOpenTransition()
        {
            CanvasGroup overlayGroup = popup.Group;
            RectTransform panelTransform = popup.Panel;
            overlayGroup.DOKill();
            panelTransform.DOKill();
            overlayGroup.alpha = 0f;
            overlayGroup.interactable = true;
            overlayGroup.blocksRaycasts = true;
            panelTransform.localScale = Vector3.one * (PanelDisplayScale * 0.965f);
            DOTween.To(() => overlayGroup.alpha, a => overlayGroup.alpha = a, 1f, 0.28f)
                .SetEase(Ease.InOutSine)
                .SetUpdate(true)
                .SetTarget(overlayGroup)
                .SetLink(popup.gameObject);
            panelTransform.DOScale(PanelDisplayScale, 0.32f)
                .SetEase(Ease.OutCubic)
                .SetUpdate(true)
                .SetLink(popup.gameObject);
        }

        private void UpdateSideStatus(BartenderDailyOrdersSnapshot snapshot, bool available, long nowTicks)
        {
            if (side == null) return;
            int mode = snapshot.RewardClaimed ? 2 : snapshot.CanClaim ? 1 : 0;
            bool modeChanged = mode != shownHomeMode || shownHomeDay != snapshot.UtcDayKey;
            bool revealReward = shownHomeMode == 0 && mode == 1
                && shownHomeDay == snapshot.UtcDayKey;
            shownHomeMode = mode;
            shownHomeDay = snapshot.UtcDayKey;
            side.Button.interactable = available && !snapshot.RewardClaimed;
            UpdateSideLock(snapshot.RewardClaimed && !IsOpen);

            TextMeshProUGUI timer = side.TimerText;
            timer.text = snapshot.CanClaim
                ? $"CLAIM\n<size=90%>{BartenderDailyOrdersTuning.TotalRewardCoins} COINS</size>"
                : snapshot.CompactCountdown(nowTicks);
            TextMeshProUGUI resetHint = side.ResetHint;
            if (resetHint != null && mode == 1)
                resetHint.text = "Resets in " + snapshot.CompactCountdown(nowTicks);
            // Styling, glow tint and the plate pop only change with the board's mode, not every second.
            if (!modeChanged) return;

            Image backlight = side.Backlight;
            if (backlight != null)
            {
                backlight.color = snapshot.CanClaim ? ReadyBacklight : AvailableBacklight;
                backlight.gameObject.SetActive(!snapshot.RewardClaimed);
            }
            timer.fontSize = mode == 1 ? 23f : 26f;
            timer.lineSpacing = mode == 1 ? -18f : 0f;
            if (resetHint != null) resetHint.gameObject.SetActive(mode == 1);
            Image timerPlate = side.TimerPlate;
            if (timerPlate != null && (mode != 1 || revealReward))
            {
                RectTransform rect = timerPlate.rectTransform;
                rect.DOKill();
                rect.localScale = Vector3.one;
                if (revealReward && Application.isPlaying && side.gameObject.activeInHierarchy)
                    rect.DOScale(1.055f, 0.18f).SetEase(Ease.OutCubic)
                        .SetLoops(2, LoopType.Yoyo).SetUpdate(true).SetLink(rect.gameObject);
            }
        }

        private void UpdateSideLock(bool locked)
        {
            if (sideLockShown && locked == sideLocked) return;
            bool animate = sideLockShown && Application.isPlaying
                && side.gameObject.activeInHierarchy;
            sideLocked = locked;
            sideLockShown = true;
            Color tint = locked ? LockedTint : Color.white;
            TintSide(side.Artwork, tint, animate);
            Image[] checks = side.TaskChecks;
            for (int i = 0; checks != null && i < checks.Length; i++)
                TintSide(checks[i], tint, animate);
            RectTransform sideLock = side.LockOverlay;
            if (sideLock == null) return;
            sideLock.DOKill();
            sideLock.gameObject.SetActive(locked);
            sideLock.localScale = locked && animate ? Vector3.zero : sideLockRestScale;
            if (locked && animate)
                sideLock.DOScale(sideLockRestScale, 0.5f)
                    .SetEase(Ease.OutBack, 2.2f)
                    .SetUpdate(true)
                    .SetLink(sideLock.gameObject);
        }

        private static void TintSide(Image image, Color tint, bool animate)
        {
            if (image == null) return;
            image.DOKill();
            if (!animate)
            {
                image.color = tint;
                return;
            }
            DOTween.To(() => image.color, c => image.color = c, tint, 0.45f)
                .SetUpdate(true)
                .SetTarget(image)
                .SetLink(image.gameObject);
        }

        private void RememberProgress()
        {
            if (presentedDay <= 0) return;
            BartenderDailyOrdersPresentationStore.Save(presentedDay, Cards);
            rememberAfterReveal = false;
        }

        private static Transform FindNamed(Transform root, string name)
        {
            if (root == null) return null;
            if (string.Equals(root.name, name, StringComparison.Ordinal)) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform match = FindNamed(root.GetChild(i), name);
                if (match != null) return match;
            }
            return null;
        }
    }
}
