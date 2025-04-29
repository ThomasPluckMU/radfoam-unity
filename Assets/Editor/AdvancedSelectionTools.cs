using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;

namespace Ply
{
    [CustomEditor(typeof(PlySceneProxy))]
    public class AdvancedSelectionTools : Editor
    {
        private PlySceneProxy proxy;
        private SpatialIndex spatialIndex;
        
        // Selection parameters
        private float sphereRadius = 1.0f;
        private float colorThreshold = 0.1f;
        private Color targetColor = Color.white;
        private int minNeighbors = 5;
        private float densityRadius = 0.5f;
        
        // UI states
        private bool showAdvancedTools = false;
        private bool showSphereSelection = false;
        private bool showBoxSelection = false;
        private bool showPlaneSelection = false;
        private bool showColorSelection = false;
        private bool showDensitySelection = false;
        private bool showViewSelection = false;
        
        // References for the original editor
        private Editor defaultEditor;
        
        private void OnEnable()
        {
            proxy = (PlySceneProxy)target;
            
            // Create the default editor
            defaultEditor = CreateEditor(target, typeof(PlySceneProxyEditor));
            
            // Create or find spatial index
            if (spatialIndex == null)
            {
                spatialIndex = proxy.gameObject.GetComponent<SpatialIndex>();
                if (spatialIndex == null)
                {
                    spatialIndex = proxy.gameObject.AddComponent<SpatialIndex>();
                }
                spatialIndex.Initialize(proxy);
            }
            
            // Register scene view callback for custom tools
            SceneView.duringSceneGui += OnSceneGUI;
        }
        
        private void OnDisable()
        {
            // Clean up
            SceneView.duringSceneGui -= OnSceneGUI;
            DestroyImmediate(defaultEditor);
        }
        
        public override void OnInspectorGUI()
        {
            // Draw the default inspector
            defaultEditor.OnInspectorGUI();
            
            // Advanced selection tools section
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Advanced Selection Tools", EditorStyles.boldLabel);
            
            showAdvancedTools = EditorGUILayout.Foldout(showAdvancedTools, "Selection Tools", true);
            if (showAdvancedTools)
            {
                EditorGUI.indentLevel++;
                
                // Sphere selection
                showSphereSelection = EditorGUILayout.Foldout(showSphereSelection, "Sphere Selection", true);
                if (showSphereSelection)
                {
                    EditorGUI.indentLevel++;
                    sphereRadius = EditorGUILayout.Slider("Radius", sphereRadius, 0.1f, 10.0f);
                    if (GUILayout.Button("Select Sphere at Scene Camera"))
                    {
                        SelectSphereAtSceneCamera();
                    }
                    EditorGUI.indentLevel--;
                }
                
                // Box selection
                showBoxSelection = EditorGUILayout.Foldout(showBoxSelection, "Box Selection", true);
                if (showBoxSelection)
                {
                    EditorGUI.indentLevel++;
                    if (GUILayout.Button("Select Box Around Selected Points"))
                    {
                        SelectBoxAroundSelection();
                    }
                    EditorGUI.indentLevel--;
                }
                
                // Plane selection
                showPlaneSelection = EditorGUILayout.Foldout(showPlaneSelection, "Plane Selection", true);
                if (showPlaneSelection)
                {
                    EditorGUI.indentLevel++;
                    if (GUILayout.Button("Select Points Above Scene View Plane"))
                    {
                        SelectPointsAbovePlane();
                    }
                    if (GUILayout.Button("Select Points Below Scene View Plane"))
                    {
                        SelectPointsBelowPlane();
                    }
                    EditorGUI.indentLevel--;
                }
                
                // Color-based selection
                showColorSelection = EditorGUILayout.Foldout(showColorSelection, "Color Selection", true);
                if (showColorSelection)
                {
                    EditorGUI.indentLevel++;
                    targetColor = EditorGUILayout.ColorField("Target Color", targetColor);
                    colorThreshold = EditorGUILayout.Slider("Threshold", colorThreshold, 0.01f, 1.0f);
                    if (GUILayout.Button("Select Points by Color"))
                    {
                        SelectPointsByColor();
                    }
                    if (GUILayout.Button("Sample Color from Selected Point"))
                    {
                        SampleColorFromSelection();
                    }
                    EditorGUI.indentLevel--;
                }
                
                // Density-based selection
                showDensitySelection = EditorGUILayout.Foldout(showDensitySelection, "Density Selection", true);
                if (showDensitySelection)
                {
                    EditorGUI.indentLevel++;
                    densityRadius = EditorGUILayout.Slider("Radius", densityRadius, 0.1f, 5.0f);
                    minNeighbors = EditorGUILayout.IntSlider("Min Neighbors", minNeighbors, 1, 50);
                    if (GUILayout.Button("Select Sparse Points (Noise)"))
                    {
                        SelectSparsePoints();
                    }
                    EditorGUI.indentLevel--;
                }
                
                // View-based selection
                showViewSelection = EditorGUILayout.Foldout(showViewSelection, "View Selection", true);
                if (showViewSelection)
                {
                    EditorGUI.indentLevel++;
                    if (GUILayout.Button("Select All in Current View"))
                    {
                        SelectAllInView();
                    }
                    if (GUILayout.Button("Invert Selection"))
                    {
                        InvertSelection();
                    }
                    EditorGUI.indentLevel--;
                }
                
                EditorGUI.indentLevel--;
            }
            
            EditorGUILayout.Space(5);
            if (GUILayout.Button("Rebuild Spatial Index"))
            {
                if (spatialIndex != null)
                {
                    spatialIndex.RebuildIndex();
                }
            }
        }
        
        private void OnSceneGUI(SceneView sceneView)
        {
            if (proxy == null || proxy.Points == null) return;
            
            // Draw handles for the active selection tools
            
            // Sphere selection visualization
            if (showSphereSelection)
            {
                Vector3 cameraPos = sceneView.camera.transform.position;
                Handles.color = new Color(0.3f, 0.6f, 0.9f, 0.4f);
                Handles.SphereHandleCap(0, cameraPos, Quaternion.identity, sphereRadius * 2, EventType.Repaint);
                Handles.color = new Color(0.3f, 0.6f, 0.9f, 0.8f);
                Handles.DrawWireDisc(cameraPos, sceneView.camera.transform.forward, sphereRadius);
            }
            
            // Plane selection visualization
            if (showPlaneSelection)
            {
                Camera camera = sceneView.camera;
                Vector3 cameraPos = camera.transform.position;
                Vector3 cameraForward = camera.transform.forward;
                
                // Create a plane at the camera position, facing forward
                Plane plane = new Plane(cameraForward, cameraPos);
                
                // Create a grid to visualize the plane
                float gridSize = 10.0f;
                Vector3 planeCenter = cameraPos;
                Vector3 planeRight = camera.transform.right;
                Vector3 planeUp = camera.transform.up;
                
                Vector3[] corners = new Vector3[4];
                corners[0] = planeCenter - planeRight * gridSize - planeUp * gridSize;
                corners[1] = planeCenter + planeRight * gridSize - planeUp * gridSize;
                corners[2] = planeCenter + planeRight * gridSize + planeUp * gridSize;
                corners[3] = planeCenter - planeRight * gridSize + planeUp * gridSize;
                
                Handles.color = new Color(0.8f, 0.3f, 0.3f, 0.4f);
                Handles.DrawSolidRectangleWithOutline(corners, new Color(0.8f, 0.3f, 0.3f, 0.2f), Color.red);
            }
        }
        
        // Implement selection methods
        
        private void SelectSphereAtSceneCamera()
        {
            Camera camera = SceneView.lastActiveSceneView.camera;
            if (camera == null) return;
            
            Vector3 center = camera.transform.position;
            SelectSphere(center, sphereRadius);
        }
        
        private void SelectSphere(Vector3 center, float radius)
        {
            if (proxy == null || proxy.Points == null) return;
            
            // Use the spatial index for efficient sphere queries
            List<int> pointsInSphere = spatialIndex.QuerySphere(center, radius);
            
            if (pointsInSphere.Count > 0)
            {
                Undo.RecordObject(proxy, "Sphere Selection");
                
                foreach (int index in pointsInSphere)
                {
                    proxy.SelectPoint(index);
                }
                
                EditorUtility.SetDirty(proxy);
                SceneView.RepaintAll();
            }
        }
        
        private void SelectBoxAroundSelection()
        {
            if (proxy == null || proxy.Points == null || proxy.SelectedPoints.Count == 0) return;
            
            // Calculate bounds of selected points
            Bounds bounds = CalculateBoundsOfSelection();
            
            // Use spatial index for box query
            List<int> pointsInBox = spatialIndex.QueryBox(bounds);
            
            if (pointsInBox.Count > 0)
            {
                Undo.RecordObject(proxy, "Box Selection");
                
                foreach (int index in pointsInBox)
                {
                    proxy.SelectPoint(index);
                }
                
                EditorUtility.SetDirty(proxy);
                SceneView.RepaintAll();
            }
        }
        
        private Bounds CalculateBoundsOfSelection()
        {
            Vector3[] points = proxy.Points;
            HashSet<int> selectedPoints = proxy.SelectedPoints;
            
            if (selectedPoints.Count == 0) return new Bounds(Vector3.zero, Vector3.zero);
            
            // Initialize with first selected point
            Vector3 firstPoint = Vector3.zero;
            bool firstPointFound = false;
            
            foreach (int index in selectedPoints)
            {
                if (index >= 0 && index < points.Length)
                {
                    firstPoint = points[index];
                    firstPointFound = true;
                    break;
                }
            }
            
            if (!firstPointFound) return new Bounds(Vector3.zero, Vector3.zero);
            
            Bounds bounds = new Bounds(firstPoint, Vector3.zero);
            
            // Expand to include all selected points
            foreach (int index in selectedPoints)
            {
                if (index >= 0 && index < points.Length)
                {
                    bounds.Encapsulate(points[index]);
                }
            }
            
            return bounds;
        }
        
        private void SelectPointsAbovePlane()
        {
            SelectPointsByPlane(true);
        }
        
        private void SelectPointsBelowPlane()
        {
            SelectPointsByPlane(false);
        }
        
        private void SelectPointsByPlane(bool abovePlane)
        {
            if (proxy == null || proxy.Points == null) return;
            
            Camera camera = SceneView.lastActiveSceneView.camera;
            if (camera == null) return;
            
            // Create a plane at the camera position, facing forward
            Vector3 cameraPos = camera.transform.position;
            Vector3 cameraForward = camera.transform.forward;
            Plane plane = new Plane(cameraForward, cameraPos);
            
            // Use parallel job for plane selection
            Vector3[] points = proxy.Points;
            NativeArray<bool> results = new NativeArray<bool>(points.Length, Allocator.TempJob);
            
            JobHandle jobHandle = PointSelectionJobs.SchedulePlaneSelection(
                points, plane, abovePlane, ref results);
            
            jobHandle.Complete();
            
            // Process results
            Undo.RecordObject(proxy, "Plane Selection");
            
            for (int i = 0; i < points.Length; i++)
            {
                if (results[i] && !proxy.IsPointHidden(i))
                {
                    proxy.SelectPoint(i);
                }
            }
            
            // Cleanup
            results.Dispose();
            
            EditorUtility.SetDirty(proxy);
            SceneView.RepaintAll();
        }
        
        private void SelectPointsByColor()
        {
            if (proxy == null || proxy.Points == null || proxy.Colors == null) return;
            
            Vector3[] points = proxy.Points;
            Color[] colors = proxy.Colors;
            
            // Use parallel job for color selection
            NativeArray<bool> results = new NativeArray<bool>(points.Length, Allocator.TempJob);
            
            JobHandle jobHandle = PointSelectionJobs.ScheduleColorSelection(
                points, colors, targetColor, colorThreshold, ref results);
            
            jobHandle.Complete();
            
            // Process results
            Undo.RecordObject(proxy, "Color Selection");
            
            for (int i = 0; i < points.Length; i++)
            {
                if (results[i] && !proxy.IsPointHidden(i))
                {
                    proxy.SelectPoint(i);
                }
            }
            
            // Cleanup
            results.Dispose();
            
            EditorUtility.SetDirty(proxy);
            SceneView.RepaintAll();
        }
        
        private void SampleColorFromSelection()
        {
            if (proxy == null || proxy.SelectedPoints.Count == 0 || proxy.Colors == null) return;
            
            // Get first selected point's color
            int selectedIndex = -1;
            foreach (int index in proxy.SelectedPoints)
            {
                selectedIndex = index;
                break;
            }
            
            if (selectedIndex >= 0 && selectedIndex < proxy.Colors.Length)
            {
                targetColor = proxy.Colors[selectedIndex];
                Repaint(); // Update the inspector UI
            }
        }
        
        private void SelectSparsePoints()
        {
            if (proxy == null || proxy.Points == null) return;
            
            // Query points with low density
            List<int> sparsePoints = spatialIndex.QueryDensity(densityRadius, minNeighbors);
            
            if (sparsePoints.Count > 0)
            {
                Undo.RecordObject(proxy, "Select Sparse Points");
                
                foreach (int index in sparsePoints)
                {
                    proxy.SelectPoint(index);
                }
                
                EditorUtility.SetDirty(proxy);
                SceneView.RepaintAll();
            }
        }
        
        private void SelectAllInView()
        {
            if (proxy == null || proxy.Points == null) return;
            
            Camera camera = SceneView.lastActiveSceneView.camera;
            if (camera == null) return;
            
            // Get camera frustum planes
            Plane[] frustumPlanes = GeometryUtility.CalculateFrustumPlanes(camera);
            
            // Use spatial index for frustum query
            List<int> pointsInView = spatialIndex.QueryFrustum(frustumPlanes);
            
            if (pointsInView.Count > 0)
            {
                Undo.RecordObject(proxy, "View Selection");
                
                foreach (int index in pointsInView)
                {
                    if (!proxy.IsPointHidden(index))
                    {
                        proxy.SelectPoint(index);
                    }
                }
                
                EditorUtility.SetDirty(proxy);
                SceneView.RepaintAll();
            }
        }
        
        private void InvertSelection()
        {
            if (proxy == null || proxy.Points == null) return;
            
            Undo.RecordObject(proxy, "Invert Selection");
            
            HashSet<int> newSelection = new HashSet<int>();
            HashSet<int> currentSelection = proxy.SelectedPoints;
            
            for (int i = 0; i < proxy.Points.Length; i++)
            {
                if (!proxy.IsPointHidden(i) && !currentSelection.Contains(i))
                {
                    newSelection.Add(i);
                }
            }
            
            // Clear current selection
            proxy.ClearSelection();
            
            // Add new selection
            foreach (int index in newSelection)
            {
                proxy.SelectPoint(index);
            }
            
            EditorUtility.SetDirty(proxy);
            SceneView.RepaintAll();
        }
    }
}