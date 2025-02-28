using System.Collections.Generic;
using Ply;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;

public class RadFoam : MonoBehaviour
{
    public float fisheye_fov = 90;

    public Transform Target;
    public PlyData Data;

    public ComputeShader radfoamShader;
    public ComputeShader closestShader;

    private GraphicsBuffer positions_buffer;
    private GraphicsBuffer shs_buffer;
    private GraphicsBuffer adjacency_buffer;
    private GraphicsBuffer adjacency_diff_buffer;

    private GraphicsBuffer closest_index_buffer;
    private GraphicsBuffer tmp_distance_buffer;

    private NativeArray<float4> points;
    private Material blitMat;
    private Texture2D positions_tex;
    // private GraphicsBuffer shs_buffer;
    // private GraphicsBuffer adjacency_buffer;
    // private GraphicsBuffer adjacency_diff_buffer;

    private const int SH_DEGREE_MAX = 3;
    private int SH_DIM(int degree) => (degree + 1) * (degree + 1);
    private const int FIND_CLOSEST_THREADS_PER_GROUP = 1024;

    void Start()
    {
        blitMat = new Material(Shader.Find("Hidden/Custom/RadFoamShader"));

        using var model = Data.Load();
        Load(model, 1);
    }

    void Load(in Model model, int sh_degree)
    {

        for (int i = 1; i <= SH_DEGREE_MAX; i++) {
            // var kw_compute = new LocalKeyword(radfoamShader, "SH_DEGREE_" + i);
            var kw_material = new LocalKeyword(blitMat.shader, "SH_DEGREE_" + i);
            if (i == sh_degree) {
                // radfoamShader.EnableKeyword(kw_compute);
                blitMat.EnableKeyword(kw_material);
            } else {
                // radfoamShader.DisableKeyword(kw_compute);
                blitMat.DisableKeyword(kw_material);
            }
        }

        var read_handles = new List<JobHandle>();

        var vertex_element = model.element_view("vertex");
        var adjacency_element = model.element_view("adjacency");
        var vertex_count = vertex_element.count;
        var adjacency_count = adjacency_element.count;

        using var adjacency = new NativeArray<uint>(adjacency_count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        read_handles.Add(new ReadUintJob {
            view = adjacency_element.property_view("adjacency"),
            target = adjacency
        }.Schedule(adjacency_count, 512));

        var width = 2048;
        var height = Mathf.CeilToInt(vertex_count / (float)width);
        var tex_size = width * height;

        points = new NativeArray<float4>(tex_size, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        read_handles.Add(new FillPointsDataJob {
            x = vertex_element.property_view("x"),
            y = vertex_element.property_view("y"),
            z = vertex_element.property_view("z"),
            adj_offset = vertex_element.property_view("adjacency_offset"),
            points = points
        }.Schedule(vertex_count, 512));

        var sh_dim = SH_DIM(sh_degree);
        var attribute_elem_size =
            sizeof(uint)                                    // color
            + sizeof(uint) * 2 * (SH_DIM(sh_degree) - 1)    // harmonics without base color
            + sizeof(uint);                                 // density
        using var attributes = new NativeArray<byte>(vertex_count * attribute_elem_size, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        {
            RGBView sh_view(int index)
            {
                string property_name(int channel) => "color_sh_" + ((index * 3) + channel);
                return new RGBView(vertex_element.property_view(property_name(0)), vertex_element.property_view(property_name(1)), vertex_element.property_view(property_name(2)));
            }

            // unity does not seem to like uninitialized NativeSlices when running a Job, even if they are not read
            var dummy = new RGBView(vertex_element.dummy_property_view(), vertex_element.dummy_property_view(), vertex_element.dummy_property_view());
            bool sh_1 = sh_degree > 0;
            bool sh_2 = sh_degree > 1;
            bool sh_3 = sh_degree > 2;

            var sh_job = new FillColorDataJob {
                degree = sh_degree,
                stride = attribute_elem_size,
                color = new RGBView(vertex_element.property_view("red"), vertex_element.property_view("green"), vertex_element.property_view("blue")),
                sh_1 = sh_1 ? sh_view(0) : dummy,
                sh_2 = sh_1 ? sh_view(1) : dummy,
                sh_3 = sh_1 ? sh_view(2) : dummy,
                sh_4 = sh_2 ? sh_view(3) : dummy,
                sh_5 = sh_2 ? sh_view(4) : dummy,
                sh_6 = sh_2 ? sh_view(5) : dummy,
                sh_7 = sh_2 ? sh_view(6) : dummy,
                sh_8 = sh_2 ? sh_view(7) : dummy,
                sh_9 = sh_3 ? sh_view(8) : dummy,
                sh_10 = sh_3 ? sh_view(9) : dummy,
                sh_11 = sh_3 ? sh_view(10) : dummy,
                sh_12 = sh_3 ? sh_view(11) : dummy,
                sh_13 = sh_3 ? sh_view(12) : dummy,
                sh_14 = sh_3 ? sh_view(13) : dummy,
                sh_15 = sh_3 ? sh_view(14) : dummy,
                density = vertex_element.property_view("density"),
                attributes = attributes,
            };
            read_handles.Add(sh_job.Schedule(vertex_count, 512));
        }

        using (var native_handles = new NativeArray<JobHandle>(read_handles.ToArray(), Allocator.Temp)) {
            JobHandle.CompleteAll(native_handles);
        }

        // var width = 2048;
        // var height = Mathf.CeilToInt(vertex_count / (float)width);
        positions_tex = new Texture2D(width, height, TextureFormat.RGBAFloat, 0, true, true);
        // Debug.Log(positions_tex.width + " " + positions_tex.height + " "+positions_tex.);
        positions_tex.filterMode = FilterMode.Point;
        // positions_tex.LoadRawTextureData(points);
        positions_tex.SetPixelData(points, 0, 0);
        positions_tex.Apply(false, true);

        // positions_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertex_count, sizeof(float) * 4);
        // positions_buffer.SetData(points);
        shs_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertex_count, attribute_elem_size);
        shs_buffer.SetData(attributes);
        adjacency_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency_count, sizeof(uint));
        adjacency_buffer.SetData(adjacency);

        // {
        //     var reduction_group_count = Mathf.CeilToInt(vertex_count / (float)FIND_CLOSEST_THREADS_PER_GROUP);
        //     closest_index_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, reduction_group_count, sizeof(uint));
        //     tmp_distance_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, reduction_group_count, sizeof(float));
        // }

        // calculate direction vectors between adjacent cells
        // {
        //     adjacency_diff_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency_count, 4 * 4); // 4*2 should be enough, but somehow it isnt
        //     var kernel = radfoamShader.FindKernel("BuildAdjDiff");
        //     radfoamShader.SetInt("_Count", vertex_count);
        //     radfoamShader.SetBuffer(kernel, "_start_index", closest_index_buffer);
        //     radfoamShader.SetBuffer(kernel, "_positions", positions_buffer);
        //     radfoamShader.SetBuffer(kernel, "_adjacency", adjacency_buffer);
        //     radfoamShader.SetBuffer(kernel, "_adjacency_diff_uav", adjacency_diff_buffer);
        //     radfoamShader.Dispatch(kernel, Mathf.CeilToInt(vertex_count / 1024f), 1, 1);
        // }


        {
            using var adj_diff = new NativeArray<float4>(adjacency_count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            new BuildAdjDiff { positions = points, adjacency = adjacency, adjacency_diff = adj_diff }.Schedule(vertex_count, 512).Complete();
            adjacency_diff_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency_count, 4 * 4); // 4*2 should be enough, but somehow it isnt
            adjacency_diff_buffer.SetData(adj_diff);
        }

    }

    void Update()
    {
        fisheye_fov = Mathf.Clamp(fisheye_fov + Input.mouseScrollDelta.y * -4, 10, 120);

        // if (Input.GetKeyDown(KeyCode.Return)) {
        //     OnDestroy();
        //     using (var model = Model.from_file("C:/Users/Chris/Downloads/scene(3).ply")) {
        //         Load(model, 3);
        //     }
        // }

        // using (var model = Data.Load()) {
        //     var vertex_element = model.element_view("vertex");
        //     var adjacency_element = model.element_view("adjacency");

        //     var x = vertex_element.property_view("x");
        //     var y = vertex_element.property_view("y");
        //     var z = vertex_element.property_view("z");
        //     var o = vertex_element.property_view("adjacency_offset");
        //     var a = adjacency_element.property_view("adjacency");

        //     Vector3 pos(int i)
        //     {
        //         return new Vector3(x.Get<float>(i), y.Get<float>(i), z.Get<float>(i));
        //     }

        //     for (var n = 0; n < 1; n++) {
        //         var zero = pos(n);
        //         var from = n > 0 ? o.Get<uint>(n - 1) : 0;
        //         var to = o.Get<uint>(n);
        //         for (var i = from; i < to; i++) {
        //             var other = a.Get<uint>((int)i);
        //             Debug.Log(other);
        //             Debug.DrawLine(zero, pos((int)other));
        //         }
        //     }
        // }

    }

    void OnRenderImage(RenderTexture srcRenderTex, RenderTexture outRenderTex)
    {
        var camera = Camera.current;

        var world_to_model = Matrix4x4.Scale(new Vector3(1, -1, 1)) * Target.worldToLocalMatrix;

        // find closest point to camera
        // {
        //     var kernel = closestShader.FindKernel("FindClosestPosition");

        //     var local_camera_pos = world_to_model.MultiplyPoint3x4(camera.transform.position);
        //     closestShader.SetVector("_TargetPosition", local_camera_pos);
        //     closestShader.SetInt("_Count", positions_buffer.count);
        //     closestShader.SetBuffer(kernel, "_positions", positions_buffer);
        //     closestShader.SetBuffer(kernel, "_Result", closest_index_buffer);
        //     closestShader.SetBuffer(kernel, "_Distance", tmp_distance_buffer);

        //     var count = Mathf.CeilToInt(positions_buffer.count / (float)FIND_CLOSEST_THREADS_PER_GROUP);
        //     closestShader.Dispatch(kernel, count, 1, 1);

        //     kernel = closestShader.FindKernel("FindClosestPositionStep");
        //     closestShader.SetBuffer(kernel, "_Result", closest_index_buffer);
        //     closestShader.SetBuffer(kernel, "_Distance", tmp_distance_buffer);
        //     closestShader.SetInt("_Count", closest_index_buffer.count);

        //     while (count > 1) {
        //         count = Mathf.CeilToInt(count / (float)FIND_CLOSEST_THREADS_PER_GROUP);
        //         closestShader.Dispatch(kernel, count, 1, 1);
        //     }
        // }

        // draw
        // {
        //     var descriptor = srcRenderTex.descriptor;
        //     descriptor.enableRandomWrite = true;
        //     descriptor.sRGB = false;
        //     var tmp = RenderTexture.GetTemporary(descriptor);

        //     var kernel = radfoamShader.FindKernel("RadFoam");
        //     radfoamShader.SetTexture(kernel, "_srcTex", srcRenderTex);
        //     radfoamShader.SetTexture(kernel, "_outTex", tmp);
        //     radfoamShader.SetTextureFromGlobal(kernel, "_CameraDepth", "_CameraDepthTexture");
        //     radfoamShader.SetMatrix("_Camera2WorldMatrix", world_to_model * camera.cameraToWorldMatrix);
        //     radfoamShader.SetMatrix("_InverseProjectionMatrix", camera.projectionMatrix.inverse);
        //     radfoamShader.SetFloat("_FisheyeFOV", fisheye_fov);

        //     radfoamShader.SetBuffer(kernel, "_start_index", closest_index_buffer);
        //     radfoamShader.SetBuffer(kernel, "_positions", positions_buffer);
        //     radfoamShader.SetBuffer(kernel, "_shs", shs_buffer);
        //     radfoamShader.SetBuffer(kernel, "_adjacency", adjacency_buffer);
        //     radfoamShader.SetBuffer(kernel, "_adjacency_diff", adjacency_diff_buffer);

        //     int gridSizeX = Mathf.CeilToInt(srcRenderTex.width / 8.0f);
        //     int gridSizeY = Mathf.CeilToInt(srcRenderTex.height / 8.0f);
        //     radfoamShader.Dispatch(kernel, gridSizeX, gridSizeY, 1);

        //     Graphics.Blit(tmp, outRenderTex);
        //     RenderTexture.ReleaseTemporary(tmp);
        // }

        {
            var local_camera_pos = world_to_model.MultiplyPoint3x4(camera.transform.position);
            using var closest = new NativeArray<uint>(1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            new FindClosest() { target = local_camera_pos, positions = points, closest = closest }.Schedule().Complete();
            blitMat.SetInt("_start_index", (int)closest[0]);
        }

        {
            // radfoamShader.SetTextureFromGlobal(kernel, "_CameraDepth", "_CameraDepthTexture");
            blitMat.SetMatrix("_Camera2WorldMatrix", world_to_model * camera.cameraToWorldMatrix);
            blitMat.SetMatrix("_InverseProjectionMatrix", camera.projectionMatrix.inverse);
            blitMat.SetFloat("_FisheyeFOV", fisheye_fov);

            blitMat.SetTexture("_positions_tex", positions_tex);
            // blitMat.SetBuffer("_positions", positions_buffer);
            blitMat.SetBuffer("_shs", shs_buffer);
            blitMat.SetBuffer("_adjacency", adjacency_buffer);
            blitMat.SetBuffer("_adjacency_diff", adjacency_diff_buffer);

            Graphics.Blit(srcRenderTex, outRenderTex, blitMat);
        }
    }

    void OnDestroy()
    {
        positions_buffer?.Release();
        shs_buffer?.Release();
        adjacency_buffer?.Release();
        adjacency_diff_buffer?.Release();

        closest_index_buffer?.Release();
        tmp_distance_buffer?.Release();

        points.Dispose();
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

    struct RGBView
    {
        public PropertyView R, G, B;

        public RGBView(PropertyView r, PropertyView g, PropertyView b)
        {
            R = r;
            G = g;
            B = b;
        }

        public RGBView(PropertyView dummy)
        {
            R = dummy;
            G = dummy;
            B = dummy;
        }
    }

    [BurstCompile]
    struct FillColorDataJob : IJobParallelFor
    {
        public int degree;
        public int stride;
        public RGBView color;
        public RGBView sh_1;
        public RGBView sh_2;
        public RGBView sh_3;
        public RGBView sh_4;
        public RGBView sh_5;
        public RGBView sh_6;
        public RGBView sh_7;
        public RGBView sh_8;
        public RGBView sh_9;
        public RGBView sh_10;
        public RGBView sh_11;
        public RGBView sh_12;
        public RGBView sh_13;
        public RGBView sh_14;
        public RGBView sh_15;
        public PropertyView density;

        [WriteOnly] public NativeSlice<byte> attributes;

        public void Execute(int index)
        {
            int offset = 0;
            var start_byte = index * stride;
            var slice = attributes;

            void write<T>(T value, int size) where T : unmanaged
            {
                slice.WriteAs<T>(start_byte + offset, value);
                offset += size;
            }

            // TODO: packing and compression could be a lot better..
            void write_sh(in RGBView sh)
            {
                write(math.half(sh.R.Get<float>(index)), 2);
                write(math.half(sh.G.Get<float>(index)), 2);
                write(math.half(sh.B.Get<float>(index)), 2);
                offset += 2;
            }

            // Density
            write(math.half(density.Get<float>(index)), 2);
            offset += 2;

            // color
            write(color.R.Get<byte>(index), 1);
            write(color.G.Get<byte>(index), 1);
            write(color.B.Get<byte>(index), 1);
            offset += 1;

            if (degree > 0) {
                write_sh(sh_1);
                write_sh(sh_2);
                write_sh(sh_3);
            }

            if (degree > 1) {
                write_sh(sh_4);
                write_sh(sh_5);
                write_sh(sh_6);
                write_sh(sh_7);
                write_sh(sh_8);
            }

            if (degree > 2) {
                write_sh(sh_9);
                write_sh(sh_10);
                write_sh(sh_11);
                write_sh(sh_12);
                write_sh(sh_13);
                write_sh(sh_14);
                write_sh(sh_15);
            }
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

        // NativeDisableParallelForRestriction may be bad here.. especially as it may run with user provided data, which may be invalid
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<float4> adjacency_diff;

        public void Execute(int index)
        {
            float4 cell_data = positions[index];

            int adj_from = (int)(index > 0 ? math.asuint(positions[index - 1].w) : 0);
            int adj_to = (int)math.asuint(cell_data.w);

            for (int a = adj_from; a < adj_to; a++) {
                int adj = (int)adjacency[a];
                float3 adj_pos = positions[adj].xyz;
                float3 adj_diff = adj_pos - cell_data.xyz;

                adjacency_diff[a] = new float4(adj_diff, 0);
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
