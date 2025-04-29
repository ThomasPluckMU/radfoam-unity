using UnityEngine;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Burst;

namespace Ply
{
    public class SpatialIndex : MonoBehaviour
    {
        // Octree implementation for fast spatial queries
        public class Octree
        {
            private class OctreeNode
            {
                public Bounds bounds;
                public Vector3 center;
                public float halfSize;
                public OctreeNode[] children;  // 8 children for octree
                public List<int> pointIndices;
                public bool isLeaf => children == null;
                
                // Constructor
                public OctreeNode(Vector3 center, float halfSize)
                {
                    this.center = center;
                    this.halfSize = halfSize;
                    this.bounds = new Bounds(center, Vector3.one * halfSize * 2);
                    this.pointIndices = new List<int>();
                }
                
                // Split node into 8 children
                public void Split()
                {
                    if (!isLeaf) return;
                    
                    children = new OctreeNode[8];
                    float childHalfSize = halfSize * 0.5f;
                    
                    // Create 8 children in a 2x2x2 grid
                    for (int i = 0; i < 8; i++)
                    {
                        float x = center.x + ((i & 1) == 0 ? -childHalfSize : childHalfSize);
                        float y = center.y + ((i & 2) == 0 ? -childHalfSize : childHalfSize);
                        float z = center.z + ((i & 4) == 0 ? -childHalfSize : childHalfSize);
                        
                        children[i] = new OctreeNode(new Vector3(x, y, z), childHalfSize);
                    }
                    
                    // Redistribute points to children
                    foreach (int index in pointIndices)
                    {
                        Vector3 point = points[index];
                        int childIndex = GetChildIndex(point);
                        children[childIndex].pointIndices.Add(index);
                    }
                    
                    // Clear parent's point list to save memory
                    pointIndices.Clear();
                }
                
                // Determine which child a point belongs to
                private int GetChildIndex(Vector3 point)
                {
                    int index = 0;
                    if (point.x >= center.x) index |= 1;
                    if (point.y >= center.y) index |= 2;
                    if (point.z >= center.z) index |= 4;
                    return index;
                }
            }
            
            private OctreeNode root;
            private static Vector3[] points;
            private int maxPointsPerNode;
            private float minNodeSize;
            
            // Constructor
            public Octree(Vector3[] inPoints, int maxPointsPerNode = 100, float minNodeSize = 0.01f)
            {
                points = inPoints;
                this.maxPointsPerNode = maxPointsPerNode;
                this.minNodeSize = minNodeSize;
                
                // Calculate bounds of the entire point cloud
                Bounds bounds = CalculateBounds(points);
                
                // Create root node with padding
                float maxSize = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                float halfSize = maxSize * 0.51f;  // Add padding
                root = new OctreeNode(bounds.center, halfSize);
                
                // Add all points to root
                for (int i = 0; i < points.Length; i++)
                {
                    root.pointIndices.Add(i);
                }
                
                // Build tree recursively
                BuildTree(root);
            }
            
            // Calculate bounds of point array
            private Bounds CalculateBounds(Vector3[] points)
            {
                if (points.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);
                
                Bounds bounds = new Bounds(points[0], Vector3.zero);
                for (int i = 1; i < points.Length; i++)
                {
                    bounds.Encapsulate(points[i]);
                }
                return bounds;
            }
            
            // Recursive tree building
            private void BuildTree(OctreeNode node)
            {
                // Stop recursion if we've reached our limits
                if (node.pointIndices.Count <= maxPointsPerNode || node.halfSize <= minNodeSize)
                    return;
                
                // Split and process children
                node.Split();
                for (int i = 0; i < 8; i++)
                {
                    if (node.children[i].pointIndices.Count > 0)
                    {
                        BuildTree(node.children[i]);
                    }
                }
            }
            
            // Find all points within a box
            public List<int> QueryBox(Bounds box)
            {
                List<int> result = new List<int>();
                QueryBoxRecursive(root, box, result);
                return result;
            }
            
            private void QueryBoxRecursive(OctreeNode node, Bounds box, List<int> result)
            {
                // Early out if this node doesn't intersect the query box
                if (!node.bounds.Intersects(box)) return;
                
                // If it's a leaf, check all contained points
                if (node.isLeaf)
                {
                    foreach (int i in node.pointIndices)
                    {
                        if (box.Contains(points[i]))
                        {
                            result.Add(i);
                        }
                    }
                }
                else
                {
                    // Recurse into children
                    for (int i = 0; i < 8; i++)
                    {
                        if (node.children[i].pointIndices.Count > 0)
                        {
                            QueryBoxRecursive(node.children[i], box, result);
                        }
                    }
                }
            }
            
            // Find all points in a frustum
            public List<int> QueryFrustum(Plane[] frustumPlanes)
            {
                List<int> result = new List<int>();
                QueryFrustumRecursive(root, frustumPlanes, result);
                return result;
            }
            
            private void QueryFrustumRecursive(OctreeNode node, Plane[] frustumPlanes, List<int> result)
            {
                // Check if node is outside any frustum plane
                foreach (Plane plane in frustumPlanes)
                {
                    Vector3 normal = plane.normal;
                    float dist = plane.distance;
                    
                    // Find the point furthest in the direction of the plane normal
                    Vector3 positive = node.center;
                    positive.x += normal.x >= 0 ? node.halfSize : -node.halfSize;
                    positive.y += normal.y >= 0 ? node.halfSize : -node.halfSize;
                    positive.z += normal.z >= 0 ? node.halfSize : -node.halfSize;
                    
                    // If this point is outside, the whole node is outside
                    if (plane.GetDistanceToPoint(positive) < 0)
                    {
                        return;
                    }
                }
                
                // Node is at least partially inside the frustum
                if (node.isLeaf)
                {
                    // For leaf nodes, check individual points
                    foreach (int i in node.pointIndices)
                    {
                        bool inside = true;
                        foreach (Plane plane in frustumPlanes)
                        {
                            if (plane.GetDistanceToPoint(points[i]) < 0)
                            {
                                inside = false;
                                break;
                            }
                        }
                        
                        if (inside)
                        {
                            result.Add(i);
                        }
                    }
                }
                else
                {
                    // Recurse into children
                    for (int i = 0; i < 8; i++)
                    {
                        if (node.children[i].pointIndices.Count > 0)
                        {
                            QueryFrustumRecursive(node.children[i], frustumPlanes, result);
                        }
                    }
                }
            }
            
            // Find points within a sphere
            public List<int> QuerySphere(Vector3 center, float radius)
            {
                List<int> result = new List<int>();
                QuerySphereRecursive(root, center, radius, result);
                return result;
            }
            
            private void QuerySphereRecursive(OctreeNode node, Vector3 center, float radius, List<int> result)
            {
                // Early out - check if sphere and node bounds overlap
                float sqrDist = SqrDistancePointAABB(center, node.bounds);
                if (sqrDist > radius * radius) return;
                
                // For leaf nodes, check contained points
                if (node.isLeaf)
                {
                    foreach (int i in node.pointIndices)
                    {
                        float sqrDistToPoint = (points[i] - center).sqrMagnitude;
                        if (sqrDistToPoint <= radius * radius)
                        {
                            result.Add(i);
                        }
                    }
                }
                else
                {
                    // Recurse into children
                    for (int i = 0; i < 8; i++)
                    {
                        if (node.children[i].pointIndices.Count > 0)
                        {
                            QuerySphereRecursive(node.children[i], center, radius, result);
                        }
                    }
                }
            }
            
            // Helper to calculate squared distance from point to AABB
            private float SqrDistancePointAABB(Vector3 point, Bounds bounds)
            {
                float sqrDist = 0.0f;
                
                // For each axis, calculate squared distance
                for (int i = 0; i < 3; i++)
                {
                    float v = point[i];
                    float min = bounds.min[i];
                    float max = bounds.max[i];
                    
                    if (v < min) sqrDist += (min - v) * (min - v);
                    if (v > max) sqrDist += (v - max) * (v - max);
                }
                
                return sqrDist;
            }
        }
        
        private Octree octree;
        private bool isDirty = true;
        private PlySceneProxy proxy;
        
        public void Initialize(PlySceneProxy proxy)
        {
            this.proxy = proxy;
            RebuildIndex();
        }
        
        public void RebuildIndex()
        {
            if (proxy == null || proxy.Points == null) return;
            
            // Build octree with proxy's points
            octree = new Octree(proxy.Points);
            isDirty = false;
        }
        
        // Find all points within a box
        public List<int> QueryBox(Bounds box)
        {
            if (isDirty || octree == null)
            {
                RebuildIndex();
            }
            
            return octree.QueryBox(box);
        }
        
        // Find points within view frustum
        public List<int> QueryFrustum(Plane[] frustumPlanes)
        {
            if (isDirty || octree == null)
            {
                RebuildIndex();
            }
            
            return octree.QueryFrustum(frustumPlanes);
        }
        
        // Find points within sphere
        public List<int> QuerySphere(Vector3 center, float radius)
        {
            if (isDirty || octree == null)
            {
                RebuildIndex();
            }
            
            return octree.QuerySphere(center, radius);
        }
        
        // Find points by plane
        public List<int> QueryPlane(Plane plane, bool positiveHalfspace)
        {
            if (isDirty || octree == null || proxy == null)
            {
                RebuildIndex();
            }
            
            // Create result list
            List<int> result = new List<int>();
            Vector3[] points = proxy.Points;
            
            for (int i = 0; i < points.Length; i++)
            {
                if (proxy.IsPointHidden(i)) continue;
                
                float distance = plane.GetDistanceToPoint(points[i]);
                bool onPositiveSide = distance >= 0;
                
                if (onPositiveSide == positiveHalfspace)
                {
                    result.Add(i);
                }
            }
            
            return result;
        }
        
        // Find points by color similarity
        public List<int> QueryColorSimilarity(Color targetColor, float threshold)
        {
            if (proxy == null || proxy.Colors == null) return new List<int>();
            
            List<int> result = new List<int>();
            Color[] colors = proxy.Colors;
            
            for (int i = 0; i < colors.Length; i++)
            {
                if (proxy.IsPointHidden(i)) continue;
                
                float distance = ColorDistance(colors[i], targetColor);
                if (distance < threshold)
                {
                    result.Add(i);
                }
            }
            
            return result;
        }
        
        // Helper method for color distance
        private float ColorDistance(Color a, Color b)
        {
            return Mathf.Sqrt(
                (a.r - b.r) * (a.r - b.r) +
                (a.g - b.g) * (a.g - b.g) +
                (a.b - b.b) * (a.b - b.b));
        }
        
        // Query density (points with fewer than X neighbors in radius R)
        public List<int> QueryDensity(float radius, int minNeighbors)
        {
            if (isDirty || octree == null || proxy == null)
            {
                RebuildIndex();
            }
            
            List<int> result = new List<int>();
            Vector3[] points = proxy.Points;
            
            for (int i = 0; i < points.Length; i++)
            {
                if (proxy.IsPointHidden(i)) continue;
                
                List<int> neighbors = octree.QuerySphere(points[i], radius);
                
                // Don't count itself
                int neighborCount = neighbors.Count - 1;
                
                if (neighborCount < minNeighbors)
                {
                    result.Add(i);
                }
            }
            
            return result;
        }
    }
}