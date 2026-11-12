using System;
using UnityEditor;
using UnityEngine;

namespace GuardWave.Editor
{
    /// <summary>
    /// Rebinds the actual Unity-imported OBJ mesh on editor startup and after
    /// model/prefab imports. No hardcoded OBJ fileID: Unity 6 may generate
    /// distinct 64-bit local IDs for imported mesh sub-assets.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class GuardWaveForestPebbleRebinder : AssetPostprocessor
    {
        private const string ModelPath =
            "Assets/Resources/Prefabs/Objects/ForestPebble.obj";
        private const string PrefabPath =
            "Assets/Resources/Prefabs/Objects/forest_pebble.prefab";
        private static bool _scheduled;

        static GuardWaveForestPebbleRebinder()
        {
            Schedule();
        }

        private static void OnPostprocessAllAssets(
            string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (string path in imported)
            {
                if (path != ModelPath && path != PrefabPath) continue;
                Schedule();
                break;
            }
        }

        private static void Schedule()
        {
            if (_scheduled) return;
            _scheduled = true;
            EditorApplication.delayCall += () =>
            {
                _scheduled = false;
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    RepairPrefab();
            };
        }

        [MenuItem("Tools/Guard Wave/Repair Forest Pebble Mesh")]
        private static void RepairPrefab()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            Mesh mesh = null;
            foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(ModelPath))
            {
                if (asset is Mesh candidate && candidate.vertexCount > 0)
                {
                    mesh = candidate;
                    break;
                }
            }
            if (mesh == null) return;

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null) return;
            MeshFilter sourceFilter = prefab.GetComponent<MeshFilter>();
            MeshCollider sourceCollider = prefab.GetComponent<MeshCollider>();
            if (sourceFilter == null || sourceCollider == null) return;
            if (sourceFilter.sharedMesh == mesh && sourceCollider.sharedMesh == mesh)
                return;

            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                MeshFilter filter = root.GetComponent<MeshFilter>();
                MeshCollider collider = root.GetComponent<MeshCollider>();
                if (filter == null || collider == null) return;
                filter.sharedMesh = mesh;
                collider.sharedMesh = null;
                collider.sharedMesh = mesh;
                collider.convex = true;
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
