using UnityEngine;
using System.IO;

namespace Ply
{
    public static class PlyExporter
    {
        public static bool ExportPointCloud(string path, Vector3[] points, Color[] colors)
        {
            if (points == null || colors == null || points.Length != colors.Length)
                return false;

            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (BinaryWriter writer = new BinaryWriter(fs))
                {
                    // Write PLY header
                    WriteHeader(writer, points.Length);
                    
                    // Write binary data
                    for (int i = 0; i < points.Length; i++)
                    {
                        // Write position (x, y, z as float)
                        writer.Write(points[i].x);
                        writer.Write(points[i].y);
                        writer.Write(points[i].z);
                        
                        // Write color (r, g, b as byte)
                        writer.Write((byte)(colors[i].r * 255));
                        writer.Write((byte)(colors[i].g * 255));
                        writer.Write((byte)(colors[i].b * 255));
                    }
                }
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error exporting PLY: {e.Message}");
                return false;
            }
        }
        
        private static void WriteHeader(BinaryWriter writer, int pointCount)
        {
            string header = 
                "ply\n" +
                "format binary_little_endian 1.0\n" +
                "element vertex " + pointCount + "\n" +
                "property float x\n" +
                "property float y\n" +
                "property float z\n" +
                "property uchar red\n" +
                "property uchar green\n" +
                "property uchar blue\n" +
                "end_header\n";
            
            // Write ASCII header
            writer.Write(System.Text.Encoding.ASCII.GetBytes(header));
        }
    }
}
