using UnityEditor;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [CustomEditor(typeof(NavigationBatchDebugger))]
    internal sealed class NavigationBatchDebuggerEditor : UnityEditor.Editor
    {
        private double _nextRepaint;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var debugger = (NavigationBatchDebugger)target;
            if (GUILayout.Button("Start or Repeat Batch")) debugger.StartBatch();
            if (GUILayout.Button("Cancel Batch")) debugger.CancelBatch();
            if (GUILayout.Button("Clear Batch Results")) debugger.ClearResults();
            EditorGUILayout.HelpBox(debugger.Summary, MessageType.Info);
        }

        private void OnEnable() => EditorApplication.update += Refresh;

        private void OnDisable() => EditorApplication.update -= Refresh;

        private void Refresh()
        {
            if (EditorApplication.timeSinceStartup < _nextRepaint)
                return;
            _nextRepaint = EditorApplication.timeSinceStartup + 0.25;
            Repaint();
        }
    }
}
