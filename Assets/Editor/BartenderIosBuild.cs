// Batch iOS build entry point.
//
// Usage:
//   Unity -batchmode -quit -projectPath <proje> -buildTarget iOS \
//         -executeMethod BartenderIosBuild.Build -logFile <log>
//
// Output: <project>/Builds/iOS (Xcode project, not IPA).
// BARTENDER_IOS_DEV=1 enables a development build.
// BARTENDER_IOS_OUTPUT sets the output directory.

using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BartenderIosBuild
{
    const string DefaultOutputDir = "Builds/iOS";

    public static void Build()
    {
        try
        {
            var scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                Fail("No enabled scenes.");
                return;
            }

            Debug.Log($"[iOS BUILD] {scenes.Length} scenes: {string.Join(", ", scenes)}");
            Debug.Log($"[iOS BUILD] bundle={PlayerSettings.GetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.iOS)} " +
                      $"team={PlayerSettings.iOS.appleDeveloperTeamID} " +
                      $"autoSign={PlayerSettings.iOS.appleEnableAutomaticSigning}");

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.iOS)
                Debug.LogWarning($"[iOS BUILD] switching from {EditorUserBuildSettings.activeBuildTarget} to iOS.");

            string outputDir = Environment.GetEnvironmentVariable("BARTENDER_IOS_OUTPUT");
            if (string.IsNullOrWhiteSpace(outputDir)) outputDir = DefaultOutputDir;

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputDir,
                target = BuildTarget.iOS,
                targetGroup = BuildTargetGroup.iOS,
                options = Environment.GetEnvironmentVariable("BARTENDER_IOS_DEV") == "1"
                    ? BuildOptions.Development
                    : BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[iOS BUILD] SUCCESS — {summary.totalSize / (1024 * 1024)} MB, " +
                          $"{summary.totalTime.TotalMinutes:F1} min, output: {summary.outputPath}");
                EditorApplication.Exit(0);
                return;
            }

            foreach (var step in report.steps)
                foreach (var msg in step.messages)
                    if (msg.type == LogType.Error || msg.type == LogType.Exception)
                        Debug.LogError($"[iOS BUILD] {step.name}: {msg.content}");

            Fail($"Build result: {summary.result}, {summary.totalErrors} errors");
        }
        catch (Exception e)
        {
            Fail($"Exception: {e}");
        }
    }

    static void Fail(string reason)
    {
        Debug.LogError($"[iOS BUILD] FAILED — {reason}");
        EditorApplication.Exit(1);
    }
}
