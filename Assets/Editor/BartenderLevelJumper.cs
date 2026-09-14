using System.Collections.Generic;
using BartenderSort.Core;
using LiquidSort.Levels;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class BartenderLevelJumper : EditorWindow
{
    private const string SessionPrefix = "GlassPourMathDemo.LevelJumper.";
    private const string PendingLevelKey = SessionPrefix + "PendingLevel";
    private const string PendingSceneKey = SessionPrefix + "PendingScene";
    private const string DebugSessionKey = SessionPrefix + "DebugSession";
    private const string DebugAttemptIdKey = SessionPrefix + "DebugAttemptId";
    private const string DebugAttemptSlotKey = SessionPrefix + "DebugAttemptSlot";
    private const string LastMessageKey = SessionPrefix + "LastMessage";
    private const string LastMessageTypeKey = SessionPrefix + "LastMessageType";
    private const string FirstShiftGameplayScenePath =
        "Assets/LiquidSort/RoyalGlassLab/RoyalGlassLab_WorkingCopy.unity";
    private const int NoPendingLevel = 0;
    private const double PendingTimeoutSeconds = 8d;

    private static double pendingDeadline;
    private static int pendingStartFrame;

    private BartenderLevelController controller;
    private List<BsLevel> campaign = new List<BsLevel>();
    private Vector2 scroll;
    private Vector2 windowScroll;
    [SerializeField]
    private int quickLevel = 1;
    [SerializeField]
    private int coinAmount = 1000;
    private string lastMessage;
    private MessageType lastMessageType = MessageType.Info;

    [MenuItem("Tools/LiquidSort/Level Jumper")]
    private static void Open()
    {
        BartenderLevelJumper window = GetWindow<BartenderLevelJumper>();
        window.titleContent = new GUIContent("Level Jumper");
        window.minSize = new Vector2(420f, 510f);
        window.Rescan();
    }

    [InitializeOnLoadMethod]
    private static void HookPlayModeLifecycle()
    {
        EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
        EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
        AssemblyReloadEvents.beforeAssemblyReload -= HandleBeforeAssemblyReload;
        AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;
        StopPendingWatch();
        if (EditorApplication.isPlaying && PendingLevel > NoPendingLevel)
            BeginPendingWatch();
    }

    private void OnEnable()
    {
        Rescan();
        coinAmount = Mathf.Max(0, BartenderProgressService.Coins);
        lastMessage = SessionState.GetString(LastMessageKey, string.Empty);
        lastMessageType = (MessageType)Mathf.Clamp(
            SessionState.GetInt(LastMessageTypeKey, (int)MessageType.Info),
            (int)MessageType.None, (int)MessageType.Error);
    }

    private void OnInspectorUpdate() => Repaint();

    private static int PendingLevel =>
        SessionState.GetInt(PendingLevelKey, NoPendingLevel);

    private static void HandlePlayModeStateChanged(PlayModeStateChange change)
    {
        switch (change)
        {
            case PlayModeStateChange.EnteredPlayMode:
                if (PendingLevel > NoPendingLevel) BeginPendingWatch();
                break;

            case PlayModeStateChange.ExitingPlayMode:
                StopPendingWatch();
                CloseDebugAttempt("Play exit");
                BartenderFirstShiftProgress.EditorCancelTestRun();
                BartenderTimedOrdersTutorialProgress.EditorCancelReplay();
                BartenderTimedOrdersTutorialProgress
                    .EditorClearNaturalStartSuppression();
                if (PendingLevel > NoPendingLevel)
                {
                    ClearPendingRequest();
                    BroadcastReport("Play ended before the level opened.",
                        MessageType.Warning);
                }
                break;

            case PlayModeStateChange.EnteredEditMode:
                StopPendingWatch();
                if (PendingLevel > NoPendingLevel)
                {
                    ClearPendingRequest();
                    BroadcastReport("Play did not start. Pending request cleared.",
                        MessageType.Warning);
                }
                CloseDebugAttempt("Return to Edit mode");
                BartenderFirstShiftProgress.EditorCancelTestRun();
                BartenderTimedOrdersTutorialProgress.EditorCancelReplay();
                BartenderTimedOrdersTutorialProgress
                    .EditorClearNaturalStartSuppression();
                break;
        }
    }

    private static void HandleBeforeAssemblyReload()
    {
        if (EditorApplication.isPlaying)
        {
            CloseDebugAttempt("assembly reload");
            if (BartenderTimedOrdersTutorialProgress.EditorReplayRunning)
            {
                BartenderTimedOrdersTutorialDirector
                    .EditorAbortActiveReplaysBeforeAssemblyReload();
                EditorApplication.ExitPlaymode();
            }
        }
    }

    private static void BeginPendingWatch()
    {
        pendingDeadline = EditorApplication.timeSinceStartup + PendingTimeoutSeconds;
        pendingStartFrame = Time.frameCount;
        EditorApplication.update -= TryRunPendingJump;
        EditorApplication.update += TryRunPendingJump;
    }

    private static void StopPendingWatch() =>
        EditorApplication.update -= TryRunPendingJump;

    private static void TryRunPendingJump()
    {
        int levelNumber = PendingLevel;
        if (levelNumber <= NoPendingLevel)
        {
            StopPendingWatch();
            return;
        }
        if (!EditorApplication.isPlaying) return;
        bool bootstrapWaiting = EditorApplication.isCompiling
                             || EditorApplication.isUpdating
                             || Time.frameCount <= pendingStartFrame;
        if (bootstrapWaiting)
        {
            if (EditorApplication.timeSinceStartup < pendingDeadline) return;
            FinishPendingJump(false,
                "Level setup timed out.", MessageType.Error, null);
            return;
        }

        string expectedScene = SessionState.GetString(PendingSceneKey, string.Empty);
        BartenderLevelController target = FindControllerInScene(expectedScene,
            out string findReason);
        if (target == null || !target.EditorLevelJumpReady)
        {
            if (EditorApplication.timeSinceStartup < pendingDeadline) return;
            FinishPendingJump(false,
                string.IsNullOrEmpty(findReason)
                    ? "Level setup timed out."
                    : findReason,
                MessageType.Error, null);
            return;
        }

        bool loaded = target.EditorTryJumpToLevelNumber(
            levelNumber, out bool ownershipTouched, out string ownedAttemptId,
            out int ownedAttemptSlot, out string rejectionReason);
        ApplyDebugAttemptOwnership(ownershipTouched, ownedAttemptId,
            ownedAttemptSlot);
        if (loaded)
        {
            FinishPendingJump(true,
                "Level " + levelNumber
              + " opened. Lives and progress kept.",
                MessageType.Info, target);
            return;
        }

        if (BartenderProgressService.Lives <= 0)
        {
            FinishPendingJump(false, rejectionReason, MessageType.Warning, target);
            return;
        }
        if (EditorApplication.timeSinceStartup < pendingDeadline) return;
        FinishPendingJump(false,
            "Level " + levelNumber + " could not open: " + rejectionReason,
            MessageType.Error, target);
    }

    private static BartenderLevelController FindControllerInScene(
        string expectedScene, out string rejectionReason)
    {
        rejectionReason = null;
        BartenderLevelController[] found =
            Object.FindObjectsByType<BartenderLevelController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
        BartenderLevelController match = null;
        int matches = 0;
        for (int i = 0; i < found.Length; i++)
        {
            BartenderLevelController candidate = found[i];
            if (candidate == null) continue;
            string candidateScene = candidate.gameObject.scene.path;
            if (!string.IsNullOrEmpty(expectedScene)
                && !string.Equals(candidateScene, expectedScene,
                    System.StringComparison.Ordinal))
                continue;
            match = candidate;
            matches++;
        }

        if (matches == 1) return match;
        rejectionReason = matches == 0
            ? "No level controller in the requested scene."
            : "Multiple level controllers in the requested scene.";
        return null;
    }

    private static void FinishPendingJump(bool success, string message,
                                          MessageType type,
                                          BartenderLevelController target)
    {
        ClearPendingRequest();
        StopPendingWatch();
        if (!success)
            BartenderTimedOrdersTutorialProgress
                .EditorClearNaturalStartSuppression();
        if (success)
            Debug.Log("[Level Jumper] " + message, target);
        BroadcastReport(message, type);
    }

    private static void ApplyDebugAttemptOwnership(bool ownershipTouched,
                                                   string attemptId, int campaignSlot)
    {
        if (!ownershipTouched) return;
        if (string.IsNullOrEmpty(attemptId) || campaignSlot < 0)
        {
            ClearDebugAttemptOwnership();
            return;
        }

        SessionState.SetBool(DebugSessionKey, true);
        SessionState.SetString(DebugAttemptIdKey, attemptId);
        SessionState.SetInt(DebugAttemptSlotKey, campaignSlot);
    }

    private static bool CloseDebugAttempt(string context)
    {
        if (!SessionState.GetBool(DebugSessionKey, false)) return true;
        string attemptId = SessionState.GetString(DebugAttemptIdKey, string.Empty);
        int campaignSlot = SessionState.GetInt(DebugAttemptSlotKey, -1);
        if (string.IsNullOrEmpty(attemptId) || campaignSlot < 0)
        {
            Debug.LogWarning("[Level Jumper] " + context
                           + " has no debug attempt ID.");
            return false;
        }
        if (!BartenderProgressService.EditorTryDiscardActiveAttempt(
                attemptId, campaignSlot, out string rejectionReason))
        {
            Debug.LogWarning("[Level Jumper] " + context
                           + " could not close the debug attempt: " + rejectionReason);
            return false;
        }

        ClearDebugAttemptOwnership();
        return true;
    }

    private static void ClearDebugAttemptOwnership()
    {
        SessionState.EraseBool(DebugSessionKey);
        SessionState.EraseString(DebugAttemptIdKey);
        SessionState.EraseInt(DebugAttemptSlotKey);
    }

    private static void ClearPendingRequest()
    {
        SessionState.EraseInt(PendingLevelKey);
        SessionState.EraseString(PendingSceneKey);
    }

    private static void BroadcastReport(string message, MessageType type)
    {
        SessionState.SetString(LastMessageKey, message ?? string.Empty);
        SessionState.SetInt(LastMessageTypeKey, (int)type);
        BartenderLevelJumper[] windows =
            Resources.FindObjectsOfTypeAll<BartenderLevelJumper>();
        for (int i = 0; i < windows.Length; i++)
            windows[i].SetReport(message, type);
    }

    private void Rescan()
    {
        controller = FindFirstObjectByType<BartenderLevelController>(
            FindObjectsInactive.Include);

        BsLevel[] found = Resources.LoadAll<BsLevel>("Levels");
        campaign = new List<BsLevel>(found);
        campaign.RemoveAll(level => level == null);
        campaign.Sort((a, b) => a.Index.CompareTo(b.Index));
    }

    private void OnGUI()
    {
        using (var view = new EditorGUILayout.ScrollViewScope(windowScroll))
        {
            windowScroll = view.scrollPosition;
            DrawHeader();
            DrawDailyOrderTools();
            DrawFirstShiftTutorial();
            DrawTimedOrdersTutorial();
            if (controller != null)
            {
                DrawStatus();
                DrawQuickJump();
                DrawCampaignList();
                DrawLifeTools();
                DrawCoinTools();
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Open the gameplay scene and click Refresh for level controls. Daily Orders work here too.",
                    MessageType.Info);
            }

            if (!string.IsNullOrEmpty(lastMessage))
                EditorGUILayout.HelpBox(lastMessage, lastMessageType);
        }
    }

    private enum DailyOrdersPreset { Reset, Complete, Locked }

    private void DrawDailyOrderTools()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Daily Orders", EditorStyles.boldLabel);
            BartenderDailyOrdersSnapshot snapshot = BartenderProgressService.DailyOrders;
            System.TimeSpan remaining = snapshot.RemainingUntilReset(
                BartenderProgressService.DailyUtcNowTicks);
            Row("State", snapshot.RewardClaimed ? "Locked (reward claimed)"
                : snapshot.CanClaim ? "Ready to claim" : "Available");
            Row("Resets in", string.Format("{0:00}h {1:00}m {2:00}s",
                (int)remaining.TotalHours, remaining.Minutes, remaining.Seconds));
            Row("Progress", string.Format("Orders {0}/{1}   Wins {2}/{3}   Units {4}/{5}",
                snapshot.DeliveredOrders, BartenderDailyOrdersTuning.DeliveredOrderTarget,
                snapshot.WonLevels, BartenderDailyOrdersTuning.WonLevelTarget,
                snapshot.ServedUnits, BartenderDailyOrdersTuning.ServedUnitTarget));

            bool unavailable = EditorApplication.isCompiling || EditorApplication.isUpdating
                || (!EditorApplication.isPlaying && EditorApplication.isPlayingOrWillChangePlaymode)
                || !BartenderProgressService.IsAvailable;
            using (new EditorGUI.DisabledScope(unavailable))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("Reset / Unlock",
                            "Clear today's task progress and unlock Daily Orders."), GUILayout.Height(27f)))
                        SetDailyOrders(DailyOrdersPreset.Reset);
                    if (GUILayout.Button(new GUIContent("Complete Tasks",
                            "Complete all three tasks so the normal CLAIM button can be tried."), GUILayout.Height(27f)))
                        SetDailyOrders(DailyOrdersPreset.Complete);
                    if (GUILayout.Button(new GUIContent("Lock",
                            "Show the claimed, locked state without granting coins."), GUILayout.Height(27f)))
                        SetDailyOrders(DailyOrdersPreset.Locked);
                }

                BartenderDailyOrdersPresenter presenter = FindDailyOrdersPresenter();
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(presenter == null
                               || presenter.IsOpen || snapshot.RewardClaimed))
                    {
                        if (GUILayout.Button("Open Cards"))
                        {
                            presenter.Open();
                            Report(presenter.IsOpen ? "Daily Orders opened."
                                : "Home screen is busy or not active. Return home, wait, and try again.",
                                presenter.IsOpen ? MessageType.Info : MessageType.Warning);
                        }
                    }
                    using (new EditorGUI.DisabledScope(presenter == null || !presenter.IsOpen))
                    {
                        if (GUILayout.Button("Close Cards")) presenter.Close();
                    }
                }
            }
            EditorGUILayout.LabelField(
                "Updates today's tasks in the current save. Lock grants no coins.\nOpen / Close Cards work on the home screen during Play.",
                EditorStyles.wordWrappedMiniLabel);
        }
        EditorGUILayout.Space(2f);
    }

    private static BartenderDailyOrdersPresenter FindDailyOrdersPresenter() =>
        EditorApplication.isPlaying
            ? FindFirstObjectByType<BartenderDailyOrdersPresenter>() : null;

    private void SetDailyOrders(DailyOrdersPreset preset)
    {
        string reason;
        bool applied;
        string message;
        switch (preset)
        {
            case DailyOrdersPreset.Complete:
                applied = BartenderProgressService.EditorCompleteDailyOrders(out reason);
                message = "Daily tasks completed. Open the cards to try CLAIM.";
                break;
            case DailyOrdersPreset.Locked:
                applied = BartenderProgressService.EditorLockDailyOrders(out reason);
                message = "Daily Orders locked. No coins granted.";
                break;
            default:
                applied = BartenderProgressService.EditorResetDailyOrders(out reason);
                message = "Daily Orders reset and unlocked. Progress is 0 / 3.";
                break;
        }
        if (applied)
            FindDailyOrdersPresenter()?.Close();
        Report(applied ? message : "Could not update Daily Orders: " + reason,
            applied ? MessageType.Info : MessageType.Warning);
    }

    private void DrawFirstShiftTutorial()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("First Shift Tutorial", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Runs the three-order tutorial. Keeps lives and progress. Exits Play when done.",
                EditorStyles.wordWrappedMiniLabel);

            bool transitionPending = !EditorApplication.isPlaying
                && EditorApplication.isPlayingOrWillChangePlaymode;
            using (new EditorGUI.DisabledScope(
                       EditorApplication.isPlaying || transitionPending))
            {
                if (GUILayout.Button("Play Tutorial", GUILayout.Height(30f)))
                    StartFirstShiftTutorial();
            }
        }
        EditorGUILayout.Space(2f);
    }

    private void DrawTimedOrdersTutorial()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField(
                "Timed Orders Tutorial (Level 15)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Runs the Level 15 timer tutorial. Keeps lives, coins and progress. Exits Play when done.",
                EditorStyles.wordWrappedMiniLabel);

            bool transitionPending = !EditorApplication.isPlaying
                && EditorApplication.isPlayingOrWillChangePlaymode;
            using (new EditorGUI.DisabledScope(
                       EditorApplication.isPlaying || transitionPending))
            {
                if (GUILayout.Button(
                        "Play Timer Tutorial", GUILayout.Height(30f)))
                    StartTimedOrdersTutorial();
            }
        }
        EditorGUILayout.Space(2f);
    }

    private void DrawHeader()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(
                controller != null ? controller.gameObject.name : "No controller",
                EditorStyles.boldLabel);
            if (GUILayout.Button("Refresh", GUILayout.Width(70f))) Rescan();
        }
        EditorGUILayout.Space(2f);
    }

    private void DrawStatus()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            Row("Game State", controller.State.ToString());
            Row("Current Level", controller.CurrentLevel != null
                ? controller.CurrentLevel.Index + "  (slot "
                  + controller.CurrentCampaignSlot + ")"
                : "-");
            Row("Unlocked Level", controller.NextUnlockedLevelNumber.ToString());
            Row("Lives", BartenderProgressService.Lives + " / "
                     + BartenderProgressService.MaxLives);
            Row("Coins", BartenderProgressService.Coins.ToString());
            Row("Campaign", campaign.Count + " level");
            if (PendingLevel > NoPendingLevel)
                Row("Pending Level", "Level " + PendingLevel);

            if (BartenderProgressService.Lives <= 0)
                EditorGUILayout.HelpBox(
                    "No lives left. Refill below to open a level.",
                    MessageType.Warning);
        }
    }

    private void DrawQuickJump()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Quick Jump", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                quickLevel = Mathf.Max(1, EditorGUILayout.IntField("Level", quickLevel));
                using (new EditorGUI.DisabledScope(
                           BartenderProgressService.Lives <= 0
                        || (!EditorApplication.isPlaying
                            && EditorApplication.isPlayingOrWillChangePlaymode)))
                {
                    if (GUILayout.Button(
                            EditorApplication.isPlaying ? "Go" : "Open in Play",
                            GUILayout.Width(105f)))
                        Go(quickLevel);
                }
                if (PendingLevel > NoPendingLevel
                    && GUILayout.Button("Cancel", GUILayout.Width(48f)))
                    CancelPendingJump();
            }

            EditorGUILayout.LabelField(
                EditorApplication.isPlaying
                    ? "Waits for the current animation. Uses no lives."
                    : "Starts Play without changing the saved scene.",
                EditorStyles.miniLabel);
        }
    }

    private void DrawCampaignList()
    {
        EditorGUILayout.LabelField("Campaign", EditorStyles.boldLabel);
        bool jumpDisabled = BartenderProgressService.Lives <= 0
                         || (!EditorApplication.isPlaying
                             && EditorApplication.isPlayingOrWillChangePlaymode);
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(180f));
        for (int i = 0; i < campaign.Count; i++)
        {
            BsLevel level = campaign[i];
            int glasses = level.Glasses != null ? level.Glasses.Count : 0;
            int columns = Mathf.Max(1, level.ColumnsPerRow);
            int rows = Mathf.Max(2, Mathf.CeilToInt(glasses / (float)columns));
            bool isCurrent = controller.CurrentLevel == level;

            using (new EditorGUILayout.HorizontalScope(
                       isCurrent ? EditorStyles.helpBox : GUIStyle.none))
            {
                EditorGUILayout.LabelField(
                    (isCurrent ? "> " : "   ") + "Level " + level.Index,
                    GUILayout.Width(90f));
                EditorGUILayout.LabelField(
                    glasses + " glasses - " + columns + " columns - " + rows + " rows",
                    EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(
                           jumpDisabled))
                {
                    if (GUILayout.Button(
                            EditorApplication.isPlaying ? "Go" : "Open",
                            GUILayout.Width(60f)))
                        Go(level.Index);
                }
            }
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawLifeTools()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Lives", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(BartenderProgressService.IsLifeFull))
                {
                    if (GUILayout.Button("Refill Lives")) RefillLives();
                }
                if (GUILayout.Button("+1 Life")) GrantOneLife();
            }
            EditorGUILayout.LabelField(
                BartenderProgressService.LifeTimer > System.TimeSpan.Zero
                    ? "Next life: " + BartenderProgressService.LifeTimer.ToString(@"mm\:ss")
                    : "Lives full.",
                EditorStyles.miniLabel);
        }
    }

    private void DrawCoinTools()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Coins", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                coinAmount = Mathf.Clamp(
                    EditorGUILayout.IntField("Balance", coinAmount),
                    0, BartenderProgressService.EditorMaxCoins);
                if (GUILayout.Button("Apply", GUILayout.Width(105f)))
                    SetCoins(coinAmount);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("+100")) AddCoins(100);
                if (GUILayout.Button("+500")) AddCoins(500);
                if (GUILayout.Button("+1000")) AddCoins(1000);
                using (new EditorGUI.DisabledScope(BartenderProgressService.Coins <= 0))
                {
                    if (GUILayout.Button("Reset")) SetCoins(0);
                }
            }
            EditorGUILayout.LabelField(
                "Updates the current save.",
                EditorStyles.miniLabel);
        }
    }

    private void Go(int levelNumber)
    {
        if (BartenderTimedOrdersTutorialProgress.EditorReplayActive)
        {
            Report("Finish the timer tutorial or exit Play first.",
                MessageType.Warning);
            return;
        }
        if (controller == null)
        {
            Report("No level controller in this scene.", MessageType.Error);
            return;
        }
        bool levelExists = false;
        for (int i = 0; i < campaign.Count; i++)
        {
            if (campaign[i] == null || campaign[i].Index != levelNumber) continue;
            levelExists = true;
            break;
        }
        if (!levelExists)
        {
            Report("Level " + levelNumber + " is not in the campaign.", MessageType.Error);
            return;
        }

        Scene scene = controller.gameObject.scene;
        if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
        {
            Report("Save the gameplay scene before using Level Jumper.",
                MessageType.Error);
            return;
        }

        quickLevel = levelNumber;
        BartenderFirstShiftProgress.EditorCancelTestRun();
        BartenderTimedOrdersTutorialProgress.EditorCancelReplay();
        BartenderTimedOrdersTutorialProgress
            .EditorSuppressNaturalStartForLevelJumper();
        SessionState.SetInt(PendingLevelKey, levelNumber);
        SessionState.SetString(PendingSceneKey, scene.path);
        if (EditorApplication.isPlaying)
        {
            Report("Level " + levelNumber
                 + " queued until the current animation ends.",
                MessageType.Info);
            BeginPendingWatch();
            return;
        }

        Report("Level " + levelNumber
             + " ready. Starting Play.",
            MessageType.Info);
        if (!EditorApplication.isPlayingOrWillChangePlaymode)
        {
            BartenderPlayModeStartScene.UseCurrentSceneForNextPlay();
            EditorApplication.EnterPlaymode();
        }
    }

    private void StartFirstShiftTutorial()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Report("Start the tutorial from Edit mode.",
                MessageType.Warning);
            return;
        }

        SceneAsset gameplayScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(
            FirstShiftGameplayScenePath);
        if (gameplayScene == null)
        {
            Report("Tutorial scene not found: "
                 + FirstShiftGameplayScenePath, MessageType.Error);
            return;
        }
        if (!BartenderFirstShiftProgress.EditorTryArmTestRun(
                gameplayScene.name, out string rejectionReason))
        {
            Report("Tutorial setup failed: " + rejectionReason,
                MessageType.Error);
            return;
        }

        BartenderTimedOrdersTutorialProgress.EditorCancelReplay();
        BartenderTimedOrdersTutorialProgress
            .EditorClearNaturalStartSuppression();

        ClearPendingRequest();
        StopPendingWatch();
        Report("First Shift ready. Starting Play without changing progress.", MessageType.Info);
        BartenderPlayModeStartScene.UseSceneForNextPlay(gameplayScene);
        EditorApplication.EnterPlaymode();
    }

    private void StartTimedOrdersTutorial()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Report("Start the timer tutorial from Edit mode.",
                MessageType.Warning);
            return;
        }

        SceneAsset gameplayScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(
            FirstShiftGameplayScenePath);
        if (gameplayScene == null)
        {
            Report("Tutorial scene not found: "
                 + FirstShiftGameplayScenePath, MessageType.Error);
            return;
        }

        BartenderFirstShiftProgress.EditorCancelTestRun();
        BartenderTimedOrdersTutorialProgress
            .EditorClearNaturalStartSuppression();
        if (!BartenderTimedOrdersTutorialProgress.EditorTryArmReplay(
                gameplayScene.name, out string rejectionReason))
        {
            Report("Timer tutorial setup failed: " + rejectionReason,
                MessageType.Error);
            return;
        }

        ClearPendingRequest();
        StopPendingWatch();
        Report("Timer tutorial ready. Starting Level 15 without changing lives, coins or progress.",
            MessageType.Info);
        BartenderPlayModeStartScene.UseSceneForNextPlay(gameplayScene);
        EditorApplication.EnterPlaymode();
    }

    private void CancelPendingJump()
    {
        if (PendingLevel <= NoPendingLevel) return;
        ClearPendingRequest();
        StopPendingWatch();
        BartenderTimedOrdersTutorialProgress
            .EditorClearNaturalStartSuppression();
        Report("Pending level jump cancelled.", MessageType.Info);
    }

    private void SetCoins(int value)
    {
        int target = Mathf.Clamp(value, 0, BartenderProgressService.EditorMaxCoins);
        if (BartenderProgressService.EditorSetCoins(target, out string rejectionReason))
        {
            coinAmount = BartenderProgressService.Coins;
            Report("Coins: " + BartenderProgressService.Coins + ".",
                MessageType.Info);
            return;
        }
        Report("Could not set coins: " + rejectionReason, MessageType.Warning);
    }

    private void AddCoins(int amount)
    {
        if (BartenderProgressService.EditorAddCoins(amount, out string rejectionReason))
        {
            coinAmount = BartenderProgressService.Coins;
            Report("Coins: " + BartenderProgressService.Coins + ".",
                MessageType.Info);
            return;
        }
        Report("Could not add coins: " + rejectionReason, MessageType.Warning);
    }

    private void RefillLives()
    {
        if (BartenderProgressService.EditorRefillLives(out string rejectionReason))
        {
            Report("Lives refilled.", MessageType.Info);
            return;
        }
        Report("Could not refill lives: " + rejectionReason, MessageType.Warning);
    }

    private void GrantOneLife()
    {
        int next = BartenderProgressService.Lives + 1;
        if (next > BartenderProgressService.MaxLives)
        {
            Report("Lives already full.", MessageType.Info);
            return;
        }
        if (BartenderProgressService.EditorSetLives(next, out string rejectionReason))
        {
            Report("Lives: " + next + ".", MessageType.Info);
            return;
        }
        Report("Could not add a life: " + rejectionReason, MessageType.Warning);
    }

    private void Report(string message, MessageType type) =>
        BroadcastReport(message, type);

    private void SetReport(string message, MessageType type)
    {
        lastMessage = message;
        lastMessageType = type;
        Repaint();
    }

    private static void Row(string label, string value)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(label, GUILayout.Width(140f));
            EditorGUILayout.LabelField(value, EditorStyles.miniLabel);
        }
    }
}
