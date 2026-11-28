using UnityEditor;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [CustomEditor(typeof(NavigationHierarchyDebugger))]
    internal sealed class NavigationHierarchyDebuggerEditor : UnityEditor.Editor
    {
        private double _nextRepaint;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var debugger = (NavigationHierarchyDebugger)target;
            if (GUILayout.Button("Build Hierarchical Graph")) debugger.BuildGraph();
            if (GUILayout.Button("Run Hierarchical Search")) debugger.Search();
            if (GUILayout.Button("Clear Hierarchical Graph")) debugger.ClearGraph();
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
