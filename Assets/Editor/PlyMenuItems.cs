using UnityEngine;
using UnityEditor;
using System.IO;
using Ply;

public static class PlyMenuItems
{
    // Import PLY file by creating a copy in the project and letting PlyImporter handle it
    [MenuItem("Assets/Create/PLY/Import PLY File")]
    public static void ImportPlyFile()
    {
        string plyPath = EditorUtility.OpenFilePanel("Select PLY File", "", "ply");
        if (string.IsNullOrEmpty(plyPath))
            return;
            
        string fileName = Path.GetFileName(plyPath);
        string targetPath = Path.Combine("Assets", fileName);
        
        // Make sure we have a unique path
        targetPath = AssetDatabase.GenerateUniqueAssetPath(targetPath);
        
        try
        {
            // Copy the PLY file to the project folder
            File.Copy(plyPath, targetPath);
            
            // Refresh the asset database to detect the new file
            AssetDatabase.Refresh();
            
            // Let the PlyImporter handle the import process
            Object importedAsset = AssetDatabase.LoadAssetAtPath<Object>(targetPath);
            if (importedAsset != null)
            {
                Selection.activeObject = importedAsset;
                EditorGUIUtility.PingObject(importedAsset);
                Debug.Log($"Successfully imported PLY file: {fileName}");
            }
            else
            {
                Debug.LogWarning($"File was copied but import may have failed: {targetPath}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to import PLY file: {e.Message}");
            EditorUtility.DisplayDialog("Import Failed", $"Could not import PLY file: {e.Message}", "OK");
        }
    }
    
    // Add a PLY Scene Proxy component to a GameObject
    [MenuItem("GameObject/3D Object/PLY/PLY Scene Proxy", false, 10)]
    public static void CreatePlySceneProxy()
    {
        GameObject go = new GameObject("PLY Scene Proxy");
        PlySceneProxy proxy = go.AddComponent<PlySceneProxy>();
        PlyRenderer renderer = go.AddComponent<PlyRenderer>();
        
        // Set the object as selected
        Selection.activeGameObject = go;
        
        // Prompt to assign a PLY file
        bool shouldAssign = EditorUtility.DisplayDialog("Assign PLY Data?", 
            "Would you like to assign a PLY file to this proxy?", "Yes", "No");
            
        if (shouldAssign)
        {
            // Use the object picker to select an existing PLY asset
            EditorGUIUtility.ShowObjectPicker<PlyData>(null, false, "", 0);
            EditorApplication.ExecuteMenuItem("Window/General/Project");
            
            // Register for the object picker closed event
            EditorApplication.delayCall += () => {
                Object selectedObject = EditorGUIUtility.GetObjectPickerObject();
                
                if (selectedObject != null && selectedObject is PlyData plyData)
                {
                    proxy.SourceData = plyData;
                    EditorUtility.SetDirty(proxy);
                    Debug.Log($"Assigned PLY data: {plyData.name} to PLY Scene Proxy");
                }
                else if (EditorUtility.DisplayDialog("Import New PLY?", 
                    "No PLY data selected. Would you like to import a new PLY file?", "Yes", "No"))
                {
                    // Import a new file
                    ImportPlyFileForProxy(proxy);
                }
            };
        }
    }
    
    private static void ImportPlyFileForProxy(PlySceneProxy proxy)
    {
        string plyPath = EditorUtility.OpenFilePanel("Select PLY File", "", "ply");
        if (string.IsNullOrEmpty(plyPath))
            return;
            
        string fileName = Path.GetFileName(plyPath);
        string targetPath = Path.Combine("Assets", fileName);
        
        // Make sure we have a unique path
        targetPath = AssetDatabase.GenerateUniqueAssetPath(targetPath);
        
        try
        {
            // Copy the PLY file to the project folder
            File.Copy(plyPath, targetPath);
            
            // Refresh the asset database to detect the new file
            AssetDatabase.Refresh();
            
            // Let the PlyImporter handle the import process
            PlyData plyData = AssetDatabase.LoadAssetAtPath<PlyData>(targetPath);
            if (plyData != null)
            {
                // Assign to proxy
                proxy.SourceData = plyData;
                proxy.ForceRefresh();
                EditorUtility.SetDirty(proxy);
                
                Debug.Log($"Successfully imported PLY file: {fileName} and assigned to PLY Scene Proxy");
            }
            else
            {
                Debug.LogWarning($"File was copied but import may have failed: {targetPath}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to import PLY file: {e.Message}");
            EditorUtility.DisplayDialog("Import Failed", $"Could not import PLY file: {e.Message}", "OK");
        }
    }
    
    // Create a quick PLY viewer with the scene proxy system
    [MenuItem("GameObject/3D Object/PLY/Quick Import and View", false, 11)]
    public static void QuickImportAndViewPlyFile()
    {
        string plyPath = EditorUtility.OpenFilePanel("Select PLY File", "", "ply");
        if (string.IsNullOrEmpty(plyPath))
            return;
            
        string fileName = Path.GetFileName(plyPath);
        string targetPath = Path.Combine("Assets", fileName);
        
        // Make sure we have a unique path
        targetPath = AssetDatabase.GenerateUniqueAssetPath(targetPath);
        
        try
        {
            // Copy the PLY file to the project folder
            File.Copy(plyPath, targetPath);
            
            // Refresh the asset database to detect the new file
            AssetDatabase.Refresh();
            
            // Let the PlyImporter handle the import process
            PlyData plyData = AssetDatabase.LoadAssetAtPath<PlyData>(targetPath);
            if (plyData != null)
            {
                // Create a new GameObject with the PLY scene proxy
                GameObject go = new GameObject(Path.GetFileNameWithoutExtension(plyPath) + " Proxy");
                PlySceneProxy proxy = go.AddComponent<PlySceneProxy>();
                PlyRenderer renderer = go.AddComponent<PlyRenderer>();
                
                // Assign to proxy
                proxy.SourceData = plyData;
                
                // Set the object as selected
                Selection.activeGameObject = go;
                
                // Focus scene view on the new object
                SceneView.lastActiveSceneView.FrameSelected();
                
                Debug.Log($"Successfully imported PLY file: {fileName} and created proxy");
            }
            else
            {
                Debug.LogWarning($"File was copied but import may have failed: {targetPath}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to import PLY file: {e.Message}");
            EditorUtility.DisplayDialog("Import Failed", $"Could not import PLY file: {e.Message}", "OK");
        }
    }
}