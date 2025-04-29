using UnityEngine;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;

namespace Ply
{
    public class PointCloudLOD : MonoBehaviour
    {
        [SerializeField] private PlySceneProxy pointCloudProxy;
        
        // LOD settings
        [Range(0.01f, 1.0f)]
        [SerializeField] private float detailLevel = 0.5f;
        [SerializeField] private bool autoLOD = true;
        [SerializeField] private int maxPointsPerLOD = 500000;
        
        // LOD levels - percentage of points to display at each level
        private readonly float[] lodLevels = { 0.01f, 0.05f, 0.1f, 0.25f, 0.5f, 1.0f };
        
        // Octree for multi-resolution sampling
        private Octree octree;
        private bool octreeBuilt = false;
        
        // Active point set
        private HashSet<int> activePointIndices = new HashSet<int>();
        
        // Caching for distance-based LOD
        private Vector3 lastCameraPosition;
        private float lastDetailLevel;
        
        private void Start()
        {
            if (pointCloudProxy == null)
            {
                pointCloudProxy = GetComponent<PlySceneProxy>();
            }
            
            // Build octree on start
            if (pointCloudProxy != null && pointCloudProxy.Points != null)
            {
                BuildOctree();
            }
        }
        
        private void OnEnable()
        {
            if (pointCloudProxy == null)
            {
                pointCloudProxy = GetComponent<PlySceneProxy>();
            }
        }
        
        private void Update()
        {
            if (pointCloudProxy == null || pointCloudProxy.Points == null) return;
            
            // Build octree if not built yet
            if (!octreeBuilt)
            {
                BuildOctree();
            }
            
            // Update LOD based on camera distance if auto LOD is enabled
            if (autoLOD)
            {
                Camera cam = Camera.main ?? FindActiveCamera();
                if (cam != null)
                {
                    Vector3 cameraPos = cam.transform.position;
                    
                    // Only update if camera has moved significantly
                    if (Vector3.Distance(cameraPos, lastCameraPosition) > 1.0f || 
                        Mathf.Abs(detailLevel - lastDetailLevel) > 0.05f)
                    {
                        UpdateLOD(cameraPos);
                        lastCameraPosition = cameraPos;
                        lastDetailLevel = detailLevel;
                    }
                }
            }
        }
        
        private Camera FindActiveCamera()
        {
            // Try to find scene view camera in editor
            #if UNITY_EDITOR
            if (UnityEditor.SceneView.lastActiveSceneView != null)
            {
                return UnityEditor.SceneView.lastActiveSceneView.camera;
            }
            #endif
            
            // Fallback to any camera
            return Camera.main ?? Camera.current;
        }
        
        public void BuildOctree()
        {
            if (pointCloudProxy == null || pointCloudProxy.Points == null) return;
            
            Debug.Log("Building LOD octree...");
            Vector3[] points = pointCloudProxy.Points;
            Color[] colors = pointCloudProxy.Colors;
            
            // Create octree
            octree = new Octree(points, colors, pointCloudProxy.HiddenPoints);
            octreeBuilt = true;
            
            // Initial LOD update
            UpdateLOD(Camera.main != null ? Camera.main.transform.position : Vector3.zero);
            
            Debug.Log("Octree build complete");
        }
        
        public void SetDetailLevel(float level)
        {
            detailLevel = Mathf.Clamp01(level);
            UpdateLOD(lastCameraPosition);
        }
        
        private void UpdateLOD(Vector3 cameraPosition)
        {
            if (octree == null || !octreeBuilt) return;
            
            // Get visible points for current LOD level
            HashSet<int> newVisiblePoints = octree.GetPointsForLOD(cameraPosition, detailLevel, maxPointsPerLOD);
            
            // Skip if no change
            if (SetEquals(activePointIndices, newVisiblePoints)) return;
            
            // Update active points
            activePointIndices = newVisiblePoints;
            
            // Apply to proxy visibility (without changing hidden points status)
            ApplyActiveLODToProxy();
        }
        
        private bool SetEquals(HashSet<int> a, HashSet<int> b)
        {
            if (a.Count != b.Count) return false;
            
            foreach (int item in a)
            {
                if (!b.Contains(item)) return false;
            }
            
            return true;
        }
        
        private void ApplyActiveLODToProxy()
        {
            // This method does not actually change the hidden points in the proxy
            // It just provides a filtered set of points for rendering
            // The custom renderer should check both HiddenPoints and our activePointIndices
        }
        
        // Custom class for multi-resolution point cloud rendering
        public class Octree
        {
            private class OctreeNode
            {
                public Bounds bounds;
                public Vector3 centerPoint;
                public Color averageColor;
                public List<int> points;
                public OctreeNode[] children;
                public bool isLeaf => children == null;
                
                // Importance metric for view-dependent LOD
                public float importance;
                
                public OctreeNode(Bounds bounds)
                {
                    this.bounds = bounds;
                    this.points = new List<int>();
                    this.centerPoint = bounds.center;
                    this.averageColor = Color.white;
                }
                
                public void Subdivide()
                {
                    Vector3 size = bounds.size * 0.5f;
                    Vector3 center = bounds.center;
                    
                    children = new OctreeNode[8];
                    
                    // Create 8 children in a 2x2x2 grid
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 offset = new Vector3(
                            ((i & 1) != 0) ? size.x * 0.5f : -size.x * 0.5f,
                            ((i & 2) != 0) ? size.y * 0.5f : -size.y * 0.5f,
                            ((i & 4) != 0) ? size.z * 0.5f : -size.z * 0.5f
                        );
                        
                        Vector3 childCenter = center + offset;
                        Bounds childBounds = new Bounds(childCenter, size);
                        children[i] = new OctreeNode(childBounds);
                    }
                }
            }
            
            private OctreeNode rootNode;
            private Vector3[] points;
            private Color[] colors;
            private HashSet<int> hiddenPoints;
            private int maxDepth = 8;
            private int minPointsPerNode = 10;
            
            public Octree(Vector3[] points, Color[] colors, HashSet<int> hiddenPoints)
            {
                this.points = points;
                this.colors = colors;
                this.hiddenPoints = hiddenPoints;
                
                // Calculate bounds
                Bounds bounds = CalculateBounds(points);
                
                // Create root node
                rootNode = new OctreeNode(bounds);
                
                // Add all points to root (except hidden ones)
                for (int i = 0; i < points.Length; i++)
                {
                    if (!hiddenPoints.Contains(i))
                    {
                        rootNode.points.Add(i);
                    }
                }
                
                // Build tree recursively
                BuildTree(rootNode, 0);
                
                // Compute representative points and colors
                ComputeNodeStatistics(rootNode);
            }
            
            private Bounds CalculateBounds(Vector3[] points)
            {
                if (points.Length == 0) return new Bounds(Vector3.zero, Vector3.one);
                
                Bounds bounds = new Bounds(points[0], Vector3.zero);
                for (int i = 1; i < points.Length; i++)
                {
                    bounds.Encapsulate(points[i]);
                }
                
                // Add padding
                bounds.Expand(bounds.size * 0.1f);
                return bounds;
            }
            
            private void BuildTree(OctreeNode node, int depth)
            {
                // Stop recursion if we've reached maximum depth or minimum points
                if (depth >= maxDepth || node.points.Count <= minPointsPerNode)
                {
                    return;
                }
                
                // Subdivide node
                node.Subdivide();
                
                // Distribute points to children
                foreach (int i in node.points)
                {
                    Vector3 point = points[i];
                    
                    // Find child that contains this point
                    for (int c = 0; c < 8; c++)
                    {
                        if (node.children[c].bounds.Contains(point))
                        {
                            node.children[c].points.Add(i);
                            break;
                        }
                    }
                }
                
                // Clear parent points to save memory
                if (depth > 0)  // Keep root points for backup
                {
                    node.points.Clear();
                }
                
                // Recursively build children
                for (int c = 0; c < 8; c++)
                {
                    if (node.children[c].points.Count > 0)
                    {
                        BuildTree(node.children[c], depth + 1);
                    }
                }
            }
            
            private void ComputeNodeStatistics(OctreeNode node)
            {
                if (node.isLeaf)
                {
                    // For leaf nodes, compute average position and color
                    if (node.points.Count > 0)
                    {
                        Vector3 sum = Vector3.zero;
                        Color colorSum = Color.clear;
                        
                        foreach (int i in node.points)
                        {
                            sum += points[i];
                            colorSum += colors[i];
                        }
                        
                        node.centerPoint = sum / node.points.Count;
                        node.averageColor = colorSum / node.points.Count;
                    }
                }
                else
                {
                    // For internal nodes, recurse to children first
                    for (int c = 0; c < 8; c++)
                    {
                        if (node.children[c].points.Count > 0 || !node.children[c].isLeaf)
                        {
                            ComputeNodeStatistics(node.children[c]);
                        }
                    }
                    
                    // Then compute stats from children
                    Vector3 sum = Vector3.zero;
                    Color colorSum = Color.clear;
                    int count = 0;
                    
                    for (int c = 0; c < 8; c++)
                    {
                        if (node.children[c].points.Count > 0 || !node.children[c].isLeaf)
                        {
                            sum += node.children[c].centerPoint;
                            colorSum += node.children[c].averageColor;
                            count++;
                        }
                    }
                    
                    if (count > 0)
                    {
                        node.centerPoint = sum / count;
                        node.averageColor = colorSum / count;
                    }
                    else
                    {
                        node.centerPoint = node.bounds.center;
                        node.averageColor = Color.white;
                    }
                }
            }
            
            // Calculate node importance based on screen-space size and camera distance
            private float CalculateImportance(OctreeNode node, Vector3 cameraPos)
            {
                Vector3 closestPoint = node.bounds.ClosestPoint(cameraPos);
                float distance = Vector3.Distance(closestPoint, cameraPos);
                
                // Add small epsilon to avoid division by zero
                distance = Mathf.Max(distance, 0.01f);
                
                // Approximate node's screen-space size
                float apparentSize = node.bounds.size.magnitude / distance;
                
                // Importance is higher for larger apparent size
                return apparentSize;
            }
            
            // Get points for a certain LOD level
            public HashSet<int> GetPointsForLOD(Vector3 cameraPos, float detailLevel, int maxPoints)
            {
                // Initialize result set
                HashSet<int> result = new HashSet<int>();
                
                // Early out if detail level is at maximum - return all points
                if (detailLevel >= 0.99f)
                {
                    for (int i = 0; i < points.Length; i++)
                    {
                        if (!hiddenPoints.Contains(i))
                        {
                            result.Add(i);
                        }
                    }
                    return result;
                }
                
                // Calculate target point count based on detail level
                int targetPointCount = Mathf.Min(
                    maxPoints, 
                    Mathf.CeilToInt(points.Length * detailLevel)
                );
                
                // Priority queue of nodes by importance
                List<KeyValuePair<float, OctreeNode>> nodeQueue = new List<KeyValuePair<float, OctreeNode>>();
                
                // Add root to queue with its importance
                float rootImportance = CalculateImportance(rootNode, cameraPos);
                nodeQueue.Add(new KeyValuePair<float, OctreeNode>(rootImportance, rootNode));
                
                while (nodeQueue.Count > 0 && result.Count < targetPointCount)
                {
                    // Sort by importance (descending)
                    nodeQueue.Sort((a, b) => b.Key.CompareTo(a.Key));
                    
                    // Get most important node
                    OctreeNode currentNode = nodeQueue[0].Value;
                    nodeQueue.RemoveAt(0);
                    
                    if (currentNode.isLeaf)
                    {
                        // For leaf nodes, add all points
                        foreach (int i in currentNode.points)
                        {
                            result.Add(i);
                            
                            // Early out if we've reached target count
                            if (result.Count >= targetPointCount)
                                break;
                        }
                    }
                    else
                    {
                        // For internal nodes, add children to queue
                        for (int c = 0; c < 8; c++)
                        {
                            if (currentNode.children[c].points.Count > 0 || !currentNode.children[c].isLeaf)
                            {
                                float importance = CalculateImportance(currentNode.children[c], cameraPos);
                                nodeQueue.Add(new KeyValuePair<float, OctreeNode>(importance, currentNode.children[c]));
                            }
                        }
                    }
                }
                
                return result;
            }
        }
    }
}