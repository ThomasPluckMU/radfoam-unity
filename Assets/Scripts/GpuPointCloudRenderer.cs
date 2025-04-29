using UnityEngine;
using System.Collections.Generic;

namespace Ply
{
    public class GpuPointCloudRenderer : MonoBehaviour
    {
        // Core GPU buffers
        private ComputeBuffer positionBuffer;
        private ComputeBuffer colorBuffer;
        private ComputeBuffer visibilityBuffer; // 0 = hidden, 1 = visible
        private ComputeBuffer selectionBuffer;  // 0 = not selected, 1 = selected
        
        // Rendering materials and data
        private Material pointMaterial;
        private Mesh quadMesh;
        
        // Reference to the point cloud proxy
        [SerializeField] private PlySceneProxy pointCloudProxy;
        
        // Shader properties
        private static readonly int PositionsID = Shader.PropertyToID("_Positions");
        private static readonly int ColorsID = Shader.PropertyToID("_Colors");
        private static readonly int VisibilityID = Shader.PropertyToID("_Visibility");
        private static readonly int SelectionID = Shader.PropertyToID("_Selection");
        private static readonly int PointSizeID = Shader.PropertyToID("_PointSize");
        
        private int pointCount = 0;
        private bool isInitialized = false;
        
        private void OnEnable()
        {
            // Create the material if needed
            if (pointMaterial == null)
            {
                pointMaterial = new Material(Shader.Find("Ply/PointCloudShader"));
            }
            
            // Create a simple quad mesh for instancing
            if (quadMesh == null)
            {
                CreateQuadMesh();
            }
            
            // Initialize if we have proxy data
            if (pointCloudProxy != null && pointCloudProxy.Points != null)
            {
                InitializeBuffers();
            }
        }
        
        private void OnDisable()
        {
            ReleaseBuffers();
        }
        
        private void Update()
        {
            // Check if the proxy has been updated
            if (pointCloudProxy != null && pointCloudProxy.Points != null)
            {
                if (!isInitialized || pointCount != pointCloudProxy.Points.Length)
                {
                    InitializeBuffers();
                }
                else
                {
                    // Update visibility and selection based on proxy changes
                    UpdateBuffers();
                }
            }
        }
        
        private void CreateQuadMesh()
        {
            quadMesh = new Mesh();
            
            // Simple quad centered at origin
            Vector3[] vertices = new Vector3[4]
            {
                new Vector3(-0.5f, -0.5f, 0),
                new Vector3(0.5f, -0.5f, 0),
                new Vector3(-0.5f, 0.5f, 0),
                new Vector3(0.5f, 0.5f, 0)
            };
            
            int[] indices = new int[6] { 0, 1, 2, 2, 1, 3 };
            
            Vector2[] uvs = new Vector2[4]
            {
                new Vector2(0, 0),
                new Vector2(1, 0),
                new Vector2(0, 1),
                new Vector2(1, 1)
            };
            
            quadMesh.vertices = vertices;
            quadMesh.triangles = indices;
            quadMesh.uv = uvs;
            quadMesh.RecalculateNormals();
        }
        
        public void InitializeBuffers()
        {
            // Clean up any existing buffers
            ReleaseBuffers();
            
            // Get point data from proxy
            Vector3[] points = pointCloudProxy.Points;
            Color[] colors = pointCloudProxy.Colors;
            HashSet<int> hiddenPoints = pointCloudProxy.HiddenPoints;
            HashSet<int> selectedPoints = pointCloudProxy.SelectedPoints;
            
            pointCount = points.Length;
            
            // Create visibility array (1 = visible, 0 = hidden)
            int[] visibility = new int[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                visibility[i] = hiddenPoints.Contains(i) ? 0 : 1;
            }
            
            // Create selection array (1 = selected, 0 = not selected)
            int[] selection = new int[pointCount];
            foreach (int i in selectedPoints)
            {
                if (i >= 0 && i < pointCount)
                {
                    selection[i] = 1;
                }
            }
            
            // Create and populate buffers
            positionBuffer = new ComputeBuffer(pointCount, sizeof(float) * 3);
            colorBuffer = new ComputeBuffer(pointCount, sizeof(float) * 4);
            visibilityBuffer = new ComputeBuffer(pointCount, sizeof(int));
            selectionBuffer = new ComputeBuffer(pointCount, sizeof(int));
            
            positionBuffer.SetData(points);
            colorBuffer.SetData(colors);
            visibilityBuffer.SetData(visibility);
            selectionBuffer.SetData(selection);
            
            // Set shader properties
            pointMaterial.SetBuffer(PositionsID, positionBuffer);
            pointMaterial.SetBuffer(ColorsID, colorBuffer);
            pointMaterial.SetBuffer(VisibilityID, visibilityBuffer);
            pointMaterial.SetBuffer(SelectionID, selectionBuffer);
            pointMaterial.SetFloat(PointSizeID, pointCloudProxy.VisualScale);
            
            isInitialized = true;
        }
        
        private void UpdateBuffers()
        {
            if (!isInitialized || pointCloudProxy == null) return;
            
            // Update visibility
            int[] visibility = new int[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                visibility[i] = pointCloudProxy.IsPointHidden(i) ? 0 : 1;
            }
            visibilityBuffer.SetData(visibility);
            
            // Update selection
            int[] selection = new int[pointCount];
            foreach (int i in pointCloudProxy.SelectedPoints)
            {
                if (i >= 0 && i < pointCount)
                {
                    selection[i] = 1;
                }
            }
            selectionBuffer.SetData(selection);
            
            // Update point size
            pointMaterial.SetFloat(PointSizeID, pointCloudProxy.VisualScale);
        }
        
        public void UpdateVisibility()
        {
            if (!isInitialized || pointCloudProxy == null) return;
            
            int[] visibility = new int[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                visibility[i] = pointCloudProxy.IsPointHidden(i) ? 0 : 1;
            }
            visibilityBuffer.SetData(visibility);
        }
        
        public void UpdateSelection()
        {
            if (!isInitialized || pointCloudProxy == null) return;
            
            int[] selection = new int[pointCount];
            foreach (int i in pointCloudProxy.SelectedPoints)
            {
                if (i >= 0 && i < pointCount)
                {
                    selection[i] = 1;
                }
            }
            selectionBuffer.SetData(selection);
        }
        
        private void ReleaseBuffers()
        {
            // Release GPU resources
            positionBuffer?.Release();
            colorBuffer?.Release();
            visibilityBuffer?.Release();
            selectionBuffer?.Release();
            
            positionBuffer = null;
            colorBuffer = null;
            visibilityBuffer = null;
            selectionBuffer = null;
            
            isInitialized = false;
        }
        
        private void OnRenderObject()
        {
            if (!isInitialized || pointMaterial == null || pointCount == 0) return;
            
            // Set camera-relative matrices for proper depth sorting
            pointMaterial.SetMatrix("_CameraToWorld", Camera.current.cameraToWorldMatrix);
            pointMaterial.SetMatrix("_WorldToObject", transform.worldToLocalMatrix);
            
            // Set point size based on distance from camera
            pointMaterial.SetFloat(PointSizeID, pointCloudProxy.VisualScale);
            
            // Draw the mesh with instancing
            pointMaterial.SetPass(0);
            Graphics.DrawMeshNow(quadMesh, Matrix4x4.identity);
            Graphics.DrawMeshInstancedIndirect(quadMesh, 0, pointMaterial, 
                new Bounds(Vector3.zero, Vector3.one * 1000), null);
        }
    }
}