using Ply;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public class RadFoamWebGL : MonoBehaviour
{
    public PlyData Data;
    public float fisheye_fov = 60;
    public Transform Target;

    private Material blitMat;
    private Texture2D positions_tex;
    private Texture2D attr_tex;
    private Texture2D adjacency_tex;
    private Texture2D adjacency_diff_tex;

    private NativeArray<float4> points; // store this for finding the closest cell to the camera on the CPU

    // Bounding box data
    private bool _hasBoundingBox = false;
    private Vector3 _boundingBoxCenter = Vector3.zero;
    private Vector3 _boundingBoxSize = Vector3.zero;
    private Quaternion _boundingBoxRotation = Quaternion.identity;
    private Vector4[] _boundingPlanes = new Vector4[6];

    void Start()
    {
        blitMat = new Material(Shader.Find("Hidden/Custom/RadFoamShader"));
        Load();
    }

    void OnDestroy()
    {
        if (points.IsCreated)
            points.Dispose();
        Destroy(blitMat);
    }

    private bool ParseBoundingBoxFromPly(Model model)
    {
        try
        {
            // Check if the model has bounding box data 
            if (model.HasBoundingBox)
            {
                _boundingBoxCenter = model.BoundingBoxCenter;
                _boundingBoxSize = model.BoundingBoxSize;
                _boundingBoxRotation = model.BoundingBoxRotation;
                
                // Calculate the 6 bounding planes from center, size, and rotation
                CalculateBoundingPlanes();
                
                _hasBoundingBox = true;
                Debug.Log($"Loaded bounding box from PLY: Center={_boundingBoxCenter}, Size={_boundingBoxSize}, Rotation={_boundingBoxRotation.eulerAngles}");
                return true;
            }
            
            // Try to find bounding_box element
            try
            {
                ElementView boundingBoxView = model.element_view("bounding_box");
                if (boundingBoxView.count > 0)
                {
                    PropertyView centerXView = boundingBoxView.property_view("center_x");
                    PropertyView centerYView = boundingBoxView.property_view("center_y");
                    PropertyView centerZView = boundingBoxView.property_view("center_z");
                    
                    PropertyView sizeXView = boundingBoxView.property_view("size_x");
                    PropertyView sizeYView = boundingBoxView.property_view("size_y");
                    PropertyView sizeZView = boundingBoxView.property_view("size_z");
                    
                    PropertyView rotXView = boundingBoxView.property_view("rotation_x");
                    PropertyView rotYView = boundingBoxView.property_view("rotation_y");
                    PropertyView rotZView = boundingBoxView.property_view("rotation_z");
                    PropertyView rotWView = boundingBoxView.property_view("rotation_w");
                    
                    _boundingBoxCenter = new Vector3(
                        centerXView.Get<float>(0),
                        centerYView.Get<float>(0),
                        centerZView.Get<float>(0)
                    );
                    
                    _boundingBoxSize = new Vector3(
                        sizeXView.Get<float>(0),
                        sizeYView.Get<float>(0),
                        sizeZView.Get<float>(0)
                    );
                    
                    _boundingBoxRotation = new Quaternion(
                        rotXView.Get<float>(0),
                        rotYView.Get<float>(0),
                        rotZView.Get<float>(0),
                        rotWView.Get<float>(0)
                    );

                    // Calculate the bounding planes
                    CalculateBoundingPlanes();
                    
                    _hasBoundingBox = true;
                    Debug.Log($"Loaded bounding box from element: Center={_boundingBoxCenter}, Size={_boundingBoxSize}, Rotation={_boundingBoxRotation.eulerAngles}");
                    return true;
                }
            }
            catch (System.ArgumentException)
            {
                Debug.Log("No bounding_box element found in PLY data");
            }
            
            // No bounding box found
            _hasBoundingBox = false;
            return false;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"Failed to parse bounding box data: {e.Message}");
            _hasBoundingBox = false;
            return false;
        }
    }

    private bool ParseBoundingBoxFromComments(string headerText)
    {
        try
        {
            // Extract bounding box info from PLY comments
            string[] lines = headerText.Split('\n');
            string centerLine = null;
            string sizeLine = null;
            string rotationLine = null;
            
            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("comment boundingbox_center"))
                {
                    centerLine = trimmedLine.Substring("comment boundingbox_center".Length).Trim();
                }
                else if (trimmedLine.StartsWith("comment boundingbox_size"))
                {
                    sizeLine = trimmedLine.Substring("comment boundingbox_size".Length).Trim();
                }
                else if (trimmedLine.StartsWith("comment boundingbox_rotation"))
                {
                    rotationLine = trimmedLine.Substring("comment boundingbox_rotation".Length).Trim();
                }
            }
            
            if (centerLine != null && sizeLine != null && rotationLine != null)
            {
                string[] centerParts = centerLine.Split(' ');
                string[] sizeParts = sizeLine.Split(' ');
                string[] rotationParts = rotationLine.Split(' ');
                
                if (centerParts.Length >= 3 && sizeParts.Length >= 3 && rotationParts.Length >= 4)
                {
                    _boundingBoxCenter = new Vector3(
                        float.Parse(centerParts[0]),
                        float.Parse(centerParts[1]),
                        float.Parse(centerParts[2])
                    );
                    
                    _boundingBoxSize = new Vector3(
                        float.Parse(sizeParts[0]),
                        float.Parse(sizeParts[1]),
                        float.Parse(sizeParts[2])
                    );
                    
                    _boundingBoxRotation = new Quaternion(
                        float.Parse(rotationParts[0]),
                        float.Parse(rotationParts[1]),
                        float.Parse(rotationParts[2]),
                        float.Parse(rotationParts[3])
                    );

                    // Calculate the bounding planes
                    CalculateBoundingPlanes();
                    
                    _hasBoundingBox = true;
                    Debug.Log($"Loaded bounding box from comments: Center={_boundingBoxCenter}, Size={_boundingBoxSize}, Rotation={_boundingBoxRotation.eulerAngles}");
                    return true;
                }
            }
            
            _hasBoundingBox = false;
            return false;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"Failed to parse bounding box from comments: {e.Message}");
            _hasBoundingBox = false;
            return false;
        }
    }

    private void CalculateBoundingPlanes()
    {
        // Calculate the 6 bounding planes from center, size, and rotation
        Vector3[] directions = {
            Vector3.right, Vector3.left,    // X-axis planes
            Vector3.up, Vector3.down,       // Y-axis planes
            Vector3.forward, Vector3.back   // Z-axis planes
        };
        
        _boundingPlanes = new Vector4[6];
        for (int i = 0; i < 6; i++) {
            // Transform the direction by rotation
            Vector3 normal = _boundingBoxRotation * directions[i];
            Vector3 planePos = _boundingBoxCenter + normal * (_boundingBoxSize[i/2] * 0.5f);
            float distance = Vector3.Dot(normal, planePos);
            // Store as (normal.xyz, distance)
            _boundingPlanes[i] = new Vector4(normal.x, normal.y, normal.z, distance);
        }
    }

    public void Load()
    {
        using var model = Data.Load();

        // Try to parse bounding box data
        _hasBoundingBox = ParseBoundingBoxFromPly(model);

        var vertex_element = model.element_view("vertex");
        var adjacency_element = model.element_view("adjacency");
        var vertex_count = vertex_element.count;
        var adjacency_count = adjacency_element.count;

        var vertex_tex_width = 4096;
        var vertex_tex_height = Mathf.CeilToInt(vertex_count / (float)vertex_tex_width);
        var vertex_tex_size = vertex_tex_width * vertex_tex_height;

        var adj_tex_width = 4096;
        var adj_tex_height = Mathf.CeilToInt(adjacency_count / (float)adj_tex_width);
        var adj_tex_size = adj_tex_width * adj_tex_height;

        // filling buffers one after the other, to ensure we don't run out of memory on webgl
        {
            using var attributes = new NativeArray<half4>(vertex_tex_size, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            new FillColorDataJob {
                r = vertex_element.property_view("red"),
                g = vertex_element.property_view("green"),
                b = vertex_element.property_view("blue"),
                density = vertex_element.property_view("density"),
                attributes = attributes,
            }.Schedule(vertex_count, 512).Complete();
            attr_tex = new Texture2D(vertex_tex_width, vertex_tex_height, TextureFormat.RGBAHalf, 0, true, true);
            attr_tex.filterMode = FilterMode.Point;
            attr_tex.SetPixelData(attributes, 0, 0);
            attr_tex.Apply(false, true);
        }

        points = new NativeArray<float4>(vertex_tex_size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        var points_handle = new FillPointsDataJob {
            x = vertex_element.property_view("x"),
            y = vertex_element.property_view("y"),
            z = vertex_element.property_view("z"),
            adj_offset = vertex_element.property_view("adjacency_offset"),
            points = points
        }.Schedule(vertex_count, 512);

        using var adjacency = new NativeArray<uint>(adj_tex_size, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        var adjacency_handle = new ReadUintJob {
            view = adjacency_element.property_view("adjacency"),
            target = adjacency
        }.Schedule(adjacency_count, 512);

        points_handle.Complete();
        adjacency_handle.Complete();
        model.Dispose(); // we can already dispose of the ply model here, as all data is already loaded.. reducing RAM usage maybe?

        using var adj_diff = new NativeArray<half4>(adj_tex_size, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        new BuildAdjDiff { positions = points, adjacency = adjacency, adjacency_diff = adj_diff }.Schedule(vertex_count, 512).Complete();
        adjacency_diff_tex = new Texture2D(adj_tex_width, adj_tex_height, TextureFormat.RGBAHalf, 0, true, true);
        adjacency_diff_tex.filterMode = FilterMode.Point;
        adjacency_diff_tex.SetPixelData(adj_diff, 0, 0);
        adjacency_diff_tex.Apply(false, true);

        positions_tex = new Texture2D(vertex_tex_width, vertex_tex_height, TextureFormat.RGBAFloat, 0, true, true);
        positions_tex.filterMode = FilterMode.Point;
        positions_tex.SetPixelData(points, 0, 0);
        positions_tex.Apply(false, true);

        adjacency_tex = new Texture2D(adj_tex_width, adj_tex_height, TextureFormat.RFloat, 0, true, true);
        adjacency_tex.filterMode = FilterMode.Point;
        adjacency_tex.SetPixelData(adjacency, 0, 0);
        adjacency_tex.Apply(false, true);
    }

    void OnGUI()
    {
        if (_hasBoundingBox)
        {
            GUILayout.Label("Bounding Box: Active");
            GUILayout.Label($"  Center: {_boundingBoxCenter}");
            GUILayout.Label($"  Size: {_boundingBoxSize}");
            GUILayout.Label($"  Rotation: {_boundingBoxRotation.eulerAngles}");
        }
        else
        {
            GUILayout.Label("Bounding Box: Not Active");
        }
    }

    void Update()
    {
        fisheye_fov = Mathf.Clamp(fisheye_fov + Input.mouseScrollDelta.y * -4, 10, 120);
        
        // No toggle - bounding box is always active if detected in the PLY
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
            // TODO: we could use some acceleration structure for finding the closest cell.. 
            // but it's a few million points, a linear search should not be the bottleneck here (+the added memory for the acceleration structure)
            var local_camera_pos = world_to_model.MultiplyPoint3x4(camera.transform.position);
            using var closest = new NativeArray<uint>(1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            new FindClosest() { target = local_camera_pos, positions = points, closest = closest }.Schedule().Complete();
            blitMat.SetInt("_start_index", (int)closest[0]);
        }

        {
            blitMat.SetMatrix("_Camera2WorldMatrix", world_to_model * camera.cameraToWorldMatrix);
            blitMat.SetMatrix("_InverseProjectionMatrix", camera.projectionMatrix.inverse);
            blitMat.SetFloat("_FisheyeFOV", fisheye_fov);

            // Set bounding box data
            blitMat.SetInt("_HasBoundingBox", _hasBoundingBox ? 1 : 0);
            if (_hasBoundingBox)
            {
                
                for (int i = 0; i < 6; i++){
                    _boundingPlanes[i] = _boundingPlanes[i];
                }
                blitMat.SetVectorArray("_BoundingPlanes", _boundingPlanes);
            }

            blitMat.SetTexture("_positions_tex", positions_tex);
            blitMat.SetTexture("_adjacency_tex", adjacency_tex);
            blitMat.SetTexture("_adjacency_diff_tex", adjacency_diff_tex);
            blitMat.SetTexture("_attr_tex", attr_tex);

            Graphics.Blit(srcRenderTex, outRenderTex, blitMat);
        }
    }

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
                adj_offset.Get<float>(index)); // this actually contains a uint, but we just treat it as a float 
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

        // [NativeDisableParallelForRestriction] may be bad here.. especially as it may run with user provided data, which may be invalid
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