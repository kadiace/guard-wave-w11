using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// Unity 6 may remap the imported OBJ's local Mesh fileID, leaving the
    /// prefab's serialized MeshFilter/MeshCollider reference empty. Load the
    /// actual Mesh sub-asset by name rather than relying on a hardcoded ID.
    /// </summary>
    internal static class GuardWaveForestPebbleMesh
    {
        private const string ModelResourcePath = "Prefabs/Objects/ForestPebble";

        public static Mesh LoadImportedMesh()
        {
            Mesh[] meshes = Resources.LoadAll<Mesh>(ModelResourcePath);
            foreach (Mesh mesh in meshes)
                if (mesh != null && mesh.vertexCount > 0)
                    return mesh;

            // The main OBJ import asset is a model GameObject; its mesh may
            // be exposed through the imported hierarchy instead of LoadAll.
            GameObject model = Resources.Load<GameObject>(ModelResourcePath);
            if (model != null)
            {
                foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null && filter.sharedMesh.vertexCount > 0)
                        return filter.sharedMesh;
            }
            return null;
        }

        public static void EnsureMesh(GameObject instance)
        {
            if (instance == null) return;
            MeshFilter filter = instance.GetComponent<MeshFilter>();
            MeshCollider collider = instance.GetComponent<MeshCollider>();
            if (filter == null || collider == null) return;
            if (filter.sharedMesh != null && collider.sharedMesh != null) return;

            Mesh mesh = filter.sharedMesh != null
                ? filter.sharedMesh
                : collider.sharedMesh != null
                    ? collider.sharedMesh
                    : LoadImportedMesh();
            if (mesh == null)
            {
                Debug.LogError("Forest pebble model mesh is missing. Reimport "
                    + "Assets/Resources/Prefabs/Objects/ForestPebble.obj and its prefab.");
                return;
            }
            filter.sharedMesh = mesh;
            if (collider.sharedMesh == null)
                collider.sharedMesh = mesh;
        }
    }
}
