using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Ply
{
    public class PlyData : ScriptableObject
    {
        [SerializeField]
        private Element[] elements;

        [SerializeField]
        private TextAsset binary;

        [SerializeField]
        private bool hasTSRData;

        [SerializeField]
        private Vector3 translation;

        [SerializeField]
        private Vector3 scale;

        [SerializeField]
        private Quaternion rotation;

        [SerializeField]
        private Texture2D[] boundaryTextures;

        public Element[] Elements { get => elements; }
        public TextAsset Binary { get => binary; }
        public bool HasTSRData { get => hasTSRData; }
        public Vector3 Translation { get => translation; }
        public Vector3 Scale { get => scale; }
        public Quaternion Rotation { get => rotation; }
        public Texture2D[] BoundaryTextures { get => boundaryTextures; }

        public void ReadFromStream(FileStream stream)
        {
            var (header, read_count, tsr) = PlyDataParser.read_header(stream);
            using var binary_data = PlyDataParser.read_binary_blob(stream, read_count, Allocator.Temp);
            var binary_asset = new TextAsset(binary_data) {
                name = "binary_data"
            };
            elements = header;
            binary = binary_asset;
            
            // Store TSR data if available
            hasTSRData = tsr.hasTSRData;
            translation = tsr.translation;
            scale = tsr.scale;
            rotation = tsr.rotation;

            boundaryTextures = tsr.boundaryTextures;
        }

        public Model Load()
        {
            return new Model(elements, binary.GetData<byte>());
        }
    }

    [Serializable]
    public enum DataType
    {
        Float, UChar, UInt
    }

    [Serializable]
    public struct Property
    {
        public string name;
        public DataType data_type;

        public int byte_size() => data_type.byte_size();
    }

    [Serializable]
    public struct Element
    {
        public string name;
        public int count;
        public Property[] properties;

        public int byte_size()
        {
            int bytes = 0;
            for (var p = 0; p < properties.Length; p++) {
                bytes += properties[p].byte_size();
            }
            return bytes;
        }
    }

    public readonly struct Model : IDisposable
    {
        private readonly NativeArray<byte> binary_blob;
        private readonly (string, ElementView)[] element_views;

        public static Model from_file(string path, Allocator alloc = Allocator.TempJob)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024 * 64, FileOptions.SequentialScan);
            return from_stream(stream, alloc);
        }

        public static Model from_stream(Stream stream, Allocator alloc = Allocator.TempJob)
        {
            var (header, read_count, _) = PlyDataParser.read_header(stream);
            return new Model(header, PlyDataParser.read_binary_blob(stream, read_count, alloc));
        }

        public Model(Element[] elements, NativeArray<byte> binary_blob)
        {
            this.binary_blob = binary_blob;

            element_views = new (string, ElementView)[elements.Length];
            var element_offset = 0;
            for (var e = 0; e < element_views.Length; e++) {
                var element = elements[e];

                var element_count = element.count;
                var element_stride = element.byte_size();
                var element_size = element_count * element_stride;

                var property_offset = 0;
                var property_views = new (string, int)[element.properties.Length];
                for (var p = 0; p < property_views.Length; p++) {
                    property_views[p] = (element.properties[p].name, property_offset);
                    property_offset += element.properties[p].byte_size();
                }

                var element_view = new ElementView(binary_blob.Slice(element_offset, element_size), element_count, element_stride, property_views);
                element_views[e] = (element.name, element_view);
                element_offset += element_size;
            }
        }

        public ElementView element_view(string element)
        {
            for (var e = 0; e < element_views.Length; e++) {
                if (element_views[e].Item1 == element) {
                    return element_views[e].Item2;
                }
            }
            throw new ArgumentException(element);
        }

        public void Dispose()
        {
            binary_blob.Dispose();
        }
    }

    public readonly struct ElementView
    {
        private readonly (string, int)[] properties;
        public readonly int count;
        public readonly int stride;
        private readonly NativeSlice<byte> data;

        public ElementView(NativeSlice<byte> data, int count, int stride, (string, int)[] properties)
        {
            this.data = data;
            this.count = count;
            this.stride = stride;
            this.properties = properties;
        }

        public PropertyView property_view(string property)
        {
            var offset = -1;
            for (var i = 0; i < properties.Length; i++) {
                if (properties[i].Item1 == property) {
                    offset = properties[i].Item2;
                    break;
                }
            }
            if (offset < 0)
                throw new ArgumentException(property);
            return new PropertyView(data, count, stride, offset);
        }

        public PropertyView dummy_property_view()
        {
            return new PropertyView(data, 0, 0, 0);
        }

        public (JobHandle, NativeArray<T>) read_property<T>(string property, Allocator alloc = Allocator.TempJob) where T : unmanaged
        {
            return property_view(property).read<T>(alloc);
        }
    }

    public readonly struct PropertyView
    {
        [ReadOnly] public readonly NativeSlice<byte> data;
        public readonly int count;
        private readonly int stride;
        private readonly int offset;

        public PropertyView(NativeSlice<byte> data, int count, int stride, int offset)
        {
            this.data = data;
            this.count = count;
            this.stride = stride;
            this.offset = offset;
        }

        public T Get<T>(int index) where T : unmanaged
        {
            return data.ReadAs<T>(index * stride + offset);
        }

        public (JobHandle, NativeArray<T>) read<T>(Allocator alloc = Allocator.TempJob) where T : unmanaged
        {
            var job = new ReadJob<T>(this, alloc);
            return (job.Schedule(count, 512), job.target);
        }
    }

    public struct ReadJob<T> : IJobParallelFor where T : unmanaged
    {
        [ReadOnly] private PropertyView view;

        [WriteOnly] public NativeArray<T> target;

        public ReadJob(PropertyView view, Allocator alloc)
        {
            this.view = view;
            this.target = new NativeArray<T>(view.count, alloc, NativeArrayOptions.UninitializedMemory);
        }

        public void Execute(int index)
        {
            target[index] = view.Get<T>(index);
        }
    }

    // Added TSRData struct to hold the Translation, Scale, Rotation data
    public struct TSRData
    {
        public bool hasTSRData;
        public Vector3 translation;
        public Vector3 scale;
        public Quaternion rotation;
        public Texture2D[] boundaryTextures;

        public TSRData(
            bool hasTSRData,
            Vector3 translation,
            Vector3 scale,
            Quaternion rotation,
            Texture2D[] boundaryTextures
)
        {
            this.hasTSRData = hasTSRData;
            this.translation = translation;
            this.scale = scale;
            this.rotation = rotation;
            this.boundaryTextures = boundaryTextures ?? new Texture2D[0];
        }
    }

    public static class PlyDataParser
    {
        public const int MAX_HEADER_LINES = 256;
        private enum HeaderLineKind
        {
            Element,
            Property,
            Comment,
            EndHeader,
        }

        private static HeaderLineKind line_kind_from_name(string name)
        {
            return name switch {
                "element" => HeaderLineKind.Element,
                "property" => HeaderLineKind.Property,
                "comment" => HeaderLineKind.Comment,
                "end_header" => HeaderLineKind.EndHeader,
                _ => throw new ArgumentException("Unknown header line kind. " + name),
            };
        }

        public static DataType data_type_from_name(string name)
        {
            return name switch {
                "float" => DataType.Float,
                "uchar" => DataType.UChar,
                "uint" => DataType.UInt,
                _ => throw new ArgumentException("Unknown data type. " + name),
            };
        }

        public static int byte_size(this DataType data_type)
        {
            return data_type switch {
                DataType.Float => 4,
                DataType.UChar => 1,
                DataType.UInt => 4,
                _ => throw new ArgumentException("Unknown data type size. " + data_type),
            };
        }

        public static (Element[], int, TSRData) read_header(Stream stream)
        {
            var reader = new StreamReader(stream);
            var read_count = 0;
            string read_next_line()
            {
                var line = reader.ReadLine();
                read_count += line.Length + 1;   // line + '\n'
                return line;
            }

            {
                // validate header
                if (read_next_line() != "ply")
                    throw new ArgumentException("Magic number ('ply') mismatch.");
                var format = read_next_line();
                if (format != "format binary_little_endian 1.0")
                    throw new ArgumentException("Invalid data format ('" + format + "'). Should be binary/little endian.");
            }

            var elements = new List<Element>();
            string name = "";
            int count = -1;
            List<Property> properties = new();

            // Initialize TSR data components
            bool hasTSRData = false;
            Vector3 translation = Vector3.zero;
            Vector3 scale = Vector3.one;
            Quaternion rotation = Quaternion.identity;

            void add_current_element()
            {
                if (count != -1)
                    elements.Add(new Element { name = name, count = count, properties = properties.ToArray() });
            }

            // For storing boundary textures
            Dictionary<int, string> textureDataDict = new Dictionary<int, string>();
            string texturePrefix = "";

            for (int line = 0; line < MAX_HEADER_LINES; ++line) {
                var col = read_next_line().Split();
                var kind = line_kind_from_name(col[0]);
                if (kind == HeaderLineKind.Element) {
                    add_current_element();
                    name = col[1];
                    count = Convert.ToInt32(col[2]);
                    properties.Clear();
                } else if (kind == HeaderLineKind.Property) {
                    properties.Add(new Property { name = col[2], data_type = data_type_from_name(col[1]) });                    
                } else if (kind == HeaderLineKind.Comment) {
                    // Parse comment lines for TSR data
                    if (col.Length >= 3) {
                        // Check for Translation (center) data
                        if (col[1] == "bb_center_x") {
                            hasTSRData = true;
                            translation.x = float.Parse(col[2]);
                        } else if (col[1] == "bb_center_y") {
                            translation.y = float.Parse(col[2]);
                        } else if (col[1] == "bb_center_z") {
                            translation.z = float.Parse(col[2]);
                        }
                        // Check for Scale (size) data
                        else if (col[1] == "bb_size_x") {
                            scale.x = float.Parse(col[2]);
                        } else if (col[1] == "bb_size_y") {
                            scale.y = float.Parse(col[2]);
                        } else if (col[1] == "bb_size_z") {
                            scale.z = float.Parse(col[2]);
                        }
                        // Check for Rotation data
                        else if (col[1] == "bb_rotation_x") {
                            rotation.x = float.Parse(col[2]);
                        } else if (col[1] == "bb_rotation_y") {
                            rotation.y = float.Parse(col[2]);
                        } else if (col[1] == "bb_rotation_z") {
                            rotation.z = float.Parse(col[2]);
                        } else if (col[1] == "bb_rotation_w") {
                            rotation.w = float.Parse(col[2]);
                        }
                    }
                } else if (col[1] == "boundary_texture_prefix") {
                    texturePrefix = col[2];
                } else if (col[1].StartsWith("boundary_texture_") && col[1].Contains("_data")) {
                        // Extract texture index from boundary_texture_X_data
                        string[] parts = col[1].Split('_');
                        if (parts.Length >= 3 && int.TryParse(parts[2], out int textureIndex))
                        {
                            // Collect the base64 data
                            string textureData = col[2]; // Or join remaining columns if needed
                            textureDataDict[textureIndex] = textureData;
                        }
                } else if (kind == HeaderLineKind.EndHeader) {
                    add_current_element();
                    break;
                } 
            }

            // After the header parsing loop, before returning:
            Texture2D[] textures = new Texture2D[6]; // Assuming 6 textures for cube faces

            // Convert base64 data to textures
            foreach (var pair in textureDataDict)
            {
                try
                {
                    int index = pair.Key;
                    if (index >= 0 && index < 6)
                    {
                        byte[] textureBytes = Convert.FromBase64String(pair.Value);
                        Texture2D texture = new Texture2D(2, 2);
                        texture.LoadImage(textureBytes);
                        textures[index] = texture;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Failed to decode texture {pair.Key}: {e.Message}");
                }
            }

            // Create a TSRData struct to return with the header information
            TSRData tsrData = new TSRData(
                hasTSRData, 
                translation, 
                scale, 
                rotation,
                textures
            );
            
            return (elements.ToArray(), read_count, tsrData);
        }

        public static NativeArray<byte> read_binary_blob(Stream stream, int binary_offset, Allocator alloc = Allocator.Persistent)
        {
            stream.Seek(binary_offset, SeekOrigin.Begin);
            var buffer = new NativeArray<byte>((int)stream.Length - binary_offset, alloc, NativeArrayOptions.UninitializedMemory);
            if (stream.Read(buffer) != buffer.Length) {
                buffer.Dispose();
                throw new IOException("Incomplete binary read.");
            }
            return buffer;
        }
    }
}