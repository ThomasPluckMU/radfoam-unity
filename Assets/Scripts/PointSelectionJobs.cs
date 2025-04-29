using UnityEngine;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Burst;

namespace Ply
{
    public static class PointSelectionJobs
    {
        // Burst-compiled job to select points within a bounding box
        [BurstCompile]
        public struct BoxSelectionJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> points;
            [ReadOnly] public float3 boxMin;
            [ReadOnly] public float3 boxMax;
            [WriteOnly] public NativeArray<bool> results;
            
            public void Execute(int i)
            {
                float3 point = points[i];
                bool isInBox = 
                    point.x >= boxMin.x && point.x <= boxMax.x &&
                    point.y >= boxMin.y && point.y <= boxMax.y &&
                    point.z >= boxMin.z && point.z <= boxMax.z;
                    
                results[i] = isInBox;
            }
        }
        
        // Select points within view frustum
        [BurstCompile]
        public struct FrustumSelectionJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> points;
            [ReadOnly] public NativeArray<float4> frustumPlanes; // Normal (xyz) and distance (w)
            [WriteOnly] public NativeArray<bool> results;
            
            public void Execute(int i)
            {
                float3 point = points[i];
                bool isInFrustum = true;
                
                // Test point against all planes
                for (int p = 0; p < frustumPlanes.Length; p++)
                {
                    float4 plane = frustumPlanes[p];
                    float3 normal = new float3(plane.x, plane.y, plane.z);
                    float distance = plane.w;
                    
                    // If point is behind any plane, it's outside the frustum
                    if (math.dot(normal, point) + distance < 0)
                    {
                        isInFrustum = false;
                        break;
                    }
                }
                
                results[i] = isInFrustum;
            }
        }
        
        // Select points by sphere
        [BurstCompile]
        public struct SphereSelectionJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> points;
            [ReadOnly] public float3 center;
            [ReadOnly] public float radiusSquared;
            [WriteOnly] public NativeArray<bool> results;
            
            public void Execute(int i)
            {
                float3 point = points[i];
                float sqrDist = math.distancesq(point, center);
                results[i] = sqrDist <= radiusSquared;
            }
        }
        
        // Select points by plane
        [BurstCompile]
        public struct PlaneSelectionJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> points;
            [ReadOnly] public float4 plane; // Normal (xyz) and distance (w)
            [ReadOnly] public bool selectPositive; // True to select points on positive side
            [WriteOnly] public NativeArray<bool> results;
            
            public void Execute(int i)
            {
                float3 point = points[i];
                float3 normal = new float3(plane.x, plane.y, plane.z);
                float distance = plane.w;
                
                float pointDistance = math.dot(normal, point) + distance;
                bool isPositive = pointDistance >= 0;
                
                results[i] = isPositive == selectPositive;
            }
        }
        
        // Select points by color similarity
        [BurstCompile]
        public struct ColorSelectionJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> points;
            [ReadOnly] public NativeArray<float4> colors;
            [ReadOnly] public float4 targetColor;
            [ReadOnly] public float threshold;
            [WriteOnly] public NativeArray<bool> results;
            
            public void Execute(int i)
            {
                float4 color = colors[i];
                
                // Calculate color distance (RGB space)
                float distance = math.sqrt(
                    math.pow(color.x - targetColor.x, 2) + 
                    math.pow(color.y - targetColor.y, 2) + 
                    math.pow(color.z - targetColor.z, 2));
                
                results[i] = distance <= threshold;
            }
        }
        
        // Helper methods to schedule jobs
        
        public static JobHandle ScheduleBoxSelection(Vector3[] points, Bounds box, 
            ref NativeArray<bool> results, JobHandle dependency = default)
        {
            // Convert to NativeArray for job system
            NativeArray<float3> pointsArray = new NativeArray<float3>(points.Length, Allocator.TempJob);
            for (int i = 0; i < points.Length; i++)
            {
                pointsArray[i] = new float3(points[i].x, points[i].y, points[i].z);
            }
            
            // Setup job data
            BoxSelectionJob job = new BoxSelectionJob
            {
                points = pointsArray,
                boxMin = new float3(box.min.x, box.min.y, box.min.z),
                boxMax = new float3(box.max.x, box.max.y, box.max.z),
                results = results
            };
            
            // Schedule job
            JobHandle handle = job.Schedule(points.Length, 64, dependency);
            
            // Register disposal
            handle = pointsArray.Dispose(handle);
            
            return handle;
        }
        
        public static JobHandle ScheduleFrustumSelection(Vector3[] points, Plane[] frustumPlanes, 
            ref NativeArray<bool> results, JobHandle dependency = default)
        {
            // Convert points to NativeArray
            NativeArray<float3> pointsArray = new NativeArray<float3>(points.Length, Allocator.TempJob);
            for (int i = 0; i < points.Length; i++)
            {
                pointsArray[i] = new float3(points[i].x, points[i].y, points[i].z);
            }
            
            // Convert planes to NativeArray
            NativeArray<float4> planesArray = new NativeArray<float4>(frustumPlanes.Length, Allocator.TempJob);
            for (int i = 0; i < frustumPlanes.Length; i++)
            {
                planesArray[i] = new float4(
                    frustumPlanes[i].normal.x, 
                    frustumPlanes[i].normal.y, 
                    frustumPlanes[i].normal.z,
                    frustumPlanes[i].distance);
            }
            
            // Setup job
            FrustumSelectionJob job = new FrustumSelectionJob
            {
                points = pointsArray,
                frustumPlanes = planesArray,
                results = results
            };
            
            // Schedule job
            JobHandle handle = job.Schedule(points.Length, 64, dependency);
            
            // Register disposal
            handle = pointsArray.Dispose(handle);
            handle = planesArray.Dispose(handle);
            
            return handle;
        }
        
        public static JobHandle ScheduleSphereSelection(Vector3[] points, Vector3 center, float radius, 
            ref NativeArray<bool> results, JobHandle dependency = default)
        {
            // Convert points to NativeArray
            NativeArray<float3> pointsArray = new NativeArray<float3>(points.Length, Allocator.TempJob);
            for (int i = 0; i < points.Length; i++)
            {
                pointsArray[i] = new float3(points[i].x, points[i].y, points[i].z);
            }
            
            // Setup job
            SphereSelectionJob job = new SphereSelectionJob
            {
                points = pointsArray,
                center = new float3(center.x, center.y, center.z),
                radiusSquared = radius * radius,
                results = results
            };
            
            // Schedule job
            JobHandle handle = job.Schedule(points.Length, 64, dependency);
            
            // Register disposal
            handle = pointsArray.Dispose(handle);
            
            return handle;
        }
        
        public static JobHandle SchedulePlaneSelection(Vector3[] points, Plane plane, bool selectPositive, 
            ref NativeArray<bool> results, JobHandle dependency = default)
        {
            // Convert points to NativeArray
            NativeArray<float3> pointsArray = new NativeArray<float3>(points.Length, Allocator.TempJob);
            for (int i = 0; i < points.Length; i++)
            {
                pointsArray[i] = new float3(points[i].x, points[i].y, points[i].z);
            }
            
            // Setup job
            PlaneSelectionJob job = new PlaneSelectionJob
            {
                points = pointsArray,
                plane = new float4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance),
                selectPositive = selectPositive,
                results = results
            };
            
            // Schedule job
            JobHandle handle = job.Schedule(points.Length, 64, dependency);
            
            // Register disposal
            handle = pointsArray.Dispose(handle);
            
            return handle;
        }
        
        public static JobHandle ScheduleColorSelection(Vector3[] points, Color[] colors, Color targetColor, float threshold,
            ref NativeArray<bool> results, JobHandle dependency = default)
        {
            // Convert points and colors to NativeArray
            NativeArray<float3> pointsArray = new NativeArray<float3>(points.Length, Allocator.TempJob);
            NativeArray<float4> colorsArray = new NativeArray<float4>(colors.Length, Allocator.TempJob);
            
            for (int i = 0; i < points.Length; i++)
            {
                pointsArray[i] = new float3(points[i].x, points[i].y, points[i].z);
                colorsArray[i] = new float4(colors[i].r, colors[i].g, colors[i].b, colors[i].a);
            }
            
            // Setup job
            ColorSelectionJob job = new ColorSelectionJob
            {
                points = pointsArray,
                colors = colorsArray,
                targetColor = new float4(targetColor.r, targetColor.g, targetColor.b, targetColor.a),
                threshold = threshold,
                results = results
            };
            
            // Schedule job
            JobHandle handle = job.Schedule(points.Length, 64, dependency);
            
            // Register disposal
            handle = pointsArray.Dispose(handle);
            handle = colorsArray.Dispose(handle);
            
            return handle;
        }