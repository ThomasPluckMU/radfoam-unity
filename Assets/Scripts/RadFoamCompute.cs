using System;
using System.Collections.Generic;
using Ply;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

public class RadFoamCompute : MonoBehaviour
{
    public Transform Target;
    public PlyData[] DataList;

    public ComputeShader radfoamShader;
    public ComputeShader closestShader;
    public ComputeShader pbdShader;

    // model data
    private GraphicsBuffer positions_buffer;
    private GraphicsBuffer shs_buffer;
    private GraphicsBuffer adjacency_buffer;
    private GraphicsBuffer adjacency_diff_buffer;

    private GraphicsBuffer closest_index_buffer;
    private GraphicsBuffer tmp_distance_buffer;

    // PBD buffers
    private GraphicsBuffer velocities_buffer;
    private GraphicsBuffer inv_masses_buffer;
    private GraphicsBuffer predicted_positions_buffer;
    private GraphicsBuffer constraint_stiffness_buffer;
    
    // Bounding box data
    private bool _hasBoundingBox = false;
    private Vector3 _boundingBoxCenter = Vector3.zero;
    private Vector3 _boundingBoxSize = Vector3.zero;
    private Quaternion _boundingBoxRotation = Quaternion.identity;
    private Vector4[] _BoundingPlanes = new Vector4[6];

    // viewer state 
    private int debug_view = 0;
    private int camera_model = 0;
    private float fisheye_fov = 60;
    private int current_model = -1;
    private int current_sh_level = -1;
    private bool current_morton_reorder = true;
    private int test_algo = 0;
    private bool enable_pbd = true;

    private const int SH_DEGREE_MAX = 3;
    private int SH_DIM(int degree) => (degree + 1) * (degree + 1);
    private const int FIND_CLOSEST_THREADS_PER_GROUP = 1024;

    void OnDestroy()
    {
        positions_buffer?.Release();
        shs_buffer?.Release();
        adjacency_buffer?.Release();
        adjacency_diff_buffer?.Release();

        closest_index_buffer?.Release();
        tmp_distance_buffer?.Release();
        
        velocities_buffer?.Release();
        inv_masses_buffer?.Release();
        predicted_positions_buffer?.Release();
        constraint_stiffness_buffer?.Release();
    }

    void OnGUI()
    {
        GUILayout.Label("Model (Left/right): " + current_model);
        GUILayout.Label("Spherical Harmonics (Up/Down): " + current_sh_level);
        GUILayout.Label("Morton reorder (R): " + current_morton_reorder);
        GUILayout.Label("Debug view (D): " + debug_view);
        GUILayout.Label("Camera Model (C): " + camera_model);
        GUILayout.Label("PBD Enabled (P): " + enable_pbd);
        
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

    private bool ParseBoundingBoxFromPly(Model model)
    {
        try
        {
            // First check if the model has bounding box data directly from the PLY file
            if (model.HasBoundingBox)
            {
                _boundingBoxCenter = model.BoundingBoxCenter;
                _boundingBoxSize = model.BoundingBoxSize;
                _boundingBoxRotation = model.BoundingBoxRotation;

                // Calculate the 6 bounding planes from center, size, and rotation
                Vector3[] directions = {
                    Vector3.right, Vector3.left,    // X-axis planes
                    Vector3.up, Vector3.down,       // Y-axis planes
                    Vector3.forward, Vector3.back   // Z-axis planes
                };
                
                // Create transformation matrices
                Vector4[] _BoundingPlanes = new Vector4[6];
                for (int i = 0; i < 6; i++) {
                    // Transform the direction by rotation
                    Vector3 normal = _boundingBoxRotation * directions[i];
                    
                    // Calculate the plane distance (half-size in that direction + center projection)
                    float halfSize = _boundingBoxSize[i/2] * 0.5f;
                    float distance = Vector3.Dot(normal, _boundingBoxCenter) + (i % 2 == 0 ? halfSize : -halfSize);
                    
                    // Store as (normal.xyz, distance)
                    _BoundingPlanes[i] = new Vector4(normal.x, normal.y, normal.z, distance);
                }
                
                _hasBoundingBox = true;
                Debug.Log($"Loaded bounding box from PLY metadata: Center={_boundingBoxCenter}, Size={_boundingBoxSize}, Rotation={_boundingBoxRotation.eulerAngles}");
                return true;
            }
            
            // If not directly available, try to find bounding_box element
            try
            {
                ElementView boundingBoxView;
                boundingBoxView = model.element_view("bounding_box");
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

                    // Calculate the 6 bounding planes from center, size, and rotation
                    Vector3[] directions = {
                        Vector3.right, Vector3.left,    // X-axis planes
                        Vector3.up, Vector3.down,       // Y-axis planes
                        Vector3.forward, Vector3.back   // Z-axis planes
                    };

                    
                    // Create transformation matrices
                    Vector4[] _BoundingPlanes = new Vector4[6];
                    for (int i = 0; i < 6; i++) {
                        // Transform the direction by rotation
                        Vector3 normal = _boundingBoxRotation * directions[i];
                        
                        // Calculate the plane distance (half-size in that direction + center projection)
                        float halfSize = _boundingBoxSize[i/2] * 0.5f;
                        float distance = Vector3.Dot(normal, _boundingBoxCenter) + (i % 2 == 0 ? halfSize : -halfSize);
                        
                        // Store as (normal.xyz, distance)
                        _BoundingPlanes[i] = new Vector4(normal.x, normal.y, normal.z, distance);
                    }
                    
                    _hasBoundingBox = true;
                    Debug.Log($"Loaded bounding box from element: Center={_boundingBoxCenter}, Size={_boundingBoxSize}, Rotation={_boundingBoxRotation.eulerAngles}");
                    return true;
                }
            }
            catch (ArgumentException)
            {
                Debug.Log("No bounding_box element found in PLY data");
            }
            
            // No bounding box found
            _hasBoundingBox = false;
            return false;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Failed to parse bounding box data: {e.Message}");
            _hasBoundingBox = false;
            return false;
        }
    }

    private void LoadBoundingBox(Model model)
    {
        try
        {
            // First check if model has a bounding_box element
            try
            {
                ElementView boundingBoxView = model.element_view("bounding_box");
                if (boundingBoxView.count > 0)
                {
                    // Read bounding box data from the element
                    _boundingBoxCenter = new Vector3(
                        boundingBoxView.property_view("center_x").Get<float>(0),
                        boundingBoxView.property_view("center_y").Get<float>(0),
                        boundingBoxView.property_view("center_z").Get<float>(0)
                    );
                    
                    _boundingBoxSize = new Vector3(
                        boundingBoxView.property_view("size_x").Get<float>(0),
                        boundingBoxView.property_view("size_y").Get<float>(0),
                        boundingBoxView.property_view("size_z").Get<float>(0)
                    );
                    
                    _boundingBoxRotation = new Quaternion(
                        boundingBoxView.property_view("rotation_x").Get<float>(0),
                        boundingBoxView.property_view("rotation_y").Get<float>(0),
                        boundingBoxView.property_view("rotation_z").Get<float>(0),
                        boundingBoxView.property_view("rotation_w").Get<float>(0)
                    );
                    
                    _hasBoundingBox = true;
                    Debug.Log($"Loaded bounding box from element: Center={_boundingBoxCenter}, Size={_boundingBoxSize}, Rotation={_boundingBoxRotation.eulerAngles}");
                    return;
                }
            }
            catch (ArgumentException)
            {
                // No bounding_box element, continue to other methods
            }
            
            // Fallback to default bounding box if needed
            _hasBoundingBox = false;
            Debug.Log("No bounding box found in model");
        }
        catch (Exception e)
        {
            _hasBoundingBox = false;
            Debug.LogWarning($"Failed to load bounding box: {e.Message}");
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

                    // Calculate the 6 bounding planes from center, size, and rotation
                    Vector3[] directions = {
                        Vector3.right, Vector3.left,    // X-axis planes
                        Vector3.up, Vector3.down,       // Y-axis planes
                        Vector3.forward, Vector3.back   // Z-axis planes
                    };
                    
                    // Create transformation matrices
                    Vector4[] _BoundingPlanes = new Vector4[6];
                    for (int i = 0; i < 6; i++) {
                        // Transform the direction by rotation
                        Vector3 normal = _boundingBoxRotation * directions[i];
                        
                        // Calculate the plane distance (half-size in that direction + center projection)
                        float halfSize = _boundingBoxSize[i/2] * 0.5f;
                        float distance = Vector3.Dot(normal, _boundingBoxCenter) + (i % 2 == 0 ? halfSize : -halfSize);
                        
                        // Store as (normal.xyz, distance)
                        _BoundingPlanes[i] = new Vector4(normal.x, normal.y, normal.z, distance);
                    }
                    
                    _hasBoundingBox = true;
                    Debug.Log($"Loaded bounding box from comments: Center={_boundingBoxCenter}, Size={_boundingBoxSize}, Rotation={_boundingBoxRotation.eulerAngles}");
                    return true;
                }
            }
            
            _hasBoundingBox = false;
            return false;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Failed to parse bounding box from comments: {e.Message}");
            _hasBoundingBox = false;
            return false;
        }
    }

    void Load(in Model model, int sh_degree, bool morton_reorder)
    {
        // Try to parse bounding box data
        _hasBoundingBox = ParseBoundingBoxFromPly(model);
        if (_hasBoundingBox){
            LoadBoundingBox(model);
        }
        
        for (int i = 1; i <= SH_DEGREE_MAX; i++) {
            var kw_compute = new LocalKeyword(radfoamShader, "SH_DEGREE_" + i);
            if (i == sh_degree) {
                radfoamShader.EnableKeyword(kw_compute);
            } else {
                radfoamShader.DisableKeyword(kw_compute);
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

        using var points = new NativeArray<float4>(vertex_count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
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

        positions_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertex_count, sizeof(float) * 4);
        shs_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertex_count, attribute_elem_size);
        adjacency_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency_count, sizeof(uint));

        if (morton_reorder) {
            using var mapping = new NativeArray<MortonOrder.Data>(vertex_count, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var handle = new MortonOrder() { points = points, map = mapping }.Schedule(vertex_count, 512);
            handle.Complete();

            // FIXME somehow SortJob is insanely slow..
            // handle = mapping.SortJob(new MortonOrder.Comparer()).Schedule(handle);
            mapping.Sort(new MortonOrder.Comparer());

            using var mapping_inv = new NativeArray<uint>(vertex_count, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            handle = new InvertMapping() { mapping = mapping, mapping_inv = mapping_inv }.Schedule(vertex_count, 512, handle);

            using var re_points = new NativeArray<float4>(vertex_count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            using var re_attributes = new NativeArray<byte>(vertex_count * attribute_elem_size, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            using var re_adjacency = new NativeArray<uint>(adjacency_count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            handle = new Shuffle() {
                points = points,
                attributes = attributes,
                adjacency = adjacency,
                attribute_elem_size = attribute_elem_size,
                mapping = mapping,
                mapping_inv = mapping_inv,
                shuffled_points = re_points,
                shuffled_attributes = re_attributes,
                shuffled_adjacency = re_adjacency,
            }.Schedule(handle);
            handle.Complete();

            positions_buffer.SetData(re_points);
            shs_buffer.SetData(re_attributes);
            adjacency_buffer.SetData(re_adjacency);
        } else {
            positions_buffer.SetData(points);
            shs_buffer.SetData(attributes);
            adjacency_buffer.SetData(adjacency);
        }

        {
            var reduction_group_count = Mathf.CeilToInt(vertex_count / (float)FIND_CLOSEST_THREADS_PER_GROUP);
            closest_index_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, reduction_group_count, sizeof(uint));
            tmp_distance_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, reduction_group_count, sizeof(float));
        }

        // calculate direction vectors between adjacent cells
        {
            adjacency_diff_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency_count, 4 * 4);

            // Initialize PBD buffers
            velocities_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertex_count, 12);
            inv_masses_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertex_count, 4);
            predicted_positions_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertex_count, 12);
            constraint_stiffness_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency_count, 4);

            // Initialize velocities to zero and masses to 1
            var init_velocities = new NativeArray<float3>(vertex_count, Allocator.Temp);
            var init_masses = new NativeArray<float>(vertex_count, Allocator.Temp);
            for (int i = 0; i < vertex_count; i++) {
                init_masses[i] = 1.0f;
            }
            velocities_buffer.SetData(init_velocities);
            inv_masses_buffer.SetData(init_masses);
            init_velocities.Dispose();
            init_masses.Dispose();
            
            var kernel = radfoamShader.FindKernel("BuildAdjDiff");
            radfoamShader.SetInt("_Count", vertex_count);
            radfoamShader.SetBuffer(kernel, "_start_index", closest_index_buffer);
            radfoamShader.SetBuffer(kernel, "_positions", positions_buffer);
            radfoamShader.SetBuffer(kernel, "_adjacency", adjacency_buffer);
            radfoamShader.SetBuffer(kernel, "_adjacency_diff_uav", adjacency_diff_buffer);
            radfoamShader.Dispatch(kernel, Mathf.CeilToInt(vertex_count / 1024f), 1, 1);
        }
    }

    void Update()
    {
        // PBD prediction step
        if (enable_pbd && positions_buffer != null && velocities_buffer != null) {
            var kernel = pbdShader.FindKernel("PredictPositions");
            pbdShader.SetBuffer(kernel, "_positions", positions_buffer);
            pbdShader.SetBuffer(kernel, "_velocities", velocities_buffer);
            pbdShader.SetBuffer(kernel, "_predicted_positions", predicted_positions_buffer);
            pbdShader.SetBuffer(kernel, "_inv_masses", inv_masses_buffer);
            pbdShader.SetFloat("_deltaTime", Time.deltaTime);
            pbdShader.Dispatch(kernel, Mathf.CeilToInt(positions_buffer.count / 64f), 1, 1);

            // Solve constraints (3 iterations)
            for (int i = 0; i < 3; i++) {
                kernel = pbdShader.FindKernel("SolveDistanceConstraints");
                pbdShader.SetBuffer(kernel, "_predicted_positions", predicted_positions_buffer);
                pbdShader.SetBuffer(kernel, "_adjacency", adjacency_buffer);
                pbdShader.SetBuffer(kernel, "_constraint_stiffness", constraint_stiffness_buffer);
                pbdShader.Dispatch(kernel, Mathf.CeilToInt(adjacency_buffer.count / 64f), 1, 1);
            }

            // Update velocities and positions
            kernel = pbdShader.FindKernel("UpdateVelocities");
            pbdShader.SetBuffer(kernel, "_positions", positions_buffer);
            pbdShader.SetBuffer(kernel, "_predicted_positions", predicted_positions_buffer);
            pbdShader.SetBuffer(kernel, "_velocities", velocities_buffer);
            pbdShader.SetFloat("_deltaTime", Time.deltaTime);
            pbdShader.Dispatch(kernel, Mathf.CeilToInt(positions_buffer.count / 64f), 1, 1);
        }

        if (Input.GetKeyDown(KeyCode.C)) {
            camera_model = camera_model == 0 ? 1 : 0;
        }
        fisheye_fov = math.clamp(fisheye_fov + Input.mouseScrollDelta.y * -4, 10, 120);
        debug_view = Input.GetKey(KeyCode.D) ? 1 : 0;

        var model_index = math.clamp(current_model + (Input.GetKeyDown(KeyCode.RightArrow) ? 1 : 0) + (Input.GetKeyDown(KeyCode.LeftArrow) ? -1 : 0), 0, DataList.Length);
        var sh_level = math.clamp(current_sh_level + (Input.GetKeyDown(KeyCode.UpArrow) ? 1 : 0) + (Input.GetKeyDown(KeyCode.DownArrow) ? -1 : 0), 0, SH_DEGREE_MAX);
        var morton_reorder = Input.GetKeyDown(KeyCode.R) ? !current_morton_reorder : current_morton_reorder;

        if (current_sh_level != sh_level || morton_reorder != current_morton_reorder || model_index != current_model) {
            OnDestroy(); // destroy previous data
            {
                using var model = DataList[model_index].Load();
                Load(model, sh_level, morton_reorder);
            }
            current_model = model_index;
            current_sh_level = sh_level;
            current_morton_reorder = morton_reorder;
        }

        test_algo = Input.GetKey(KeyCode.A) ? 1 : 0;
        enable_pbd = Input.GetKeyDown(KeyCode.P) ? !enable_pbd : enable_pbd;
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

            radfoamShader.SetInt("_HasBoundingBox", _hasBoundingBox ? 1 : 0);
            if (_hasBoundingBox)
            {
                int planesPropID = Shader.PropertyToID("_BoundingPlanes");
                radfoamShader.SetVectorArray(planesPropID, _BoundingPlanes);
            }

            while (count > 1) {
                count = Mathf.CeilToInt(count / (float)FIND_CLOSEST_THREADS_PER_GROUP);
                closestShader.Dispatch(kernel, count, 1, 1);
            }
        }

        // draw
        {
            var descriptor = srcRenderTex.descriptor;
            descriptor.enableRandomWrite = true;
            descriptor.sRGB = true;
            var tmp = RenderTexture.GetTemporary(descriptor);

            var kernel = radfoamShader.FindKernel("RadFoam");
            radfoamShader.SetTexture(kernel, "_srcTex", srcRenderTex);
            radfoamShader.SetTexture(kernel, "_outTex", tmp);
            radfoamShader.SetMatrix("_Camera2WorldMatrix", world_to_model * camera.cameraToWorldMatrix);
            radfoamShader.SetMatrix("_InverseProjectionMatrix", camera.projectionMatrix.inverse);
            radfoamShader.SetFloat("_CameraModel", camera_model);
            radfoamShader.SetFloat("_FisheyeFOV", fisheye_fov);
            radfoamShader.SetInt("_DebugView", debug_view);
            radfoamShader.SetInt("_TestAlgo", test_algo);
            
            // Set bounding box data if available
            radfoamShader.SetInt("_HasBoundingBox", _hasBoundingBox ? 1 : 0);
            if (_hasBoundingBox)
            {
                int planesPropID = Shader.PropertyToID("_BoundingPlanes");
                radfoamShader.SetVectorArray(planesPropID, _BoundingPlanes);
            }

            radfoamShader.SetBuffer(kernel, "_start_index", closest_index_buffer);
            radfoamShader.SetBuffer(kernel, "_positions", positions_buffer);
            radfoamShader.SetBuffer(kernel, "_shs", shs_buffer);
            radfoamShader.SetBuffer(kernel, "_adjacency", adjacency_buffer);
            radfoamShader.SetBuffer(kernel, "_adjacency_diff", adjacency_diff_buffer);

            int size = 8;
            int gridSizeX = Mathf.CeilToInt(srcRenderTex.width / (float)size);
            int gridSizeY = Mathf.CeilToInt(srcRenderTex.height / (float)size);
            radfoamShader.Dispatch(kernel, gridSizeX, gridSizeY, 1);

            Graphics.Blit(tmp, outRenderTex);
            RenderTexture.ReleaseTemporary(tmp);
        }
    }

    [BurstCompile]
    struct MortonOrder : IJobParallelFor
    {
        public struct Comparer : IComparer<Data>
        {
            int IComparer<Data>.Compare(Data x, Data y)
            {
                return x.order.CompareTo(y.order);
            }
        }

        public struct Data
        {
            public ulong order;
            public uint index;
        }
        [ReadOnly] public NativeArray<float4> points;
        [WriteOnly] public NativeArray<Data> map;

        // Based on https://fgiesen.wordpress.com/2009/12/13/decoding-morton-codes/ 
        // Insert two 0 bits after each of the 21 low bits of x
        static ulong MortonPart1By2(ulong x)
        {
            x &= 0x1fffff;
            x = (x ^ (x << 32)) & 0x1f00000000ffffUL;
            x = (x ^ (x << 16)) & 0x1f0000ff0000ffUL;
            x = (x ^ (x << 8)) & 0x100f00f00f00f00fUL;
            x = (x ^ (x << 4)) & 0x10c30c30c30c30c3UL;
            x = (x ^ (x << 2)) & 0x1249249249249249UL;
            return x;
        }

        static ulong MortonEncode3(uint3 v)
        {   // Encode three 21-bit integers into 3D Morton order
            return (MortonPart1By2(v.z) << 2) | (MortonPart1By2(v.y) << 1) | MortonPart1By2(v.x);
        }

        public void Execute(int index)
        {
            var scale = 1000;
            map[index] = new Data {
                order = MortonEncode3(new uint3(points[index].xyz * scale)),
                index = (uint)index
            };
        }
    }

    [BurstCompile]
    struct InvertMapping : IJobParallelFor
    {
        [ReadOnly] public NativeArray<MortonOrder.Data> mapping;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<uint> mapping_inv;

        public void Execute(int index)
        {
            mapping_inv[(int)mapping[index].index] = (uint)index;
        }
    }

    [BurstCompile]
    struct Shuffle : IJob
    {
        [ReadOnly] public NativeArray<float4> points;
        [ReadOnly] public NativeArray<byte> attributes;
        [ReadOnly] public NativeArray<uint> adjacency;
        public int attribute_elem_size;
        [ReadOnly] public NativeArray<MortonOrder.Data> mapping;
        [ReadOnly] public NativeArray<uint> mapping_inv;
        [WriteOnly] public NativeArray<float4> shuffled_points;
        [WriteOnly] public NativeArray<byte> shuffled_attributes;
        [WriteOnly] public NativeArray<uint> shuffled_adjacency;

        public void Execute()
        {
            var adj_index = 0;
            for (var p = 0; p < points.Length; p++)
            {
                var old_index = (int)mapping[p].index;
                var old_point = points[old_index];

                var old_adj_from = old_index > 0 ? math.asuint(points[old_index - 1].w) : 0;
                var old_adj_to = math.asuint(old_point.w);

                for (var adj = old_adj_from; adj < old_adj_to; adj++)
                {
                    shuffled_adjacency[adj_index++] = mapping_inv[(int)adjacency[(int)adj]];
                }

                shuffled_points[p] = new float4(old_point.xyz, math.asfloat((uint)adj_index));  // update the adjacency offset to the shuffled version
                // shuffled_attributes.Slice()
                // NativeSlice.Copy() ?
                for (var a = 0; a < attribute_elem_size; a++)
                {
                    shuffled_attributes[p * attribute_elem_size + a] = attributes[old_index * attribute_elem_size + a];
                }
            }
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
