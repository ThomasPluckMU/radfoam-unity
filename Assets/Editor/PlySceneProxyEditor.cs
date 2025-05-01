using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

namespace Ply
{
    [CustomEditor(typeof(PlySceneProxy))]
    public class PlySceneProxyEditor : Editor
    {
        private SerializedProperty sourceDataProp;
        private SerializedProperty visualScaleProp;
        private SerializedProperty persistInSceneProp;
        private SerializedProperty maxPointsProp;
        private SerializedProperty useBoundingBoxFilterProp;
        private SerializedProperty boundingBoxCenterProp;
        private SerializedProperty boundingBoxSizeProp;
        
        private PlySceneProxy proxy;
        private const int MAX_POINTS = 50000; // Higher limit since we're using simple GL
        
        // Static flag to control global visibility of all PLY objects
        private static bool showAllPlyObjects = false;


        private void OnEnable()
        {
            sourceDataProp = serializedObject.FindProperty("sourceData");
            visualScaleProp = serializedObject.FindProperty("visualScale");
            persistInSceneProp = serializedObject.FindProperty("persistInScene");
            maxPointsProp = serializedObject.FindProperty("maxPointsToRender");
            useBoundingBoxFilterProp = serializedObject.FindProperty("useBoundingBoxFilter");
            boundingBoxCenterProp = serializedObject.FindProperty("boundingBoxCenter");
            boundingBoxSizeProp = serializedObject.FindProperty("boundingBoxSize");
            
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
            
            // Bounding Box Filter UI
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Bounding Box Filter", EditorStyles.boldLabel);
            
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(useBoundingBoxFilterProp, new GUIContent("Use Bounding Box Filter", "Only show/export points inside this box"));
            
            if (useBoundingBoxFilterProp.boolValue)
            {
                EditorGUILayout.HelpBox("Use the transform tools to position, rotate, and scale the bounding box in the scene view.", MessageType.Info);
                
                // Button to set box based on selection
                if (GUILayout.Button("Set to Selection Bounds"))
                {
                    SetBoundingBoxToSelection();
                }
                
                // Button to reset to data bounds
                if (GUILayout.Button("Reset to Data Bounds"))
                {
                    ResetBoundingBoxToDataBounds();
                }
                
                // Button to focus on box handle
                if (proxy.BoundingBoxHandle != null && GUILayout.Button("Focus on Bounding Box"))
                {
                    Selection.activeGameObject = proxy.BoundingBoxHandle;
                    SceneView.lastActiveSceneView.FrameSelected();
                }
            }
            
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                proxy.UpdateBoundingBox();
                SceneView.RepaintAll();
            }
            
            serializedObject.ApplyModifiedProperties();
            
            // Export section with advanced options
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Export", EditorStyles.boldLabel);
                        
            if (GUILayout.Button("Export Filtered PLY"))
            {
                ExportFilteredPly();
            }
                        
            // Add debug buttons
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Debugging", EditorStyles.boldLabel);
            
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
                
                if (GUILayout.Button("Hide Selected Points"))
                {
                    HideSelectedPoints();
                }
            }
            
            // Point management UI
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Point Management", EditorStyles.boldLabel);
            
            EditorGUILayout.LabelField($"Hidden Points: {proxy.HiddenPoints.Count}");
            
            using (new EditorGUI.DisabledScope(proxy.HiddenPoints.Count == 0))
            {
                if (GUILayout.Button("Show All Hidden Points"))
                {
                    ShowAllPoints();
                }
            }
        }

        private void SetBoundingBoxToSelection()
        {
            if (proxy.SelectedPoints.Count == 0) return;
            
            Vector3[] points = proxy.Points;
            Bounds selectionBounds = new Bounds();
            bool isFirst = true;
            
            foreach (int index in proxy.SelectedPoints)
            {
                if (index < 0 || index >= points.Length) continue;
                
                if (isFirst)
                {
                    selectionBounds = new Bounds(points[index], Vector3.zero);
                    isFirst = false;
                }
                else
                {
                    selectionBounds.Encapsulate(points[index]);
                }
            }
            
            if (!isFirst) // If we added at least one point
            {
                Undo.RecordObject(proxy, "Set Bounding Box To Selection");
                proxy.BoundingBoxCenter = selectionBounds.center;
                proxy.BoundingBoxSize = selectionBounds.size * 1.05f; // Add a small margin
                EditorUtility.SetDirty(proxy);
            }
        }
        
        private void ResetBoundingBoxToDataBounds()
        {
            Bounds dataBounds = proxy.DataBounds;
            
            Undo.RecordObject(proxy, "Reset Bounding Box");
            proxy.BoundingBoxCenter = dataBounds.center;
            proxy.BoundingBoxSize = dataBounds.size;
            EditorUtility.SetDirty(proxy);
        }
        
        private void ExportFilteredPly()
        {
            if (proxy.SourceData == null)
            {
                EditorUtility.DisplayDialog("Export Error", "No PLY data source assigned", "OK");
                return;
            }
            
            string defaultName = proxy.gameObject.name + "_filtered.ply";
            string path = EditorUtility.SaveFilePanel("Export Filtered PLY", "", defaultName, "ply");
            
            if (string.IsNullOrEmpty(path)) return;
            
            bool success;
            
            success = PlyExporter.ExportWithIndices(
                path, 
                proxy.SourceData, 
                proxy.HiddenPoints,
                proxy.UseBoundingBoxFilter,  // Use the bounding box flag
                proxy.BoundingBoxCenter,     // Pass bounding box parameters
                proxy.BoundingBoxSize,
                proxy.BoundingBoxRotation,
                (progress, message) => {
                    EditorUtility.DisplayProgressBar("Exporting PLY", message, progress);
                }
            );
            
            EditorUtility.ClearProgressBar();
            
            if (success)
            {
                EditorUtility.DisplayDialog("Export Complete", "Filtered PLY file exported successfully", "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("Export Error", "Failed to export PLY file", "OK");
            }
        }

        private void HideSelectedPoints()
        {
            if (proxy.SelectedPoints.Count == 0)
                return;
                
            Undo.RecordObject(proxy, "Hide Selected Points");
            proxy.HideSelectedPoints();
            Debug.Log($"Hid {proxy.HiddenPoints.Count} points");
            EditorUtility.SetDirty(proxy);
        }
        
        private void ShowAllPoints()
        {
            Undo.RecordObject(proxy, "Show All Points");
            proxy.ShowAllPoints();
            Debug.Log("Showing all previously hidden points");
            EditorUtility.SetDirty(proxy);
        }
    }
}