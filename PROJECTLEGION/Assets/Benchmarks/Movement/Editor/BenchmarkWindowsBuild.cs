using System;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace ProjectLegion.MovementBenchmark
{
    internal static class BenchmarkWindowsBuild
    {
        private const string Scene = "Assets/Benchmarks/Movement/Scenes/MovementBenchmark.unity";

        [MenuItem("Tools/Project Legion/Build Windows Benchmark Player")]
        private static void BuildFromMenu()
        {
            string path = EditorUtility.SaveFilePanel("Build Windows Benchmark Player", "", "LegionBenchmark", "exe");
            if (!string.IsNullOrEmpty(path))
                Build(path);
        }

        public static void BuildFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-legion-build-path");
            if (index < 0 || index + 1 == args.Length)
                throw new ArgumentException("Pass -legion-build-path with an .exe path.");
            Build(args[index + 1]);
        }

        private static void Build(string path)
        {
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The Windows benchmark output must be an .exe path.");
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                throw new InvalidOperationException("Select the Windows x64 build target before building.");

            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                locationPathName = path,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development | BuildOptions.StrictMode
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException($"Windows benchmark build {report.summary.result}.");
        }
    }
}
