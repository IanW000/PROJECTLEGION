using System;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace ProjectLegion.Gameplay
{
    internal static class PrototypeWindowsBuild
    {
        private const string Scene = "Assets/Gameplay/Scenes/RtsPrototype.unity";

        [MenuItem("Tools/Project Legion/Build Windows RTS Prototype")]
        private static void BuildFromMenu()
        {
            string path = EditorUtility.SaveFilePanel("Build Windows RTS Prototype", "", "LegionRTS", "exe");
            if (!string.IsNullOrEmpty(path))
                Build(path);
        }

        public static void BuildFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-prototype-build-path");
            if (index < 0 || index + 1 >= args.Length)
                throw new ArgumentException("Supply -prototype-build-path followed by an .exe path.");
            Build(args[index + 1]);
        }

        private static void Build(string path)
        {
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                throw new InvalidOperationException("Use the active Windows x64 target and an .exe path.");

            var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                target = BuildTarget.StandaloneWindows64,
                locationPathName = path,
                options = BuildOptions.Development | BuildOptions.StrictMode
            });
            if (result.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException($"Prototype build result: {result.summary.result}.");
        }
    }
}
