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
    
    [Tooltip("Override the bounding box settings from the PLY file")]
    public bool overrideBoundingBox = false;
    public Vector3 boundingBoxCenter = Vector3.zero;
    public Vector3 boundingBoxSize = Vector3.one;
    public Quaternion boundingBoxRotation = Quaternion.identity;

    private Material blitMat;
    private Texture2D positions_tex;
    private Texture2D attr_tex;
    private Texture2D adjacency_tex;
    private Texture2D adjacency_diff_tex;
    private NativeArray<float4> points; // store this for finding the closest cell to the camera on the CPU

    private bool _HasBoundingBox = false;
    private Vector3 _BoundingBoxCenter = Vector3.zero;
    private Vector3 _BoundingBoxSize = Vector3.one;
    private Quaternion _BoundingBoxRotation = Quaternion.identity;
    private Texture2D[] boundaryTextures;
    private bool hasBoundaryTextures = false;

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

    void Update()
    {
        fisheye_fov = Mathf.Clamp(fisheye_fov + Input.mouseScrollDelta.y * -4, 10, 120);
    }

    public void Load()
    {
        // First, check if the PlyData has TSR information
        InitializeBoundingBox();
        
        using var model = Data.Load();

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

        points = new NativeArray<float4>(vertex_tex_size, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
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
    
    void InitializeBoundingBox()
    {
        // If we're overriding the bounding box settings from the inspector, use those
        if (overrideBoundingBox)
        {
            _HasBoundingBox = true;
            _BoundingBoxCenter = boundingBoxCenter;
            _BoundingBoxSize = boundingBoxSize;
            _BoundingBoxRotation = boundingBoxRotation;
            Debug.Log("Using manual bounding box settings");
        }
        // Otherwise, check if the PlyData has TSR information
        else if (Data != null && Data.HasTSRData)
        {
            _HasBoundingBox = true;
            _BoundingBoxCenter = Data.Translation;
            _BoundingBoxSize = Data.Scale;
            _BoundingBoxRotation = Data.Rotation;
            Debug.Log($"Using bounding box from PLY file: Center={_BoundingBoxCenter}, Size={_BoundingBoxSize}");
        }
        else
        {
            _HasBoundingBox = false;
            Debug.Log("No bounding box information available");
        }
        
        // For debugging, show bounding box settings in editor view
        if (_HasBoundingBox)
        {
            // Update inspector values to reflect what's being used
            boundingBoxCenter = _BoundingBoxCenter;
            boundingBoxSize = _BoundingBoxSize;
            boundingBoxRotation = _BoundingBoxRotation;
        }

        // Check for boundary textures
        if (Data != null && Data.BoundaryTextures != null && Data.BoundaryTextures.Length > 0)
        {
            boundaryTextures = Data.BoundaryTextures;
            hasBoundaryTextures = true;
            Debug.Log($"Found {boundaryTextures.Length} boundary textures in PLY data");
        }
        else
        {
            hasBoundaryTextures = false;
            Debug.Log("No boundary textures available");
        }
    }
    
    void OnGUI()
    {
        // Show FOV info
        GUI.Label(new Rect(10, 50, 200, 30), $"FOV: {fisheye_fov}° (Scroll to adjust)");
        
        // Show bounding box info if available
        if (_HasBoundingBox)
        {
            GUI.Label(new Rect(10, 80, 300, 30), $"Bounding Box: {(_HasBoundingBox ? "Active" : "Inactive")}");
        }
    }
    
    #if UNITY_EDITOR
    void OnDrawGizmos()
    {
        // Draw bounding box in the editor for visualization
        if (_HasBoundingBox && Application.isEditor)
        {
            Gizmos.color = new Color(0.5f, 1f, 0.5f, 0.3f);
            Matrix4x4 oldMatrix = Gizmos.matrix;
            
            // Create a matrix that represents the bounding box transformation
            Matrix4x4 boxMatrix = Matrix4x4.TRS(_BoundingBoxCenter, _BoundingBoxRotation, _BoundingBoxSize);
            Gizmos.matrix = boxMatrix;
            
            // Draw a cube representing the bounding box (centered at origin with size 1)
            Gizmos.DrawCube(Vector3.zero, Vector3.one);
            
            // Draw wireframe
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
            
            // Restore original matrix
            Gizmos.matrix = oldMatrix;
        }
    }
    #endif

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

            // Set bounding box TSR data if available
            blitMat.SetInt("_HasBoundingBox", _HasBoundingBox ? 1 : 0);
            
            if (_HasBoundingBox)
            {
                // Set individual TSR components
                blitMat.SetVector("_BoundingBoxCenter", _BoundingBoxCenter);
                blitMat.SetVector("_BoundingBoxSize", _BoundingBoxSize);
                
                // Set rotation as a matrix for easier use in shader
                Matrix4x4 rotationMatrix = Matrix4x4.Rotate(_BoundingBoxRotation);
                blitMat.SetMatrix("_BoundingBoxRotation", rotationMatrix);
                
                // Combined TRS matrix and its inverse for ray intersection calculations
                Matrix4x4 boundingBoxMatrix = Matrix4x4.TRS(_BoundingBoxCenter, _BoundingBoxRotation, Vector3.one);
                Matrix4x4 invBoundingBoxMatrix = boundingBoxMatrix.inverse;
                
                blitMat.SetMatrix("_BoundingBoxTRS", boundingBoxMatrix);
                blitMat.SetMatrix("_InvBoundingBoxTRS", invBoundingBoxMatrix);
            }

            blitMat.SetTexture("_positions_tex", positions_tex);
            blitMat.SetTexture("_adjacency_tex", adjacency_tex);
            blitMat.SetTexture("_adjacency_diff_tex", adjacency_diff_tex);
            blitMat.SetTexture("_attr_tex", attr_tex);

            blitMat.SetInt("_NumCells", points.Length);
            
            if (hasBoundaryTextures && boundaryTextures != null)
            {
                blitMat.SetInt("_HasBoundaryTextures", 1);
                for (int i = 0; i < boundaryTextures.Length && i < 6; i++)
                {
                    if (boundaryTextures[i] != null)
                    {
                        // Set each face texture (+X, -X, +Y, -Y, +Z, -Z)
                        blitMat.SetTexture($"_BoundaryTexture{i}", boundaryTextures[i]);
                    }
                }
            }
            else
            {
                blitMat.SetInt("_HasBoundaryTextures", 0);
            }

            Graphics.Blit(srcRenderTex, outRenderTex, blitMat);
        }
    }

    // Job structs remain the same
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