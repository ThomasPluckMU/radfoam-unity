Shader "Ply/PointCloudShader"
{
    Properties
    {
        _PointSize ("Point Size", Range(0.001, 0.1)) = 0.01
    }
    
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100
        
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            
            // Buffers from compute shader
            StructuredBuffer<float3> _Positions;
            StructuredBuffer<float4> _Colors;
            StructuredBuffer<int> _Visibility;
            StructuredBuffer<int> _Selection;
            
            // Properties
            float _PointSize;
            float4x4 _CameraToWorld;
            float4x4 _WorldToObject;
            
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                uint instanceID : SV_InstanceID;
            };
            
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float size : TEXCOORD1;
                float depth : TEXCOORD2;
            };
            
            v2f vert(appdata v)
            {
                v2f o;
                
                // Get instance data
                uint id = v.instanceID;
                
                // Skip if not visible
                if (_Visibility[id] == 0)
                {
                    o.vertex = float4(0, 0, 0, 0);
                    o.color = float4(0, 0, 0, 0);
                    return o;
                }
                
                // Get position
                float3 worldPos = _Positions[id];
                
                // Billboard quad
                float3 camPos = _WorldSpaceCameraPos;
                float3 up = float3(0, 1, 0);
                float3 camForward = normalize(camPos - worldPos);
                float3 right = normalize(cross(up, camForward));
                up = normalize(cross(camForward, right));
                
                // Position quad corners
                float pointScale = _PointSize * (1.0 + distance(camPos, worldPos) * 0.01);
                float3 vertPos = worldPos + (v.vertex.x * right + v.vertex.y * up) * pointScale;
                
                // Project
                o.vertex = UnityObjectToClipPos(float4(vertPos, 1.0));
                
                // Get color - add selection highlight
                float4 color = _Colors[id];
                if (_Selection[id] > 0)
                {
                    // Purple selection highlight
                    color = lerp(color, float4(0.6, 0.4, 0.8, 1.0), 0.6);
                }
                
                // Output data
                o.color = color;
                o.uv = v.uv;
                o.size = pointScale;
                o.depth = distance(camPos, worldPos);
                
                return o;
            }
            
            fixed4 frag(v2f i) : SV_Target
            {
                // Circular point
                float dist = length(i.uv - float2(0.5, 0.5));
                if (dist > 0.5)
                    discard;
                
                // Apply simple lighting based on position in circle
                float shade = 1.0 - smoothstep(0.0, 0.5, dist);
                return i.color * shade;
            }
            ENDCG
        }
    }
}