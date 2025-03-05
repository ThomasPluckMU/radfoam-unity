using UnityEditor;

namespace Ply
{
    [CustomEditor(typeof(PlyData))]
    public sealed class PointCloudDataInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("elements"));
        }
    }
}