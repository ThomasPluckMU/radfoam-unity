using Ply;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using System.Collections.Generic;
using System;

public class RadFoamWebGL : MonoBehaviour
{
    public PlyData Data;
    public float fisheye_fov = 60;
    public Transform Target;
    
    [Tooltip("Toggle to show or hide infinite cells")]
    public bool showUnboundedCells = false; // Toggle for infinite cells

    private Material blitMat;
    private Texture2D positions_tex;
    private Texture2D attr_tex;
    private Texture2D adjacency_tex;
    private Texture2D adjacency_diff_tex;
    private Texture2D convex_hull_tex; // Texture to identify cells on the convex hull

    private NativeArray<float4> points; // store this for finding the closest cell to the camera on the CPU

    // GUI toggle settings
    private Rect toggleButtonRect;
    private GUIStyle buttonStyle;

    void Start()
    {
        blitMat = new Material(Shader.Find("Hidden/Custom/RadFoamShader"));
        Load();
        ComputeConvexHull(); // Compute convex hull after loading data
        
        // Setup GUI elements
        toggleButtonRect = new Rect(10, 10, 150, 30);
        buttonStyle = new GUIStyle(GUI.skin.button);
        buttonStyle.fontSize = 14;
        buttonStyle.normal.textColor = Color.white;
    }

    void OnDestroy()
    {
        if (points.IsCreated)
            points.Dispose();
        Destroy(blitMat);
    }

    void Update()
    {
        fisheye_fov = Mathf.Clamp(fisheye_fov + Input.mouseScrollDelta.y * -4, 10, 120);
        
        // Update shader with current unbounded cells setting
        blitMat.SetInt("_ShowUnboundedCells", showUnboundedCells ? 1 : 0);
    }
    
    void OnGUI()
    {
        // Create a toggle button for showing/hiding unbounded cells
        string buttonText = showUnboundedCells ? "Hide Unbounded Cells" : "Show Unbounded Cells";
        GUI.backgroundColor = showUnboundedCells ? Color.green : Color.red;
        
        if (GUI.Button(toggleButtonRect, buttonText, buttonStyle))
        {
            showUnboundedCells = !showUnboundedCells;
            blitMat.SetInt("_ShowUnboundedCells", showUnboundedCells ? 1 : 0);
        }
        
        // Show FOV info
        GUI.Label(new Rect(10, 50, 200, 30), $"FOV: {fisheye_fov}° (Scroll to adjust)");
    }

    // Quick Hull 3D implementation for convex hull calculation
    private void ComputeConvexHull()
    {
        if (points.Length == 0)
            return;

        // Convert float4 points to Vector3 array for convex hull calculation
        Vector3[] vertices = new Vector3[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            vertices[i] = new Vector3(points[i].x, points[i].y, points[i].z);
        }

        // Compute the convex hull using QuickHull3D algorithm
        HashSet<int> hullIndices = new HashSet<int>();
        ComputeQuickHull3D(vertices, hullIndices);

        // Create a texture to store which cells are on the convex hull
        int texWidth = Mathf.CeilToInt(Mathf.Sqrt(points.Length));
        int texHeight = Mathf.CeilToInt((float)points.Length / texWidth);
        convex_hull_tex = new Texture2D(texWidth, texHeight, TextureFormat.RFloat, false);
        convex_hull_tex.filterMode = FilterMode.Point;
        convex_hull_tex.wrapMode = TextureWrapMode.Clamp;

        // Initialize all cells as not on the convex hull
        Color[] colors = new Color[texWidth * texHeight];
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = new Color(0, 0, 0, 0);
        }

        // Mark cells that are on the convex hull
        foreach (int index in hullIndices)
        {
            if (index >= 0 && index < colors.Length)
            {
                colors[index] = new Color(1, 0, 0, 0); // r=1 means on hull
            }
        }

        convex_hull_tex.SetPixels(colors);
        convex_hull_tex.Apply();

        // Pass to shader
        blitMat.SetTexture("_convex_hull_tex", convex_hull_tex);
        // Set the TexelSize property
        blitMat.SetVector("_convex_hull_tex_TexelSize", 
            new Vector4(1.0f/texWidth, 1.0f/texHeight, texWidth, texHeight));
            
        // Initialize the unbounded cells toggle in the shader
        blitMat.SetInt("_ShowUnboundedCells", showUnboundedCells ? 1 : 0);
        
        Debug.Log($"Convex hull computed with {hullIndices.Count} vertices on the hull out of {points.Length} total points");
    }

    // QuickHull 3D implementation
    private void ComputeQuickHull3D(Vector3[] points, HashSet<int> hullIndices)
    {
        if (points.Length < 4)
        {
            // If we have less than 4 points, all points are on the hull
            for (int i = 0; i < points.Length; i++)
                hullIndices.Add(i);
            return;
        }

        // Step 1: Find the initial tetrahedron
        int[] extremePoints = FindExtremePoints(points);
        
        // Step 2: Initialize the convex hull with the tetrahedron
        List<Triangle> faces = InitializeTetrahedron(points, extremePoints);
        
        // Mapping of points to their assigned faces for quick lookup
        Dictionary<int, int> pointToFace = new Dictionary<int, int>();
        
        // Step 3: Assign each point to a face if it's outside
        for (int i = 0; i < points.Length; i++)
        {
            if (Array.IndexOf(extremePoints, i) >= 0)
                continue; // Skip the extreme points as they're already on the hull
                
            float maxDistance = 0;
            int assignedFace = -1;
            
            for (int f = 0; f < faces.Count; f++)
            {
                float distance = PointFaceDistance(points[i], faces[f], points);
                if (distance > 0 && distance > maxDistance)
                {
                    maxDistance = distance;
                    assignedFace = f;
                }
            }
            
            if (assignedFace >= 0)
            {
                faces[assignedFace].outsidePoints.Add(i);
                pointToFace[i] = assignedFace;
            }
        }
        
        // Step 4: Process each face
        while (true)
        {
            int faceIndex = -1;
            int furthestPoint = -1;
            float maxDistance = 0;
            
            // Find the face with the furthest point
            for (int f = 0; f < faces.Count; f++)
            {
                if (faces[f].outsidePoints.Count == 0)
                    continue;
                    
                // Find furthest point for this face
                foreach (int p in faces[f].outsidePoints)
                {
                    float distance = PointFaceDistance(points[p], faces[f], points);
                    if (distance > maxDistance)
                    {
                        maxDistance = distance;
                        furthestPoint = p;
                        faceIndex = f;
                    }
                }
            }
            
            if (faceIndex == -1)
                break; // No more points outside
                
            // Add this point to the hull
            hullIndices.Add(furthestPoint);
            
            // Find all faces visible from this point
            List<int> visibleFaces = new List<int>();
            for (int f = 0; f < faces.Count; f++)
            {
                if (PointFaceDistance(points[furthestPoint], faces[f], points) > 0)
                    visibleFaces.Add(f);
            }
            
            // Find horizon edges
            List<Edge> horizon = new List<Edge>();
            FindHorizonEdges(faces, visibleFaces, horizon);
            
            // Create new faces connecting the point to horizon edges
            List<int> newFaces = new List<int>();
            foreach (Edge edge in horizon)
            {
                Triangle newFace = new Triangle(
                    edge.v1, edge.v2, furthestPoint,
                    CalculateNormal(points[edge.v1], points[edge.v2], points[furthestPoint]));
                faces.Add(newFace);
                newFaces.Add(faces.Count - 1);
            }
            
            // Reassign points from deleted faces to new faces
            foreach (int f in visibleFaces)
            {
                foreach (int p in faces[f].outsidePoints)
                {
                    float maxDist = 0;
                    int bestFace = -1;
                    
                    for (int nf = 0; nf < newFaces.Count; nf++)
                    {
                        float dist = PointFaceDistance(points[p], faces[newFaces[nf]], points);
                        if (dist > 0 && dist > maxDist)
                        {
                            maxDist = dist;
                            bestFace = newFaces[nf];
                        }
                    }
                    
                    if (bestFace >= 0)
                    {
                        faces[bestFace].outsidePoints.Add(p);
                        pointToFace[p] = bestFace;
                    }
                }
            }
            
            // Remove visible faces (in reverse order to avoid index issues)
            visibleFaces.Sort((a, b) => b.CompareTo(a));
            foreach (int f in visibleFaces)
            {
                faces.RemoveAt(f);
            }
        }
        
        // Add all vertices from the final faces to the hull
        foreach (var face in faces)
        {
            hullIndices.Add(face.v1);
            hullIndices.Add(face.v2);
            hullIndices.Add(face.v3);
        }
    }

    // Helper method to find extreme points for the initial tetrahedron
    private int[] FindExtremePoints(Vector3[] points)
    {
        int[] result = new int[4];
        
        // Find min/max points along each axis
        int minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;
        
        for (int i = 1; i < points.Length; i++)
        {
            if (points[i].x < points[minX].x) minX = i;
            if (points[i].x > points[maxX].x) maxX = i;
            if (points[i].y < points[minY].y) minY = i;
            if (points[i].y > points[maxY].y) maxY = i;
            if (points[i].z < points[minZ].z) minZ = i;
            if (points[i].z > points[maxZ].z) maxZ = i;
        }
        
        // Find two most distant points for initial edge
        int[] candidates = { minX, maxX, minY, maxY, minZ, maxZ };
        float maxDist = 0;
        int p1 = 0, p2 = 0;
        
        for (int i = 0; i < candidates.Length; i++)
        {
            for (int j = i + 1; j < candidates.Length; j++)
            {
                float dist = Vector3.Distance(points[candidates[i]], points[candidates[j]]);
                if (dist > maxDist)
                {
                    maxDist = dist;
                    p1 = candidates[i];
                    p2 = candidates[j];
                }
            }
        }
        
        result[0] = p1;
        result[1] = p2;
        
        // Find point farthest from the line p1-p2
        float maxLineDistance = 0;
        int p3 = 0;
        
        for (int i = 0; i < points.Length; i++)
        {
            if (i == p1 || i == p2) continue;
            
            float dist = PointLineDistance(points[i], points[p1], points[p2]);
            if (dist > maxLineDistance)
            {
                maxLineDistance = dist;
                p3 = i;
            }
        }
        
        result[2] = p3;
        
        // Find point farthest from the plane p1-p2-p3
        float maxPlaneDistance = 0;
        int p4 = 0;
        Vector3 normal = Vector3.Cross(
            points[p2] - points[p1],
            points[p3] - points[p1]
        ).normalized;
        
        for (int i = 0; i < points.Length; i++)
        {
            if (i == p1 || i == p2 || i == p3) continue;
            
            float dist = Mathf.Abs(Vector3.Dot(points[i] - points[p1], normal));
            if (dist > maxPlaneDistance)
            {
                maxPlaneDistance = dist;
                p4 = i;
            }
        }
        
        result[3] = p4;
        return result;
    }

    // Helper methods: These remain the same as previous implementation
    private List<Triangle> InitializeTetrahedron(Vector3[] points, int[] extremePoints)
    {
        // Same as before
        List<Triangle> faces = new List<Triangle>();
        
        // Calculate face normals pointing outward
        Vector3 center = (points[extremePoints[0]] + points[extremePoints[1]] + 
                         points[extremePoints[2]] + points[extremePoints[3]]) / 4;
        
        // Create faces ensuring consistent orientation
        Triangle face1 = new Triangle(
            extremePoints[0], extremePoints[1], extremePoints[2],
            CalculateNormal(points[extremePoints[0]], points[extremePoints[1]], points[extremePoints[2]])
        );
        
        Triangle face2 = new Triangle(
            extremePoints[0], extremePoints[2], extremePoints[3],
            CalculateNormal(points[extremePoints[0]], points[extremePoints[2]], points[extremePoints[3]])
        );
        
        Triangle face3 = new Triangle(
            extremePoints[0], extremePoints[3], extremePoints[1],
            CalculateNormal(points[extremePoints[0]], points[extremePoints[3]], points[extremePoints[1]])
        );
        
        Triangle face4 = new Triangle(
            extremePoints[1], extremePoints[3], extremePoints[2],
            CalculateNormal(points[extremePoints[1]], points[extremePoints[3]], points[extremePoints[2]])
        );
        
        // Ensure all normals point outward
        EnsureOutwardNormal(face1, center, points);
        EnsureOutwardNormal(face2, center, points);
        EnsureOutwardNormal(face3, center, points);
        EnsureOutwardNormal(face4, center, points);
        
        faces.Add(face1);
        faces.Add(face2);
        faces.Add(face3);
        faces.Add(face4);
        
        return faces;
    }

    private Vector3 CalculateNormal(Vector3 a, Vector3 b, Vector3 c)
    {
        return Vector3.Cross(b - a, c - a).normalized;
    }

    private void EnsureOutwardNormal(Triangle face, Vector3 center, Vector3[] points)
    {
        Vector3 faceCenter = (points[face.v1] + points[face.v2] + points[face.v3]) / 3;
        Vector3 toCenter = center - faceCenter;
        
        if (Vector3.Dot(face.normal, toCenter) > 0)
        {
            // Normal points inward, flip it
            face.normal = -face.normal;
            int temp = face.v1;
            face.v1 = face.v2;
            face.v2 = temp;
        }
    }

    private float PointFaceDistance(Vector3 point, Triangle face, Vector3[] vertices)
    {
        Vector3 v1 = vertices[face.v1];
        return Vector3.Dot(point - v1, face.normal);
    }

    private float PointLineDistance(Vector3 point, Vector3 lineStart, Vector3 lineEnd)
    {
        Vector3 line = lineEnd - lineStart;
        Vector3 pointVector = point - lineStart;
        Vector3 projection = Vector3.Project(pointVector, line);
        return Vector3.Distance(pointVector, projection);
    }

    private void FindHorizonEdges(List<Triangle> faces, List<int> visibleFaces, List<Edge> horizon)
    {
        // Same as before
        List<Edge> allEdges = new List<Edge>();
        foreach (int f in visibleFaces)
        {
            allEdges.Add(new Edge(faces[f].v1, faces[f].v2));
            allEdges.Add(new Edge(faces[f].v2, faces[f].v3));
            allEdges.Add(new Edge(faces[f].v3, faces[f].v1));
        }
        
        for (int i = 0; i < allEdges.Count; i++)
        {
            bool isDuplicate = false;
            for (int j = 0; j < allEdges.Count; j++)
            {
                if (i == j) continue;
                if (allEdges[i].Equals(allEdges[j]))
                {
                    isDuplicate = true;
                    break;
                }
            }
            
            if (!isDuplicate)
                horizon.Add(allEdges[i]);
        }
    }

    private class Triangle
    {
        public int v1, v2, v3;
        public Vector3 normal;
        public List<int> outsidePoints = new List<int>();
        
        public Triangle(int v1, int v2, int v3, Vector3 normal)
        {
            this.v1 = v1;
            this.v2 = v2;
            this.v3 = v3;
            this.normal = normal;
        }
    }

    private class Edge
    {
        public int v1, v2;
        
        public Edge(int v1, int v2)
        {
            if (v1 <= v2)
            {
                this.v1 = v1;
                this.v2 = v2;
            }
            else
            {
                this.v1 = v2;
                this.v2 = v1;
            }
        }
        
        public override bool Equals(object obj)
        {
            if (obj is Edge other)
                return v1 == other.v1 && v2 == other.v2;
            return false;
        }
        
        public override int GetHashCode()
        {
            return v1.GetHashCode() ^ v2.GetHashCode();
        }
    }

    void OnRenderImage(RenderTexture srcRenderTex, RenderTexture outRenderTex)
    {
        if (points.Length == 0) {
            Graphics.Blit(srcRenderTex, outRenderTex);
            return;
        }

        var camera = Camera.current;

        var world_to_model = Matrix4x4.Scale(new Vector3(1, -1, 1)) * Target.worldToLocalMatrix;

        {
            var local_camera_pos = world_to_model.MultiplyPoint3x4(camera.transform.position);
            using var closest = new NativeArray<uint>(1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            new FindClosest() { target = local_camera_pos, positions = points, closest = closest }.Schedule().Complete();
            blitMat.SetInt("_start_index", (int)closest[0]);
        }

        {
            blitMat.SetMatrix("_Camera2WorldMatrix", world_to_model * camera.cameraToWorldMatrix);
            blitMat.SetMatrix("_InverseProjectionMatrix", camera.projectionMatrix.inverse);
            blitMat.SetFloat("_FisheyeFOV", fisheye_fov);

            blitMat.SetTexture("_positions_tex", positions_tex);
            blitMat.SetTexture("_adjacency_tex", adjacency_tex);
            blitMat.SetTexture("_adjacency_diff_tex", adjacency_diff_tex);
            blitMat.SetTexture("_attr_tex", attr_tex);
            blitMat.SetTexture("_convex_hull_tex", convex_hull_tex);
            
            // Update the show unbounded cells parameter
            blitMat.SetInt("_ShowUnboundedCells", showUnboundedCells ? 1 : 0);

            Graphics.Blit(srcRenderTex, outRenderTex, blitMat);
        }
    }

    // Existing job structs remain the same
    [BurstCompile]
    struct FillPointsDataJob : IJobParallelFor
    {
        public PropertyView x;
        public PropertyView y;
        public PropertyView z;
        public PropertyView adj_offset;
        [WriteOnly] public NativeArray<float4> points;

        public void Execute(int index)
        {
            points[index] = new float4(
                x.Get<float>(index),
                y.Get<float>(index),
                z.Get<float>(index),
                adj_offset.Get<float>(index));
        }
    }

    [BurstCompile]
    struct FillColorDataJob : IJobParallelFor
    {
        public int stride;
        public PropertyView r;
        public PropertyView g;
        public PropertyView b;
        public PropertyView density;

        [WriteOnly] public NativeSlice<half4> attributes;

        public void Execute(int index)
        {
            attributes[index] = new half4(
                math.half(r.Get<byte>(index) * (1.0 / (float)byte.MaxValue)),
                math.half(g.Get<byte>(index) * (1.0 / (float)byte.MaxValue)),
                math.half(b.Get<byte>(index) * (1.0 / (float)byte.MaxValue)),
                math.half(density.Get<float>(index))
            );
        }
    }

    [BurstCompile]
    public struct ReadUintJob : IJobParallelFor
    {
        [ReadOnly] public PropertyView view;
        [WriteOnly] public NativeArray<uint> target;

        public void Execute(int index)
        {
            target[index] = view.Get<uint>(index);
        }
    }

    [BurstCompile]
    public struct BuildAdjDiff : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float4> positions;
        [ReadOnly] public NativeArray<uint> adjacency;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<half4> adjacency_diff;

        public void Execute(int index)
        {
            float4 cell_data = positions[index];

            int adj_from = (int)(index > 0 ? math.asuint(positions[index - 1].w) : 0);
            int adj_to = (int)math.asuint(cell_data.w);

            for (int a = adj_from; a < adj_to; a++) {
                int adj = (int)adjacency[a];
                float3 adj_pos = positions[adj].xyz;
                float3 adj_diff = adj_pos - cell_data.xyz;

                adjacency_diff[a] = math.half4(new float4(adj_diff, 0));
            }
        }
    }

    [BurstCompile]
    public struct FindClosest : IJob
    {
        public float3 target;
        [ReadOnly] public NativeArray<float4> positions;
        [WriteOnly] public NativeArray<uint> closest;

        public void Execute()
        {
            var closest_dist = float.MaxValue;
            var closest_index = 0;
            for (var i = 0; i < positions.Length; i++) {
                float4 cell_data = positions[i];
                var dist = math.distancesq(cell_data.xyz, target);
                if (dist < closest_dist) {
                    closest_dist = dist;
                    closest_index = i;
                }
            }
            closest[0] = (uint)closest_index;
        }
    }
}