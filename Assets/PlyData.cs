
using System;
using System.Collections.Generic;
using System.IO;
using Unity.Burst;
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

        public Element[] Elements { get => elements; }
        public TextAsset Binary { get => binary; }


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


        public void ReadFromStream(Stream stream)
        {
            var (header, read_count) = PlyDataParser.read_header(stream);
            var blob = PlyDataParser.read_binary_blob(stream, read_count);
            blob.name = "binary_data";
            this.elements = header;
            this.binary = blob;
        }
    }

    public readonly struct PlyDataView : IDisposable
    {
        private readonly (string, PlyElementView)[] element_views;
        private readonly NativeArray<byte> blob;

        public PlyDataView(PlyData data)
        {
            blob = data.Binary.GetData<byte>();

            var element_offset = 0;
            element_views = new (string, PlyElementView)[data.Elements.Length];
            for (var e = 0; e < element_views.Length; e++) {
                var element = data.Elements[e];

                var element_count = element.count;
                var element_stride = element.byte_size();
                var element_size = element_count * element_stride;

                var property_offset = 0;
                var property_views = new (string, int)[element.properties.Length];
                for (var p = 0; p < property_views.Length; p++) {
                    property_views[p] = (element.properties[p].name, property_offset);
                    property_offset += element.properties[p].byte_size();
                }

                var element_view = new PlyElementView(blob.Slice(element_offset, element_size), element_count, element_stride, property_views);
                element_views[e] = (element.name, element_view);
                element_offset += element_size;
            }
        }

        public PlyElementView element_view(string element)
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
            if (blob.IsCreated)
                blob.Dispose();
        }
    }

    public readonly struct PlyElementView
    {
        private readonly (string, int)[] properties;
        public readonly int count;
        public readonly int stride;
        private readonly NativeSlice<byte> data;

        public PlyElementView(NativeSlice<byte> data, int count, int stride, (string, int)[] properties)
        {
            this.data = data;
            this.count = count;
            this.stride = stride;
            this.properties = properties;
        }

        public PlyPropertyView property_view(string property)
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
            return new PlyPropertyView(data, count, stride, offset);
        }

        public PlyPropertyView dummy_property_view()
        {
            return new PlyPropertyView(data, 0, 0, 0);
        }

        public (JobHandle, NativeArray<T>) read_property<T>(string property, Allocator alloc = Allocator.TempJob) where T : unmanaged
        {
            return property_view(property).read<T>(alloc);
        }
    }

    public readonly struct PlyPropertyView
    {
        [ReadOnly] public readonly NativeSlice<byte> data;
        public readonly int count;
        private readonly int stride;
        private readonly int offset;

        public PlyPropertyView(NativeSlice<byte> data, int count, int stride, int offset)
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
        [ReadOnly] private PlyPropertyView view;

        [WriteOnly] public NativeArray<T> target;

        public ReadJob(PlyPropertyView view, Allocator alloc)
        {
            this.view = view;
            this.target = new NativeArray<T>(view.count, alloc, NativeArrayOptions.UninitializedMemory);
        }

        public void Execute(int index)
        {
            target[index] = view.Get<T>(index);
        }
    }



    public static class PlyDataParser
    {
        private enum HeaderLineKind
        {
            Element,
            Property,
            EndHeader,
        }

        private static HeaderLineKind line_kind_from_name(string name)
        {
            return name switch {
                "element" => HeaderLineKind.Element,
                "property" => HeaderLineKind.Property,
                "end_header" => HeaderLineKind.EndHeader,
                _ => throw new ArgumentException("Unknown header line kind. " + name),
            };
        }

        public static PlyData.DataType data_type_from_name(string name)
        {
            return name switch {
                "float" => PlyData.DataType.Float,
                "uchar" => PlyData.DataType.UChar,
                "uint" => PlyData.DataType.UInt,
                _ => throw new ArgumentException("Unknown data type. " + name),
            };
        }

        public static int byte_size(this PlyData.DataType data_type)
        {
            return data_type switch {
                PlyData.DataType.Float => 4,
                PlyData.DataType.UChar => 1,
                PlyData.DataType.UInt => 4,
                _ => throw new ArgumentException("Unknown data type size. " + data_type),
            };
        }

        public static (PlyData.Element[], int) read_header(Stream stream)
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

            var elements = new List<PlyData.Element>();
            string name = "";
            int count = -1;
            List<PlyData.Property> properties = new();

            void add_current_element()
            {
                if (count != -1)
                    elements.Add(new PlyData.Element { name = name, count = count, properties = properties.ToArray() });
            }

            while (true) {
                var col = read_next_line().Split();
                var kind = line_kind_from_name(col[0]);
                if (kind == HeaderLineKind.Element) {
                    add_current_element();
                    name = col[1];
                    count = Convert.ToInt32(col[2]);
                    properties.Clear();
                } else if (kind == HeaderLineKind.Property) {
                    properties.Add(new PlyData.Property { name = col[2], data_type = data_type_from_name(col[1]) });
                } else if (kind == HeaderLineKind.EndHeader) {
                    add_current_element();
                    break;
                }
            }
            return (elements.ToArray(), read_count);
        }

        public static TextAsset read_binary_blob(Stream stream, int binary_offset)
        {
            var binary_length = (int)stream.Length - binary_offset;
            var buffer = new byte[binary_length];
            stream.Seek(binary_offset, SeekOrigin.Begin);
            stream.Read(buffer, 0, binary_length);
            return new TextAsset(buffer);
        }
    }
}
