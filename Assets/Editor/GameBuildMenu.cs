using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace LatticeVeil.EditorTools
{
    /// <summary>
    /// Editor menu for building and running the standalone Unity game by
    /// itself (no launcher involved). Builds ONLY the MainMenu scene to
    /// builds/Game/LatticeVeilGame.exe (THE GAME; the LAUNCHER is built to
    /// builds/Launcher/LatticeVeil.exe — see builds/README.txt) — never the
    /// project's build-scene
    /// list, so Unity's own Ctrl+B stays untouched.
    /// </summary>
    public static class GameBuildMenu
    {
        private const string GameScenePath = "Assets/Scenes/MainMenu.unity";

        private static string OutputExePath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "builds", "Game", "LatticeVeilGame.exe"));

        [MenuItem("LatticeVeil/Build Game %#g", false, 0)]
        public static void BuildGame()
        {
            BuildAndOptionallyRun(run: false);
        }

        [MenuItem("LatticeVeil/Build And Run Game %#r", false, 1)]
        public static void BuildAndRunGame()
        {
            BuildAndOptionallyRun(run: true);
        }

        private static void BuildAndOptionallyRun(bool run)
        {
            var exePath = OutputExePath;
            Directory.CreateDirectory(Path.GetDirectoryName(exePath));

            var report = BuildPipeline.BuildPlayer(
                new[] { GameScenePath },
                exePath,
                BuildTarget.StandaloneWindows64,
                BuildOptions.None);

            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"LatticeVeil game build failed: {report.summary.result} ({report.summary.totalErrors} errors). See Console.");
                return;
            }

            Debug.Log($"LatticeVeil game built: {exePath} ({report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalErrors} errors, {report.summary.totalWarnings} warnings)");

            if (!run)
                return;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? ".",
                    UseShellExecute = true
                });
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Failed to start the game: {ex.Message}");
            }
        }
    }
}
