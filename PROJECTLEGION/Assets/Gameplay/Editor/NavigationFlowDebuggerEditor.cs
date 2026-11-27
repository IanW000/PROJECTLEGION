using UnityEditor;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [CustomEditor(typeof(NavigationFlowDebugger))]
    internal sealed class NavigationFlowDebuggerEditor : UnityEditor.Editor
    {
        private double _nextRepaint;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var debugger = (NavigationFlowDebugger)target;
            if (GUILayout.Button("Build Flow Field")) debugger.BuildField();
            if (GUILayout.Button("Clear Flow Field")) debugger.ClearField();
            EditorGUILayout.HelpBox(debugger.Summary, MessageType.Info);
        }

        private void OnEnable() => EditorApplication.update += Refresh;
        private void OnDisable() => EditorApplication.update -= Refresh;

        private void Refresh()
        {
            if (EditorApplication.timeSinceStartup < _nextRepaint) return;
            _nextRepaint = EditorApplication.timeSinceStartup + 0.25;
            Repaint();
        }
    }
}
