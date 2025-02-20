
using Ply;
using UnityEngine;

public class RadFoam : MonoBehaviour
{
    public PlyData Data;

    public ComputeShader radfoamShader;

    private Vector3[] positions;
    private GraphicsBuffer positions_buffer;
    private GraphicsBuffer attributes_buffer;
    private GraphicsBuffer adjacency_offset_buffer;
    private GraphicsBuffer adjacency_buffer;

    void Start()
    {
        var vertex = Data.Element("vertex");

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

        positions = new Vector3[count];
        var colors = new Color[count];
        var adjacency_offsets = new uint[count + 1];


        Debug.Log(density.as_float(0)); Debug.Log(density.as_float(22)); Debug.Log(density.as_float(3));

        for (var i = 0; i < count; i++) {
            positions[i] = new Vector3(x.as_float(i), y.as_float(i), z.as_float(i));
            colors[i] = new Vector4(red.as_byte(i), green.as_byte(i), blue.as_byte(i)) * (1.0f / byte.MaxValue)
                + new Vector4(0, 0, 0, density.as_float(i));
            adjacency_offsets[i] = adjacency_offset.as_uint(i);
        }

        for (var i = 0; i < 256; i++) {
            // Debug.Log(adjacency_offsets[i]);
            // Debug.Log(positions[i]);
        }
        adjacency_offsets[count] = (uint)adjacency_count;

        var adjacencies = new uint[adjacency_count];
        for (var i = 0; i < adjacency_count; i++) {
            adjacencies[i] = adjacency.as_uint(i);
        }

        positions_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 3);
        attributes_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 4);
        adjacency_offset_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count + 1, sizeof(uint));
        adjacency_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency_count, sizeof(uint));
        positions_buffer.SetData(positions);
        attributes_buffer.SetData(colors);
        adjacency_offset_buffer.SetData(adjacency_offsets);
        adjacency_buffer.SetData(adjacencies);
    }


    void Update()
    {

    }

    void OnRenderImage(RenderTexture srcRenderTex, RenderTexture outRenderTex)
    {
        var camera = Camera.current;
        var descriptor = srcRenderTex.descriptor;
        descriptor.enableRandomWrite = true;
        var tmp = RenderTexture.GetTemporary(descriptor);

        var kernel = radfoamShader.FindKernel("RadFoam");
        radfoamShader.SetTexture(kernel, "_srcTex", srcRenderTex);
        radfoamShader.SetTexture(kernel, "_outTex", tmp);
        radfoamShader.SetMatrix("_Camera2WorldMatrix", camera.cameraToWorldMatrix);
        radfoamShader.SetMatrix("_InverseProjectionMatrix", camera.projectionMatrix.inverse);

        {
            // lol
            var index = 0;
            var view_pos = camera.transform.position;
            var closest = float.MaxValue;
            for (var i = 0; i < positions.Length; i++) {
                var dist = (view_pos - positions[i]).sqrMagnitude;
                if (dist < closest) {
                    closest = dist;
                    index = i;
                }
            }
            // Debug.Log(index);
            radfoamShader.SetInt("_start_index", index);
        }

        radfoamShader.SetBuffer(kernel, "_positions", positions_buffer);
        radfoamShader.SetBuffer(kernel, "_attributes", attributes_buffer);
        radfoamShader.SetBuffer(kernel, "_adjacency_offset", adjacency_offset_buffer);
        radfoamShader.SetBuffer(kernel, "_adjacency", adjacency_buffer);

        int gridSizeX = Mathf.CeilToInt(srcRenderTex.width / 8.0f);
        int gridSizeY = Mathf.CeilToInt(srcRenderTex.height / 8.0f);
        radfoamShader.Dispatch(kernel, gridSizeX, gridSizeY, 1);

        Graphics.Blit(tmp, outRenderTex);
        RenderTexture.ReleaseTemporary(tmp);
    }

    void OnDestroy()
    {
        positions_buffer.Release();
        attributes_buffer.Release();
        adjacency_offset_buffer.Release();
        adjacency_buffer.Release();
    }
}
