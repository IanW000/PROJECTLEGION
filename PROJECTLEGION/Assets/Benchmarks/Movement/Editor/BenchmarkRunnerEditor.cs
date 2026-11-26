using UnityEditor;
using UnityEngine;

namespace ProjectLegion.MovementBenchmark
{
    [CustomEditor(typeof(BenchmarkRunner))]
    internal sealed class BenchmarkRunnerEditor : Editor
    {
        public override bool RequiresConstantRepaint() => EditorApplication.isPlaying;

        public override void OnInspectorGUI()
        {
            var runner = (BenchmarkRunner)target;
            using (new EditorGUI.DisabledScope(runner.IsRunning))
                DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Mode", runner.Mode.ToString());
            EditorGUILayout.LabelField("Configured units", runner.UnitCount.ToString());
            EditorGUILayout.LabelField("Lifecycle", runner.Lifecycle.ToString());
            EditorGUILayout.LabelField("Completed warm-up steps", runner.CompletedWarmupSteps.ToString());
            EditorGUILayout.LabelField("Completed sampling steps", runner.CompletedSampleSteps.ToString());
            EditorGUILayout.HelpBox(runner.Status,
                runner.Lifecycle == BenchmarkLifecycle.Failed ? MessageType.Error : MessageType.Info);

            using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying || !runner.isActiveAndEnabled || runner.IsRunning))
            {
                if (GUILayout.Button("Start Benchmark"))
                    runner.TryStartConfiguredRun();
            }

            using (new EditorGUI.DisabledScope(!runner.IsRunning))
            {
                if (GUILayout.Button("Cancel Benchmark"))
                    runner.Cancel();
            }
        }
    }
}
