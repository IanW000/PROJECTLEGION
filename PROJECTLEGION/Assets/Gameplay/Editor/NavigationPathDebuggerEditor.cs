using UnityEditor;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [CustomEditor(typeof(NavigationPathDebugger))]
    internal sealed class NavigationPathDebuggerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var debugger = (NavigationPathDebugger)target;
            if (GUILayout.Button("Run Path Search"))
            {
                debugger.RunSearch();
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("Clear Path"))
            {
                debugger.ClearSearch();
                SceneView.RepaintAll();
            }
            EditorGUILayout.HelpBox(debugger.SearchSummary, MessageType.Info);
        }
    }
}
