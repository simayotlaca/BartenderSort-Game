// Headless iOS build girisi. Unity'yi batchmode'da -executeMethod ile cagirmak icin
// projede statik bir metot bulunmasi sart; bu dosya sadece o kancayi saglar.
// Oyun mantigina dokunmaz, tamamen Editor tarafidir.
//
// Kullanim:
//   Unity -batchmode -quit -projectPath <proje> -buildTarget iOS \
//         -executeMethod BartenderIosBuild.Build -logFile <log>
//
// Cikti: <proje>/Builds/iOS  (bir Xcode projesi, .ipa degil — .gitignore'da zaten Builds/ var)
// Ortam degiskeni BARTENDER_IOS_DEV=1 verilirse development build alinir.
// BARTENDER_IOS_OUTPUT verilirse mevcut Xcode projesini ezmeden o klasore yazar.

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
                Fail("Build listesinde etkin sahne yok. File > Build Settings'e sahne ekle.");
                return;
            }

            Debug.Log($"[iOS BUILD] {scenes.Length} sahne: {string.Join(", ", scenes)}");
            Debug.Log($"[iOS BUILD] bundle={PlayerSettings.GetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.iOS)} " +
                      $"team={PlayerSettings.iOS.appleDeveloperTeamID} " +
                      $"autoSign={PlayerSettings.iOS.appleEnableAutomaticSigning}");

            // Aktif hedef iOS degilse batchmode -buildTarget bayragi tutmamis demektir;
            // BuildPlayer yine de dogru hedefe alir ama uyarmakta fayda var.
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.iOS)
                Debug.LogWarning($"[iOS BUILD] aktif hedef {EditorUserBuildSettings.activeBuildTarget}, iOS'a gecirilecek");

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
                Debug.Log($"[iOS BUILD] BASARILI — {summary.totalSize / (1024 * 1024)} MB, " +
                          $"{summary.totalTime.TotalMinutes:F1} dk, cikti: {summary.outputPath}");
                EditorApplication.Exit(0);
                return;
            }

            // Hata mesajlarini loga tek tek dok; batchmode ozeti cok kisa oluyor.
            foreach (var step in report.steps)
                foreach (var msg in step.messages)
                    if (msg.type == LogType.Error || msg.type == LogType.Exception)
                        Debug.LogError($"[iOS BUILD] {step.name}: {msg.content}");

            Fail($"Build sonucu: {summary.result}, {summary.totalErrors} hata");
        }
        catch (Exception e)
        {
            Fail($"Istisna: {e}");
        }
    }

    static void Fail(string reason)
    {
        Debug.LogError($"[iOS BUILD] BASARISIZ — {reason}");
        EditorApplication.Exit(1);
    }
}
