// Pcx - Point cloud importer & renderer for Unity
// https://github.com/keijiro/Pcx

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Ply
{
    [ScriptedImporter(1, "ply")]
    class PlyImporter : ScriptedImporter
    {
        #region ScriptedImporter implementation

        public override void OnImportAsset(AssetImportContext context)
        {
            var data = ImportPly(context.assetPath);
            if (data != null) {
                context.AddObjectToAsset("content", data);
                context.SetMainObject(data);
            }
        }

        #endregion

        #region Internal data structure


        static int data_type_size(DataType data_type)
        {
            return data_type switch {
                DataType.Float => 4,
                DataType.UChar => 1,
                DataType.UInt => 4,
                _ => throw new ArgumentException("Unhandled data type."),
            };
        }

        struct PropertyHeader
        {
            public string name;
            public DataType data_type;
        }

        struct ElementHeader
        {
            public string name;
            public int count;
            public List<PropertyHeader> properties;
        }

        #endregion

        #region Reader implementation

        PlyData ImportPly(string path)
        {
            try {
                var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var header = ReadDataHeader(new StreamReader(stream));
                var elements = ReadDataBody(header, new BinaryReader(stream));
                var data = ScriptableObject.CreateInstance<PlyData>();
                data.Init(elements);
                data.name = Path.GetFileNameWithoutExtension(path);
                return data;
            } catch (Exception e) {
                Debug.LogError("Failed importing " + path + ". " + e.Message);
                return null;
            }
        }

        List<ElementHeader> ReadDataHeader(StreamReader reader)
        {
            var readCount = 0;

            {
                // Magic number line ("ply")
                var line = reader.ReadLine();
                readCount += line.Length + 1;
                if (line != "ply")
                    throw new ArgumentException("Magic number ('ply') mismatch.");

                // Data format: check if it's binary/little endian.
                line = reader.ReadLine();
                readCount += line.Length + 1;
                if (line != "format binary_little_endian 1.0")
                    throw new ArgumentException("Invalid data format ('" + line + "'). " + "Should be binary/little endian.");
            }


            var elements = new List<ElementHeader>();
            var element = new ElementHeader {
                name = "",
                count = -1,
                properties = new List<PropertyHeader>(),
            };

            while (true) {
                var line = reader.ReadLine();
                readCount += line.Length + 1;
                var col = line.Split();

                var end_header = col[0] == "end_header";
                var begin_element = col[0] == "element";
                var property = col[0] == "property";

                if (end_header || begin_element) {
                    if (element.count != -1) {
                        elements.Add(element);
                    }
                }

                if (end_header) {
                    break;
                }

                if (begin_element) {
                    element = new ElementHeader {
                        name = col[1],
                        count = Convert.ToInt32(col[2]),
                        properties = new List<PropertyHeader>(),
                    };
                }

                if (property) {
                    var data_type = col[1] switch {
                        "float" => DataType.Float,
                        "uchar" => DataType.UChar,
                        "uint" => DataType.UInt,
                        _ => throw new ArgumentException("Unknown data type. " + col[2]),
                    };
                    element.properties.Add(new PropertyHeader { name = col[2], data_type = data_type });
                }
            }

            // Rewind the stream back to the exact position of the reader.
            reader.BaseStream.Position = readCount;
            return elements;
        }

        List<Element> ReadDataBody(List<ElementHeader> elements, BinaryReader reader)
        {
            var elements_data = new List<Element>(elements.Count);

            foreach (var element in elements) {
                var property_count = element.properties.Count;
                var property_sizes = new List<int>(property_count);
                var properties = new List<Property>(property_count);
                foreach (var property in element.properties) {
                    var element_size = data_type_size(property.data_type);
                    Debug.Log(property.name + " " + element_size + " " + element.count);
                    property_sizes.Add(element_size);
                    properties.Add(new Property { name = property.name, type = property.data_type, data = new byte[element_size * element.count] });
                }

                // FIXME: this is so f***ing slow, but works..
                for (var i = 0; i < element.count; i++) {
                    for (var p = 0; p < property_count; p++) {
                        var start = property_sizes[p] * i;
                        var end = property_sizes[p] * (i + 1);
                        for (var n = start; n < end; n++) {
                            properties[p].data[n] = reader.ReadByte();
                        }
                    }
                }

                elements_data.Add(new Element { name = element.name, count = element.count, properties = properties });
            }

            return elements_data;
        }
    }

    #endregion
}