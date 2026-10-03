using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class CommandLineBuilder
{
    public static void BuildLauncher()
    {
        var scenes = new[] { "Assets/Scenes/Launcher.unity" };
        var report = BuildPipeline.BuildPlayer(
            scenes,
            "builds/LatticeVeil.exe",
            BuildTarget.StandaloneWindows64,
            BuildOptions.None);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Build result: {report.summary.result}");
        sb.AppendLine($"Errors: {report.summary.totalErrors}, Warnings: {report.summary.totalWarnings}");
        foreach (var step in report.steps)
        {
            foreach (var msg in step.messages.Where(m => m.type == LogType.Error || m.type == LogType.Exception))
                sb.AppendLine($"  [{step.name}] {msg.content}");
        }

        UnityEngine.Debug.Log("LVC_BUILD_MARKER " + sb.ToString().Replace("\n", " | "));

        if (report.summary.result != BuildResult.Succeeded)
        {
            EditorApplication.Exit(1);
        }
        EditorApplication.Exit(0);
    }
}
