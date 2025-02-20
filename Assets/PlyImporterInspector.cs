// Pcx - Point cloud importer & renderer for Unity
// https://github.com/keijiro/Pcx

using UnityEditor;
using UnityEditor.AssetImporters;

namespace Ply
{
    // Note: Not sure why but EnumPopup doesn't work in ScriptedImporterEditor,
    // so it has been replaced with a normal Popup control.

    [CustomEditor(typeof(PlyImporter))]
    class PlyImporterInspector : ScriptedImporterEditor
    {
        // SerializedProperty _containerType;

        // string[] _containerTypeNames;

        protected override bool useAssetDrawPreview { get { return false; } }

        public override void OnEnable()
        {
            base.OnEnable();

            // _containerType = serializedObject.FindProperty("_containerType");
            // _containerTypeNames = System.Enum.GetNames(typeof(PlyImporter.ContainerType));
        }

        public override void OnInspectorGUI()
        {

            base.ApplyRevertGUI();
        }
    }
}