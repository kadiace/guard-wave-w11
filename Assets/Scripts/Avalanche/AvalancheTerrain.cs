using UnityEngine;

namespace Avalanche
{
    /// <summary>Shared height field used by terrain collision, water and placement in Avalanche.</summary>
    public static class AvalancheTerrain
    {
        public const int WIDTH = 60;
        public const int LENGTH = 100;
        public const float CELL = 0.6f;
        public const float MIN_X = -18f;
        public const float MAX_Z = 60f;

        /// <summary>Returns the world-space bed elevation, including channels and citizen terraces.</summary>
        public static float Height(float x, float z)
        {
            float split = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(42f, 30f, z));
            float center = 7f * split;
            float halfWidth = Mathf.Lerp(5.8f, 2.1f, split);
            float distance = Mathf.Abs(Mathf.Abs(x) - center);
            float bank = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(halfWidth - 0.5f, halfWidth + 1f, distance));
            float height = Mathf.Lerp(-1f, 0.05f, bank) + 0.003f * z;
            height = Terrace(height, x, z, -10f, 26f, 0.25f);
            height = Terrace(height, x, z, 0f, 16f, 0.35f);
            return Terrace(height, x, z, 10f, 14f, 0.25f);
        }

        /// <summary>Blends a three-metre citizen terrace into the surrounding ground.</summary>
        private static float Terrace(float height, float x, float z, float tx, float tz, float elevation)
        {
            float distance = Mathf.Max(Mathf.Abs(x - tx), Mathf.Abs(z - tz));
            float blend = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.5f, 2.2f, distance));
            return Mathf.Lerp(height, elevation, blend);
        }

        /// <summary>Builds the coloured terrain mesh; the same mesh is used by the ground collider.</summary>
        public static Mesh CreateMesh()
        {
            int stride = WIDTH + 1;
            var vertices = new Vector3[stride * (LENGTH + 1)];
            var colors = new Color[vertices.Length];
            var indices = new int[WIDTH * LENGTH * 6];
            for (int z = 0; z <= LENGTH; z++)
            for (int x = 0; x <= WIDTH; x++)
            {
                int i = z * stride + x;
                float wx = MIN_X + x * CELL;
                float wz = z * CELL;
                float y = Height(wx, wz);
                vertices[i] = new Vector3(wx, y, wz);
                Color soil = Color.Lerp(new Color(0.32f, 0.41f, 0.39f), new Color(0.55f, 0.65f, 0.49f), Mathf.InverseLerp(-0.9f, 0.2f, y));
                float variation = Mathf.PerlinNoise(wx * 0.38f + 20f, wz * 0.38f) * 0.08f;
                colors[i] = soil * (0.94f + variation);
                colors[i].a = 1f;
                if (x == WIDTH || z == LENGTH) continue;
                int t = (z * WIDTH + x) * 6;
                indices[t] = i; indices[t + 1] = i + stride; indices[t + 2] = i + 1;
                indices[t + 3] = i + 1; indices[t + 4] = i + stride; indices[t + 5] = i + stride + 1;
            }
            var mesh = new Mesh { name = "Avalanche river terrain", vertices = vertices, colors = colors, triangles = indices };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
