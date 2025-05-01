using UnityEngine;
using System.IO;
using System.Collections.Generic;
using Unity.Collections;
using System;

namespace Ply
{
    public static class PlyExporter
    {
        /// <summary>
        /// Exports point cloud to a binary PLY file
        /// </summary>
        /// <param name="path">Output file path</param>
        /// <param name="points">Array of point positions</param>
        /// <param name="colors">Array of point colors (optional, can be null)</param>
        /// <returns>True if export is successful</returns>
        public static bool ExportPointCloud(string path, Vector3[] points, Color[] colors = null)
        {
            if (points == null || points.Length == 0)
                return false;

            bool hasColors = colors != null && colors.Length == points.Length;

            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (BinaryWriter writer = new BinaryWriter(fs))
                {
                    // Write PLY header
                    WriteHeader(writer, points.Length, hasColors);

                    // Write binary data
                    for (int i = 0; i < points.Length; i++)
                    {
                        // Write position (x, y, z as float)
                        writer.Write(points[i].x);
                        writer.Write(points[i].y);
                        writer.Write(points[i].z);

                        // Write color (r, g, b as byte) if available
                        if (hasColors)
                        {
                            writer.Write((byte)(colors[i].r * 255));
                            writer.Write((byte)(colors[i].g * 255));
                            writer.Write((byte)(colors[i].b * 255));
                        }
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Error exporting PLY: {e.Message}");
                return false;
            }
        }
        
        public static bool ExportWithIndices(
            string path, 
            PlyData sourceData, 
            HashSet<int> hiddenIndices,
            bool useBoundingBox = false,
            Vector3 boundingBoxCenter = default,
            Vector3 boundingBoxSize = default,
            Quaternion boundingBoxRotation = default,
            Action<float, string> progressCallback = null)
        {
            if (sourceData == null)
            {
                Debug.LogError("Null PlyData source provided to exporter");
                return false;
            }

            try
            {
                using (Model model = sourceData.Load())
                {
                    // Get element views
                    ElementView vertexView = model.element_view("vertex");
                    bool hasAdjacency = false;
                    ElementView adjacencyElement;
                    
                    try
                    {
                        adjacencyElement = model.element_view("adjacency");
                        hasAdjacency = true;
                    }
                    catch (ArgumentException)
                    {
                        Debug.Log("No adjacency element found in PLY data");
                        adjacencyElement = default;
                    }
                    
                    int totalVertices = vertexView.count;
                    if (totalVertices == 0)
                    {
                        Debug.LogError("No vertices found in PLY data");
                        return false;
                    }

                    ReportProgress(progressCallback, 0f, "Analyzing PLY data structure...");
                    
                    // Collect all property names in vertex element
                    List<string> propertyNames = new List<string>();
                    Dictionary<string, DataType> propertyTypes = new Dictionary<string, DataType>();
                    
                    foreach (var element in sourceData.Elements)
                    {
                        if (element.name == "vertex")
                        {
                            foreach (var property in element.properties)
                            {
                                propertyNames.Add(property.name);
                                propertyTypes[property.name] = property.data_type;
                            }
                            break;
                        }
                    }
                    
                    // Create property views for all vertex properties
                    Dictionary<string, PropertyView> propertyViews = new Dictionary<string, PropertyView>();
                    foreach (string propName in propertyNames)
                    {
                        try
                        {
                            propertyViews[propName] = vertexView.property_view(propName);
                        }
                        catch (ArgumentException)
                        {
                            Debug.LogWarning($"Failed to access property: {propName}");
                        }
                    }
                    
                    // Ensure we have position properties for bounding box filtering
                    PropertyView xView = vertexView.property_view("x");
                    PropertyView yView = vertexView.property_view("y");
                    PropertyView zView = vertexView.property_view("z");
                    
                    // Setup bounding box filtering if enabled
                    Bounds localBoundingBox = default;
                    Matrix4x4 worldToLocal = default;
                    
                    if (useBoundingBox)
                    {
                        ReportProgress(progressCallback, 0.05f, "Setting up bounding box filter...");
                        worldToLocal = Matrix4x4.TRS(boundingBoxCenter, boundingBoxRotation, Vector3.one).inverse;
                        localBoundingBox = new Bounds(Vector3.zero, boundingBoxSize);
                    }
                    
                    // Create a combined set of excluded points (hidden + outside bounding box)
                    HashSet<int> excludedPoints = new HashSet<int>(hiddenIndices);
                    
                    if (useBoundingBox)
                    {
                        ReportProgress(progressCallback, 0.1f, "Calculating bounding box exclusions...");
                        
                        for (int i = 0; i < totalVertices; i++)
                        {
                            if (i % 10000 == 0)
                            {
                                float progress = 0.1f + 0.1f * ((float)i / totalVertices);
                                ReportProgress(progressCallback, progress, $"Filtering point {i} of {totalVertices}...");
                            }
                            
                            // Skip if already excluded
                            if (excludedPoints.Contains(i))
                                continue;
                            
                            // Check if point is outside the bounding box
                            Vector3 position = new Vector3(
                                xView.Get<float>(i),
                                yView.Get<float>(i),
                                zView.Get<float>(i)
                            );
                            
                            Vector3 localPoint = worldToLocal.MultiplyPoint3x4(position);
                            if (!localBoundingBox.Contains(localPoint))
                            {
                                excludedPoints.Add(i);
                            }
                        }
                    }
                    
                    // Count how many points will be in the output (not excluded)
                    int outputCount = totalVertices - excludedPoints.Count;
                    
                    ReportProgress(progressCallback, 0.2f, $"Exporting {outputCount} of {totalVertices} points...");
                    
                    // Create mapping from old indices to new indices
                    Dictionary<int, int> oldToNewIndex = new Dictionary<int, int>();
                    int newIndex = 0;
                    
                    for (int i = 0; i < totalVertices; i++)
                    {
                        if (!excludedPoints.Contains(i))
                        {
                            oldToNewIndex[i] = newIndex++;
                        }
                    }
                    
                    // Process adjacency data if available
                    List<uint> newAdjacencyData = new List<uint>();
                    List<uint> newAdjacencyOffsets = new List<uint>(); 
                    
                    if (hasAdjacency)
                    {
                        PropertyView adjacencyView = adjacencyElement.property_view("adjacency");
                        PropertyView offsetView = vertexView.property_view("adjacency_offset");
                        
                        ReportProgress(progressCallback, 0.3f, "Processing adjacency data...");
                        
                        uint currentOffset = 0;
                        
                        for (int i = 0; i < totalVertices; i++)
                        {
                            if (excludedPoints.Contains(i)) continue;
                            
                            // Get adjacency range for this vertex
                            uint adjOffsetStart = i > 0 ? offsetView.Get<uint>(i - 1) : 0;
                            uint adjOffsetEnd = offsetView.Get<uint>(i);
                            
                            // Store the new offset
                            newAdjacencyOffsets.Add(currentOffset);
                            
                            // Process adjacencies
                            for (uint adj = adjOffsetStart; adj < adjOffsetEnd; adj++)
                            {
                                uint adjVertex = adjacencyView.Get<uint>((int)adj);
                                
                                // Only include adjacencies to vertices that aren't excluded
                                if (!excludedPoints.Contains((int)adjVertex) && oldToNewIndex.TryGetValue((int)adjVertex, out int newAdjIndex))
                                {
                                    newAdjacencyData.Add((uint)newAdjIndex);
                                    currentOffset++;
                                }
                            }
                        }
                    }
                    
                    // Open the file for writing
                    using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                    using (BinaryWriter writer = new BinaryWriter(fs))
                    {
                        ReportProgress(progressCallback, 0.4f, "Writing PLY header...");
                        
                        // Write PLY header
                        writer.Write(System.Text.Encoding.ASCII.GetBytes("ply\n"));
                        writer.Write(System.Text.Encoding.ASCII.GetBytes("format binary_little_endian 1.0\n"));
                        
                        // Add bounding box info as a comment
                        if (useBoundingBox)
                        {
                            string bbCenterStr = $"{boundingBoxCenter.x} {boundingBoxCenter.y} {boundingBoxCenter.z}";
                            string bbSizeStr = $"{boundingBoxSize.x} {boundingBoxSize.y} {boundingBoxSize.z}";
                            string bbRotStr = $"{boundingBoxRotation.x} {boundingBoxRotation.y} {boundingBoxRotation.z} {boundingBoxRotation.w}";
                            
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"comment boundingbox_center {bbCenterStr}\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"comment boundingbox_size {bbSizeStr}\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"comment boundingbox_rotation {bbRotStr}\n"));
                        }
                        
                        // Add a dedicated bounding_box element 
                        if (useBoundingBox)
                        {
                            writer.Write(System.Text.Encoding.ASCII.GetBytes("element bounding_box 1\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes("property float center_x\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes("property float center_y\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes("property float center_z\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes("property float size_x\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes("property float size_y\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes("property float size_z\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes("property float rotation_x\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes("property float rotation_y\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes("property float rotation_z\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes("property float rotation_w\n"));
                        }
                        
                        // Write vertex element
                        writer.Write(System.Text.Encoding.ASCII.GetBytes($"element vertex {outputCount}\n"));
                        
                        // Write all vertex properties
                        foreach (string propName in propertyNames)
                        {
                            string typeStr = propertyTypes[propName] switch
                            {
                                DataType.Float => "float",
                                DataType.UChar => "uchar",
                                DataType.UInt => "uint",
                                _ => "float" // Default
                            };
                            
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"property {typeStr} {propName}\n"));
                        }
                        
                        // Write adjacency element
                        if (hasAdjacency && newAdjacencyData.Count > 0)
                        {
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"element adjacency {newAdjacencyData.Count}\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes("property uint adjacency\n"));
                        }
                        
                        writer.Write(System.Text.Encoding.ASCII.GetBytes("end_header\n"));
                        
                        // Write bounding box data
                        if (useBoundingBox)
                        {
                            writer.Write(boundingBoxCenter.x);
                            writer.Write(boundingBoxCenter.y);
                            writer.Write(boundingBoxCenter.z);
                            writer.Write(boundingBoxSize.x);
                            writer.Write(boundingBoxSize.y);
                            writer.Write(boundingBoxSize.z);
                            writer.Write(boundingBoxRotation.x);
                            writer.Write(boundingBoxRotation.y);
                            writer.Write(boundingBoxRotation.z);
                            writer.Write(boundingBoxRotation.w);
                        }
                        
                        // Write vertex data
                        ReportProgress(progressCallback, 0.5f, "Writing vertex data...");
                        
                        for (int i = 0; i < totalVertices; i++)
                        {
                            if (excludedPoints.Contains(i)) continue;
                            
                            if (i % 50000 == 0)
                            {
                                float progress = 0.5f + 0.4f * ((float)i / totalVertices);
                                ReportProgress(progressCallback, progress, $"Writing vertex {i} of {totalVertices}...");
                            }
                            
                            // Write all properties for this vertex
                            foreach (string propName in propertyNames)
                            {
                                if (!propertyViews.TryGetValue(propName, out PropertyView view))
                                    continue;
                                
                                // Special handling for adjacency_offset - use our recomputed values
                                if (propName == "adjacency_offset" && hasAdjacency)
                                {
                                    int newOffsetIndex = oldToNewIndex[i];
                                    writer.Write(newAdjacencyOffsets[newOffsetIndex]);
                                    continue;
                                }
                                
                                // Write property value based on its type
                                DataType type = propertyTypes[propName];
                                switch (type)
                                {
                                    case DataType.Float:
                                        writer.Write(view.Get<float>(i));
                                        break;
                                    case DataType.UChar:
                                        writer.Write(view.Get<byte>(i));
                                        break;
                                    case DataType.UInt:
                                        writer.Write(view.Get<uint>(i));
                                        break;
                                }
                            }
                        }
                        
                        // Write adjacency data
                        if (hasAdjacency && newAdjacencyData.Count > 0)
                        {
                            ReportProgress(progressCallback, 0.9f, "Writing adjacency data...");
                            
                            foreach (uint adj in newAdjacencyData)
                            {
                                writer.Write(adj);
                            }
                        }
                    }
                    
                    ReportProgress(progressCallback, 1.0f, "Export complete!");
                    Debug.Log($"Exported {outputCount} points to {path}");
                    return true;
                }
            }
            catch (Exception e)
            {
                ReportProgress(progressCallback, 1.0f, "Export failed!");
                Debug.LogError($"Error in PLY export: {e.Message}\n{e.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Exports filtered point cloud from a PlyData source using a bounding box filter
        /// </summary>
        public static bool ExportBoundingBoxFiltered(string path, PlyData sourceData, 
            Vector3 center, Vector3 size, Quaternion rotation, bool useFilter)
        {
            if (sourceData == null)
            {
                Debug.LogError("Null PlyData source provided to exporter");
                return false;
            }

            try
            {
                using (Model model = sourceData.Load())
                {
                    // Make sure we have access to vertex data
                    ElementView vertexView;
                    try
                    {
                        vertexView = model.element_view("vertex");
                    }
                    catch (ArgumentException e)
                    {
                        Debug.LogError($"Failed to access vertex element: {e.Message}");
                        return false;
                    }
                    
                    int totalVertices = vertexView.count;
                    if (totalVertices == 0)
                    {
                        Debug.LogError("No vertices found in PLY data");
                        return false;
                    }

                    // Set up position properties
                    PropertyView xView, yView, zView;
                    try
                    {
                        xView = vertexView.property_view("x");
                        yView = vertexView.property_view("y");
                        zView = vertexView.property_view("z");
                    }
                    catch (ArgumentException e)
                    {
                        Debug.LogError($"Failed to access position properties: {e.Message}");
                        return false;
                    }

                    // Try to get color properties
                    PropertyView redView = default;
                    PropertyView greenView = default;
                    PropertyView blueView = default;

                    try
                    {
                        redView = vertexView.property_view("red");
                        greenView = vertexView.property_view("green");
                        blueView = vertexView.property_view("blue");
                    }
                    catch (ArgumentException)
                    {
                        Debug.Log("No color properties found in PLY data, exporting positions only");
                    }

                    // Get adjacency data for Voronoi cells
                    ElementView adjacencyElement = default;
                    PropertyView adjacencyOffsetView = default;
                    PropertyView adjacencyView = default;
                    bool hasAdjacencyData = false;

                    try
                    {
                        adjacencyElement = model.element_view("adjacency");
                        adjacencyOffsetView = vertexView.property_view("adjacency_offset");
                        adjacencyView = adjacencyElement.property_view("adjacency");
                        hasAdjacencyData = true;
                        Debug.Log("Found adjacency data for Voronoi cells");
                    }
                    catch (ArgumentException)
                    {
                        Debug.LogError("No adjacency data found - will only filter by point position");
                    }

                    // Create bounding box filter
                    Matrix4x4 worldToLocal = Matrix4x4.identity;
                    Bounds localBounds = new Bounds(Vector3.zero, Vector3.one);
                    
                    if (useFilter)
                    {
                        worldToLocal = Matrix4x4.TRS(center, rotation, Vector3.one).inverse;
                        localBounds = new Bounds(Vector3.zero, size);
                    }

                    // First pass: collect points that are directly inside the bounding box
                    HashSet<int> relevantPoints = new HashSet<int>();
                    HashSet<int> hiddenPoints = new HashSet<int>();
                    
                    if (useFilter)
                    {
                        for (int i = 0; i < totalVertices; i++)
                        {
                            if (i % 10000 == 0)
                            {
                                Debug.Log($"Processing point {i} of {totalVertices}");
                            }
                            
                            Vector3 position = new Vector3(
                                xView.Get<float>(i),
                                yView.Get<float>(i),
                                zView.Get<float>(i)
                            );
                            
                            // Check if point is inside the bounding box
                            Vector3 localPoint = worldToLocal.MultiplyPoint3x4(position);
                            if (localBounds.Contains(localPoint))
                            {
                                relevantPoints.Add(i);
                            }
                            else
                            {
                                // Temporary add to hidden points - we'll remove some later
                                hiddenPoints.Add(i);
                            }
                        }

                        // Second pass: for points inside the box, check their neighbors
                        if (hasAdjacencyData)
                        {
                            Debug.Log($"Found {relevantPoints.Count} points inside box, now checking neighbors...");
                            HashSet<int> borderPoints = new HashSet<int>();
                            
                            foreach (int pointIndex in relevantPoints)
                            {
                                // Get adjacency offset and count
                                uint adjOffset = adjacencyOffsetView.Get<uint>(pointIndex);
                                
                                // Determine number of neighbors by looking at next point's offset or total count
                                uint nextOffset;
                                if (pointIndex < totalVertices - 1)
                                {
                                    nextOffset = adjacencyOffsetView.Get<uint>(pointIndex + 1);
                                }
                                else
                                {
                                    nextOffset = (uint)adjacencyElement.count;
                                }
                                
                                int neighborCount = (int)(nextOffset - adjOffset);
                                
                                // Get the position of this point
                                Vector3 pointPos = new Vector3(
                                    xView.Get<float>(pointIndex),
                                    yView.Get<float>(pointIndex),
                                    zView.Get<float>(pointIndex)
                                );
                                
                                // Check each neighbor
                                for (int j = 0; j < neighborCount; j++)
                                {
                                    uint neighborIndex = adjacencyView.Get<uint>((int)adjOffset + j);
                                    
                                    // Skip if already marked as relevant
                                    if (relevantPoints.Contains((int)neighborIndex))
                                        continue;
                                        
                                    // Get neighbor position
                                    Vector3 neighborPos = new Vector3(
                                        xView.Get<float>((int)neighborIndex),
                                        yView.Get<float>((int)neighborIndex),
                                        zView.Get<float>((int)neighborIndex)
                                    );
                                    
                                    // For Voronoi cells, the boundary between two cells lies on the perpendicular
                                    // bisector of the line connecting their centroids.
                                    // We'll check if this boundary intersects our bounding box
                                    
                                    // For simplicity, we'll check if the line segment between the points
                                    // intersects the bounding box - this is a conservative approximation
                                    if (LineIntersectsBounds(pointPos, neighborPos, center, size, rotation))
                                    {
                                        borderPoints.Add((int)neighborIndex);
                                        // Remove from hidden points if it was there
                                        hiddenPoints.Remove((int)neighborIndex);
                                    }
                                }
                            }
                            
                            Debug.Log($"Found {borderPoints.Count} additional border points");
                            // Add border points to relevant points
                            relevantPoints.UnionWith(borderPoints);
                        }
                        
                        // Now hiddenPoints should only contain points that are:
                        // 1. Outside the bounding box
                        // 2. Not neighbors to points inside the box
                        Debug.Log($"Filtering out {hiddenPoints.Count} points of {totalVertices} total points");
                    }
                    
                    return ExportWithIndices(path, sourceData, hiddenPoints);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error in PLY export: {e.Message}\n{e.StackTrace}");
                return false;
            }
        }

        // Helper method to check if a line segment between two points intersects a bounding box
        private static bool LineIntersectsBounds(Vector3 p1, Vector3 p2, Vector3 boxCenter, Vector3 boxSize, Quaternion boxRotation)
        {
            // Transform points to local space of the bounding box
            Matrix4x4 worldToLocal = Matrix4x4.TRS(boxCenter, boxRotation, Vector3.one).inverse;
            Vector3 localP1 = worldToLocal.MultiplyPoint3x4(p1);
            Vector3 localP2 = worldToLocal.MultiplyPoint3x4(p2);
            
            // Create local bounds
            Bounds localBounds = new Bounds(Vector3.zero, boxSize);
            
            // Quick check - if either point is inside, the line intersects
            if (localBounds.Contains(localP1) || localBounds.Contains(localP2))
                return true;
            
            // Check if the line segment intersects any of the 6 faces of the box
            Vector3 min = localBounds.min;
            Vector3 max = localBounds.max;
            
            // Check each axis
            return 
                IntersectsAxisAlignedPlane(localP1, localP2, 0, min.x, min.y, max.y, min.z, max.z) || // Left face
                IntersectsAxisAlignedPlane(localP1, localP2, 0, max.x, min.y, max.y, min.z, max.z) || // Right face
                IntersectsAxisAlignedPlane(localP1, localP2, 1, min.y, min.x, max.x, min.z, max.z) || // Bottom face
                IntersectsAxisAlignedPlane(localP1, localP2, 1, max.y, min.x, max.x, min.z, max.z) || // Top face
                IntersectsAxisAlignedPlane(localP1, localP2, 2, min.z, min.x, max.x, min.y, max.y) || // Back face
                IntersectsAxisAlignedPlane(localP1, localP2, 2, max.z, min.x, max.x, min.y, max.y);   // Front face
        }

        // Helper method to check if a line segment intersects an axis-aligned plane
        private static bool IntersectsAxisAlignedPlane(Vector3 p1, Vector3 p2, int axis, float planeCoord,
            float minA, float maxA, float minB, float maxB)
        {
            // Get the coordinates for the two other axes
            int axisA = (axis + 1) % 3;
            int axisB = (axis + 2) % 3;
            
            // If both points are on the same side of the plane, no intersection
            if ((p1[axis] < planeCoord && p2[axis] < planeCoord) || 
                (p1[axis] > planeCoord && p2[axis] > planeCoord))
                return false;
            
            // Compute the intersection point on the plane
            float t = (planeCoord - p1[axis]) / (p2[axis] - p1[axis]);
            
            // Check if t is between 0 and 1 (line segment, not full line)
            if (t < 0 || t > 1)
                return false;
            
            // Compute the intersection coordinates on the other two axes
            float intersectA = p1[axisA] + t * (p2[axisA] - p1[axisA]);
            float intersectB = p1[axisB] + t * (p2[axisB] - p1[axisB]);
            
            // Check if the intersection point is within the bounds of the face
            return intersectA >= minA && intersectA <= maxA && 
                intersectB >= minB && intersectB <= maxB;
        }

        private static void WriteHeader(BinaryWriter writer, int pointCount, bool includeColors)
        {
            string header =
                "ply\n" +
                "format binary_little_endian 1.0\n" +
                "element vertex " + pointCount + "\n" +
                "property float x\n" +
                "property float y\n" +
                "property float z\n";

            if (includeColors)
            {
                header +=
                    "property uchar red\n" +
                    "property uchar green\n" +
                    "property uchar blue\n";
            }

            header += "end_header\n";

            // Write ASCII header
            writer.Write(System.Text.Encoding.ASCII.GetBytes(header));
        }
        
        private static void ReportProgress(Action<float, string> callback, float progress, string message)
        {
            callback?.Invoke(progress, message);
            Debug.Log($"[{progress:P0}] {message}");
        }
    }
}