
using Ply;
using Unity.Mathematics;
using UnityEngine;

public class RadFoam : MonoBehaviour
{
    public Transform Target;
    public PlyData Data;

    public ComputeShader radfoamShader;
    public ComputeShader closestShader;

    private GraphicsBuffer positions_buffer;
    // private GraphicsBuffer attributes_buffer;
    private GraphicsBuffer shs_buffer;
    private GraphicsBuffer adjacency_offset_buffer;
    private GraphicsBuffer adjacency_buffer;
    private GraphicsBuffer adjacency_diff_buffer;

    private GraphicsBuffer closest_index_buffer;
    private GraphicsBuffer tmp_distance_buffer;

    private const int FIND_MIN_GROUP_THREADS = 512;

    void Start()
    {
        var vertex = Data.Element("vertex");
        vertex.Debug();

        var x = vertex.Property("x");
        var y = vertex.Property("y");
        var z = vertex.Property("z");

        var red = vertex.Property("red");
        var green = vertex.Property("green");
        var blue = vertex.Property("blue");
        var density = vertex.Property("density");

        var adjacency_offset = vertex.Property("adjacency_offset");

        var adjacency_element = Data.Element("adjacency");
        var adjacency = adjacency_element.Property("adjacency");

        var count = vertex.count;
        var adjacency_count = adjacency_element.count;

        var positions = new float4[count];
        // var colors = new float4[count];
        var adjacency_offsets = new uint[count];

        var shs = new float[count * 48];
        var sh_props = new Property[45];
        for (int s = 0; s < 45; s++) {
            sh_props[s] = vertex.Property("color_sh_" + s);
        }


        var byte_inv = 1.0f / byte.MaxValue;
        for (var i = 0; i < count; i++) {
            positions[i] = new float4(x.as_float(i), y.as_float(i), z.as_float(i), density.as_float(i));

            // colors[i] = new float4(
            //     red.as_byte(i) * byte_inv,
            //     green.as_byte(i) * byte_inv,
            //     blue.as_byte(i) * byte_inv,
            //     0);

            adjacency_offsets[i] = adjacency_offset.as_uint(i);

            var sh_base_index = i * 48;
            shs[sh_base_index + 0] = red.as_byte(i) * byte_inv;
            shs[sh_base_index + 1] = green.as_byte(i) * byte_inv;
            shs[sh_base_index + 2] = blue.as_byte(i) * byte_inv;
            for (int s = 0; s < 45; s++) {
                shs[sh_base_index + s + 3] = sh_props[s].as_float(i);
            }
        }

        var adjacencies = new uint[adjacency_count];
        for (var i = 0; i < adjacency_count; i++) {
            adjacencies[i] = adjacency.as_uint(i);
        }

        positions_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 4);
        // attributes_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 4);
        shs_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 48);
        adjacency_offset_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(uint));
        adjacency_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency_count, sizeof(uint));
        positions_buffer.SetData(positions);
        // attributes_buffer.SetData(colors);
        shs_buffer.SetData(shs);
        adjacency_offset_buffer.SetData(adjacency_offsets);
        adjacency_buffer.SetData(adjacencies);


        {
            var reduction_group_count = Mathf.CeilToInt(count / (float)FIND_MIN_GROUP_THREADS);
            closest_index_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, reduction_group_count, sizeof(uint));
            tmp_distance_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, reduction_group_count, sizeof(float));
        }

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

            var count = Mathf.CeilToInt(positions_buffer.count / (float)FIND_MIN_GROUP_THREADS);
            closestShader.Dispatch(kernel, count, 1, 1);

            kernel = closestShader.FindKernel("FindClosestPositionStep");
            closestShader.SetBuffer(kernel, "_Result", closest_index_buffer);
            closestShader.SetBuffer(kernel, "_Distance", tmp_distance_buffer);
            closestShader.SetInt("_Count", closest_index_buffer.count);

            while (count > 1) {
                count = Mathf.CeilToInt(count / (float)FIND_MIN_GROUP_THREADS);
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

            radfoamShader.SetBuffer(kernel, "_start_index", closest_index_buffer);
            radfoamShader.SetBuffer(kernel, "_positions", positions_buffer);
            // radfoamShader.SetBuffer(kernel, "_attributes", attributes_buffer);
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
