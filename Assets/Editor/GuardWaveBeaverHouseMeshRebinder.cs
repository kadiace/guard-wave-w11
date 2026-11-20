using UnityEditor;
using UnityEngine;

namespace GuardWave.Editor
{
    /// <summary>
    /// Binds the imported static OBJ mesh to the beaver house prefab.
    /// Unity 6 may assign non-legacy local IDs to imported mesh sub-assets.
    /// This editor-only script does not generate or modify geometry at runtime.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class GuardWaveBeaverHouseMeshRebinder : AssetPostprocessor
    {
        private const string MODEL_PATH = "Assets/Resources/Modelings/beaver_house.obj";
        private const string PREFAB_PATH = "Assets/Resources/Prefabs/Targets/beaver_house.prefab";

        private static bool _scheduled;

        static GuardWaveBeaverHouseMeshRebinder()
        {
            Schedule();
        }

        private static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            foreach (string path in importedAssets)
            {
                if (path != MODEL_PATH && path != PREFAB_PATH)
                    continue;
                Schedule();
                break;
            }
        }

        /// <summary>Defers prefab rebinding until the current import or domain reload completes.</summary>
        private static void Schedule()
        {
            if (_scheduled)
                return;

            _scheduled = true;
            EditorApplication.delayCall += () =>
            {
                _scheduled = false;
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    RepairPrefab();
            };
        }

        /// <summary>Resolves the imported mesh by asset reference instead of assuming a local file ID.</summary>
        [MenuItem("Tools/Guard Wave/Repair Beaver House Mesh")]
        private static void RepairPrefab()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            Mesh mesh = null;
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(MODEL_PATH))
            {
                if (asset is Mesh candidate && candidate.vertexCount > 0)
                {
                    mesh = candidate;
                    break;
                }
            }

            if (mesh == null)
                return;

            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(PREFAB_PATH);
            if (source == null || source.GetComponent<MeshFilter>().sharedMesh == mesh)
                return;

            GameObject root = PrefabUtility.LoadPrefabContents(PREFAB_PATH);
            try
            {
                root.GetComponent<MeshFilter>().sharedMesh = mesh;
                PrefabUtility.SaveAsPrefabAsset(root, PREFAB_PATH);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
