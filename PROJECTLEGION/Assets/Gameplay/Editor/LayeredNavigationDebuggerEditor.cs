using UnityEditor;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [CustomEditor(typeof(LayeredNavigationDebugger))]
    internal sealed class LayeredNavigationDebuggerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var debugger = (LayeredNavigationDebugger)target;
            if (GUILayout.Button("Build Multi-layer Snapshot")) debugger.Build();
            if (GUILayout.Button("Run Ground and Bridge Paths")) debugger.RunPaths();
            if (GUILayout.Button("Inspect Node")) debugger.Inspect();
            if (GUILayout.Button("Clear Multi-layer Snapshot")) debugger.Clear();
            EditorGUILayout.HelpBox(debugger.Summary, MessageType.Info);
            if (!string.IsNullOrEmpty(debugger.Inspection))
                EditorGUILayout.HelpBox(debugger.Inspection, MessageType.None);
            if (GUI.changed) SceneView.RepaintAll();
        }
    }
}
