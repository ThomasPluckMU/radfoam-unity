using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Ply
{
    [InitializeOnLoad]
    public static class PlySceneViewRenderer
    {
        private static List<PlySceneProxy> activeProxies = new List<PlySceneProxy>();
        private static PlySceneProxy currentHoverProxy = null;
        private static int hoveredPointIndex = -1;
        private static bool isSelectingRect = false;
        private static Vector2 rectStart;
        private static Rect selectionRect;

        static PlySceneViewRenderer()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.hierarchyChanged += RefreshProxyRegistry;
            RefreshProxyRegistry();
        }

        public static void RegisterProxy(PlySceneProxy proxy)
        {
            if (proxy != null && !activeProxies.Contains(proxy))
            {
                activeProxies.Add(proxy);
            }
        }

        public static void UnregisterProxy(PlySceneProxy proxy)
        {
            activeProxies.Remove(proxy);
        }

        public static void RefreshProxyRegistry()
        {
            activeProxies.RemoveAll(p => p == null);
            // Replace deprecated FindObjectsOfType with FindObjectsByType
            PlySceneProxy[] sceneProxies = Object.FindObjectsByType<PlySceneProxy>(FindObjectsSortMode.None);
            foreach (var proxy in sceneProxies)
            {
                RegisterProxy(proxy);
            }
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            Event e = Event.current;

            HandleSelectionInput(e, sceneView);
            HandlePointHover(e, sceneView);
            DrawGLBillboards(sceneView);
            DrawBoundingBoxes();
            OnGUI(sceneView);

            if (isSelectingRect)
            {
                sceneView.Repaint();
            }
        }

        private static void DrawBoundingBoxes()
        {
            // Bounding boxes are now drawn by the BoundingBoxHandle component
            // This method is kept for compatibility but no longer needs to do anything
        }

        private static void HandleSelectionInput(Event e, SceneView sceneView)
        {
            // Handle keyboard shortcuts
            if (e.type == EventType.KeyDown)
            {
                // Delete key to remove selected points
                if (e.keyCode == KeyCode.Delete)
                {
                    // Check all active proxies for selected points
                    foreach (var proxy in activeProxies)
                    {
                        if (proxy != null && proxy.SelectedPoints.Count > 0)
                        {
                            Undo.RecordObject(proxy, "Delete Points");
                            proxy.HideSelectedPoints();
                            EditorUtility.SetDirty(proxy);
                            e.Use();
                            return;
                        }
                    }
                }
                // Ctrl+Z for undo
                else if (e.keyCode == KeyCode.Z && e.control)
                {
                    Undo.PerformUndo();
                    e.Use();
                    return;
                }
            }

            // Single click deselects all points
            if (e.type == EventType.MouseDown && e.button == 0 && !e.shift)
            {
                // Find the target proxy
                PlySceneProxy targetProxy = GetActiveSceneProxy();
                if (targetProxy != null && targetProxy.SelectedPoints.Count > 0)
                {
                    Undo.RecordObject(targetProxy, "Deselect All");
                    targetProxy.ClearSelection();
                    EditorUtility.SetDirty(targetProxy);
                    e.Use();
                }
            }

            // Rectangle selection only with shift+click+drag
            if (e.type == EventType.MouseDown && e.button == 0 && e.shift)
            {
                isSelectingRect = true;
                rectStart = e.mousePosition;
                e.Use();
            }
            else if (isSelectingRect && e.type == EventType.MouseDrag)
            {
                selectionRect = new Rect(
                    Mathf.Min(rectStart.x, e.mousePosition.x),
                    Mathf.Min(rectStart.y, e.mousePosition.y),
                    Mathf.Abs(e.mousePosition.x - rectStart.x),
                    Mathf.Abs(e.mousePosition.y - rectStart.y)
                );
                e.Use();
            }
            else if (isSelectingRect && e.type == EventType.MouseUp)
            {
                isSelectingRect = false;
                SelectPointsInRect(sceneView.camera, selectionRect);
                e.Use();
            }
        }

        // Helper method to find the current target proxy
        private static PlySceneProxy GetActiveSceneProxy()
        {
            // First priority: selected GameObject
            if (Selection.activeGameObject != null)
            {
                PlySceneProxy selectedProxy = Selection.activeGameObject.GetComponent<PlySceneProxy>();
                if (selectedProxy != null)
                    return selectedProxy;
            }
            
            // Second priority: any proxy with persistInScene=true
            foreach (var proxy in activeProxies)
            {
                if (proxy != null && proxy.persistInScene)
                    return proxy;
            }
            
            return null;
        }

        private static void FindNearestPointUnderCursor(Ray ray, out PlySceneProxy hitProxy, out int hitPointIndex)
        {
            hitProxy = null;
            hitPointIndex = -1;
            float closestDistSqr = float.MaxValue;

            foreach (var proxy in activeProxies)
            {
                if (proxy == null || proxy.Points == null) continue;

                Vector3[] points = proxy.Points;
                float pointSizeSqr = proxy.VisualScale * proxy.VisualScale * 4;

                for (int i = 0; i < points.Length; i++)
                {
                    if (proxy.IsPointHidden(i)) continue;

                    Vector3 point = points[i];
                    float distSqr = HandleUtility.DistancePointLine(point, ray.origin, ray.origin + ray.direction * 100);

                    if (distSqr < pointSizeSqr && distSqr < closestDistSqr)
                    {
                        closestDistSqr = distSqr;
                        hitProxy = proxy;
                        hitPointIndex = i;
                    }
                }
            }
        }

        private static void HandlePointHover(Event e, SceneView sceneView)
        {
            if (e.type == EventType.MouseMove)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                FindNearestPointUnderCursor(ray, out PlySceneProxy hitProxy, out int hitPointIndex);

                currentHoverProxy = hitProxy;
                hoveredPointIndex = hitPointIndex;
                
                if (hitPointIndex >= 0)
                {
                    // Force repaint when hovering over a point
                    sceneView.Repaint();
                }
            }
        }

        private static void SelectPointsInRect(Camera camera, Rect rect)
        {
            // Find the active proxy using our helper method
            PlySceneProxy targetProxy = GetActiveSceneProxy();
            
            // If no eligible proxy found, exit
            if (targetProxy == null || targetProxy.Points == null)
                return;
            
            // Collect points that fall within the selection rectangle
            HashSet<int> pointsToSelect = new HashSet<int>();
            Vector3[] points = targetProxy.Points;
            
            for (int i = 0; i < points.Length; i++)
            {
                if (targetProxy.IsPointHidden(i)) continue;
                
                Vector3 screenPoint = camera.WorldToScreenPoint(points[i]);
                screenPoint.y = camera.pixelHeight - screenPoint.y;
                
                if (rect.Contains(screenPoint))
                {
                    pointsToSelect.Add(i);
                }
            }
            
            // Apply the selection
            if (pointsToSelect.Count > 0)
            {
                Undo.RecordObject(targetProxy, "Select Points");
                
                // Add to current selection (don't clear first)
                foreach (int index in pointsToSelect)
                {
                    targetProxy.SelectPoint(index);
                }
                
                EditorUtility.SetDirty(targetProxy);
            }
        }

        private static void DrawGLBillboards(SceneView sceneView)
        {
            foreach (var proxy in activeProxies)
            {
                if (proxy == null || proxy.Points == null) continue;
                if (!proxy.persistInScene && Selection.activeGameObject != proxy.gameObject) continue;

                Vector3[] points = proxy.Points;
                Color[] colors = proxy.Colors;
                float pointSize = proxy.VisualScale;

                Camera cam = sceneView.camera;
                Vector3 camRight = cam.transform.right;
                Vector3 camUp = cam.transform.up;
                Vector3 camPos = cam.transform.position;

                // Create a list to hold points with their distances
                List<PointWithDistance> sortedPoints = new List<PointWithDistance>();
                
                // Gather all visible points with distances
                for (int i = 0; i < points.Length; i++)
                {
                    if (proxy.IsPointHidden(i)) continue;
                    
                    float distanceToCamera = Vector3.Distance(points[i], camPos);
                    sortedPoints.Add(new PointWithDistance(i, distanceToCamera));
                }
                
                // Sort by distance (furthest first)
                sortedPoints.Sort((a, b) => b.Distance.CompareTo(a.Distance));

                Material material = new Material(Shader.Find("GUI/Text Shader"));
                material.SetPass(0);

                GL.PushMatrix();
                GL.Begin(GL.QUADS);

                // Define a nice purple selection color
                Color selectionColor = new Color(0.6f, 0.4f, 0.8f, 1f); // Purple hue

                // Draw points in sorted order
                foreach (var pointData in sortedPoints)
                {
                    int i = pointData.Index;
                    Vector3 pos = points[i];
                    Color col = colors[i];
                    col.a = 1f;
                    float currentPointSize = pointSize;

                    if (proxy.SelectedPoints.Contains(i))
                    {
                        // Use the purple selection color
                        col = Color.Lerp(col, selectionColor, 0.6f);
                        currentPointSize *= 1.2f;
                    }
                    else if (proxy == currentHoverProxy && i == hoveredPointIndex)
                    {
                        col = Color.Lerp(col, Color.yellow, 0.3f);
                    }

                    GL.Color(col);
                    Vector3 right = camRight * currentPointSize;
                    Vector3 up = camUp * currentPointSize;

                    GL.Vertex(pos - right + up);
                    GL.Vertex(pos + right + up);
                    GL.Vertex(pos + right - up);
                    GL.Vertex(pos - right - up);
                }

                GL.End();
                GL.PopMatrix();
            }
        }

        // Helper class to store point index and distance
        private class PointWithDistance
        {
            public int Index { get; private set; }
            public float Distance { get; private set; }
            
            public PointWithDistance(int index, float distance)
            {
                Index = index;
                Distance = distance;
            }
        }

        private static void OnGUI(SceneView sceneView)
        {
            if (isSelectingRect)
            {
                Handles.BeginGUI();
                Color oldColor = GUI.color;
                GUI.color = new Color(0.8f, 0.8f, 1f, 0.3f);
                GUI.Box(selectionRect, "");
                GUI.color = new Color(0.8f, 0.8f, 1f, 1f);
                GUI.Box(selectionRect, "", "box");
                GUI.color = oldColor;
                Handles.EndGUI();
            }
        }
    }
}