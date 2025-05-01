Shader "Hidden/Custom/RadFoamShader"
{
    Properties
    {
        [HideInInspector] _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off
		Lighting Off
		ZWrite Off
		ZTest Always


        Pass
        {
            CGPROGRAM
            #pragma multi_compile_local _ SH_DEGREE_1 SH_DEGREE_2 SH_DEGREE_3 

            #include "UnityCG.cginc"

            struct blit_data
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct blit_v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 ray : TEXCOORD1;
            };

            #pragma vertex blitvert
            #pragma fragment frag            

            struct Ray
            {
                float3 origin;
                float3 direction;
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            float _FisheyeFOV;
            float4x4 _Camera2WorldMatrix;
            float4x4 _InverseProjectionMatrix;
            uint _start_index;

            sampler2D _attr_tex;
            sampler2D _positions_tex;
            float4 _positions_tex_TexelSize;

            sampler2D _adjacency_diff_tex; 
            sampler2D _adjacency_tex;
            float4 _adjacency_tex_TexelSize;

            int _HasBoundingBox;
            float4 _BoundingPlanes[6];


            blit_v2f blitvert(blit_data v)
            {
                blit_v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.ray = v.uv * 2 - 1;
                o.ray.x *= _MainTex_TexelSize.z / _MainTex_TexelSize.w;
                return o;
            }

            static const float PI = 3.14159265f;
            Ray GetCameraRayFisheye(float2 uv, float fov)
            {
                Ray o;
                o.origin       = mul(_Camera2WorldMatrix, float4(0,0,0,1)).xyz;

                float theta = atan2(uv.y, uv.x);
                float phi = sqrt(dot(uv, uv)) * fov * (1.0 / 360.0) * 2 * PI;
                float3 local_dir = sin(phi) * cos(theta) * float3(1, 0, 0) 
                                 + sin(phi) * sin(theta) * float3(0, 1, 0) 
                                 + cos(phi) *  float3(0, 0, -1);
                o.direction = mul(_Camera2WorldMatrix, float4(local_dir, 0)).xyz;
                if (phi >= PI) {
                    o.direction = (float3)0;
                }
                return o;
            }

            float2 index_to_tex_buffer(uint i, float2 texel_size, uint width) {
                uint y = i / width;
                uint x = i % width;
                return float2((x + 0.5) * texel_size.x, (y + 0.5) * texel_size.y);
            }

            float4 positions_buff(uint i) {
                return tex2Dlod(_positions_tex, float4(index_to_tex_buffer(i, _positions_tex_TexelSize.xy, 4096), 0, 0));
            }

            float4 attrs_buff(uint i) {
                return tex2Dlod(_attr_tex, float4(index_to_tex_buffer(i, _positions_tex_TexelSize.xy, 4096), 0, 0));
            }

            uint adjacency_buffer(uint i) {
                return asuint(tex2Dlod(_adjacency_tex, float4(index_to_tex_buffer(i, _adjacency_tex_TexelSize.xy, 4096), 0, 0)).x);
            }

            float3 adjacency_diff_buffer(uint i) {
                return tex2Dlod(_adjacency_diff_tex, float4(index_to_tex_buffer(i, _adjacency_tex_TexelSize.xy, 4096), 0, 0)).xyz;
            }

            bool IsRayInsideBoundary(float3 currentPosition)
            {
                if (_HasBoundingBox == 0)
                    return true;
                
                [unroll(6)]
                for (int i = 0; i < 6; i++)
                {
                    float4 plane = _BoundingPlanes[i];
                    if (dot(currentPosition, plane.xyz) + plane.w < 0)
                        return false;
                }
                return true;
            }

            bool RayBoxIntersection(Ray ray, out float t_enter, out float t_exit)
            {
                t_enter = -1000000.0;
                t_exit = 1000000.0;
                
                // Check all 6 boundary planes
                for (int i = 0; i < 6; i++)
                {
                    float4 plane = _BoundingPlanes[i];
                    float denom = dot(ray.direction, plane.xyz);
                    
                    float t = -(dot(ray.origin, plane.xyz) + plane.w) / denom;
                    
                    // Update entry/exit points based on plane orientation
                    if (denom > 0) // Ray entering the boundary from outside
                        t_enter = max(t_enter, t);
                    else if (denom < 0) // Ray exiting the boundary from inside
                        t_exit = min(t_exit, t);
                    else if (dot(ray.origin, plane.xyz) + plane.w > 0) // Ray parallel to plane and outside boundary
                        return false;
                }
                
                // If entry > exit, ray misses box entirely
                return t_enter <= t_exit && t_exit > 0;
            }

            #define CHUNK_SIZE 8

            fixed4 frag (blit_v2f input) : SV_Target
            {
                float4 src_color = tex2D(_MainTex, input.uv);
                Ray ray = GetCameraRayFisheye(input.ray, _FisheyeFOV);
                if (dot(ray.direction, ray.direction) == 0) {
                    return src_color; // fisheye fov too large
                }
                ray.direction = normalize(ray.direction);

                float scene_depth = 10000;
                float3 diffs[CHUNK_SIZE];

                // Early exit: If we have a bounding box, test ray intersection with it
                if (_HasBoundingBox != 0)
                {
                    float t_enter, t_exit;
                    bool intersects = RayBoxIntersection(ray, t_enter, t_exit);
                    
                    // If ray misses the box entirely, return src_color immediately
                    // if (!intersects)
                    //      return src_color;
                    if (!intersects)
                        return float4(1,0,0,1);
                    
                    // Adjust starting t_0 to the entry point if the ray starts outside the box
                    // This effectively jumps to the boundary
                    float t_0 = t_enter;
                    
                    // Also limit scene_depth to t_exit
                    scene_depth = min(scene_depth, t_exit);
                    
                    // If ray starts outside the box, move ray origin to the entry point
                    ray.origin = ray.origin + ray.direction * t_enter;
                }

                // tracing state
                uint cell = _start_index;
                float transmittance = 1.0f;
                float3 color = float3(0, 0, 0);
                float t_0 = 0.0;

                int i = 0;
                for (; i < 200 && transmittance > 0.01; i++) {
                    float4 cell_data = positions_buff(cell);
                    uint adj_from = cell > 0 ? asuint(positions_buff(cell - 1).w) : 0;
                    uint adj_to = asuint(cell_data.w);

                    float4 attrs = attrs_buff(cell);

                    float t_1 = scene_depth;
                    uint next_face = 0xFFFFFFFF; 

                    uint faces = adj_to - adj_from;
                    for (uint f = 0; f < faces; f += CHUNK_SIZE) {

                        [unroll(CHUNK_SIZE)]
                        for (uint a1 = 0; a1 < CHUNK_SIZE; a1++) {
                            diffs[a1] = adjacency_diff_buffer(adj_from + f + a1).xyz;
                        }

                        [loop]
                        for (uint a2 = 0; a2 < CHUNK_SIZE; a2++) {
                            half3 diff = diffs[a2];
                            float denom = dot(diff, ray.direction);
                            float3 mid = cell_data.xyz + diff * 0.5f;
                            float t = dot(mid - ray.origin, diff) / denom;
                            bool valid = denom > 0 && t < t_1 && t > t_0 && f + a2 < faces;
                            t_1 = valid ? t : t_1;
                            next_face = valid ? adj_from + f + a2 : next_face;
                        }
                    }

                    float density = attrs.w;
                    float alpha = 1.0 - exp(-density * (t_1 - t_0));
                    float weight = transmittance * alpha;
                    cell = adjacency_buffer(next_face);
                    t_0 = t_1;

                    float3 rgb = pow(attrs.rgb, 2.2);
                    color += rgb * weight;

                    transmittance = transmittance * (1.0 - alpha);

                    if (next_face == 0xFFFFFFFF || t_1 >= scene_depth) {
                        break;
                    }
                }

                return float4(lerp(color, src_color.xyz, transmittance), 1);
            }
            ENDCG
        }
    }
}
