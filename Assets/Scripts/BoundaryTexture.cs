using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Burst;
using System;
using System.Collections.Generic;

namespace Ply
{
    public static class BoundaryTextureGenerator
    {
        /// <summary>
        /// Generates a Voronoi boundary texture for a given face of the 
        /// bounding box using a scanline approach, measuring distances to actual 3D centroids
        /// </summary>
        public static (Texture2D, HashSet<int>) GenerateBoundaryTexture(
            PlyData sourceData,
            int boundingBoxFace,
            Vector3 boundingBoxCenter,
            Vector3 boundingBoxSize,
            Quaternion boundingBoxRotation,
            int resolution = 1024,
            Action<float, string> progressCallback = null)
        {
            ReportProgress(progressCallback, 0f, "Loading PLY data...");

            HashSet<int> boundary_cells = new HashSet<int>();
            
            using var model = sourceData.Load();

            // Get vertex and adjacency elements
            var vertex_element = model.element_view("vertex");
            var adjacency_element = model.element_view("adjacency");
            int vertex_count = vertex_element.count;
            int adjacency_count = adjacency_element.count;
            
            ReportProgress(progressCallback, 0.1f, "Setting up boundary plane...");
            
            // Define the face plane in local space of the bounding box
            Vector3 planeNormal = GetFaceNormal(boundingBoxFace);
            Vector3 planePoint = Vector3.Scale(planeNormal, boundingBoxSize * 0.5f);
            
            // Transform to world space
            Matrix4x4 localToWorld = Matrix4x4.TRS(boundingBoxCenter, boundingBoxRotation, Vector3.one);
            Vector3 worldPlaneNormal = localToWorld.MultiplyVector(planeNormal).normalized;
            Vector3 worldPlanePoint = localToWorld.MultiplyPoint3x4(planePoint);
            
            // Get face axes for mapping 3D to 2D
            GetFaceAxes(boundingBoxFace, out Vector3 uAxis, out Vector3 vAxis);
            uAxis = localToWorld.MultiplyVector(uAxis);
            vAxis = localToWorld.MultiplyVector(vAxis);
            
            ReportProgress(progressCallback, 0.2f, "Loading vertex data...");
            
            // Load vertex positions and adjacency offsets
            var points = new NativeArray<float4>(vertex_count, Allocator.TempJob);
            var pointsJobHandle = new FillPointsDataJob {
                x = vertex_element.property_view("x"),
                y = vertex_element.property_view("y"),
                z = vertex_element.property_view("z"),
                adj_offset = vertex_element.property_view("adjacency_offset"),
                points = points
            }.Schedule(vertex_count, 64);
            
            // Load adjacency data
            var adjacency = new NativeArray<uint>(adjacency_count, Allocator.TempJob);
            var adjacencyJobHandle = new ReadUintJob {
                view = adjacency_element.property_view("adjacency"),
                target = adjacency
            }.Schedule(adjacency_count, 64);
            
            // Wait for data loading to complete
            JobHandle.CompleteAll(ref pointsJobHandle, ref adjacencyJobHandle);
                        
            // Identify valid centroids (those not excluded and within culling distance)
            var positions3D = new NativeArray<float3>(vertex_count, Allocator.TempJob);
            
            for (int i = 0; i < vertex_count; i++)
            {
                // Get vertex position
                float4 point = points[i];
                Vector3 position = new Vector3(point.x, point.y, point.z);
                
                // Store 3D position for distance calculations
                positions3D[i] = new float3(position.x, position.y, position.z);
            }
    
            // Create texture data array
            var textureData = new NativeArray<int>(resolution * resolution, Allocator.TempJob);
            
            ReportProgress(progressCallback, 0.5f, "Finding seed point and initializing first column...");
            
            // Create plane equation for pixel-to-world projection
            float4 planeEquation = new float4(
                worldPlaneNormal.x,
                worldPlaneNormal.y,
                worldPlaneNormal.z,
                -Vector3.Dot(worldPlaneNormal, worldPlanePoint)
            );
            
            // Find seed point (top-left corner) - this is the only exhaustive search
            int seedCellId = -1;
            float closestDistSq = float.MaxValue;
            
            // Calculate world position of top-left UV point
            float2 uvCorner = new float2(0, 0); // Top-left in UV is (0,0)
            Vector3 worldPos = WorldPositionFromUV(uvCorner, worldPlanePoint, uAxis, vAxis, boundingBoxSize);
            
            for (int i = 0; i < vertex_count; i++)
            {
                float3 pos = positions3D[i];
                float distSq = math.distancesq(worldPos, pos);
                
                if (distSq < closestDistSq)
                {
                    closestDistSq = distSq;
                    seedCellId = i;
                }
            }
            
            // Initialize first column using adjacency data
            textureData[0] = seedCellId; // Top-left corner
            
            for (int y = 1; y < resolution; y++)
            {
                int pixelIndex = y * resolution;
                int prevPixelIndex = (y - 1) * resolution;
                int prevCellId = textureData[prevPixelIndex];
                
                // Calculate world position of the current pixel
                float2 pixelUV = new float2(0, y / (float)(resolution - 1));
                Vector3 pixelWorldPos = WorldPositionFromUV(pixelUV, worldPlanePoint, uAxis, vAxis, boundingBoxSize);
                float3 pixelPos = new float3(pixelWorldPos.x, pixelWorldPos.y, pixelWorldPos.z);
                
                // Start with the cell from above
                int closestCellId = prevCellId;
                closestDistSq = float.MaxValue;
                
                if (prevCellId >= 0 && prevCellId < points.Length)
                {
                    float3 prevPos = positions3D[prevCellId];
                    closestDistSq = math.distancesq(prevPos, pixelPos);
                    
                    // Get adjacency info for the previous cell
                    float adjOffset = points[prevCellId].w;
                    float nextAdjOffset = (prevCellId < points.Length - 1) ? 
                        points[prevCellId + 1].w : adjacency.Length;
                    
                    int adjFrom = (int)adjOffset;
                    int adjTo = (int)nextAdjOffset;
                    
                    // Check adjacent cells
                    for (int a = adjFrom; a < adjTo; a++)
                    {
                        if (a < 0 || a >= adjacency.Length)
                            continue;
                            
                        int adjCellId = (int)adjacency[a];
                            
                        float3 adjPos = positions3D[adjCellId];
                        float distSq = math.distancesq(adjPos, pixelPos);
                        
                        if (distSq < closestDistSq)
                        {
                            closestDistSq = distSq;
                            closestCellId = adjCellId;
                        }
                    }
                }
                
                textureData[pixelIndex] = closestCellId;
            }
            
            ReportProgress(progressCallback, 0.6f, "Scanning rows in parallel...");
            
            // Process rows in parallel
            var scanJobHandle = new ScanRowsJob
            {
                resolution = resolution,
                points = points,
                positions3D = positions3D,
                adjacency = adjacency,
                textureData = textureData,
                planePoint = worldPlanePoint,
                uAxis = uAxis,
                vAxis = vAxis,
                boxSize = boundingBoxSize
            }.Schedule(resolution, 1);
            
            scanJobHandle.Complete();
            
            ReportProgress(progressCallback, 0.9f, "Converting texture data...");
            
            // Convert the texture data to colors
            Texture2D texture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Point;
            Color32[] pixels = new Color32[resolution * resolution];
            
            for (int i = 0; i < textureData.Length; i++)
            {
                int cellId = textureData[i];
                pixels[i] = IndexToColor(cellId);
                boundary_cells.Add(cellId);
            }
            
            // Apply the texture
            texture.SetPixels32(pixels);
            texture.Apply();
            
            // Cleanup
            points.Dispose();
            adjacency.Dispose();
            positions3D.Dispose();
            textureData.Dispose();
            
            ReportProgress(progressCallback, 1.0f, "Voronoi boundary texture generation complete!");
            return (texture, boundary_cells);
        }
        
        
        public static Texture2D RemapBoundaryTexture(
            Texture2D texture,
            Dictionary<int, int> oldToNewIndex,
            int resolution
        ) {
            // Create a new texture with the same resolution
            Texture2D newTexture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
            
            // Get all pixels from the original texture
            Color32[] pixels = texture.GetPixels32();
            Color32[] newPixels = new Color32[pixels.Length];
            
            // Process each pixel to remap indexes
            for (int i = 0; i < pixels.Length; i++) {
                // Convert color to index
                int oldIndex = ColorToIndex(pixels[i]);
                
                // Check if this index needs remapping
                int newIndex;
                oldToNewIndex.TryGetValue(oldIndex, out newIndex);
                newPixels[i] = IndexToColor(newIndex);

            }
            
            // Apply the new pixels to the texture
            newTexture.SetPixels32(newPixels);
            newTexture.Apply();
            
            return newTexture;
        }
        
        // Helper method to convert UV to world position on the face
        private static Vector3 WorldPositionFromUV(float2 uv, Vector3 planePoint, Vector3 uAxis, Vector3 vAxis, Vector3 boxSize)
        {
            // Map UV from [0,1] to [-0.5,0.5] for each axis
            float uMapped = uv.x - 0.5f;
            float vMapped = uv.y - 0.5f;
            
            // Calculate world position
            return planePoint + uMapped * boxSize.x * uAxis + vMapped * boxSize.y * vAxis;
        }
        
        [BurstCompile]
        private struct FillPointsDataJob : IJobParallelFor
        {
            [ReadOnly] public PropertyView x;
            [ReadOnly] public PropertyView y;
            [ReadOnly] public PropertyView z;
            [ReadOnly] public PropertyView adj_offset;
            [WriteOnly] public NativeArray<float4> points;

            public void Execute(int i)
            {
                points[i] = new float4(
                    x.Get<float>(i),
                    y.Get<float>(i),
                    z.Get<float>(i),
                    adj_offset.Get<float>(i)
                );
            }
        }
        
        [BurstCompile]
        private struct ReadUintJob : IJobParallelFor
        {
            [ReadOnly] public PropertyView view;
            [WriteOnly] public NativeArray<uint> target;

            public void Execute(int i)
            {
                target[i] = view.Get<uint>(i);
            }
        }
        
        [BurstCompile]
        private struct ScanRowsJob : IJobParallelFor
        {
            public int resolution;
            [ReadOnly] public NativeArray<float4> points;
            [ReadOnly] public NativeArray<float3> positions3D;
            [ReadOnly] public NativeArray<uint> adjacency;
            [NativeDisableParallelForRestriction] public NativeArray<int> textureData;
            
            // Parameters for world position calculation
            public Vector3 planePoint;
            public Vector3 uAxis;
            public Vector3 vAxis;
            public Vector3 boxSize;
            
            public void Execute(int y)
            {
                // Skip first column (already initialized)
                for (int x = 1; x < resolution; x++)
                {
                    int pixelIndex = y * resolution + x;
                    int prevPixelIndex = y * resolution + (x - 1);
                    int prevCellId = textureData[prevPixelIndex];
                    
                    // Calculate world position for this pixel
                    float2 pixelUV = new float2(x / (float)(resolution - 1), y / (float)(resolution - 1));
                    Vector3 pixelWorldPos = WorldPositionFromUV(pixelUV, planePoint, uAxis, vAxis, boxSize);
                    float3 pixelPos = new float3(pixelWorldPos.x, pixelWorldPos.y, pixelWorldPos.z);
                    
                    // Start with previous cell as closest
                    int closestCellId = prevCellId;
                    float closestDistSq = float.MaxValue;
                    
                    if (prevCellId >= 0 && prevCellId < points.Length)
                    {

                        float3 prevPos = positions3D[prevCellId];
                        closestDistSq = math.distancesq(prevPos, pixelPos);
                        
                        
                        // Get adjacency info for the previous cell
                        float adjOffset = points[prevCellId].w;
                        float nextAdjOffset = (prevCellId < points.Length - 1) ? 
                            points[prevCellId + 1].w : adjacency.Length;
                        
                        int adjFrom = (int)adjOffset;
                        int adjTo = (int)nextAdjOffset;
                        
                        // Check all adjacent cells
                        for (int a = adjFrom; a < adjTo; a++)
                        {
                            if (a < 0 || a >= adjacency.Length)
                                continue;
                                
                            int adjCellId = (int)adjacency[a];
                            
                            float3 adjPos = positions3D[adjCellId];
                            float distSq = math.distancesq(adjPos, pixelPos);
                            
                            if (distSq < closestDistSq)
                            {
                                closestDistSq = distSq;
                                closestCellId = adjCellId;
                            }
                        }
                    }
                    
                    // Store the closest cell ID
                    textureData[pixelIndex] = closestCellId;
                }
            }
        }
        
        // Helper methods
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
            // Reverse the encoding from IndexToColor
            int index = 0;
            index |= color.r << 16;
            index |= color.g << 8;
            index |= color.b;
            return index;
        }
        
        private static void ReportProgress(Action<float, string> callback, float progress, string message)
        {
            callback?.Invoke(progress, message);
            Debug.Log($"[{progress:P0}] {message}");
        }
    }
}