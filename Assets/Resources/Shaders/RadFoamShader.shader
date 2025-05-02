Shader "Hidden/Custom/RadFoamShader"
{
    Properties
    {
        [HideInInspector] _MainTex ("Texture", 2D) = "white" {}
        [Toggle] _ShowUnboundedCells ("Show Unbounded Cells", Int) = 0
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

            int _NumCells;

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

            // Bounding box parameters
            int _HasBoundingBox;
            float3 _BoundingBoxCenter;
            float3 _BoundingBoxSize;
            float4x4 _BoundingBoxRotation;
            float4x4 _BoundingBoxTRS;
            float4x4 _InvBoundingBoxTRS;

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

            // Ray-box intersection test using slab method
            bool RayBoxIntersection(Ray ray, out float t_enter, out float t_exit)
            {
                // Initialize output parameters
                t_enter = 0;
                t_exit = 10000;
                
                // Early out if no bounding box is defined
                if (_HasBoundingBox == 0) {
                    return true; // No bounding box, so the ray continues
                }
                
                // Transform ray to local box space (where the box is a unit cube centered at origin)
                float3 localOrigin = mul(_InvBoundingBoxTRS, float4(ray.origin, 1.0)).xyz;
                float3 localDir = mul((float3x3)_InvBoundingBoxTRS, ray.direction);
                
                // Unit AABB bounds (box is centered at origin with size 1)
                float3 boxMin = -_BoundingBoxSize*0.5;
                float3 boxMax = _BoundingBoxSize*0.5;
                
                // Calculate intersection parameters for each axis
                float3 t_min = (boxMin - localOrigin) / localDir;
                float3 t_max = (boxMax - localOrigin) / localDir;
                
                // Ensure t_min <= t_max for each axis
                float3 t_near = min(t_min, t_max);
                float3 t_far = max(t_min, t_max);
                
                // Find the furthest near intersection and the closest far intersection
                float t_n = max(max(t_near.x, t_near.y), t_near.z);
                float t_f = min(min(t_far.x, t_far.y), t_far.z);
                
                // Check if there's a valid intersection
                if (t_n > t_f || t_f < 0) {
                    return false; // No intersection
                }
                
                // Set output parameters

                // After computing t_n and t_f in local space
                float3 localEnterPoint = localOrigin + localDir * t_n;
                float3 localExitPoint = localOrigin + localDir * t_f;

                // Transform back to world space
                float3 worldEnterPoint = mul(_BoundingBoxTRS, float4(localEnterPoint, 1.0)).xyz;
                float3 worldExitPoint = mul(_BoundingBoxTRS, float4(localExitPoint, 1.0)).xyz;

                // Compute world space t values
                t_enter = length(worldEnterPoint - ray.origin) / length(ray.direction);
                t_exit = length(worldExitPoint - ray.origin) / length(ray.direction);
                
                return true;
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

            #define CHUNK_SIZE 8

            fixed4 frag (blit_v2f input) : SV_Target
            {
                float4 src_color = tex2D(_MainTex, input.uv);
                Ray ray = GetCameraRayFisheye(input.ray, _FisheyeFOV);
                if (dot(ray.direction, ray.direction) == 0) {
                    return src_color; // fisheye fov too large
                }
                ray.direction = normalize(ray.direction);

                uint cell;

                // Perform ray-box intersection test if bounding box is present
                float box_t_enter, box_t_exit;
                if (_HasBoundingBox) {
                    
                    bool intersects_box = RayBoxIntersection(ray, box_t_enter, box_t_exit);

                    // Early termination if ray doesn't hit bounding box
                    if (!intersects_box) {
                        return src_color;
                    } else if (intersects_box) {
                        float3 entry_point = ray.origin + ray.direction * box_t_enter;
                        
                        // Find the closest cell to this entry point
                        uint closest_cell = 0xFFFFFFFF;
                        float min_dist = 10000.0;
                        
                        // This could be optimized, but for now, a linear search through cells
                        for (uint c = 0; c < _NumCells; c++) {
                            float3 cell_pos = positions_buff(c).xyz;
                            float dist = length(entry_point - cell_pos);
                            
                            if (dist < min_dist) {
                                min_dist = dist;
                                closest_cell = c;
                            }
                        }
                        
                        // Use this as our starting cell instead of _start_index
                        cell = (closest_cell != 0xFFFFFFFF) ? closest_cell : _start_index;
                    }
                } else {
                    cell = _start_index;
                }
                
                float scene_depth = _HasBoundingBox ? box_t_exit : 10000;
                float3 diffs[CHUNK_SIZE];

                // tracing state
                float transmittance = 1.0f;
                float3 color = float3(0, 0, 0);
                float t_0 = _HasBoundingBox ? max(0,box_t_enter) : 0.0; // Start at box entry point if applicable
                float progress = 0;
                int i = 0;
                for (; i < 1000 && transmittance > 0.1; i++) {
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
                    
                    if (t_1 >= scene_depth || next_face == 0xFFFFFFFF) {
                        break;
                    }
                }
                return float4(lerp(color, src_color.xyz, transmittance), 1);
            }
            ENDCG
        }
    }
}