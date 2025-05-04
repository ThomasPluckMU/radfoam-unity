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
        // Size of spatial grid cells for partitioning space
        private const float SPATIAL_GRID_CELL_SIZE = 1.0f;
        // Step size for hierarchical sampling (coarse grid)
        private const int HIERARCHICAL_STEP = 16;
        // Maximum candidates to check from spatial grid
        private const int MAX_CANDIDATES_PER_CELL = 32;

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
            GetFaceAxes(boundingBoxFace, boundingBoxSize, out Vector3 uAxis, out Vector3 vAxis);
            uAxis = localToWorld.MultiplyVector(uAxis);
            vAxis = localToWorld.MultiplyVector(vAxis);

            ReportProgress(progressCallback, 0.2f, "Loading vertex data...");

            // Load vertex positions and adjacency offsets
            var points = new NativeArray<float4>(vertex_count, Allocator.TempJob);
            var pointsJobHandle = new FillPointsDataJob
            {
                x = vertex_element.property_view("x"),
                y = vertex_element.property_view("y"),
                z = vertex_element.property_view("z"),
                adj_offset = vertex_element.property_view("adjacency_offset"),
                points = points
            }.Schedule(vertex_count, 64);

            // Load adjacency data
            var adjacency = new NativeArray<uint>(adjacency_count, Allocator.TempJob);
            var adjacencyJobHandle = new ReadUintJob
            {
                view = adjacency_element.property_view("adjacency"),
                target = adjacency
            }.Schedule(adjacency_count, 64);

            // Wait for data loading to complete
            JobHandle.CompleteAll(ref pointsJobHandle, ref adjacencyJobHandle);

            // Store 3D positions for distance calculations
            var positions3D = new NativeArray<float3>(vertex_count, Allocator.TempJob);

            // Build spatial grid for acceleration
            Dictionary<Vector3Int, List<int>> spatialGrid = new Dictionary<Vector3Int, List<int>>();

            for (int i = 0; i < vertex_count; i++)
            {
                // Get vertex position
                float4 point = points[i];
                Vector3 position = new Vector3(point.x, point.y, point.z);

                // Store 3D position for distance calculations
                positions3D[i] = new float3(position.x, position.y, position.z);

                // Add to spatial grid
                Vector3Int gridCell = WorldToGridCell(position, SPATIAL_GRID_CELL_SIZE);
                if (!spatialGrid.TryGetValue(gridCell, out var cellList))
                {
                    cellList = new List<int>();
                    spatialGrid[gridCell] = cellList;
                }
                cellList.Add(i);
            }

            // Create texture data array
            var textureData = new NativeArray<int>(resolution * resolution, Allocator.TempJob);

            ReportProgress(progressCallback, 0.4f, "Creating hierarchical sampling grid...");

            // Create a coarse grid for hierarchical sampling
            int coarseGridSize = resolution / HIERARCHICAL_STEP + 1;
            int[,] coarseGrid = new int[coarseGridSize, coarseGridSize];

            // Fill coarse grid with global search (exhaustive but only at coarse level)
            for (int cy = 0; cy < coarseGridSize; cy++)
            {
                for (int cx = 0; cx < coarseGridSize; cx++)
                {
                    int x = cx * HIERARCHICAL_STEP;
                    int y = cy * HIERARCHICAL_STEP;

                    if (x >= resolution) x = resolution - 1;
                    if (y >= resolution) y = resolution - 1;

                    float2 pixelUV = new float2(x / (float)(resolution - 1), y / (float)(resolution - 1));
                    Vector3 pixelWorldPos = WorldPositionFromUV(pixelUV, worldPlanePoint, uAxis, vAxis);
                    float3 pixelPos = new float3(pixelWorldPos.x, pixelWorldPos.y, pixelWorldPos.z);

                    // Find closest cell via spatial grid (faster than full linear search)
                    int closestCellId = FindClosestCellViaSpatialGrid(pixelPos, positions3D, spatialGrid);
                    coarseGrid[cx, cy] = closestCellId;
                }
            }

            ReportProgress(progressCallback, 0.5f, "Initializing first column with hierarchical sampling...");

            // Initialize first column using hierarchical grid + adjacency
            for (int y = 0; y < resolution; y++)
            {
                int pixelIndex = y * resolution;

                // Calculate world position of the current pixel
                float2 pixelUV = new float2(0, y / (float)(resolution - 1));
                Vector3 pixelWorldPos = WorldPositionFromUV(pixelUV, worldPlanePoint, uAxis, vAxis);
                float3 pixelPos = new float3(pixelWorldPos.x, pixelWorldPos.y, pixelWorldPos.z);

                // Get closest coarse grid cell
                int cy = y / HIERARCHICAL_STEP;
                if (cy >= coarseGridSize) cy = coarseGridSize - 1;

                // Start with coarse grid cell as initial guess
                int seedCellId = coarseGrid[0, cy];
                float closestDistSq = float.MaxValue;

                if (seedCellId >= 0 && seedCellId < positions3D.Length)
                {
                    closestDistSq = math.distancesq(positions3D[seedCellId], pixelPos);
                }

                // Get candidates from spatial grid
                Vector3Int gridCell = WorldToGridCell(pixelWorldPos, SPATIAL_GRID_CELL_SIZE);
                List<int> candidates = GetSpatialNeighborCandidates(gridCell, spatialGrid);

                // Check all candidates
                foreach (int candidateId in candidates)
                {
                    if (candidateId < 0 || candidateId >= positions3D.Length)
                        continue;

                    float distSq = math.distancesq(positions3D[candidateId], pixelPos);
                    if (distSq < closestDistSq)
                    {
                        closestDistSq = distSq;
                        seedCellId = candidateId;
                    }
                }

                // If y > 0, also check adjacency from cell above
                if (y > 0)
                {
                    int prevCellId = textureData[(y - 1) * resolution];

                    if (prevCellId >= 0 && prevCellId < points.Length)
                    {
                        // Check distance to previous cell
                        float distSq = math.distancesq(positions3D[prevCellId], pixelPos);
                        if (distSq < closestDistSq)
                        {
                            closestDistSq = distSq;
                            seedCellId = prevCellId;
                        }

                        // Check adjacency of previous cell
                        float adjOffset = points[prevCellId].w;
                        float nextAdjOffset = (prevCellId < points.Length - 1) ?
                            points[prevCellId + 1].w : adjacency.Length;

                        int adjFrom = math.asint(adjOffset);
                        int adjTo = math.asint(nextAdjOffset);

                        for (int a = adjFrom; a < adjTo; a++)
                        {
                            if (a < 0 || a >= adjacency.Length)
                                continue;

                            int adjCellId = (int)adjacency[a];

                            float adjDistSq = math.distancesq(positions3D[adjCellId], pixelPos);
                            if (adjDistSq < closestDistSq)
                            {
                                closestDistSq = adjDistSq;
                                seedCellId = adjCellId;
                            }
                        }
                    }
                }

                textureData[pixelIndex] = seedCellId;
            }

            ReportProgress(progressCallback, 0.6f, "Scanning rows in parallel with enhanced propagation...");

            // Process rows in parallel with enhanced algorithm
            var scanJobHandle = new EnhancedScanRowsJob
            {
                resolution = resolution,
                points = points,
                positions3D = positions3D,
                adjacency = adjacency,
                textureData = textureData,
                planePoint = worldPlanePoint,
                uAxis = uAxis,
                vAxis = vAxis,
                coarseGridSize = coarseGridSize,
                hierarchicalStep = HIERARCHICAL_STEP
            }.Schedule(resolution, 1);

            scanJobHandle.Complete();

            ReportProgress(progressCallback, 0.8f, "Verification pass for boundary regions...");

            // Verification pass: For pixels near cell boundaries, double-check with spatial grid
            var verifyJobHandle = new VerifyBoundariesJob
            {
                resolution = resolution,
                points = points,
                positions3D = positions3D,
                adjacency = adjacency,
                textureData = textureData,
                planePoint = worldPlanePoint,
                uAxis = uAxis,
                vAxis = vAxis
            }.Schedule(resolution * resolution, 256);

            verifyJobHandle.Complete();

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

        // Convert world position to spatial grid cell
        private static Vector3Int WorldToGridCell(Vector3 position, float cellSize)
        {
            return new Vector3Int(
                Mathf.FloorToInt(position.x / cellSize),
                Mathf.FloorToInt(position.y / cellSize),
                Mathf.FloorToInt(position.z / cellSize)
            );
        }

        // Get candidate cells from spatial grid (including neighbors)
        private static List<int> GetSpatialNeighborCandidates(
            Vector3Int gridCell,
            Dictionary<Vector3Int, List<int>> spatialGrid)
        {
            List<int> candidates = new List<int>();

            // Check 3x3x3 neighborhood
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        Vector3Int neighborCell = new Vector3Int(
                            gridCell.x + x,
                            gridCell.y + y,
                            gridCell.z + z
                        );

                        if (spatialGrid.TryGetValue(neighborCell, out var cellList))
                        {
                            // Limit candidates per cell to avoid excessive checking
                            for (int i = 0; i < Math.Min(cellList.Count, MAX_CANDIDATES_PER_CELL); i++)
                            {
                                candidates.Add(cellList[i]);
                            }
                        }
                    }
                }
            }

            return candidates;
        }

        // Find closest cell using spatial grid acceleration
        private static int FindClosestCellViaSpatialGrid(
            float3 position,
            NativeArray<float3> positions3D,
            Dictionary<Vector3Int, List<int>> spatialGrid)
        {
            Vector3Int gridCell = WorldToGridCell(new Vector3(position.x, position.y, position.z), SPATIAL_GRID_CELL_SIZE);
            List<int> candidates = GetSpatialNeighborCandidates(gridCell, spatialGrid);

            int closestCellId = -1;
            float closestDistSq = float.MaxValue;

            foreach (int candidateId in candidates)
            {
                if (candidateId < 0 || candidateId >= positions3D.Length)
                    continue;

                float distSq = math.distancesq(positions3D[candidateId], position);
                if (distSq < closestDistSq)
                {
                    closestDistSq = distSq;
                    closestCellId = candidateId;
                }
            }

            return closestCellId;
        }

        public static Texture2D RemapBoundaryTexture(
            Texture2D texture,
            Dictionary<int, int> oldToNewIndex,
            int resolution
        )
        {
            // Create a new texture with the same resolution
            Texture2D newTexture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);

            // Get all pixels from the original texture
            Color32[] pixels = texture.GetPixels32();
            Color32[] newPixels = new Color32[pixels.Length];

            // Process each pixel to remap indexes
            for (int i = 0; i < pixels.Length; i++)
            {
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
        private struct EnhancedScanRowsJob : IJobParallelFor
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

            // Hierarchical sampling parameters
            public int coarseGridSize;
            public int hierarchicalStep;

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
                    Vector3 pixelWorldPos = WorldPositionFromUV(pixelUV, planePoint, uAxis, vAxis);
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

                        int adjFrom = math.asint(adjOffset);
                        int adjTo = math.asint(nextAdjOffset);

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

                    // Also check the cell above (if y > 0) for better propagation
                    if (y > 0)
                    {
                        int abovePixelIndex = (y - 1) * resolution + x;
                        int aboveCellId = textureData[abovePixelIndex];

                        if (aboveCellId >= 0 && aboveCellId < positions3D.Length)
                        {
                            float aboveDistSq = math.distancesq(positions3D[aboveCellId], pixelPos);
                            if (aboveDistSq < closestDistSq)
                            {
                                closestDistSq = aboveDistSq;
                                closestCellId = aboveCellId;
                            }

                            // Check adjacency from cell above
                            float adjOffset = points[aboveCellId].w;
                            float nextAdjOffset = (aboveCellId < points.Length - 1) ?
                                points[aboveCellId + 1].w : adjacency.Length;

                            int adjFrom = math.asint(adjOffset);
                            int adjTo = math.asint(nextAdjOffset);

                            for (int a = adjFrom; a < adjTo; a++)
                            {
                                if (a < 0 || a >= adjacency.Length)
                                    continue;

                                int adjCellId = (int)adjacency[a];

                                float adjDistSq = math.distancesq(positions3D[adjCellId], pixelPos);
                                if (adjDistSq < closestDistSq)
                                {
                                    closestDistSq = adjDistSq;
                                    closestCellId = adjCellId;
                                }
                            }
                        }
                    }

                    // For pixels near coarse grid points, check their neighbors as well
                    // This helps connect isolated regions and prevents topology errors
                    int cx = x / hierarchicalStep;
                    int cy = y / hierarchicalStep;

                    if (cx < coarseGridSize && cy < coarseGridSize)
                    {
                        bool nearCoarsePoint = (x % hierarchicalStep < 2 || x % hierarchicalStep > hierarchicalStep - 2) &&
                                              (y % hierarchicalStep < 2 || y % hierarchicalStep > hierarchicalStep - 2);

                        if (nearCoarsePoint)
                        {
                            // Check the coarse grid point as well
                            int coarseCellId = textureData[cy * hierarchicalStep * resolution + cx * hierarchicalStep];

                            if (coarseCellId >= 0 && coarseCellId < positions3D.Length)
                            {
                                float coarseDistSq = math.distancesq(positions3D[coarseCellId], pixelPos);
                                if (coarseDistSq < closestDistSq)
                                {
                                    closestDistSq = coarseDistSq;
                                    closestCellId = coarseCellId;
                                }
                            }
                        }
                    }

                    // Store the closest cell ID
                    textureData[pixelIndex] = closestCellId;
                }
            }
        }

        [BurstCompile]
        private struct VerifyBoundariesJob : IJobParallelFor
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

            public void Execute(int index)
            {
                int x = index % resolution;
                int y = index / resolution;

                // Skip edges - we only verify interior points that have neighbors
                if (x <= 0 || x >= resolution - 1 || y <= 0 || y >= resolution - 1)
                    return;

                int cellId = textureData[index];

                // Check if this pixel might be a boundary cell
                bool isBoundary = false;
                int leftCellId = textureData[y * resolution + (x - 1)];
                int rightCellId = textureData[y * resolution + (x + 1)];
                int topCellId = textureData[(y - 1) * resolution + x];
                int bottomCellId = textureData[(y + 1) * resolution + x];

                // If any neighboring pixel has a different cell ID, this is a boundary
                if (leftCellId != cellId || rightCellId != cellId ||
                    topCellId != cellId || bottomCellId != cellId)
                {
                    isBoundary = true;
                }

                // Only do extensive verification for potential boundary cells
                if (isBoundary)
                {
                    // Calculate world position for this pixel
                    float2 pixelUV = new float2(x / (float)(resolution - 1), y / (float)(resolution - 1));
                    Vector3 pixelWorldPos = WorldPositionFromUV(pixelUV, planePoint, uAxis, vAxis);
                    float3 pixelPos = new float3(pixelWorldPos.x, pixelWorldPos.y, pixelWorldPos.z);

                    // Start with current cell
                    float closestDistSq = float.MaxValue;
                    int closestCellId = cellId;

                    if (cellId >= 0 && cellId < positions3D.Length)
                    {
                        closestDistSq = math.distancesq(positions3D[cellId], pixelPos);
                    }

                    // Gather all unique neighboring cells
                    HashSet<int> neighborCells = new HashSet<int>();
                    if (leftCellId >= 0) neighborCells.Add(leftCellId);
                    if (rightCellId >= 0) neighborCells.Add(rightCellId);
                    if (topCellId >= 0) neighborCells.Add(topCellId);
                    if (bottomCellId >= 0) neighborCells.Add(bottomCellId);

                    // Check all neighboring cells
                    foreach (int neighborId in neighborCells)
                    {
                        if (neighborId >= 0 && neighborId < positions3D.Length)
                        {
                            float distSq = math.distancesq(positions3D[neighborId], pixelPos);
                            if (distSq < closestDistSq)
                            {
                                closestDistSq = distSq;
                                closestCellId = neighborId;
                            }

                            // Check adjacency from this neighbor as well
                            if (neighborId < points.Length)
                            {
                                float adjOffset = points[neighborId].w;
                                float nextAdjOffset = (neighborId < points.Length - 1) ?
                                    points[neighborId + 1].w : adjacency.Length;

                                int adjFrom = math.asint(adjOffset);
                                int adjTo = math.asint(nextAdjOffset);

                                for (int a = adjFrom; a < adjTo; a++)
                                {
                                    if (a < 0 || a >= adjacency.Length)
                                        continue;

                                    int adjCellId = (int)adjacency[a];

                                    float adjDistSq = math.distancesq(positions3D[adjCellId], pixelPos);
                                    if (adjDistSq < closestDistSq)
                                    {
                                        closestDistSq = adjDistSq;
                                        closestCellId = adjCellId;
                                    }
                                }
                            }
                        }
                    }

                    // Update cell ID if needed
                    if (closestCellId != cellId)
                    {
                        textureData[index] = closestCellId;
                    }
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

        private static void GetFaceAxes(int face, Vector3 boundingBoxSize, out Vector3 uAxis, out Vector3 vAxis)
        {
            switch (face)
            {
                case 0: // +X
                    uAxis = new Vector3(0, 0, boundingBoxSize.z);
                    vAxis = new Vector3(0, boundingBoxSize.y, 0);
                    break;
                case 1: // -X
                    uAxis = new Vector3(0, 0, -boundingBoxSize.z);
                    vAxis = new Vector3(0, boundingBoxSize.y, 0);
                    break;
                case 2: // +Y
                    uAxis = new Vector3(boundingBoxSize.x, 0, 0);
                    vAxis = new Vector3(0, 0, boundingBoxSize.z);
                    break;
                case 3: // -Y
                    uAxis = new Vector3(boundingBoxSize.x, 0, 0);
                    vAxis = new Vector3(0, 0, -boundingBoxSize.z);
                    break;
                case 4: // +Z
                    uAxis = new Vector3(boundingBoxSize.x, 0, 0);
                    vAxis = new Vector3(0, boundingBoxSize.y, 0);
                    break;
                case 5: // -Z
                    uAxis = new Vector3(-boundingBoxSize.x, 0, 0);
                    vAxis = new Vector3(0, boundingBoxSize.y, 0);
                    break;
                default:
                    uAxis = new Vector3(boundingBoxSize.x, 0, 0);
                    vAxis = new Vector3(0, boundingBoxSize.y, 0);
                    break;
            }
        }

        // Helper method to convert UV to world position on the face
        private static Vector3 WorldPositionFromUV(float2 uv, Vector3 planePoint, Vector3 uAxis, Vector3 vAxis)
        {
            // Map UV from [0,1] to [-0.5,0.5] for each axis
            float uMapped = uv.x - 0.5f;
            float vMapped = uv.y - 0.5f;

            // Calculate world position
            return planePoint + uMapped * uAxis + vMapped * vAxis;
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