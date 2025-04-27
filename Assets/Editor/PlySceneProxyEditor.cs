using UnityEngine;
using UnityEditor;

namespace Ply
{
    [CustomEditor(typeof(PlySceneProxy))]
    public class PlySceneProxyEditor : Editor
    {
        // SerializedProperty references
        private SerializedProperty sourceDataProp;
        private SerializedProperty localOffsetProp;
        private SerializedProperty localScaleProp;
        private SerializedProperty rotationAnglesProp;
        private SerializedProperty densityThresholdProp;
        private SerializedProperty visualScaleProp;
        private SerializedProperty maxPointsToRenderProp;
        private SerializedProperty regionBoundsProp;
        private SerializedProperty useRegionBoundsProp;
        private SerializedProperty defaultColorProp;
        private SerializedProperty visualizationModeProp;
        
        // Foldout states
        private bool showTransformSettings = true;
        private bool showFilteringOptions = true;
        private bool showVisualSettings = true;
        private bool showDebugInfo = false;
        
        // Gizmo control
        private Tool lastTool = Tool.None;
        private bool editingRegion = false;
        
        // Properties for region editing
        private Vector3 regionCenter;
        private Vector3 regionSize;
        
        // Reference to the actual component
        private PlySceneProxy proxy;
        
        private void OnEnable()
        {
            // Get serialized properties
            sourceDataProp = serializedObject.FindProperty("sourceData");
            localOffsetProp = serializedObject.FindProperty("localOffset");
            localScaleProp = serializedObject.FindProperty("localScale");
            rotationAnglesProp = serializedObject.FindProperty("rotationAngles");
            densityThresholdProp = serializedObject.FindProperty("densityThreshold");
            visualScaleProp = serializedObject.FindProperty("visualScale");
            maxPointsToRenderProp = serializedObject.FindProperty("maxPointsToRender");
            regionBoundsProp = serializedObject.FindProperty("regionBounds");
            useRegionBoundsProp = serializedObject.FindProperty("useRegionBounds");
            defaultColorProp = serializedObject.FindProperty("defaultColor");
            visualizationModeProp = serializedObject.FindProperty("visualizationMode");
            
            // Get reference to the component
            proxy = (PlySceneProxy)target;
            
            // Enable SceneGUI callback
            SceneView.duringSceneGui += OnSceneGUI;
        }
        
        private void OnDisable()
        {
            // Clean up SceneGUI callback
            SceneView.duringSceneGui -= OnSceneGUI;
            
            // Restore original tool if we were editing
            if (editingRegion)
            {
                Tools.current = lastTool;
                editingRegion = false;
            }
        }
        
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            
            // PLY Data Source
            EditorGUILayout.LabelField("PLY Data Source", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(sourceDataProp);
            
            // Check if source data is assigned
            PlyData plyData = (PlyData)sourceDataProp.objectReferenceValue;
            if (plyData == null)
            {
                EditorGUILayout.HelpBox("Please assign a PLY Data asset", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Refresh Visualization"))
                {
                    proxy.ForceRefresh();
                    SceneView.RepaintAll();
                }
                if (GUILayout.Button("Frame in Scene View"))
                {
                    if (proxy.Points != null && proxy.Points.Length > 0)
                    {
                        SceneView.lastActiveSceneView.Frame(proxy.DataBounds, false);
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            
            EditorGUILayout.Space();
            
            // Transform Settings
            showTransformSettings = EditorGUILayout.Foldout(showTransformSettings, "Transform Settings", true);
            if (showTransformSettings)
            {
                EditorGUI.indentLevel++;
                
                EditorGUILayout.PropertyField(localOffsetProp, new GUIContent("Position Offset"));
                EditorGUILayout.PropertyField(rotationAnglesProp, new GUIContent("Rotation"));
                EditorGUILayout.PropertyField(localScaleProp, new GUIContent("Scale"));
                
                // Quick buttons for common transformations
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Reset Position"))
                {
                    localOffsetProp.vector3Value = Vector3.zero;
                }
                if (GUILayout.Button("Reset Rotation"))
                {
                    rotationAnglesProp.vector3Value = Vector3.zero;
                }
                if (GUILayout.Button("Reset Scale"))
                {
                    localScaleProp.vector3Value = Vector3.one;
                }
                EditorGUILayout.EndHorizontal();
                
                EditorGUI.indentLevel--;
            }
            
            EditorGUILayout.Space();
            
            // Filtering Options
            showFilteringOptions = EditorGUILayout.Foldout(showFilteringOptions, "Filtering Options", true);
            if (showFilteringOptions)
            {
                EditorGUI.indentLevel++;
                
                EditorGUILayout.Slider(densityThresholdProp, 0f, 1f, new GUIContent("Density Threshold"));
                EditorGUILayout.PropertyField(maxPointsToRenderProp, new GUIContent("Max Points"));
                
                // Region bounds editing
                EditorGUILayout.PropertyField(useRegionBoundsProp, new GUIContent("Use Region Bounds"));
                
                if (useRegionBoundsProp.boolValue)
                {
                    EditorGUI.indentLevel++;
                    
                    // Extract the bounds values for easier editing
                    regionCenter = regionBoundsProp.FindPropertyRelative("m_Center").vector3Value;
                    regionSize = regionBoundsProp.FindPropertyRelative("m_Extents").vector3Value * 2f; // Convert extents to size
                    
                    // Display fields for center and size
                    regionCenter = EditorGUILayout.Vector3Field("Center", regionCenter);
                    regionSize = EditorGUILayout.Vector3Field("Size", regionSize);
                    
                    // Update the bounds properties
                    regionBoundsProp.FindPropertyRelative("m_Center").vector3Value = regionCenter;
                    regionBoundsProp.FindPropertyRelative("m_Extents").vector3Value = regionSize * 0.5f; // Convert size back to extents
                    
                    if (GUILayout.Button("Edit Region in Scene"))
                    {
                        // Toggle region editing mode
                        editingRegion = !editingRegion;
                        
                        if (editingRegion)
                        {
                            // Store the current tool and switch to none to avoid conflicts
                            lastTool = Tools.current;
                            Tools.current = Tool.None;
                        }
                        else
                        {
                            // Restore the previous tool
                            Tools.current = lastTool;
                        }
                        
                        SceneView.RepaintAll();
                    }
                    
                    EditorGUI.indentLevel--;
                }
                
                EditorGUI.indentLevel--;
            }
            
            EditorGUILayout.Space();
            
            // Visual Settings
            showVisualSettings = EditorGUILayout.Foldout(showVisualSettings, "Visual Settings", true);
            if (showVisualSettings)
            {
                EditorGUI.indentLevel++;
                
                EditorGUILayout.PropertyField(visualizationModeProp);
                EditorGUILayout.Slider(visualScaleProp, 0.1f, 1f, new GUIContent("Visual Scale"));
                
                // Only show color field for custom visualization mode
                if (visualizationModeProp.enumValueIndex == (int)PlySceneProxy.VisualizationMode.Custom)
                {
                    EditorGUILayout.PropertyField(defaultColorProp);
                }
                
                EditorGUI.indentLevel--;
            }
            
            EditorGUILayout.Space();
            
            // Debug Information
            showDebugInfo = EditorGUILayout.Foldout(showDebugInfo, "Debug Information", true);
            if (showDebugInfo && proxy.Points != null)
            {
                EditorGUI.indentLevel++;
                
                EditorGUILayout.LabelField("Points Loaded", proxy.Points.Length.ToString());
                EditorGUILayout.LabelField("Bounds Center", proxy.DataBounds.center.ToString());
                EditorGUILayout.LabelField("Bounds Size", proxy.DataBounds.size.ToString());
                
                if (proxy.Points.Length > 0)
                {
                    EditorGUILayout.LabelField("First Point", proxy.Points[0].ToString());
                    EditorGUILayout.LabelField("Last Point", proxy.Points[proxy.Points.Length - 1].ToString());
                }
                
                if (GUILayout.Button("Log Point Cloud Info"))
                {
                    LogPointCloudInfo();
                }
                
                EditorGUI.indentLevel--;
            }
            
            serializedObject.ApplyModifiedProperties();
        }
        
        private void OnSceneGUI(SceneView sceneView)
        {
            if (proxy == null) return;
            
            // Draw point cloud visualization
            DrawPointCloud();
            
            // Handle region editing
            if (editingRegion && useRegionBoundsProp.boolValue)
            {
                // Draw a manipulatable bounds gizmo
                EditorGUI.BeginChangeCheck();
                
                // Draw the bounds with handles
                Bounds bounds = proxy.RegionBounds;
                
                // Create a matrix based on the GameObject's transform
                Matrix4x4 handleMatrix = Matrix4x4.identity;
                
                // Apply the handle matrix to make the handles draw in the right place
                Handles.matrix = handleMatrix;
                
                // Draw the bounds wire cube
                Handles.color = new Color(0.1f, 1f, 0.1f, 0.8f);
                Handles.DrawWireCube(bounds.center, bounds.size);
                
                // Position handle for the center
                Vector3 newCenter = Handles.PositionHandle(bounds.center, Quaternion.identity);
                
                // Scale handle for the size
                Vector3 newSize = Handles.ScaleHandle(bounds.size, bounds.center, Quaternion.identity, HandleUtility.GetHandleSize(bounds.center) * 1.5f);
                
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(proxy, "Modified Region Bounds");
                    
                    // Update the bounds
                    bounds.center = newCenter;
                    bounds.size = newSize;
                    
                    // Apply changes to the component
                    proxy.RegionBounds = bounds;
                    
                    // Update the serialized property
                    serializedObject.Update();
                    regionBoundsProp.FindPropertyRelative("m_Center").vector3Value = bounds.center;
                    regionBoundsProp.FindPropertyRelative("m_Extents").vector3Value = bounds.size * 0.5f;
                    serializedObject.ApplyModifiedProperties();
                }
                
                // Reset the matrix
                Handles.matrix = Matrix4x4.identity;
            }
        }
        
        private void DrawPointCloud()
        {
            if (proxy.Points == null || proxy.Points.Length == 0) return;
            
            // Draw bounds
            Handles.color = new Color(1f, 0.92f, 0.016f, 0.5f);
            Handles.DrawWireCube(proxy.DataBounds.center, proxy.DataBounds.size);
            
            // Determine point size based on bounds
            float pointSize = 0.01f * proxy.DataBounds.size.magnitude * proxy.VisualScale;
            
            // Use GL for more efficient point rendering
            Material pointMaterial = new Material(Shader.Find("Hidden/Internal-Colored"));
            pointMaterial.hideFlags = HideFlags.HideAndDontSave;
            
            pointMaterial.SetPass(0);
            
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            
            // Draw up to a maximum number of points for performance
            int pointsToRender = Mathf.Min(proxy.Points.Length, 10000); // Limit for editor performance
            int step = Mathf.Max(1, proxy.Points.Length / pointsToRender);
            
            for (int i = 0; i < proxy.Points.Length; i += step)
            {
                Vector3 pos = proxy.Points[i];
                Color color = proxy.Colors[i];
                GL.Color(color);
                
                // Draw a simple cross to represent the point
                float halfSize = pointSize * 0.5f;
                
                // Horizontal line
                GL.Vertex(pos + new Vector3(-halfSize, 0, 0));
                GL.Vertex(pos + new Vector3(halfSize, 0, 0));
                
                // Vertical line
                GL.Vertex(pos + new Vector3(0, -halfSize, 0));
                GL.Vertex(pos + new Vector3(0, halfSize, 0));
                
                // Depth line
                GL.Vertex(pos + new Vector3(0, 0, -halfSize));
                GL.Vertex(pos + new Vector3(0, 0, halfSize));
            }
            
            GL.End();
            GL.PopMatrix();
        }
        
        private void LogPointCloudInfo()
        {
            if (proxy == null || proxy.Points == null)
            {
                Debug.Log("No point cloud data available.");
                return;
            }
            
            Debug.Log($"PLY Scene Proxy: {proxy.name}");
            Debug.Log($"Source data: {(proxy.SourceData != null ? proxy.SourceData.name : "None")}");
            Debug.Log($"Points loaded: {proxy.Points.Length}");
            Debug.Log($"Bounds: Center={proxy.DataBounds.center}, Size={proxy.DataBounds.size}");
            
            if (proxy.Points.Length > 0)
            {
                Debug.Log($"First point: {proxy.Points[0]}, Color: {proxy.Colors[0]}");
                
                if (proxy.Points.Length > 1)
                    Debug.Log($"Second point: {proxy.Points[1]}, Color: {proxy.Colors[1]}");
                
                if (proxy.Points.Length > 10)
                    Debug.Log($"10th point: {proxy.Points[9]}, Color: {proxy.Colors[9]}");
                
                Debug.Log($"Last point: {proxy.Points[proxy.Points.Length - 1]}, Color: {proxy.Colors[proxy.Points.Length - 1]}");
            }
        }
    }
}