using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Ply
{
    [ExecuteInEditMode]
    public class BoundingBoxHandle : MonoBehaviour
    {
        public PlySceneProxy parentProxy;
        private Vector3 lastPosition;
        private Quaternion lastRotation;
        private Vector3 lastScale;
        
        private void Awake()
        {
            lastPosition = transform.position;
            lastRotation = transform.rotation;
            lastScale = transform.localScale;
        }
        
        private void Update()
        {
            // Only check for changes in the editor
            #if UNITY_EDITOR
            if (!EditorApplication.isPlaying)
            {
                // Check if transform has changed
                if (transform.position != lastPosition || 
                    transform.rotation != lastRotation || 
                    transform.localScale != lastScale)
                {
                    // Update parent proxy
                    if (parentProxy != null)
                    {
                        parentProxy.SyncHandleTransform();
                    }
                    
                    // Update last values
                    lastPosition = transform.position;
                    lastRotation = transform.rotation;
                    lastScale = transform.localScale;
                }
            }
            #endif
        }
        
        #if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            // Draw a semi-transparent box to represent the filtering volume
            if (parentProxy != null && parentProxy.UseBoundingBoxFilter)
            {
                Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.25f);
                Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, transform.localScale);
                Gizmos.DrawCube(Vector3.zero, Vector3.one);
                Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 1.0f);
                Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
                Gizmos.matrix = Matrix4x4.identity;
            }
        }
        #endif
    }

    #if UNITY_EDITOR
    [CustomEditor(typeof(BoundingBoxHandle))]
    public class BoundingBoxHandleEditor : Editor
    {
        private void OnEnable()
        {
            // Make sure the handle gets the default transform tools
            Tools.hidden = false;
        }
        
        public override void OnInspectorGUI()
        {
            // Hide the default inspector - we don't want users modifying this directly
            EditorGUILayout.HelpBox("This is a handle for the bounding box filter. Use the transform tools to adjust it.", MessageType.Info);
            
            BoundingBoxHandle handle = (BoundingBoxHandle)target;
            if (handle.parentProxy != null)
            {
                if (GUILayout.Button("Select Parent Proxy"))
                {
                    Selection.activeGameObject = handle.parentProxy.gameObject;
                }
            }
        }
    }
    #endif
}