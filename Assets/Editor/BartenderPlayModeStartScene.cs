using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Play starts at the home screen. Debug tools can override the scene for one session.</summary>
[InitializeOnLoad]
internal static class BartenderPlayModeStartScene
{
    internal const string StartScenePath =
        "Assets/LiquidSort/SortingShelfShowcase.unity";

    private const string BypassOnceKey =
        "GlassPourMathDemo.PlayModeStartScene.BypassOnce";

    static BartenderPlayModeStartScene()
    {
        EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
        EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
        QueueConfiguration();
    }

    internal static bool TryConfigure(out string error)
    {
        error = null;
        SceneAsset startScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(
            StartScenePath);
        if (startScene == null)
        {
            error = "Play start scene not found: " + StartScenePath;
            return false;
        }

        if (EditorSceneManager.playModeStartScene != startScene)
            EditorSceneManager.playModeStartScene = startScene;
        return true;
    }

    /// <summary>I use the open scene once, then restore the home screen on return to Edit mode.</summary>
    internal static void UseCurrentSceneForNextPlay()
    {
        UseSceneForNextPlay(null);
    }

    /// <summary>I use this scene for the next Play session, then restore the home screen.</summary>
    internal static void UseSceneForNextPlay(SceneAsset scene)
    {
        SessionState.SetBool(BypassOnceKey, true);
        EditorApplication.delayCall -= ConfigureOrReport;
        EditorSceneManager.playModeStartScene = scene;
    }

    private static void HandlePlayModeStateChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.ExitingEditMode)
        {
            if (SessionState.GetBool(BypassOnceKey, false))
            {
                // I keep the chosen scene through domain reload until Unity uses it.
                return;
            }

            ConfigureOrReport();
            return;
        }

        if (change == PlayModeStateChange.EnteredPlayMode)
        {
            SessionState.EraseBool(BypassOnceKey);
            return;
        }

        if (change == PlayModeStateChange.EnteredEditMode)
        {
            SessionState.EraseBool(BypassOnceKey);
            QueueConfiguration();
        }
    }

    private static void QueueConfiguration()
    {
        EditorApplication.delayCall -= ConfigureOrReport;
        if (SessionState.GetBool(BypassOnceKey, false)) return;
        EditorApplication.delayCall += ConfigureOrReport;
    }

    private static void ConfigureOrReport()
    {
        if (!TryConfigure(out string error))
            Debug.LogError(error);
    }
}
