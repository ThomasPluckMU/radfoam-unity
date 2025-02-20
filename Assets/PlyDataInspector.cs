// Pcx - Point cloud importer & renderer for Unity
// https://github.com/keijiro/Pcx


using UnityEditor;


namespace Ply
{
    [CustomEditor(typeof(PlyData))]
    public sealed class PointCloudDataInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            // var count = ((PlyData)target).;
            // EditorGUILayout.LabelField("Point Count", count.ToString("N0"));
            EditorGUILayout.LabelField("PlyData");
        }
    }
}