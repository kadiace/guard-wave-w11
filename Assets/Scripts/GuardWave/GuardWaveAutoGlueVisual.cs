using System.Collections.Generic;

using UnityEngine;
using UnityEngine.Rendering;

namespace GuardWave
{
    /// <summary>
    /// Single-mesh, render-only glue bulging across an automatically joined
    /// Flood contact. Irregular droplets use the existing Mud material,
    /// without generating physics colliders or per-droplet renderers.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GuardWaveAutoGlueVisual : MonoBehaviour
    {
        private const int LONGITUDES = 10;
        private const int LATITUDES = 6;
        private const int MATERIAL_COUNT = 2;

        private readonly List<Vector3> _vertices = new List<Vector3>(950);
        private readonly List<int>[] _triangles =
            { new List<int>(2400), new List<int>(900) };

        private GuardWaveMudPatch _patch;
        private GuardWavePiece _bondedPiece;
        private Mesh _mesh;

        /// <summary>
        /// Replaces the old mud oval with a cluster of irregular rounded blobs.
        /// All geometry is local to the contact and follows the support body.
        /// </summary>
        public void Initialize(GuardWaveMudPatch patch, GuardWavePiece bondedPiece)
        {
            _patch = patch;
            _bondedPiece = bondedPiece;

            foreach (MeshRenderer oldRenderer in GetComponentsInChildren<MeshRenderer>())
                oldRenderer.enabled = false;

            GameObject visual = new GameObject("Mud Glue Bulge (Visual Only)");
            visual.transform.SetParent(transform, false);
            MeshFilter filter = visual.AddComponent<MeshFilter>();
            MeshRenderer renderer = visual.AddComponent<MeshRenderer>();
            _mesh = BuildMesh();
            filter.sharedMesh = _mesh;
            // Both faceted submeshes use the existing Mud.mat. Keep the
            // gel geometry, not the previous green emissive material.
            Material mud = Resources.Load<Material>("Materials/Mud");
            renderer.sharedMaterials = new[] { mud, mud };
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
        }

        /// <summary>
        /// Hide the static glue visual when its joint breaks; Undo restores
        /// the bond and reactivates the patch without any scale animation.
        /// </summary>
        private void Update()
        {
            if (_patch == null || _bondedPiece == null ||
                !_patch.HasBond(_bondedPiece))
            {
                gameObject.SetActive(false);
            }
        }

        /// <summary>Destroy the generated mesh when this contact is discarded permanently.</summary>
        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }

        /// <summary>Build one combined low-poly gel mesh with irregular rounded lobes.</summary>
        private Mesh BuildMesh()
        {
            // The thick core bridges up to the 0.35m side gap and visibly
            // protrudes from both faces like freshly squeezed glue.
            AddBlob(Vector3.zero, new Vector3(.29f, .25f, .25f), 0, 13);
            AddBlob(new Vector3(-.12f, .055f, .08f),
                new Vector3(.17f, .19f, .19f), 0, 17);
            AddBlob(new Vector3(.14f, -.04f, -.10f),
                new Vector3(.17f, .13f, .16f), 0, 19);
            AddBlob(new Vector3(.05f, .15f, .045f),
                new Vector3(.17f, .13f, .14f), 1, 23);

            // Radial globs spread over each contact face, at unequal angles.
            for (int i = 0; i < 11; i++)
            {
                float angle = Mathf.PI * 2f * i / 11f + 0.23f;
                float radial = 0.18f + 0.09f * Mathf.PerlinNoise(i + 9f, .77f);
                float elevation = (i % 2 == 0 ? 0.12f : -0.12f);
                AddBlob(new Vector3(Mathf.Cos(angle) * radial, elevation,
                        Mathf.Sin(angle) * radial),
                    new Vector3(.085f + .024f * (i % 3), .090f,
                        .075f + .02f * (i % 2)), i % 4 == 0 ? 1 : 0, i + 39);
            }

            // Smaller beads hint at glue squeezed out of the joint.
            for (int i = 0; i < 7; i++)
            {
                float angle = Mathf.PI * 2f * (i + .3f) / 7f;
                AddBlob(new Vector3(Mathf.Cos(angle) * .33f, -.045f,
                        Mathf.Sin(angle) * .28f),
                    new Vector3(.043f, .052f, .04f), 1, i + 67);
            }

            Mesh mesh = new Mesh { name = "Flood Auto Glue - Low Poly Gel" };
            mesh.SetVertices(_vertices);
            mesh.subMeshCount = MATERIAL_COUNT;
            for (int i = 0; i < MATERIAL_COUNT; i++)
                mesh.SetTriangles(_triangles[i], i);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Append one slightly irregular faceted ellipsoid to a selected submesh.
        /// Independent triangles keep the shape low-poly without smoothed normals.
        /// </summary>
        private void AddBlob(Vector3 center, Vector3 radii, int material, int seed)
        {
            for (int y = 0; y < LATITUDES; y++)
            for (int x = 0; x < LONGITUDES; x++)
            {
                Vector3 a = BlobPoint(center, radii, x, y, seed);
                Vector3 b = BlobPoint(center, radii, x + 1, y, seed);
                Vector3 c = BlobPoint(center, radii, x + 1, y + 1, seed);
                Vector3 d = BlobPoint(center, radii, x, y + 1, seed);
                AddTriangle(a, b, c, material);
                AddTriangle(a, c, d, material);
            }
        }

        /// <summary>Sample an uneven ellipsoid surface along its polar and azimuth angles.</summary>
        private static Vector3 BlobPoint(Vector3 center, Vector3 radii,
            int longitude, int latitude, int seed)
        {
            float phi = Mathf.PI * latitude / LATITUDES;
            float theta = Mathf.PI * 2f * longitude / LONGITUDES;
            float variation = 0.92f + 0.14f *
                Mathf.PerlinNoise(longitude % LONGITUDES * .53f + seed,
                    latitude * .71f + 17f);
            return center + new Vector3(
                Mathf.Cos(theta) * Mathf.Sin(phi) * radii.x * variation,
                Mathf.Cos(phi) * radii.y * variation,
                Mathf.Sin(theta) * Mathf.Sin(phi) * radii.z * variation);
        }

        /// <summary>Add one independent outward-facing triangle for flat shading.</summary>
        private void AddTriangle(Vector3 a, Vector3 b, Vector3 c, int material)
        {
            int index = _vertices.Count;
            _vertices.Add(a);
            _vertices.Add(b);
            _vertices.Add(c);
            _triangles[material].Add(index);
            _triangles[material].Add(index + 1);
            _triangles[material].Add(index + 2);
        }

    }
}
