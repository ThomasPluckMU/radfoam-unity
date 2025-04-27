using UnityEngine;
using System.Collections.Generic;

namespace Ply
{
    // This component acts as a proxy for PLY data in the scene
    // It references the immutable PLY data asset but allows for scene-level editing
    [ExecuteInEditMode]
    public class PlySceneProxy : MonoBehaviour
    {
        // Reference to the immutable PLY data asset
        [SerializeField] private PlyData sourceData;
        
        // Transformation controls
        [SerializeField] private Vector3 localOffset = Vector3.zero;
        [SerializeField] private Vector3 localScale = Vector3.one;
        [SerializeField] private Vector3 rotationAngles = Vector3.zero;
        
        // Filtering options
        [SerializeField, Range(0, 1)] private float densityThreshold = 0.0f;
        [SerializeField, Range(0, 1)] private float visualScale = 1.0f;
        [SerializeField] private int maxPointsToRender = 10000;
        
        // Region selection (for partial visualization)
        [SerializeField] private Bounds regionBounds = new Bounds(Vector3.zero, Vector3.one);
        [SerializeField] private bool useRegionBounds = false;
        
        // Display options
        [SerializeField] private Color defaultColor = Color.white;
        [SerializeField] private VisualizationMode visualizationMode = VisualizationMode.SourceColor;
        
        // Runtime/cached data - not serialized
        private Vector3[] points;
        private Color[] colors;
        private Bounds dataBounds;
        private bool isDirty = true;
        
        // Visualization modes
        public enum VisualizationMode
        {
            SourceColor,
            Density,
            Height,
            Distance,
            Custom
        }
        
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
        
        public Vector3 LocalOffset
        {
            get => localOffset;
            set
            {
                localOffset = value;
                isDirty = true;
            }
        }
        
        public Vector3 LocalScale
        {
            get => localScale;
            set
            {
                localScale = value;
                isDirty = true;
            }
        }
        
        public Vector3 RotationAngles
        {
            get => rotationAngles;
            set
            {
                rotationAngles = value;
                isDirty = true;
            }
        }
        
        public float DensityThreshold
        {
            get => densityThreshold;
            set
            {
                densityThreshold = Mathf.Clamp01(value);
                isDirty = true;
            }
        }
        
        public Bounds RegionBounds
        {
            get => regionBounds;
            set
            {
                regionBounds = value;
                isDirty = true;
            }
        }
        
        public bool UseRegionBounds
        {
            get => useRegionBounds;
            set
            {
                useRegionBounds = value;
                isDirty = true;
            }
        }
        
        public VisualizationMode CurrentVisualizationMode
        {
            get => visualizationMode;
            set
            {
                visualizationMode = value;
                isDirty = true;
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
        
        // Public access to visualization data
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
        
        private void OnEnable()
        {
            isDirty = true;
        }
        
        private void OnValidate()
        {
            isDirty = true;
        }
        
        // Refreshes the visualization data based on the source PLY data and current settings
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
                // Load the PLY model using a using statement for proper disposal
                using (Model model = sourceData.Load())
                {
                    LoadVisualizationData(model);
                }
                
                isDirty = false;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error refreshing PLY data: {e.Message}");
                points = null;
                colors = null;
            }
        }
        
        // Forces a refresh of the visualization data
        public void ForceRefresh()
        {
            isDirty = true;
            RefreshData();
        }
        
        // Loads visualization data from the model
        private void LoadVisualizationData(Model model)
        {
            try
            {
                // Get vertex data
                ElementView vertexView = model.element_view("vertex");
                
                // Create downsampled arrays
                int totalVertices = vertexView.count;
                int pointCount = Mathf.Min(totalVertices, maxPointsToRender);
                
                List<Vector3> pointsList = new List<Vector3>(pointCount);
                List<Color> colorsList = new List<Color>(pointCount);
                
                // Get property views for position
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
                }
                catch (System.ArgumentException)
                {
                    hasColorData = false;
                    redView = vertexView.dummy_property_view();
                    greenView = vertexView.dummy_property_view();
                    blueView = vertexView.dummy_property_view();
                }
                
                // Try to get density property
                bool hasDensityData = true;
                PropertyView densityView;
                
                try
                {
                    densityView = vertexView.property_view("density");
                }
                catch (System.ArgumentException)
                {
                    hasDensityData = false;
                    densityView = vertexView.dummy_property_view();
                }
                
                // Step size for downsampling
                int step = Mathf.Max(1, totalVertices / pointCount);
                
                // Calculate the transformation matrix
                Matrix4x4 transformMatrix = Matrix4x4.TRS(
                    localOffset,
                    Quaternion.Euler(rotationAngles),
                    localScale
                );
                
                // Process vertex data
                for (int i = 0, srcIdx = 0; i < pointCount && srcIdx < totalVertices; i++, srcIdx += step)
                {
                    // Get position
                    Vector3 position = new Vector3(
                        xView.Get<float>(srcIdx),
                        yView.Get<float>(srcIdx),
                        zView.Get<float>(srcIdx)
                    );
                    
                    // Apply the y-axis flip that the original code uses
                    position.y = -position.y;
                    
                    // Get density if available
                    float density = hasDensityData ? densityView.Get<float>(srcIdx) : 0f;
                    
                    // Apply density threshold filtering
                    if (hasDensityData && densityThreshold > 0 && density < densityThreshold)
                    {
                        continue; // Skip this point
                    }
                    
                    // Apply region bounds filtering
                    if (useRegionBounds && !regionBounds.Contains(position))
                    {
                        continue; // Skip this point
                    }
                    
                    // Apply transformation
                    Vector3 transformedPosition = transformMatrix.MultiplyPoint3x4(position);
                    
                    // Determine color based on visualization mode
                    Color color = defaultColor;
                    
                    switch (visualizationMode)
                    {
                        case VisualizationMode.SourceColor:
                            if (hasColorData)
                            {
                                color = new Color(
                                    redView.Get<byte>(srcIdx) / 255f,
                                    greenView.Get<byte>(srcIdx) / 255f,
                                    blueView.Get<byte>(srcIdx) / 255f
                                );
                            }
                            else
                            {
                                // Create a position-based gradient if no color data
                                color = new Color(
                                    Mathf.Abs(position.x) / 10f % 1f,
                                    Mathf.Abs(position.y) / 10f % 1f,
                                    Mathf.Abs(position.z) / 10f % 1f
                                );
                            }
                            break;
                            
                        case VisualizationMode.Density:
                            if (hasDensityData)
                            {
                                float normalizedDensity = Mathf.Clamp01(density / 10f);
                                color = new Color(normalizedDensity, normalizedDensity, normalizedDensity);
                            }
                            else
                            {
                                // Use distance from center as alternative
                                float distance = Vector3.Distance(position, Vector3.zero);
                                float normalizedDistance = Mathf.Clamp01(distance / 10f);
                                color = new Color(normalizedDistance, normalizedDistance, normalizedDistance);
                            }
                            break;
                            
                        case VisualizationMode.Height:
                            // Color based on Y position (height)
                            float height = position.y;
                            float normalizedHeight = Mathf.Clamp01((height + 5f) / 10f); // Adjust range as needed
                            color = Color.HSVToRGB(normalizedHeight, 0.7f, 0.9f);
                            break;
                            
                        case VisualizationMode.Distance:
                            // Color based on distance from center
                            float dist = Vector3.Distance(position, Vector3.zero);
                            float normalizedDist = Mathf.Clamp01(dist / 10f);
                            color = Color.Lerp(Color.blue, Color.red, normalizedDist);
                            break;
                            
                        case VisualizationMode.Custom:
                            // Use the default color (set via inspector)
                            break;
                    }
                    
                    // Add to lists
                    pointsList.Add(transformedPosition);
                    colorsList.Add(color);
                }
                
                // Convert lists to arrays
                points = pointsList.ToArray();
                colors = colorsList.ToArray();
                
                // Calculate bounds of the transformed data
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
                Debug.LogError($"Error in LoadVisualizationData: {e.Message}");
                throw;
            }
        }
    }
}