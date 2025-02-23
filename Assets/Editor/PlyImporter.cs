using System;
using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Ply
{
    [ScriptedImporter(1, "ply")]
    class PlyImporter : ScriptedImporter
    {

        public override void OnImportAsset(AssetImportContext context)
        {
            var data = ImportPly(context.assetPath);
            if (data != null) {
                context.AddObjectToAsset("content", data);
                context.AddObjectToAsset("binary", data.Binary);
                context.SetMainObject(data);
            }
        }

        const int READ_BUFFER_SIZE = 1024 * 1024 * 8; // 8 MB

        PlyData ImportPly(string path)
        {
            try {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, READ_BUFFER_SIZE, FileOptions.SequentialScan);
                var data = ScriptableObject.CreateInstance<PlyData>();
                data.ReadFromStream(stream);
                data.name = Path.GetFileNameWithoutExtension(path);
                return data;
            } catch (Exception e) {
                Debug.LogError("Failed importing " + path + ". " + e.Message);
                return null;
            }
        }


        // List<Element> ReadDataBody(List<ElementHeader> elements, BinaryReader reader)
        // {
        //     var elements_data = new List<Element>(elements.Count);
        //     foreach (var element in elements) {
        //         var property_count = element.properties.Count;
        //         var property_sizes = new List<int>(property_count);
        //         var properties = new List<Property>(property_count);
        //         foreach (var property in element.properties) {
        //             var element_size = data_type_size(property.data_type);
        //             Debug.Log(property.name + " " + element_size + " " + element.count);
        //             property_sizes.Add(element_size);
        //             properties.Add(new Property { name = property.name, type = property.data_type, data = new byte[element_size * element.count] });
        //         }
        //         // FIXME: this is so f***ing slow, but works..
        //         for (var i = 0; i < element.count; i++) {
        //             for (var p = 0; p < property_count; p++) {
        //                 var start = property_sizes[p] * i;
        //                 var end = property_sizes[p] * (i + 1);
        //                 for (var n = start; n < end; n++) {
        //                     properties[p].data[n] = reader.ReadByte();
        //                 }
        //             }
        //         }
        //         elements_data.Add(new Element { name = element.name, count = element.count, properties = properties });
        //     }
        //     return elements_data;
        // }
    }

}