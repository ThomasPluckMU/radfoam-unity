using System.Collections.Generic;
using Ply;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
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
    private GraphicsBuffer adjacency_offset_buffer;
    private GraphicsBuffer adjacency_buffer;
    private GraphicsBuffer adjacency_diff_buffer;

    private GraphicsBuffer closest_index_buffer;
    private GraphicsBuffer tmp_distance_buffer;

    private const int SH_DEGREE_MAX = 3;
    private int SH_DIM(int degree) => (degree + 1) * (degree + 1);
    private const int FIND_CLOSEST_THREADS_PER_GROUP = 1024;
    private const float BYTE_INV = 1.0f / byte.MaxValue;

    void Start()
    {
        using var model = Data.Load();
        Load(model, 3);
    }

    void Load(in Model model, int sh_degree)
    {
        var sh_count = SH_DIM(sh_degree) * 3; // one for each rgb-channel
        for (int i = 1; i <= SH_DEGREE_MAX; i++) {
            var kw = new LocalKeyword(radfoamShader, "SH_DEGREE_" + i);
            if (i == sh_degree) {
                radfoamShader.EnableKeyword(kw);
            } else {
                radfoamShader.DisableKeyword(kw);
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

        using var adjacency_offset = new NativeArray<uint>(vertex_count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        read_handles.Add(new ReadUintJob {
            view = vertex_element.property_view("adjacency_offset"),
            target = adjacency_offset
        }.Schedule(vertex_count, 512));

        using var points = new NativeArray<float4>(vertex_count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        read_handles.Add(new FillPointsDataJob {
            x = vertex_element.property_view("x"),
            y = vertex_element.property_view("y"),
            z = vertex_element.property_view("z"),
            density = vertex_element.property_view("density"),
            points = points
        }.Schedule(vertex_count, 512));

        using var attributes = new NativeArray<byte>(sizeof(float) * vertex_count * sh_count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
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
                stride = sizeof(float) * sh_count,
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
                attributes = attributes,
            };
            read_handles.Add(sh_job.Schedule(vertex_count, 512));
        }

        using (var native_handles = new NativeArray<JobHandle>(read_handles.ToArray(), Allocator.Temp)) {
            JobHandle.CompleteAll(native_handles);
        }

        positions_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertex_count, sizeof(float) * 4);
        positions_buffer.SetData(points);
        shs_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertex_count, sizeof(float) * sh_count);
        shs_buffer.SetData(attributes);
        adjacency_offset_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertex_count, sizeof(uint));
        adjacency_offset_buffer.SetData(adjacency_offset);
        adjacency_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency_count, sizeof(uint));
        adjacency_buffer.SetData(adjacency);

        {
            var reduction_group_count = Mathf.CeilToInt(vertex_count / (float)FIND_CLOSEST_THREADS_PER_GROUP);
            closest_index_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, reduction_group_count, sizeof(uint));
            tmp_distance_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, reduction_group_count, sizeof(float));
        }

        // calculate direction vectors between adjacent cells
        {
            adjacency_diff_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency_count, 4 * 4);
            var kernel = radfoamShader.FindKernel("BuildAdjDiff");
            radfoamShader.SetInt("_Count", vertex_count);
            radfoamShader.SetBuffer(kernel, "_start_index", closest_index_buffer);
            radfoamShader.SetBuffer(kernel, "_positions", positions_buffer);
            radfoamShader.SetBuffer(kernel, "_adjacency_offset", adjacency_offset_buffer);
            radfoamShader.SetBuffer(kernel, "_adjacency", adjacency_buffer);
            radfoamShader.SetBuffer(kernel, "_adjacency_diff_uav", adjacency_diff_buffer);
            radfoamShader.Dispatch(kernel, Mathf.CeilToInt(vertex_count / 1024f), 1, 1);
        }

    }

    void Update()
    {
        fisheye_fov = Mathf.Clamp(fisheye_fov + Input.mouseScrollDelta.y * -4, 10, 120);

        if (Input.GetKeyDown(KeyCode.Return)) {
            OnDestroy();
            using (var model = Model.from_file("C:/Users/Chris/Downloads/scene(3).ply")) {
                Load(model, 0);
            }
        }
    }

    void OnRenderImage(RenderTexture srcRenderTex, RenderTexture outRenderTex)
    {
        var camera = Camera.current;

        var world_to_model = Matrix4x4.Scale(new Vector3(1, -1, 1)) * Target.worldToLocalMatrix;

        // find closest point to camera
        {
            var kernel = closestShader.FindKernel("FindClosestPosition");

            var local_camera_pos = world_to_model.MultiplyPoint3x4(camera.transform.position);
            closestShader.SetVector("_TargetPosition", local_camera_pos);
            closestShader.SetInt("_Count", positions_buffer.count);
            closestShader.SetBuffer(kernel, "_positions", positions_buffer);
            closestShader.SetBuffer(kernel, "_Result", closest_index_buffer);
            closestShader.SetBuffer(kernel, "_Distance", tmp_distance_buffer);

            var count = Mathf.CeilToInt(positions_buffer.count / (float)FIND_CLOSEST_THREADS_PER_GROUP);
            closestShader.Dispatch(kernel, count, 1, 1);

            kernel = closestShader.FindKernel("FindClosestPositionStep");
            closestShader.SetBuffer(kernel, "_Result", closest_index_buffer);
            closestShader.SetBuffer(kernel, "_Distance", tmp_distance_buffer);
            closestShader.SetInt("_Count", closest_index_buffer.count);

            while (count > 1) {
                count = Mathf.CeilToInt(count / (float)FIND_CLOSEST_THREADS_PER_GROUP);
                closestShader.Dispatch(kernel, count, 1, 1);
            }
        }

        // draw
        {
            var descriptor = srcRenderTex.descriptor;
            descriptor.enableRandomWrite = true;
            descriptor.sRGB = false;
            var tmp = RenderTexture.GetTemporary(descriptor);


            var kernel = radfoamShader.FindKernel("RadFoam");
            radfoamShader.SetTexture(kernel, "_srcTex", srcRenderTex);
            radfoamShader.SetTexture(kernel, "_outTex", tmp);
            radfoamShader.SetTextureFromGlobal(kernel, "_CameraDepth", "_CameraDepthTexture");
            radfoamShader.SetMatrix("_Camera2WorldMatrix", world_to_model * camera.cameraToWorldMatrix);
            radfoamShader.SetMatrix("_InverseProjectionMatrix", camera.projectionMatrix.inverse);
            radfoamShader.SetFloat("_FisheyeFOV", fisheye_fov);

            radfoamShader.SetBuffer(kernel, "_start_index", closest_index_buffer);
            radfoamShader.SetBuffer(kernel, "_positions", positions_buffer);
            radfoamShader.SetBuffer(kernel, "_shs", shs_buffer);
            radfoamShader.SetBuffer(kernel, "_adjacency_offset", adjacency_offset_buffer);
            radfoamShader.SetBuffer(kernel, "_adjacency", adjacency_buffer);
            radfoamShader.SetBuffer(kernel, "_adjacency_diff", adjacency_diff_buffer);

            int gridSizeX = Mathf.CeilToInt(srcRenderTex.width / 8.0f);
            int gridSizeY = Mathf.CeilToInt(srcRenderTex.height / 8.0f);
            radfoamShader.Dispatch(kernel, gridSizeX, gridSizeY, 1);

            Graphics.Blit(tmp, outRenderTex);
            RenderTexture.ReleaseTemporary(tmp);
        }
    }

    void OnDestroy()
    {
        positions_buffer.Release();
        // attributes_buffer.Release();
        shs_buffer.Release();
        adjacency_offset_buffer.Release();
        adjacency_buffer.Release();
        adjacency_diff_buffer.Release();

        closest_index_buffer.Release();
        tmp_distance_buffer.Release();
    }

    [BurstCompile]
    struct FillPointsDataJob : IJobParallelFor
    {
        public PropertyView x;
        public PropertyView y;
        public PropertyView z;
        public PropertyView density;
        [WriteOnly] public NativeArray<float4> points;

        public void Execute(int index)
        {
            points[index] = new float4(x.Get<float>(index), y.Get<float>(index), z.Get<float>(index), density.Get<float>(index));
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

            void write_sh(in RGBView sh)
            {
                write(sh.R.Get<float>(index), sizeof(float));
                write(sh.G.Get<float>(index), sizeof(float));
                write(sh.B.Get<float>(index), sizeof(float));
            }

            // color
            write(color.R.Get<byte>(index) * BYTE_INV, sizeof(float));
            write(color.G.Get<byte>(index) * BYTE_INV, sizeof(float));
            write(color.B.Get<byte>(index) * BYTE_INV, sizeof(float));

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
}
