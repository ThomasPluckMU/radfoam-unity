using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Ply
{
    [CustomEditor(typeof(PlySceneProxy))]
    public class PlySceneProxyEditor : Editor
    {
        private SerializedProperty sourceDataProp;
        private SerializedProperty visualScaleProp;
        private SerializedProperty persistInSceneProp;
        private SerializedProperty maxPointsProp;
        
        private PlySceneProxy proxy;
        private const int MAX_POINTS = 1000000000; // Higher limit since we're using simple GL
        
        // Static flag to control global visibility of all PLY objects
        private static bool showAllPlyObjects = false;

        private void OnEnable()
        {
            sourceDataProp = serializedObject.FindProperty("sourceData");
            visualScaleProp = serializedObject.FindProperty("visualScale");
            persistInSceneProp = serializedObject.FindProperty("persistInScene");
            maxPointsProp = serializedObject.FindProperty("maxPointsToRender");
            
            proxy = (PlySceneProxy)target;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            
            EditorGUILayout.LabelField("PLY Data Source", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(sourceDataProp);
            
            if (sourceDataProp.objectReferenceValue == null)
            {
                EditorGUILayout.HelpBox("Please assign a PLY Data asset", MessageType.Warning);
            }
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Visual Settings", EditorStyles.boldLabel);
            EditorGUILayout.Slider(visualScaleProp, 0.001f, 0.1f, new GUIContent("Point Size"));
            EditorGUILayout.PropertyField(maxPointsProp);
            
            EditorGUILayout.PropertyField(persistInSceneProp, new GUIContent("Persist In Scene", "Keep this PLY object visible even when not selected"));
            
            // Add a global toggle for showing all PLY objects
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            showAllPlyObjects = EditorGUILayout.Toggle(new GUIContent("Show All PLY Objects", "Make all PLY objects visible in the scene view, regardless of selection"), showAllPlyObjects);
            if (EditorGUI.EndChangeCheck())
            {
                // Force a repaint of the scene view when the global toggle changes
                SceneView.RepaintAll();
            }
            
            serializedObject.ApplyModifiedProperties();
            
            // Add debug buttons
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Debugging", EditorStyles.boldLabel);
            
            if (GUILayout.Button("Log Color Data"))
            {
                LogColorData();
            }
            
            if (GUILayout.Button("Force Refresh"))
            {
                proxy.ForceRefresh();
                SceneView.RepaintAll();
            }

            // Point selection UI
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Point Selection", EditorStyles.boldLabel);
            
            int selectedCount = proxy.SelectedPoints.Count;
            EditorGUILayout.LabelField($"Selected Points: {selectedCount}");
            
            using (new EditorGUI.DisabledScope(selectedCount == 0))
            {
                if (GUILayout.Button("Clear Selection"))
                {
                    proxy.ClearSelection();
                }
                
                if (GUILayout.Button("Delete Selected Points"))
                {
                    DeleteSelectedPoints();
                }
            }
            
            // Point management UI
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Point Management", EditorStyles.boldLabel);
            
            EditorGUILayout.LabelField($"Hidden Points: {proxy.HiddenPoints.Count}");
            
            using (new EditorGUI.DisabledScope(proxy.HiddenPoints.Count == 0))
            {
                if (GUILayout.Button("Show All Points"))
                {
                    Undo.RecordObject(proxy, "Show All Points");
                    proxy.ShowAllPoints();
                    EditorUtility.SetDirty(proxy);
                }

                if (GUILayout.Button("Export Visible Points as PLY"))
                {
                    ExportVisiblePoints();
                }
            }
        }

        private void LogColorData()
        {
            if (proxy == null || proxy.Points == null || proxy.Points.Length == 0)
            {
                Debug.Log("No point data available");
                return;
            }
            
            Color[] colors = proxy.Colors;
            int count = Mathf.Min(10, colors.Length); // Log first 10 colors
            
            Debug.Log($"Point count: {proxy.Points.Length}, Color count: {colors.Length}");
            
            for (int i = 0; i < count; i++)
            {
                Color c = colors[i];
                Debug.Log($"Color[{i}]: R={c.r:F3}, G={c.g:F3}, B={c.b:F3}, A={c.a:F3}");
            }
        }

        private void DeleteSelectedPoints()
        {
            if (proxy.SelectedPoints.Count == 0)
                return;
                
            bool confirm = EditorUtility.DisplayDialog(
                "Delete Points",
                $"Are you sure you want to delete {proxy.SelectedPoints.Count} points?",
                "Delete", "Cancel"
            );
            
            if (!confirm)
                return;
                
            Undo.RecordObject(proxy, "Delete Points");
            
            // Non-destructive approach - hide the points
            proxy.HideSelectedPoints();
            
            Debug.Log($"Hid {proxy.HiddenPoints.Count} points (non-destructive)");
            EditorUtility.SetDirty(proxy);
        }

        private void ExportVisiblePoints()
        {
            if (proxy == null || proxy.Points == null)
                return;
                
            string defaultName = proxy.name + "_filtered.ply";
            string savePath = EditorUtility.SaveFilePanel("Save Filtered PLY", "", defaultName, "ply");
            if (string.IsNullOrEmpty(savePath))
                return;
                
            if (proxy.ExportVisiblePoints(savePath))
            {
                Debug.Log($"Successfully exported filtered PLY to: {savePath}");
            }
        }
    }
}
