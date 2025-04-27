using UnityEngine;

namespace Ply
{
    [RequireComponent(typeof(PlySceneProxy))]
    public class PlyRenderer : MonoBehaviour
    {
        // Reference to the PlySceneProxy component
        private PlySceneProxy plyProxy;
        
        // Rendering settings
        [Header("Rendering Settings")]
        [SerializeField] private RenderType renderType = RenderType.Points;
        [SerializeField, Range(0.001f, 0.1f)] private float pointSize = 0.01f;
        [SerializeField] private Material pointMaterial;
        
        // Internal data
        private Mesh pointsMesh;
        private bool isDirty = true;
        
        public enum RenderType
        {
            Points,
            Billboards,
            Spheres
        }
        
        private void Awake()
        {
            plyProxy = GetComponent<PlySceneProxy>();
            
            // Create default material if none assigned
            if (pointMaterial == null)
            {
                pointMaterial = new Material(Shader.Find("Particles/Standard Unlit"));
                pointMaterial.name = "Default Point Material";
            }
        }
        
        private void OnEnable()
        {
            isDirty = true;
        }
        
        private void Update()
        {
            // Check if we need to rebuild the mesh
            if (isDirty || pointsMesh == null)
            {
                RegeneratePointsMesh();
                isDirty = false;
            }
        }
        
        private void OnValidate()
        {
            isDirty = true;
        }
        
        public void ForceRebuild()
        {
            CleanupMesh();
            isDirty = true;
        }
        
        private void CleanupMesh()
        {
            // Destroy the existing mesh
            if (pointsMesh != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(pointsMesh);
                }
                else
                {
                    DestroyImmediate(pointsMesh);
                }
                pointsMesh = null;
            }
        }
        
        private void OnDestroy()
        {
            CleanupMesh();
        }
        
        private void RegeneratePointsMesh()
        {
            // Clean up existing mesh
            CleanupMesh();
            
            // Check if we have data to render
            if (plyProxy == null || plyProxy.Points == null || plyProxy.Points.Length == 0)
            {
                return;
            }
            
            Vector3[] points = plyProxy.Points;
            Color[] colors = plyProxy.Colors;
            
            // Create a new mesh
            pointsMesh = new Mesh();
            pointsMesh.name = "PointCloudMesh";
            
            // Set vertices and colors
            pointsMesh.vertices = points;
            pointsMesh.colors = colors;
            
            // For point rendering, use all indices
            int[] indices = new int[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                indices[i] = i;
            }
            
            // Set indices based on render type
            switch (renderType)
            {
                case RenderType.Points:
                    pointsMesh.SetIndices(indices, MeshTopology.Points, 0);
                    break;
                    
                case RenderType.Billboards:
                case RenderType.Spheres:
                    // Will be handled in OnRenderObject
                    pointsMesh.SetIndices(indices, MeshTopology.Points, 0);
                    break;
            }
            
            // Set bounds
            pointsMesh.RecalculateBounds();
        }
        
        private void OnRenderObject()
        {
            if (pointsMesh == null || pointMaterial == null)
                return;
                
            // Setup material
            pointMaterial.SetPass(0);
            
            // Draw based on render type
            switch (renderType)
            {
                case RenderType.Points:
                    // Simple point rendering
                    GL.PushMatrix();
                    GL.MultMatrix(transform.localToWorldMatrix);
                    
                    Graphics.DrawMeshNow(pointsMesh, Matrix4x4.identity);
                    
                    GL.PopMatrix();
                    break;
                    
                case RenderType.Billboards:
                    // Draw camera-facing quads for each point
                    Camera cam = Camera.current;
                    if (cam == null) return;
                    
                    GL.PushMatrix();
                    GL.MultMatrix(transform.localToWorldMatrix);
                    
                    // Get mesh data
                    Vector3[] verts = pointsMesh.vertices;
                    Color[] colors = pointsMesh.colors;
                    
                    // Begin drawing quads
                    GL.Begin(GL.QUADS);
                    
                    for (int i = 0; i < verts.Length; i++)
                    {
                        Vector3 pos = verts[i];
                        Vector3 camRight = cam.transform.right * pointSize;
                        Vector3 camUp = cam.transform.up * pointSize;
                        
                        // Set color
                        GL.Color(colors[i]);
                        
                        // Draw quad vertices
                        GL.Vertex(pos - camRight - camUp);
                        GL.Vertex(pos + camRight - camUp);
                        GL.Vertex(pos + camRight + camUp);
                        GL.Vertex(pos - camRight + camUp);
                    }
                    
                    GL.End();
                    GL.PopMatrix();
                    break;
                    
                case RenderType.Spheres:
                    // Not implemented in this simplified version
                    // For proper sphere rendering, use a sphere mesh and Graphics.DrawMeshInstanced
                    // As a fallback, just render points
                    Graphics.DrawMeshNow(pointsMesh, transform.localToWorldMatrix);
                    break;
            }
        }
        
        // Optional: Draw gizmos in editor
        private void OnDrawGizmosSelected()
        {
            if (plyProxy != null && plyProxy.DataBounds.size != Vector3.zero)
            {
                Gizmos.color = new Color(0.5f, 1f, 0.5f, 0.3f);
                Gizmos.DrawWireCube(plyProxy.DataBounds.center, plyProxy.DataBounds.size);
            }
        }
    }
}