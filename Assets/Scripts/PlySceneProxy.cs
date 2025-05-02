using UnityEngine;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Ply
{
    [ExecuteInEditMode]
    public class PlySceneProxy : MonoBehaviour
    {
        // Core settings
        [SerializeField] private PlyData sourceData;
        [SerializeField] private float visualScale = 0.01f;
        [SerializeField] public bool persistInScene = false;
        [SerializeField] private int maxPointsToRender = 10000;
        
        // Selection data
        [SerializeField] private List<int> selectedPointIndices = new List<int>();
        private HashSet<int> selectedPointsSet = new HashSet<int>();
        
        // Hidden points data (non-destructive filtering)
        [SerializeField] private List<int> hiddenPointIndices = new List<int>();
        private HashSet<int> hiddenPointsSet = new HashSet<int>();
        
        // Bounding box filter
        [SerializeField] private bool useBoundingBoxFilter = false;
        [SerializeField] private Vector3 boundingBoxCenter = Vector3.zero;
        [SerializeField] private Vector3 boundingBoxSize = new Vector3(10, 10, 10);
        [SerializeField] private Quaternion boundingBoxRotation = Quaternion.identity;
        [SerializeField] private GameObject boundingBoxHandle;
        private Bounds localBoundingBox = new Bounds(Vector3.zero, new Vector3(10, 10, 10));
        
        // Runtime data
        private Vector3[] points;
        private Color[] colors;
        private Bounds dataBounds;
        private bool isDirty = true;
        
        // Public properties
        public PlyData SourceData
        {
            get => sourceData;
            set
            {
                if (sourceData != value)
                {
                    sourceData = value;
                    isDirty = true;
                }
            }
        }
        
        public float VisualScale
        {
            get => visualScale;
            set
            {
                visualScale = Mathf.Clamp01(value);
                isDirty = true;
            }
        }
        
        public int MaxPointsToRender
        {
            get => maxPointsToRender;
            set
            {
                maxPointsToRender = Mathf.Max(100, value);
                isDirty = true;
            }
        }

        public bool PersistInScene
        {
            get => persistInScene;
            set => persistInScene = value;
        }
        
        // Bounding box properties
        public bool UseBoundingBoxFilter
        {
            get => useBoundingBoxFilter;
            set 
            { 
                useBoundingBoxFilter = value;
                UpdateBoundingBox();
                isDirty = true;
            }
        }
        
        public Vector3 BoundingBoxCenter
        {
            get => boundingBoxCenter;
            set
            {
                boundingBoxCenter = value;
                UpdateBoundingBox();
                isDirty = true;
            }
        }
        
        public Vector3 BoundingBoxSize
        {
            get => boundingBoxSize;
            set
            {
                boundingBoxSize = value;
                UpdateBoundingBox();
                isDirty = true;
            }
        }
        
        public Quaternion BoundingBoxRotation
        {
            get => boundingBoxRotation;
            set
            {
                boundingBoxRotation = value;
                UpdateBoundingBox();
                isDirty = true;
            }
        }
        
        public Bounds BoundingBox => new Bounds(boundingBoxCenter, boundingBoxSize);
        public GameObject BoundingBoxHandle => boundingBoxHandle;
        
        // Data access properties
        public Vector3[] Points
        {
            get
            {
                if (isDirty) RefreshData();
                return points;
            }
        }
        
        public Color[] Colors
        {
            get
            {
                if (isDirty) RefreshData();
                return colors;
            }
        }
        
        public Bounds DataBounds
        {
            get
            {
                if (isDirty) RefreshData();
                return dataBounds;
            }
        }
        
        public HashSet<int> SelectedPoints => selectedPointsSet;
        public HashSet<int> HiddenPoints => hiddenPointsSet;

        public bool IsPointHidden(int index)
        {
            if (index >= points.Length) return true;
            
            // First check if it's in the hidden set
            if (hiddenPointsSet.Contains(index)) return true;
            
            // Then check if it's outside the bounding box when filtering is enabled
            if (useBoundingBoxFilter)
            {
                Vector3 localPoint = Quaternion.Inverse(boundingBoxRotation) * (points[index] - boundingBoxCenter);
                if (!localBoundingBox.Contains(localPoint))
                    return true;
            }
                
            return false;
        }

        public void UpdateBoundingBox()
        {
            localBoundingBox = new Bounds(Vector3.zero, boundingBoxSize);
            
            // Create or update the handle object if needed
            #if UNITY_EDITOR
            if (useBoundingBoxFilter && boundingBoxHandle == null)
            {
                #if UNITY_EDITOR
                UnityEditor.EditorApplication.delayCall += () => {
                    if (this != null && useBoundingBoxFilter)
                        CreateBoundingBoxHandle();
                };
                #endif
            }
            else if (!useBoundingBoxFilter && boundingBoxHandle != null)
            {
                DestroyImmediate(boundingBoxHandle);
            }
            
            if (boundingBoxHandle != null)
            {
                boundingBoxHandle.transform.position = boundingBoxCenter;
                boundingBoxHandle.transform.rotation = boundingBoxRotation;
                boundingBoxHandle.transform.localScale = boundingBoxSize;
            }
            #endif
            
            isDirty = true;
        }
        
        #if UNITY_EDITOR
        private void CreateBoundingBoxHandle()
        {
            boundingBoxHandle = new GameObject("BoundingBoxHandle");
            boundingBoxHandle.transform.position = boundingBoxCenter;
            boundingBoxHandle.transform.rotation = boundingBoxRotation;
            boundingBoxHandle.transform.localScale = boundingBoxSize;
            boundingBoxHandle.AddComponent<BoundingBoxHandle>().parentProxy = this;
            boundingBoxHandle.hideFlags = HideFlags.DontSave;
            
            // Ensure handle doesn't persist in play mode
            if (UnityEditor.EditorApplication.isPlaying)
            {
                UnityEditor.EditorApplication.delayCall += () => {
                    DestroyImmediate(boundingBoxHandle);
                };
            }
        }
        #endif
        
        public void SyncHandleTransform()
        {
            #if UNITY_EDITOR
            if (boundingBoxHandle != null)
            {
                boundingBoxCenter = boundingBoxHandle.transform.position;
                boundingBoxRotation = boundingBoxHandle.transform.rotation;
                boundingBoxSize = boundingBoxHandle.transform.localScale;
                UpdateBoundingBox();
                isDirty = true;
                UnityEditor.EditorUtility.SetDirty(this);
            }
            #endif
        }

        public void HideSelectedPoints()
        {
            foreach (int index in selectedPointsSet)
            {
                if (!hiddenPointsSet.Contains(index))
                {
                    hiddenPointsSet.Add(index);
                    hiddenPointIndices.Add(index);
                }
            }
            ClearSelection();
            isDirty = true;
        }

        public void ShowAllPoints()
        {
            hiddenPointsSet.Clear();
            hiddenPointIndices.Clear();
            isDirty = true;
        }

        public Vector3[] GetVisiblePoints()
        {
            if (isDirty) RefreshData();
            if (points == null) return points;
            
            List<Vector3> visiblePoints = new List<Vector3>();
            
            for (int i = 0; i < points.Length; i++)
            {
                if (!IsPointHidden(i))
                {
                    visiblePoints.Add(points[i]);
                }
            }
            
            return visiblePoints.ToArray();
        }

        public void SelectPoint(int index)
        {
            if (index >= 0 && index < points.Length && !selectedPointsSet.Contains(index))
            {
                selectedPointsSet.Add(index);
                selectedPointIndices.Add(index);
            }
        }

        public void TogglePointSelection(int index)
        {
            if (selectedPointsSet.Contains(index))
                DeselectPoint(index);
            else
                SelectPoint(index);
        }

        public void DeselectPoint(int index)
        {
            selectedPointsSet.Remove(index);
            selectedPointIndices.Remove(index);
        }

        public void ClearSelection()
        {
            selectedPointsSet.Clear();
            selectedPointIndices.Clear();
        }

        private void OnEnable()
        {
            isDirty = true;
            UpdateBoundingBox();
            
            #if UNITY_EDITOR
            if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
            UnityEditor.EditorApplication.delayCall += () => {
                var rendererType = System.Type.GetType("Ply.PlySceneViewRenderer, Assembly-CSharp-Editor");
                if (rendererType != null) {
                    var registerMethod = rendererType.GetMethod("RegisterProxy", 
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    registerMethod?.Invoke(null, new object[] { this });
                }
            };
            #endif
        }

        private void OnDisable()
        {
            #if UNITY_EDITOR
            if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
            UnityEditor.EditorApplication.delayCall += () => {
                var rendererType = System.Type.GetType("Ply.PlySceneViewRenderer, Assembly-CSharp-Editor");
                if (rendererType != null) {
                    var registerMethod = rendererType.GetMethod("RegisterProxy", 
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    registerMethod?.Invoke(null, new object[] { this });
                }
            };
            #endif
        }

        private void OnValidate()
        {
            UpdateBoundingBox();
            isDirty = true;
        }

        // Draw the bounding box as a gizmo
        private void OnDrawGizmosSelected()
        {
            if (useBoundingBoxFilter)
            {
                // Store the original matrix
                Matrix4x4 originalMatrix = Gizmos.matrix;
                
                // Set the Gizmos matrix using the bounding box parameters
                Gizmos.matrix = Matrix4x4.TRS(boundingBoxCenter, boundingBoxRotation, Vector3.one);
                
                // Draw the solid cube with semi-transparent color
                Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.3f);
                Gizmos.DrawCube(Vector3.zero, boundingBoxSize);
                
                // Draw the wireframe cube with solid color
                Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 1.0f);
                Gizmos.DrawWireCube(Vector3.zero, boundingBoxSize);

                // Rotate back
                Gizmos.matrix = originalMatrix;
            }
        }

        public void RefreshData()
        {
            if (sourceData == null)
            {
                points = null;
                colors = null;
                return;
            }
            
            try
            {
                Debug.Log("Starting data refresh...");
                using (Model model = sourceData.Load())
                {
                    LoadVisualizationData(model);
                }
                
                isDirty = false;
                Debug.Log($"Data refresh complete. Loaded {(points != null ? points.Length : 0)} points.");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error refreshing PLY data: {e.Message}\n{e.StackTrace}");
                points = null;
                colors = null;
            }
        }

        public void ForceRefresh()
        {
            isDirty = true;
            RefreshData();
        }
        
        // Load visualization data from model
        private void LoadVisualizationData(Model model)
        {
            try
            {
                // Get vertex element
                ElementView vertexView = model.element_view("vertex");
                int totalVertices = vertexView.count;
                Debug.Log($"Found {totalVertices} vertices in PLY data");
                
                // Points to render (with limit)
                int pointCount = Mathf.Min(totalVertices, maxPointsToRender);
                int step = Mathf.Max(1, totalVertices / pointCount);
                
                // Collections for data
                List<Vector3> pointsList = new List<Vector3>(pointCount);
                List<Color> colorsList = new List<Color>(pointCount);
                
                // Position properties
                PropertyView xView = vertexView.property_view("x");
                PropertyView yView = vertexView.property_view("y");
                PropertyView zView = vertexView.property_view("z");
                
                // Try to get color properties
                bool hasColorData = true;
                PropertyView redView;
                PropertyView greenView;
                PropertyView blueView;
                
                try
                {
                    redView = vertexView.property_view("red");
                    greenView = vertexView.property_view("green");
                    blueView = vertexView.property_view("blue");
                    Debug.Log("Successfully found RGB color properties");
                }
                catch (System.ArgumentException e)
                {
                    hasColorData = false;
                    redView = vertexView.dummy_property_view();
                    greenView = vertexView.dummy_property_view();
                    blueView = vertexView.dummy_property_view();
                    Debug.LogWarning($"No color properties found: {e.Message}");
                }
                
                // Process vertices
                for (int srcIdx = 0; srcIdx < totalVertices; srcIdx += step)
                {
                    // Get position
                    Vector3 position = new Vector3(
                        xView.Get<float>(srcIdx),
                        yView.Get<float>(srcIdx),
                        zView.Get<float>(srcIdx)
                    );
                    
                    // Get color
                    Color color;
                    if (hasColorData)
                    {
                        try {
                            // Get raw color bytes (0-255)
                            byte r = redView.Get<byte>(srcIdx);
                            byte g = greenView.Get<byte>(srcIdx);
                            byte b = blueView.Get<byte>(srcIdx);
                            
                            // Create color (directly using Color32 which handles byte->float conversion)
                            color = new Color32(r, g, b, 255);
                        }
                        catch (System.Exception) {
                            color = Color.white; // Fallback
                        }
                    }
                    else
                    {
                        color = Color.white;
                    }
                    
                    // Add point and color to lists
                    pointsList.Add(position);
                    colorsList.Add(color);
                    
                    // Only collect a reasonable number of points for testing
                    if (pointsList.Count >= pointCount) break;
                }
                
                // Convert to arrays
                points = pointsList.ToArray();
                colors = colorsList.ToArray();
                
                // Calculate bounds
                if (points.Length > 0)
                {
                    dataBounds = new Bounds(points[0], Vector3.zero);
                    foreach (var pos in points)
                    {
                        dataBounds.Encapsulate(pos);
                    }
                }
                else
                {
                    dataBounds = new Bounds(Vector3.zero, Vector3.zero);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error in LoadVisualizationData: {e.Message}\n{e.StackTrace}");
                throw;
            }
        }
    }
}