using System;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace ProjectLegion.RenderingBenchmark
{
    internal static class RenderBenchmarkWindowsBuild
    {
        private const string Scene = "Assets/Benchmarks/Rendering/Scenes/RenderingBenchmark.unity";

        [MenuItem("Tools/Project Legion/Build Windows Rendering Benchmark")]
        private static void BuildFromMenu()
        {
            string path = EditorUtility.SaveFilePanel("Build Rendering Benchmark", "", "LegionRenderingBenchmark", "exe");
            if (!string.IsNullOrEmpty(path))
                Build(path);
        }

        public static void BuildFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-render-build-path");
            if (index < 0 || index + 1 == args.Length)
                throw new ArgumentException("Pass -render-build-path with an executable path.");
            Build(args[index + 1]);
        }

        private static void Build(string path)
        {
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                throw new InvalidOperationException("Use the active Windows x64 target and an .exe path.");

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                target = BuildTarget.StandaloneWindows64,
                locationPathName = path,
                options = BuildOptions.Development | BuildOptions.StrictMode
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException($"Rendering benchmark build {report.summary.result}.");
        }
    }
}
