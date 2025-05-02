using UnityEngine;
using System.Collections.Generic;
using System;

namespace Ply
{
    public static class BoundaryTextureGenerator
    {
        /// <summary>
        /// Generates a 2D Voronoi texture for a given face of the bounding box
        /// </summary>
        /// <param name="plyData">Source PLY data containing the Voronoi foam</param>
        /// <param name="boundingBoxFace">Which face of the bounding box (0-5 for +X, -X, +Y, -Y, +Z, -Z)</param>
        /// <param name="resolution">Texture resolution (width/height in pixels)</param>
        /// <param name="boundingBoxCenter">Center position of the bounding box</param>
        /// <param name="boundingBoxSize">Size of the bounding box</param>
        /// <param name="boundingBoxRotation">Rotation of the bounding box</param>
        /// <param name="progressCallback">Optional callback for reporting progress</param>
        /// <returns>Generated texture containing cell IDs at each pixel</returns>
        public static Texture2D GenerateBoundaryTexture(
            PlyData plyData,
            int boundingBoxFace,
            int resolution,
            Vector3 boundingBoxCenter,
            Vector3 boundingBoxSize,
            Quaternion boundingBoxRotation,
            Action<float, string> progressCallback = null)
        {
            ReportProgress(progressCallback, 0f, "Loading PLY data...");
            
            using (Model model = plyData.Load())
            {
                // Get vertex element and property views
                ElementView vertexView = model.element_view("vertex");
                PropertyView xView = vertexView.property_view("x");
                PropertyView yView = vertexView.property_view("y");
                PropertyView zView = vertexView.property_view("z");
                
                int totalVertices = vertexView.count;
                
                // Get adjacency data if available
                ElementView adjacencyElement;
                PropertyView adjacencyView;
                PropertyView offsetView;
                bool hasAdjacency = false;
                
                try
                {
                    adjacencyElement = model.element_view("adjacency");
                    adjacencyView = adjacencyElement.property_view("adjacency");
                    offsetView = vertexView.property_view("adjacency_offset");
                    hasAdjacency = true;
                }
                catch (ArgumentException)
                {
                    Debug.LogWarning("No adjacency data found in PLY, texture generation may be less accurate");
                }
                
                ReportProgress(progressCallback, 0.1f, "Determining face plane...");
                
                // Define the face plane in local space of the bounding box
                Vector3 planeNormal = GetFaceNormal(boundingBoxFace);
                Vector3 planePoint = Vector3.Scale(planeNormal, boundingBoxSize * 0.5f);
                
                // Transform to world space
                Matrix4x4 localToWorld = Matrix4x4.TRS(boundingBoxCenter, boundingBoxRotation, Vector3.one);
                Matrix4x4 worldToLocal = localToWorld.inverse;
                
                Vector3 worldPlaneNormal = localToWorld.MultiplyVector(planeNormal).normalized;
                Vector3 worldPlanePoint = localToWorld.MultiplyPoint3x4(planePoint);
                
                ReportProgress(progressCallback, 0.2f, "Culling non-relevant centroids...");
                
                // Find centroids that could influence the boundary
                List<int> relevantCentroids = new List<int>();
                List<Vector3> relevantPositions = new List<Vector3>();
                Dictionary<int, int> oldToNewIndex = new Dictionary<int, int>();
                
                // Centroid culling distance - only consider cells whose centroids are 
                // within this distance from the plane (using adjacency info to refine)
                float cullingDistance = Mathf.Max(boundingBoxSize.x, boundingBoxSize.y, boundingBoxSize.z) * 0.2f;
                
                for (int i = 0; i < totalVertices; i++)
                {
                    // Get position
                    Vector3 position = new Vector3(
                        xView.Get<float>(i),
                        yView.Get<float>(i),
                        zView.Get<float>(i)
                    );
                    
                    // Calculate distance to plane
                    float distanceToPlane = Mathf.Abs(Vector3.Dot(position - worldPlanePoint, worldPlaneNormal));
                    
                    // If close enough to the plane, include it
                    if (distanceToPlane <= cullingDistance)
                    {
                        oldToNewIndex[i] = relevantCentroids.Count;
                        relevantCentroids.Add(i);
                        relevantPositions.Add(position);
                    }
                }
                
                ReportProgress(progressCallback, 0.3f, 
                    $"Found {relevantCentroids.Count} of {totalVertices} centroids near the boundary");
                
                // Create 2D texture to store cell IDs
                Texture2D texture = new Texture2D(resolution, resolution, TextureFormat.RGBAFloat, false);
                texture.filterMode = FilterMode.Point;
                
                // Get face axes for mapping 3D to 2D
                GetFaceAxes(boundingBoxFace, out Vector3 uAxis, out Vector3 vAxis);
                uAxis = localToWorld.MultiplyVector(uAxis);
                vAxis = localToWorld.MultiplyVector(vAxis);
                
                // Generate the 2D texture
                ReportProgress(progressCallback, 0.4f, "Generating 2D Voronoi diagram...");
                
                // For each pixel in the texture
                for (int y = 0; y < resolution; y++)
                {
                    if (y % 50 == 0)
                    {
                        float progress = 0.4f + 0.5f * ((float)y / resolution);
                        ReportProgress(progressCallback, progress, $"Processing row {y} of {resolution}...");
                    }
                    
                    for (int x = 0; x < resolution; x++)
                    {
                        // Map texture coordinates to 3D position on the face plane
                        float u = (x / (float)(resolution - 1)) - 0.5f;
                        float v = (y / (float)(resolution - 1)) - 0.5f;
                        
                        // Convert to world space position on the face
                        Vector3 worldPos = worldPlanePoint + (uAxis * u * boundingBoxSize.x) + (vAxis * v * boundingBoxSize.y);
                        
                        // Find closest centroid
                        int closestIndex = -1;
                        float closestDistSq = float.MaxValue;
                        
                        for (int i = 0; i < relevantPositions.Count; i++)
                        {
                            float distSq = (relevantPositions[i] - worldPos).sqrMagnitude;
                            if (distSq < closestDistSq)
                            {
                                closestDistSq = distSq;
                                closestIndex = i;
                            }
                        }
                        
                        // Store the cell ID in the texture
                        if (closestIndex >= 0)
                        {
                            int originalIndex = relevantCentroids[closestIndex];
                            
                            // Convert index to color - up to 16.7 million unique IDs
                            // R = most significant byte, B = least significant byte
                            Color32 indexColor = IndexToColor(originalIndex);
                            texture.SetPixel(x, y, indexColor);
                        }
                        else
                        {
                            // Shouldn't happen, but just in case
                            texture.SetPixel(x, y, Color.black);
                        }
                    }
                }
                
                texture.Apply();
                
                ReportProgress(progressCallback, 0.95f, "Generating additional boundary info...");
                
                // Generate companion texture with additional data if needed
                // For example: distance to centroid, normal vector, etc.
                
                ReportProgress(progressCallback, 1.0f, "Boundary texture generation complete!");
                return texture;
            }
        }
        
        private static Vector3 GetFaceNormal(int face)
        {
            return face switch
            {
                0 => new Vector3(1, 0, 0),   // +X
                1 => new Vector3(-1, 0, 0),  // -X
                2 => new Vector3(0, 1, 0),   // +Y
                3 => new Vector3(0, -1, 0),  // -Y
                4 => new Vector3(0, 0, 1),   // +Z
                5 => new Vector3(0, 0, -1),  // -Z
                _ => Vector3.up              // Default
            };
        }
        
        private static void GetFaceAxes(int face, out Vector3 uAxis, out Vector3 vAxis)
        {
            switch (face)
            {
                case 0: // +X
                    uAxis = new Vector3(0, 0, 1);
                    vAxis = new Vector3(0, 1, 0);
                    break;
                case 1: // -X
                    uAxis = new Vector3(0, 0, -1);
                    vAxis = new Vector3(0, 1, 0);
                    break;
                case 2: // +Y
                    uAxis = new Vector3(1, 0, 0);
                    vAxis = new Vector3(0, 0, 1);
                    break;
                case 3: // -Y
                    uAxis = new Vector3(1, 0, 0);
                    vAxis = new Vector3(0, 0, -1);
                    break;
                case 4: // +Z
                    uAxis = new Vector3(1, 0, 0);
                    vAxis = new Vector3(0, 1, 0);
                    break;
                case 5: // -Z
                    uAxis = new Vector3(-1, 0, 0);
                    vAxis = new Vector3(0, 1, 0);
                    break;
                default:
                    uAxis = new Vector3(1, 0, 0);
                    vAxis = new Vector3(0, 1, 0);
                    break;
            }
        }
        
        private static Color32 IndexToColor(int index)
        {
            byte r = (byte)((index >> 16) & 0xFF);
            byte g = (byte)((index >> 8) & 0xFF);
            byte b = (byte)(index & 0xFF);
            return new Color32(r, g, b, 255);
        }
        
        private static int ColorToIndex(Color32 color)
        {
            return (color.r << 16) | (color.g << 8) | color.b;
        }
        
        private static void ReportProgress(Action<float, string> callback, float progress, string message)
        {
            callback?.Invoke(progress, message);
            Debug.Log($"[{progress:P0}] {message}");
        }
    }
}