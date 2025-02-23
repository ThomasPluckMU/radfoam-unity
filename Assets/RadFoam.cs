
using Ply;
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

    private const int SH_DEGREE = 3;
    private const int SH_DEGREE_MAX = 3;
    private const int SH_DIM = (SH_DEGREE + 1) * (SH_DEGREE + 1);
    private const int FIND_CLOSEST_THREADS_PER_GROUP = 1024;
    private const float BYTE_INV = 1.0f / byte.MaxValue;

    void Start()
    {
        for (int i = 1; i <= SH_DEGREE_MAX; i++) {
            var kw = new LocalKeyword(radfoamShader, "SH_DEGREE_" + i);
            if (i == SH_DEGREE) {
                radfoamShader.EnableKeyword(kw);
            } else {
                radfoamShader.DisableKeyword(kw);
            }
        }

        var vertex_element = Data.Element("vertex");
        var adjacency_element = Data.Element("adjacency");
        var count = vertex_element.count;
        var adjacency_count = adjacency_element.count;
        var sh_count = SH_DIM * 3; // one for each rgb channel

        var x = vertex_element.Property("x");
        var y = vertex_element.Property("y");
        var z = vertex_element.Property("z");
        var density = vertex_element.Property("density");
        var adjacency_offset = vertex_element.Property("adjacency_offset");
        var adjacency = adjacency_element.Property("adjacency");
        var red = vertex_element.Property("red");
        var green = vertex_element.Property("green");
        var blue = vertex_element.Property("blue");
        var sh_props = new Property[sh_count - 3]; // base-color-props are stored separately
        for (int s = 0; s < sh_props.Length; s++) {
            sh_props[s] = vertex_element.Property("color_sh_" + s);
        }

        var positions = new float4[count];
        positions_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 4);
        var adjacency_offsets = new uint[count];
        adjacency_offset_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(uint));
        var adjacencies = new uint[adjacency_count];
        adjacency_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency_count, sizeof(uint));
        var shs = new float[count * sh_count];
        shs_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * sh_count);

        for (var i = 0; i < count; i++) {
            positions[i] = new float4(x.as_float(i), -y.as_float(i), z.as_float(i), density.as_float(i));
            adjacency_offsets[i] = adjacency_offset.as_uint(i);

            var sh_base_index = i * sh_count;
            shs[sh_base_index + 0] = (float)(red.as_byte(i) * BYTE_INV);
            shs[sh_base_index + 1] = (float)(green.as_byte(i) * BYTE_INV);
            shs[sh_base_index + 2] = (float)(blue.as_byte(i) * BYTE_INV);
            for (int s = 0; s < sh_props.Length; s++) {
                shs[sh_base_index + s + 3] = (float)sh_props[s].as_float(i);
            }
        }
        for (var i = 0; i < adjacency_count; i++) {
            adjacencies[i] = adjacency.as_uint(i);
        }

        positions_buffer.SetData(positions);
        adjacency_offset_buffer.SetData(adjacency_offsets);
        adjacency_buffer.SetData(adjacencies);
        shs_buffer.SetData(shs);


        {
            var reduction_group_count = Mathf.CeilToInt(count / (float)FIND_CLOSEST_THREADS_PER_GROUP);
            closest_index_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, reduction_group_count, sizeof(uint));
            tmp_distance_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, reduction_group_count, sizeof(float));
        }

        // calculate direction vectors between adjacent cells
        {
            adjacency_diff_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency_count, 4 * 4);
            var kernel = radfoamShader.FindKernel("BuildAdjDiff");
            radfoamShader.SetInt("_Count", count);
            radfoamShader.SetBuffer(kernel, "_start_index", closest_index_buffer);
            radfoamShader.SetBuffer(kernel, "_positions", positions_buffer);
            radfoamShader.SetBuffer(kernel, "_adjacency_offset", adjacency_offset_buffer);
            radfoamShader.SetBuffer(kernel, "_adjacency", adjacency_buffer);
            radfoamShader.SetBuffer(kernel, "_adjacency_diff_uav", adjacency_diff_buffer);
            radfoamShader.Dispatch(kernel, Mathf.CeilToInt(count / 1024f), 1, 1);
        }
    }


    void Update()
    {
        fisheye_fov = Mathf.Clamp(fisheye_fov + Input.mouseScrollDelta.y * -4, 10, 120);
    }

    void OnRenderImage(RenderTexture srcRenderTex, RenderTexture outRenderTex)
    {
        var camera = Camera.current;

        // find closest point to camera
        {
            var kernel = closestShader.FindKernel("FindClosestPosition");

            var local_camera_pos = Target.worldToLocalMatrix.MultiplyPoint3x4(camera.transform.position);
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
            var tmp = RenderTexture.GetTemporary(descriptor);

            var kernel = radfoamShader.FindKernel("RadFoam");
            radfoamShader.SetTexture(kernel, "_srcTex", srcRenderTex);
            radfoamShader.SetTexture(kernel, "_outTex", tmp);
            radfoamShader.SetTextureFromGlobal(kernel, "_CameraDepth", "_CameraDepthTexture");
            radfoamShader.SetMatrix("_Camera2WorldMatrix", Target.worldToLocalMatrix * camera.cameraToWorldMatrix);
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
}
