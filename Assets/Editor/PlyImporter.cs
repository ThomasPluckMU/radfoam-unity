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
    }
}