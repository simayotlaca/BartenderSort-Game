using System;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Presents the daily-orders entry point and popup. Mission data belongs to the
    /// progress service; visual styling belongs to the authored card prefab.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderDailyOrdersPresenter : MonoBehaviour
    {
        [Header("Optional authored popup")]
        [SerializeField] private BartenderDailyOrdersView authoredView;
        private const string SideButtonSpritePath =
            "Ui/DailyOrders/DailyOrders_SideButton";
        private const string CounterBadgeSpritePath =
            "Ui/DailyOrders/DailyOrders_CounterBadge";
        private const string PanelSpritePath =
            "Ui/DailyOrders/DailyOrders_Panel_PurpleGold_HD";
        private const string CardPrefabPath = "Ui/DailyOrders/DailyOrderCard";
        private const string IntroductionPrefabPath = "Ui/DailyOrders/DailyOrdersIntroduction";
        private const string IntroductionId = "daily_orders";
        private const int IntroductionVersion = 2;
        private const int UnlockAtLevel = 3;
        private const string CreamCardSpritePath =
            "Ui/DailyOrders/DailyOrders_Card_CreamOrange_HD";
        private const string BlueCardSpritePath =
            "Ui/DailyOrders/DailyOrders_Card_Blue_Empty";
        private const string PurpleCardSpritePath =
            "Ui/DailyOrders/DailyOrders_Card_Purple_Empty";
        private const string HeaderArtworkSpritePath =
            "Ui/DailyOrders/DailyOrders_Header_TodaysOrders_HD";
        private const string TimerPlateSpritePath =
            "Ui/DailyOrders/DailyOrders_TimerPlate";
        private const string ClockSpritePath =
            "Ui/DailyOrders/DailyOrders_Icon_Clock";
        private const string PopupTextMaterialPath =
            "Ui/DailyOrders/DailyOrders_PopupText_SDF";
        private const string CountdownTextFormat =
            "<mspace=0.62em>{0:00}</mspace><color=#FFCD36>:</color>"
            + "<mspace=0.62em>{1:00}</mspace><color=#FFCD36>:</color>"
            + "<mspace=0.62em>{2:00}</mspace>";
        private const string ClipboardIconSpritePath =
            "Ui/DailyOrders/DailyOrders_Icon_Clipboard_Clean";
        private const string OrderGlassIconSpritePath =
            "Ui/DailyOrders/DailyOrders_Icon_OrderGlass_Clean";
        private const string WinsIconSpritePath =
            "Ui/DailyOrders/DailyOrders_Icon_Wins_Clean";
        private const string ServingIconSpritePath =
            "Ui/DailyOrders/DailyOrders_Icon_Serving_Clean";
        private const string ClaimArtworkSpritePath =
            "Ui/DailyOrders/DailyOrders_Claim500_GreenGold";
        private const string ClaimFrameSpritePath =
            "Ui/DailyOrders/DailyOrders_ClaimFrame_GreenGold";
        private const string OrangePlateSpritePath =
            "Ui/DailyOrders/DailyOrders_IconPlate_Orange";
        private const string PinkPlateSpritePath =
            "Ui/DailyOrders/DailyOrders_IconPlate_Pink";
        private const string BluePlateSpritePath =
            "Ui/DailyOrders/DailyOrders_IconPlate_Blue";
        private const string CloseBaseSpritePath =
            "Ui/Lives/MoreLivesClean_Close_Base_Red";
        private const string CloseXSpritePath =
            "Ui/Lives/MoreLivesClean_Close_X";

        private static readonly Color Cream = new Color32(255, 245, 213, 255);
        private static readonly Color Gold = new Color32(255, 205, 54, 255);
        private static readonly Color Mint = new Color32(168, 255, 180, 255);
        private static readonly Color LockedTint = new Color(0.62f, 0.6f, 0.68f, 1f);
        private static readonly Color AvailableBacklight = new Color(1f, 0.79f, 0.35f, 0.65f);
        private static readonly Color LockedBacklight = new Color(1f, 1f, 1f, 0.72f);
        private const float PanelDisplayScale = 1.1f;
        private const float IntroductionVolume = 0.85f;
        private const string LockSpritePath =
            "Ui/Locks/Ui_WholeGlassLock_Closed_v7";

        private readonly List<DailyOrderCardView> taskCards =
            new List<DailyOrderCardView>(3);
        private readonly Sprite[] taskIcons = new Sprite[3];
        private readonly Sprite[] taskPlates = new Sprite[3];

        private BartenderMainMenuPresenter menu;
        private TMP_FontAsset tmpFont;
        private Button sideButton;
        private TextMeshProUGUI sideTimer;
        private GameObject overlay;
        private TextMeshProUGUI resetTimer;
        private TextMeshProUGUI completedCount;
        private TextMeshProUGUI actionLabel;
        private Button actionButton;
        private Image actionImage;
        private Sprite playActionSprite;
        private Sprite claimActionSprite;
        private Sprite claimFrameSprite;
        private TextMeshProUGUI feedback;
        private bool subscribed;
        private float nextTimerRefresh;
        private BartenderDailyOrdersBadgeView badge;
        private bool authoredConnected;
        private long presentedDay;
        private bool rememberAfterReveal;
        private float nextActionAllowedAt;
        private long displayedActionDay;
        private bool displayedActionIsClaim;
        private RectTransform actionTransform;
        private RectTransform panelTransform;
        private CanvasGroup overlayGroup;
        private Image sideImage;
        private DailyOrdersBacklight sideBacklight;
        private Image sideTimerImage;
        private GameObject sideLock;
        private bool sideLocked;
        private bool sideLockShown;
        private bool actionPulsing;
        private bool closing;
        private bool introductionSeen;
        private RectTransform introduction;
        private TextMeshProUGUI introductionText;
        private BartenderTutorialFocusScrim introductionScrim;
        public bool IsOpen => authoredConnected ? authoredView.IsOpen
            : overlay != null && overlay.activeSelf;

        private DailyOrderCardView[] Cards => authoredConnected
            ? authoredView.Cards : taskCards.ToArray();
        private GameObject SideEntry => authoredConnected ? authoredView.SideEntry
            : sideButton != null ? sideButton.gameObject : null;
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
            if (menu == owner && (overlay != null || authoredConnected)) return;
            menu = owner;
            introductionSeen = BartenderTutorialProgress.IsCompleted(IntroductionId, IntroductionVersion);
            tmpFont = ResolveTmpFont(owner.transform);
            if (authoredView == null)
                authoredView = owner.GetComponentInChildren<BartenderDailyOrdersView>(true);
            if (authoredView != null)
            {
                if (!authoredView.IsReady)
                {
                    Debug.LogError("Daily Orders prefab bindings are incomplete.", authoredView);
                    return;
                }
                authoredView.Connect(this);
                authoredConnected = true;
            }
            else BuildUi();
            if (isActiveAndEnabled) Subscribe();
            Refresh(BartenderProgressService.DailyOrders);
            RefreshIntroduction();
        }

        private void OnEnable()
        {
            Subscribe();
            if (overlay != null || authoredConnected)
                Refresh(BartenderProgressService.DailyOrders);
        }

        private void OnDisable()
        {
            Unsubscribe();
            HideIntroduction();
            SetOverlayVisible(false, false);
        }

        private void OnDestroy()
        {
            Unsubscribe();
            HideIntroduction();
            if (authoredConnected) authoredView.Disconnect(this);
        }

        private void Update()
        {
            RefreshIntroduction();
            if (IsOpen && (menu == null || !menu.CanUseHomeFeatures))
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
            if (sideTimer != null || authoredConnected || IsOpen)
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

        private void BuildUi()
        {
            if (menu == null || overlay != null) return;

            Transform home = FindNamed(menu.transform, "Home Page");
            Transform sideParent = home != null
                ? FindNamed(home, "10_CustomSafeAreaRoot_iPhone")
                : null;
            if (sideParent == null) sideParent = home != null ? home : menu.transform;
            BuildSideButton(sideParent);

            Canvas canvas = menu.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.rootCanvas != null) canvas = canvas.rootCanvas;
            Transform overlayParent = canvas != null ? canvas.transform : menu.transform;
            BuildOverlay(overlayParent);
        }

        private void BuildSideButton(Transform parent)
        {
            GameObject root = CreateUi("DailyOrders_SideButton", parent);
            RectTransform rect = root.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0.61f);
            rect.anchorMax = rect.anchorMin;
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = new Vector2(16f, 0f);
            rect.sizeDelta = new Vector2(244f, 257f);

            // Keep the halo behind the artwork while sharing the button's visibility and layout.
            sideBacklight = CreateUi("Backlight", root.transform)
                .AddComponent<DailyOrdersBacklight>();
            sideBacklight.color = AvailableBacklight;
            sideBacklight.raycastTarget = false;
            RectTransform lightRect = sideBacklight.rectTransform;
            lightRect.anchorMin = lightRect.anchorMax = new Vector2(0.5f, 0.57f);
            lightRect.sizeDelta = new Vector2(344f, 344f);

            Image image = CreateUi("Artwork", root.transform).AddComponent<Image>();
            Stretch(image.rectTransform, 0f, 0f, 0f, 0f);
            image.sprite = Resources.Load<Sprite>(SideButtonSpritePath);
            image.preserveAspect = true;
            sideImage = image;
            sideButton = root.AddComponent<Button>();
            sideButton.targetGraphic = image;
            ColorBlock colors = sideButton.colors;
            colors.highlightedColor = Color.white;
            colors.pressedColor = new Color(0.86f, 0.9f, 1f, 1f);
            // LockedTint owns the dimming; Button's default disabled alpha makes the artwork see-through.
            colors.disabledColor = Color.white;
            colors.fadeDuration = 0.08f;
            sideButton.colors = colors;
            sideButton.onClick.AddListener(Open);
            BuildBadge(root, image.sprite);

            GameObject timerPill = CreateImage(
                "DynamicTimer", root.transform, TimerPlateSpritePath, Color.white);
            sideTimerImage = timerPill.GetComponent<Image>();
            sideTimerImage.raycastTarget = false;
            RectTransform timerRect = timerPill.GetComponent<RectTransform>();
            timerRect.anchorMin = new Vector2(0.1538f, 0.0326f);
            timerRect.anchorMax = new Vector2(0.8322f, 0.2572f);
            timerRect.offsetMin = Vector2.zero;
            timerRect.offsetMax = Vector2.zero;
            sideTimer = AddTmpText(timerPill.transform, "Timer", "23:59", 28f,
                Color.white, TextAlignmentOptions.Center);
            Stretch(sideTimer.rectTransform, 5f, 2f, 5f, 4f);
            BuildSideLock(root.transform);
            root.transform.SetAsLastSibling();
        }

        private void BuildBadge(GameObject root, Sprite sideSprite)
        {
            GameObject glowObject = CreateUi("ReadyGlow", root.transform);
            Image glowImage = glowObject.AddComponent<Image>();
            glowImage.sprite = sideSprite;
            glowImage.color = Gold;
            glowImage.preserveAspect = true;
            glowImage.raycastTarget = false;
            Stretch(glowImage.rectTransform, -4f, -4f, -4f, -4f);
            CanvasGroup glow = glowObject.AddComponent<CanvasGroup>();
            glow.alpha = 0f;

            GameObject badgeRoot = CreateUi("ProgressBadge", root.transform);
            Image badgeImage = badgeRoot.AddComponent<Image>();
            badgeImage.sprite = Resources.Load<Sprite>(CounterBadgeSpritePath);
            badgeImage.color = Color.white;
            badgeImage.preserveAspect = true;
            badgeImage.raycastTarget = false;
            RectTransform rect = badgeImage.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.86f, 0.88f);
            rect.sizeDelta = new Vector2(66f, 66f);
            TextMeshProUGUI count = AddTmpText(badgeRoot.transform, "Remaining", "3",
                36f, Cream, TextAlignmentOptions.Center);
            Stretch(count.rectTransform, 0f, 0f, 0f, 0f);

            GameObject check = CreateUi("ReadyCheck", badgeRoot.transform);
            Stretch(check.GetComponent<RectTransform>(), 0f, 0f, 0f, 0f);
            AddCheckStroke(check.transform, new Vector2(-8f, -2f), new Vector2(16f, 7f), -45f);
            AddCheckStroke(check.transform, new Vector2(6f, 2f), new Vector2(27f, 7f), 48f);
            badge = root.AddComponent<BartenderDailyOrdersBadgeView>();
            badge.Bind(badgeRoot, count, check, glow);
        }

        private static void AddCheckStroke(Transform parent, Vector2 position,
            Vector2 size, float angle)
        {
            Image stroke = CreateUi("Stroke", parent).AddComponent<Image>();
            stroke.color = Color.white;
            stroke.raycastTarget = false;
            stroke.rectTransform.anchoredPosition = position;
            stroke.rectTransform.sizeDelta = size;
            stroke.rectTransform.localRotation = Quaternion.Euler(0f, 0f, angle);
        }

        private void BuildOverlay(Transform parent)
        {
            overlay = CreateUi("DailyOrders_Overlay", parent);
            Stretch(overlay.GetComponent<RectTransform>(), 0f, 0f, 0f, 0f);
            overlayGroup = overlay.AddComponent<CanvasGroup>();

            GameObject dim = CreateUi("Dimmer", overlay.transform);
            Stretch(dim.GetComponent<RectTransform>(), 0f, 0f, 0f, 0f);
            Image dimImage = dim.AddComponent<Image>();
            dimImage.color = new Color(0.015f, 0.02f, 0.08f, 0.8f);
            Button dimButton = dim.AddComponent<Button>();
            dimButton.targetGraphic = dimImage;
            dimButton.onClick.AddListener(CloseAnimated);

            GameObject panel = CreateImage(
                "DailyOrders_Panel", overlay.transform, PanelSpritePath, Color.white);
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelTransform = panelRect;
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = panelRect.anchorMin;
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.anchoredPosition = new Vector2(0f, 8f);
            panelRect.sizeDelta = new Vector2(860f, 1182f);
            panelRect.localScale = Vector3.one * PanelDisplayScale;

            GameObject header = CreateImage(
                "HeaderArtwork", panel.transform, HeaderArtworkSpritePath, Color.white);
            Image headerImage = header.GetComponent<Image>();
            headerImage.preserveAspect = true;
            headerImage.raycastTarget = false;
            // Account for the artwork's transparent padding and leave room for the close button.
            RectTransform headerRect = header.GetComponent<RectTransform>();
            const float headerWidth = 820f;
            float headerHeight = headerImage.sprite != null
                ? headerWidth * headerImage.sprite.rect.height / headerImage.sprite.rect.width
                : 249.2f;
            SetTopRect(headerRect, -180f, headerWidth, headerHeight);
            headerRect.anchoredPosition += new Vector2(-48f, 0f);

            BuildResetInfo(panel.transform);

            completedCount = AddTmpText(
                panel.transform, "CompletedCount", "0 / 3 complete", 32f,
                Cream, TextAlignmentOptions.Midline);
            ApplyPopupTextStyle(completedCount);
            SetTopRect(completedCount.rectTransform, 178f, 520f, 44f);

            taskIcons[0] = Resources.Load<Sprite>(ClipboardIconSpritePath);
            taskIcons[1] = Resources.Load<Sprite>(WinsIconSpritePath);
            taskIcons[2] = Resources.Load<Sprite>(ServingIconSpritePath);
            taskPlates[0] = Resources.Load<Sprite>(BluePlateSpritePath);
            taskPlates[1] = Resources.Load<Sprite>(OrangePlateSpritePath);
            taskPlates[2] = Resources.Load<Sprite>(PinkPlateSpritePath);

            AddTaskCard(panel.transform, 236f, CreamCardSpritePath, 0,
                "Deliver 5 orders", BartenderDailyOrdersTuning.DeliveredOrderTarget);
            AddTaskCard(panel.transform, 522f, CreamCardSpritePath, 1,
                "Win 2 levels", BartenderDailyOrdersTuning.WonLevelTarget);
            AddTaskCard(panel.transform, 808f, CreamCardSpritePath, 2,
                "Serve 10 units", BartenderDailyOrdersTuning.ServedUnitTarget);

            GameObject action = CreateUi("DailyOrders_Action", panel.transform);
            actionImage = action.AddComponent<Image>();
            playActionSprite = menu.PrimaryActionSprite != null
                ? menu.PrimaryActionSprite : Resources.Load<Sprite>(BlueCardSpritePath);
            claimActionSprite = Resources.Load<Sprite>(ClaimArtworkSpritePath);
            claimFrameSprite = Resources.Load<Sprite>(ClaimFrameSpritePath);
            actionImage.sprite = playActionSprite;
            actionImage.type = Image.Type.Simple;
            actionImage.preserveAspect = true;
            RectTransform actionRect = action.GetComponent<RectTransform>();
            const float actionHeight = 112f;
            float actionWidth = actionImage.sprite != null
                ? actionHeight * actionImage.sprite.rect.width / actionImage.sprite.rect.height
                : 270f;
            // Keep the original button proportions and seat its centre on the bottom rim.
            actionRect.anchorMin = actionRect.anchorMax = new Vector2(0.5f, 0f);
            actionRect.pivot = new Vector2(0.5f, 0.5f);
            actionRect.anchoredPosition = new Vector2(0f, 10f);
            actionRect.sizeDelta = new Vector2(actionWidth, actionHeight);
            actionTransform = actionRect;
            actionButton = action.AddComponent<Button>();
            actionButton.targetGraphic = actionImage;
            actionButton.onClick.AddListener(HandleAction);
            actionLabel = AddTmpText(action.transform, "Label", "PLAY", 42f,
                Color.white, TextAlignmentOptions.Midline);
            ApplyPopupTextStyle(actionLabel);
            actionLabel.lineSpacing = -10f;
            Stretch(actionLabel.rectTransform, 20f, 20f, 20f, 14f);

            feedback = AddTmpText(panel.transform, "Feedback", string.Empty, 30f,
                Cream, TextAlignmentOptions.Center);
            SetTopRect(feedback.rectTransform, 1078f, 780f, 36f);

            BuildCloseButton(panel.transform);
            overlay.SetActive(false);
        }

        private void BuildResetInfo(Transform parent)
        {
            // Centre the clock with its caption, then centre the countdown below that pair.
            GameObject row = CreateImage("ResetInfo", parent, TimerPlateSpritePath, Color.white);
            SetTopRect(row.GetComponent<RectTransform>(), 36f, 480f, 132f);
            Image plate = row.GetComponent<Image>();
            plate.type = Image.Type.Sliced;
            plate.pixelsPerUnitMultiplier = 2.5f;
            plate.raycastTarget = false;

            GameObject heading = CreateUi("ResetCaption", row.transform);
            RectTransform headingRect = heading.GetComponent<RectTransform>();
            headingRect.anchoredPosition = new Vector2(0f, 28f);
            headingRect.sizeDelta = new Vector2(280f, 38f);
            HorizontalLayoutGroup headingLayout = heading.AddComponent<HorizontalLayoutGroup>();
            headingLayout.childAlignment = TextAnchor.MiddleCenter;
            headingLayout.spacing = 8f;
            headingLayout.childControlWidth = true;
            headingLayout.childControlHeight = true;
            headingLayout.childForceExpandWidth = false;
            headingLayout.childForceExpandHeight = true;

            GameObject clock = CreateImage(
                "Clock", heading.transform, ClockSpritePath, Color.white);
            Image clockImage = clock.GetComponent<Image>();
            clockImage.preserveAspect = true;
            clockImage.raycastTarget = false;
            LayoutElement clockLayout = clock.AddComponent<LayoutElement>();
            clockLayout.minWidth = clockLayout.preferredWidth = 34f;

            TextMeshProUGUI caption = AddTmpText(
                heading.transform, "Caption", "Resets in", 27f,
                Cream, TextAlignmentOptions.Midline);
            ApplyPopupTextStyle(caption);

            resetTimer = AddTmpText(
                row.transform, "Countdown", string.Format(CountdownTextFormat, 23, 59, 59),
                54f, Color.white, TextAlignmentOptions.Midline);
            ApplyPopupTextStyle(resetTimer);
            resetTimer.rectTransform.anchoredPosition = new Vector2(0f, -19f);
            resetTimer.rectTransform.sizeDelta = new Vector2(350f, 62f);
        }

        private static void ApplyPopupTextStyle(TextMeshProUGUI label)
        {
            Material preset = Resources.Load<Material>(PopupTextMaterialPath);
            if (preset != null) label.fontSharedMaterial = preset;
            label.extraPadding = true;
            label.characterSpacing = 0f;
            label.UpdateMeshPadding();
        }

        private void AddTaskCard(
            Transform parent,
            float top,
            string backgroundPath,
            int index,
            string title,
            int target)
        {
            DailyOrderCardView cardPrefab =
                Resources.Load<DailyOrderCardView>(CardPrefabPath);
            if (cardPrefab == null)
            {
                Debug.LogError("Daily Orders card prefab is missing at Resources/"
                               + CardPrefabPath + ".");
                return;
            }

            DailyOrderCardView card = Instantiate(cardPrefab, parent, false);
            card.name = "Task_" + (index + 1);
            SetTopRect(card.GetComponent<RectTransform>(), top, 780f, 266f);
            card.SetBackground(Resources.Load<Sprite>(backgroundPath));
            card.SetIconPlate(taskPlates[index]);
            card.Setup(title, 0, target, BartenderDailyOrdersTuning.RewardPerTask,
                taskIcons[index]);
            if (index == 0)
                card.SetOrderGlass(Resources.Load<Sprite>(OrderGlassIconSpritePath));
            taskCards.Add(card);
        }

        private void BuildCloseButton(Transform parent)
        {
            GameObject close = CreateImage(
                "Close", parent, CloseBaseSpritePath, Color.white);
            RectTransform closeRect = close.GetComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(1f, 1f);
            closeRect.anchorMax = closeRect.anchorMin;
            closeRect.pivot = new Vector2(0.5f, 0.5f);
            closeRect.anchoredPosition = new Vector2(-18f, -22f);
            closeRect.sizeDelta = new Vector2(108f, 108f);
            Button button = close.AddComponent<Button>();
            button.targetGraphic = close.GetComponent<Image>();
            button.onClick.AddListener(CloseAnimated);

            GameObject x = CreateImage("X", close.transform, CloseXSpritePath, Color.white);
            x.GetComponent<Image>().raycastTarget = false;
            RectTransform xRect = x.GetComponent<RectTransform>();
            xRect.anchorMin = new Vector2(0.5f, 0.5f);
            xRect.anchorMax = xRect.anchorMin;
            xRect.pivot = new Vector2(0.5f, 0.5f);
            xRect.anchoredPosition = Vector2.zero;
            xRect.sizeDelta = new Vector2(43f, 47f);
        }

        public void Open()
        {
            if (!IsUnlocked || IsOpen || menu == null || !menu.CanUseHomeFeatures
                || (overlay == null && !authoredConnected)) return;
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

        public void HandleAction()
        {
            if (!IsOpen || menu == null || !menu.CanUseHomeFeatures
                || Time.unscaledTime < nextActionAllowedAt) return;
            // A double tap on CLAIM must not become a PLAY tap after the first save.
            nextActionAllowedAt = Time.unscaledTime + 0.35f;
            nextTimerRefresh = nextActionAllowedAt;
            // Capture what was displayed BEFORE a service read can publish a rollover.
            long expectedDay = displayedActionDay;
            bool intendedClaim = displayedActionIsClaim;
            BartenderDailyOrdersSnapshot snapshot =
                BartenderProgressService.DailyOrders;
            if (snapshot.RewardClaimed)
            {
                Refresh(snapshot);
                return;
            }
            if (snapshot.UtcDayKey != expectedDay || snapshot.CanClaim != intendedClaim)
            {
                Refresh(snapshot);
                SetFeedback("DAILY ORDERS UPDATED - CHECK YOUR TASKS");
                return;
            }
            if (intendedClaim)
            {
                if (menu.TryClaimDailyOrdersForPresentation(
                        expectedDay, RewardSourceViewport(), out string rejectionReason))
                {
                    SetFeedback(string.Empty);
                    Refresh(BartenderProgressService.DailyOrders);
                    CelebrateClaim();
                }
                else
                {
                    SetFeedback(string.IsNullOrWhiteSpace(rejectionReason)
                        ? "REWARD UNAVAILABLE"
                        : rejectionReason.ToUpperInvariant());
                }
                return;
            }

            SetOverlayVisible(false, true);
            menu?.StartGameFromDailyOrders();
        }

        private void SetOverlayVisible(bool visible, bool playSound)
        {
            if (IsOpen == visible) return;
            closing = false;
            if (!visible) RememberProgress();
            if (authoredConnected) authoredView.SetVisible(visible);
            else if (overlay != null)
            {
                overlay.SetActive(visible);
                if (visible)
                {
                    overlay.transform.SetAsLastSibling();
                    PlayOpenTransition();
                }
                else SetActionPulse(false);
            }
            if (!visible) menu?.PresentDailyOrdersRewardAfterClose();
            if (!playSound) return;
            BsAudio.Instance?.Play(
                visible ? BsSfx.PopupOpen : BsSfx.PopupClose, 0.8f);
        }

        private void Refresh(BartenderDailyOrdersSnapshot snapshot)
        {
            if (introductionText != null) introductionText.text = IntroductionCopy(snapshot);
            displayedActionDay = snapshot.UtcDayKey;
            displayedActionIsClaim = snapshot.CanClaim;
            bool available = BartenderProgressService.IsAvailable
                && Time.unscaledTime >= nextActionAllowedAt;
            if (authoredConnected) authoredView.Render(snapshot, available);
            if (badge != null) badge.Render(snapshot);
            if (IsOpen)
            {
                bool sameDay = presentedDay == snapshot.UtcDayKey;
                presentedDay = snapshot.UtcDayKey;
                ApplyTasks(snapshot, sameDay);
                if (Array.Exists(Cards, card => card != null && card.IsAnimating))
                    rememberAfterReveal = true;
            }
            TimeSpan remaining = snapshot.RemainingUntilReset(BartenderProgressService.DailyUtcNowTicks);
            int hours = Mathf.FloorToInt((float)remaining.TotalHours);
            string compact = remaining.TotalHours >= 1d
                ? string.Format("{0:00}:{1:00}", hours, remaining.Minutes)
                : string.Format("{0:00}:{1:00}", remaining.Minutes, remaining.Seconds);

            if (sideTimer != null)
            {
                sideTimer.text = compact;
            }
            if (resetTimer != null)
            {
                resetTimer.text = string.Format(
                    CountdownTextFormat, hours,
                    remaining.Minutes, remaining.Seconds);
            }
            if (completedCount != null)
                completedCount.text = "<color=#FFDC68>" + snapshot.CompletedTaskCount
                    + " / 3</color> complete";

            RefreshActionVisual(snapshot);
            if (actionButton != null)
                actionButton.interactable = available && !closing && !snapshot.RewardClaimed;
            SetActionPulse(IsOpen && snapshot.CanClaim && available && !closing);
            UpdateSideLock(snapshot.RewardClaimed);
        }

        private void RefreshActionVisual(BartenderDailyOrdersSnapshot snapshot)
        {
            bool canClaim = snapshot.CanClaim && !snapshot.RewardClaimed;
            // The approved artwork includes its lettering. Keep a text-free frame
            // available so future reward tuning cannot display an outdated amount.
            bool useClaimArtwork = canClaim && claimActionSprite != null
                && BartenderDailyOrdersTuning.TotalRewardCoins == 500;
            if (actionImage != null)
                actionImage.sprite = useClaimArtwork ? claimActionSprite
                    : canClaim && claimFrameSprite != null ? claimFrameSprite : playActionSprite;
            if (actionLabel == null) return;
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
            // Progress keeps accumulating from level one; discovery begins when level three opens.
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
            GameObject prefab = Resources.Load<GameObject>(IntroductionPrefabPath);
            if (prefab == null)
            {
                // Keep the entry usable if its optional hint asset is missing; retry next menu visit.
                introductionSeen = true;
                Debug.LogWarning("Daily Orders introduction prefab is missing.", this);
                return;
            }
            Canvas rootCanvas = entry.GetComponentInParent<Canvas>().rootCanvas;
            GameObject focus = CreateUi("Daily Orders Tutorial Focus", rootCanvas.transform);
            Stretch(focus.GetComponent<RectTransform>(), 0f, 0f, 0f, 0f);
            introductionScrim = focus.AddComponent<BartenderTutorialFocusScrim>();
            introductionScrim.color = new Color(0.015f, 0.035f, 0.085f, 0.62f);
            introduction = Instantiate(prefab, focus.transform, false).transform as RectTransform;
            introductionScrim.Focus(entry.transform as RectTransform, introduction);
            introductionText = introduction.GetComponentInChildren<TextMeshProUGUI>(true);
            introductionText.text = IntroductionCopy(BartenderProgressService.DailyOrders);
            introduction.localScale = Vector3.one * 0.88f;
            introduction.DOScale(Vector3.one, 0.3f).SetEase(Ease.OutBack)
                .SetUpdate(true).SetRecyclable(true);
            BsAudio.Instance?.Play(BsSfx.DailyRewardHint, IntroductionVolume);
        }

        private static string IntroductionCopy(BartenderDailyOrdersSnapshot snapshot) =>
            snapshot.CanClaim ? "Your daily reward is ready!\nTap the board to collect."
            : "Daily Orders unlocked!\nTap the board to open.";

        private void HideIntroduction()
        {
            if (introduction != null)
            {
                introduction.DOKill(false);
                introduction.gameObject.SetActive(false);
                Destroy(introduction.gameObject);
            }
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
            if (authoredConnected) authoredView.SetFeedback(text);
            else if (feedback != null)
            {
                feedback.rectTransform.DOKill();
                feedback.rectTransform.localScale = Vector3.one;
                feedback.color = Cream;
                feedback.text = text ?? string.Empty;
            }
        }

        private void BuildSideLock(Transform parent)
        {
            GameObject lockObject = CreateImage("LockOverlay", parent, LockSpritePath, Color.white);
            Image lockImage = lockObject.GetComponent<Image>();
            lockImage.preserveAspect = true;
            lockImage.raycastTarget = false;
            RectTransform rect = lockImage.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.56f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(112f, 112f);
            sideLock = lockObject;
            sideLock.SetActive(false);
        }

        private void SetActionPulse(bool on)
        {
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
            RectTransform source = authoredConnected ? authoredView.RewardSource : actionTransform;
            if (source == null) source = panelTransform;
            if (source == null) return new Vector2(0.5f, 0.4f);
            Canvas canvas = source.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera : null;
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera,
                source.TransformPoint(source.rect.center));
            return new Vector2(Mathf.Clamp01(screen.x / Mathf.Max(1, Screen.width)),
                Mathf.Clamp01(screen.y / Mathf.Max(1, Screen.height)));
        }

        /// <summary>Confirm the saved reward; the user closes the panel to start its coin flight.</summary>
        private void CelebrateClaim()
        {
            SetActionPulse(false);
            if (actionButton != null) actionButton.interactable = false;
            SetFeedback("+" + BartenderDailyOrdersTuning.TotalRewardCoins + " COINS COLLECTED!");
            if (feedback != null)
            {
                feedback.color = Mint;
                RectTransform rect = feedback.rectTransform;
                rect.DOKill();
                rect.localScale = Vector3.one * 0.94f;
                rect.DOScale(1f, 0.38f)
                    .SetEase(Ease.OutCubic)
                    .SetUpdate(true)
                    .SetLink(rect.gameObject);
            }
        }

        private void CloseAnimated()
        {
            if (!IsOpen || closing) return;
            if (authoredConnected || overlayGroup == null || panelTransform == null)
            {
                SetOverlayVisible(false, true);
                return;
            }
            closing = true;
            SetActionPulse(false);
            overlayGroup.DOKill();
            panelTransform.DOKill();
            overlayGroup.interactable = false;
            // Keep the fading panel's clicks from reaching the level/navigation buttons behind it.
            overlayGroup.blocksRaycasts = true;
            panelTransform.DOScale(PanelDisplayScale * 0.975f, 0.30f)
                .SetEase(Ease.InOutSine)
                .SetUpdate(true)
                .SetLink(overlay);
            DOTween.To(() => overlayGroup.alpha, a => overlayGroup.alpha = a, 0f, 0.30f)
                .SetEase(Ease.InOutSine)
                .SetUpdate(true)
                .SetTarget(overlayGroup)
                .SetLink(overlay)
                .OnComplete(() => SetOverlayVisible(false, true));
        }

        private void PlayOpenTransition()
        {
            if (overlayGroup == null || panelTransform == null) return;
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
                .SetLink(overlay);
            panelTransform.DOScale(PanelDisplayScale, 0.32f)
                .SetEase(Ease.OutCubic)
                .SetUpdate(true)
                .SetLink(overlay);
        }

        /// <summary>Locks and dims the claimed daily button, keeping its timer readable. Only state changes animate.</summary>
        private void UpdateSideLock(bool locked)
        {
            if (sideButton == null) return;
            sideButton.interactable = !locked;
            if (sideBacklight != null)
            {
                sideBacklight.color = locked ? LockedBacklight : AvailableBacklight;
                sideBacklight.gameObject.SetActive(true);
            }
            bool animate = sideLockShown && locked != sideLocked;
            sideLocked = locked;
            sideLockShown = true;
            Color tint = locked ? LockedTint : Color.white;
            TintSide(sideImage, tint, animate);
            TintSide(sideTimerImage, Color.white, animate);
            if (sideLock == null) return;
            RectTransform rect = sideLock.GetComponent<RectTransform>();
            rect.DOKill();
            sideLock.SetActive(locked);
            if (locked && animate)
            {
                rect.localScale = Vector3.zero;
                rect.DOScale(1f, 0.5f)
                    .SetEase(Ease.OutBack, 2.2f)
                    .SetUpdate(true)
                    .SetLink(sideLock);
            }
            else rect.localScale = Vector3.one;
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

        private TextMeshProUGUI AddTmpText(
            Transform parent,
            string objectName,
            string value,
            float size,
            Color color,
            TextAlignmentOptions alignment)
        {
            GameObject gameObject = CreateUi(objectName, parent);
            TextMeshProUGUI label = gameObject.AddComponent<TextMeshProUGUI>();
            label.font = tmpFont != null ? tmpFont : TMP_Settings.defaultFontAsset;
            label.fontSize = size;
            label.fontStyle = FontStyles.Normal;
            label.text = value;
            label.color = color;
            label.alignment = alignment;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.raycastTarget = false;
            label.extraPadding = true;
            label.outlineColor = new Color32(36, 20, 50, 220);
            label.outlineWidth = 0f;
            return label;
        }

        private static GameObject CreateUi(string name, Transform parent)
        {
            var gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.layer = 5;
            gameObject.transform.SetParent(parent, false);
            return gameObject;
        }

        private static GameObject CreateImage(
            string name, Transform parent, string spritePath, Color color)
        {
            GameObject gameObject = CreateUi(name, parent);
            Image image = gameObject.AddComponent<Image>();
            image.sprite = Resources.Load<Sprite>(spritePath);
            image.color = color;
            image.preserveAspect = false;
            return gameObject;
        }

        private static TMP_FontAsset ResolveTmpFont(Transform root)
        {
            // Popup and task rows share the same rounded font and SDF atlas.
            DailyOrderCardView cardPrefab = Resources.Load<DailyOrderCardView>(CardPrefabPath);
            TextMeshProUGUI cardLabel = cardPrefab != null
                ? cardPrefab.GetComponentInChildren<TextMeshProUGUI>(true) : null;
            if (cardLabel != null && cardLabel.font != null) return cardLabel.font;
            TextMeshProUGUI[] labels =
                root.GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; i < labels.Length; i++)
            {
                if (labels[i] != null && labels[i].font != null)
                    return labels[i].font;
            }
            return TMP_Settings.defaultFontAsset;
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

        private static void Stretch(
            RectTransform rect, float left, float bottom, float right, float top)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        private static void SetTopRect(
            RectTransform rect, float top, float width, float height)
        {
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = rect.anchorMin;
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -top);
            rect.sizeDelta = new Vector2(width, height);
        }
    }
}
