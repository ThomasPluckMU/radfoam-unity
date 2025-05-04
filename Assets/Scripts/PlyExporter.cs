using UnityEngine;
using System.IO;
using System.Collections.Generic;
using System;

namespace Ply
{
    public static class PlyExporter
    {
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

            const int resolution = 4096;

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
                    
                    // Generate boundary textures if bounding box is used
                    Texture2D[] boundaryTextures = new Texture2D[6];
                    HashSet<int> boundaryCells = new HashSet<int>();
                    if (useBoundingBox)
                    {
                        ReportProgress(progressCallback, 0.35f, "Generating boundary lookup data...");
                        
                        // Generate boundary textures by calling BoundaryTextureGenerator
                        (boundaryTextures, boundaryCells) = GenerateBoundaryTextures(
                            sourceData,
                            boundingBoxCenter,
                            boundingBoxSize,
                            boundingBoxRotation,
                            resolution
                        );
                    }
                    
                    excludedPoints.SymmetricExceptWith(boundaryCells);

                    // Count how many points will be in the output (not excluded)
                    int outputCount = totalVertices - excludedPoints.Count;
                    
                    ReportProgress(progressCallback, 0.2f, $"Exporting {outputCount} of {totalVertices} points...");

                    // Create mapping from old indices to new indices
                    Dictionary<int, int> oldToNewIndex = new Dictionary<int, int>();
                    int newIndex = 0;
                    
                    for (int i = 0; i < totalVertices; i++)
                    {
                        if (!excludedPoints.Contains(i)) {
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
                            uint adjOffsetStart = i > 0 ? offsetView.Get<uint>(i-1) : 0;
                            uint adjOffsetEnd = offsetView.Get<uint>(i);
                            
                            // Process adjacencies
                            for (uint adj = adjOffsetStart; adj < adjOffsetEnd; adj++)
                            {
                                uint adjVertex = adjacencyView.Get<uint>((int)adj);
                                
                                // Only include adjacencies to vertices that aren't excluded or lies on the boundary
                                if (!excludedPoints.Contains(i) && oldToNewIndex.TryGetValue((int)adjVertex, out int newAdjIndex))
                                {
                                    newAdjacencyData.Add((uint)newAdjIndex);
                                    currentOffset++;
                                }
                            }

                            // Store the new offset
                            newAdjacencyOffsets.Add(currentOffset);
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
                        
                        // Add metadata for bounding box if used
                        if (useBoundingBox)
                        {
                            // Add comment for bounding box center (translation)
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"comment bb_center_x {boundingBoxCenter.x}\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"comment bb_center_y {boundingBoxCenter.y}\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"comment bb_center_z {boundingBoxCenter.z}\n"));
                            
                            // Add comment for bounding box size (scale)
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"comment bb_size_x {boundingBoxSize.x}\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"comment bb_size_y {boundingBoxSize.y}\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"comment bb_size_z {boundingBoxSize.z}\n"));
                            
                            // Add comment for bounding box rotation
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"comment bb_rotation_x {boundingBoxRotation.x}\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"comment bb_rotation_y {boundingBoxRotation.y}\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"comment bb_rotation_z {boundingBoxRotation.z}\n"));
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"comment bb_rotation_w {boundingBoxRotation.w}\n"));
                            
                            // Add comment for boundary texture files
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"comment boundary_texture_resolution {resolution.ToString()}\n"));
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

                        // Additionally encode the texture data directly in the PLY
                        for (int i = 0; i < boundaryTextures.Length; i++)
                        {
                            string texturePrefix = Path.GetFileNameWithoutExtension(path);
                            string textureFilePath = $"{Path.GetDirectoryName(path)}/pre_boundary_{i}.png";
                            File.WriteAllBytes(textureFilePath, boundaryTextures[i].EncodeToPNG());
                            Debug.Log($"Saved boundary texture {i} to {textureFilePath}");

                            // Remap underlying boundary textures to new scheme
                            boundaryTextures[i] = BoundaryTextureGenerator.RemapBoundaryTexture(
                                boundaryTextures[i],
                                oldToNewIndex,
                                resolution
                            );

                            textureFilePath = $"{Path.GetDirectoryName(path)}/post_boundary_{i}.png";
                            File.WriteAllBytes(textureFilePath, boundaryTextures[i].EncodeToPNG());
                            Debug.Log($"Saved boundary texture {i} to {textureFilePath}");

                            // Convert texture to bytes
                            byte[] textureBytes = boundaryTextures[i].EncodeToPNG();

                            // Encode as Base64 string and write to file as comment
                            string textureData = Convert.ToBase64String(textureBytes);
                            writer.Write(System.Text.Encoding.ASCII.GetBytes($"comment boundary_texture_{i}_data {textureData}\n"));
                        }

                        writer.Write(System.Text.Encoding.ASCII.GetBytes("end_header\n"));
                        
                        // Write vertex data
                        ReportProgress(progressCallback, 0.5f, "Writing vertex data...");
                        
                        for (int i = 0; i < totalVertices; i++)
                        {
                            if (excludedPoints.Contains(i)) continue;
                            
                            if (i % 50000 == 0)
                            {
                                float progress = 0.5f + 0.3f * ((float)i / totalVertices);
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
                            ReportProgress(progressCallback, 0.8f, "Writing adjacency data...");
                            
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
        
        // Convenience method to generate only boundary textures without exporting PLY
        public static (Texture2D[], HashSet<int>) GenerateBoundaryTextures(
            PlyData sourceData,
            Vector3 boundingBoxCenter,
            Vector3 boundingBoxSize,
            Quaternion boundingBoxRotation,
            int resolution = 1024,
            Action<float, string> progressCallback = null)
        {
            if (sourceData == default)
            {
                Debug.LogError("Null PlyData source provided to texture generator");
                throw new ArgumentNullException(nameof(sourceData));
            }
            
            try
            {
                Texture2D[] boundaryTexture = new Texture2D[6];
                HashSet<int> boundaryCells = new HashSet<int>() ;
                HashSet<int> temp = new HashSet<int>() ;

                // Call the boundary texture generator from BoundaryTexture.cs
                for (int i = 0; i < 6; i++) {
                    (boundaryTexture[i], temp) = BoundaryTextureGenerator.GenerateBoundaryTexture(
                        sourceData,
                        i,
                        boundingBoxCenter,
                        boundingBoxSize,
                        boundingBoxRotation,
                        resolution,
                        progressCallback
                    );
                    boundaryCells.UnionWith(temp);
                }

                return (boundaryTexture, boundaryCells);
            }
            catch (Exception e)
            {
                Debug.LogError($"Error generating boundary textures: {e.Message}\n{e.StackTrace}");
                throw new ArgumentNullException(nameof(sourceData));
            }
        }
        
        private static void ReportProgress(Action<float, string> callback, float progress, string message)
        {
            callback?.Invoke(progress, message);
            Debug.Log($"[{progress:P0}] {message}");
        }
    }
}