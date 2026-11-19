using System;

using UnityEngine;

namespace Avalanche
{
    /// <summary>Shared height field used by terrain collision, snow and placement in Avalanche.</summary>
    public static class AvalancheTerrain
    {
        public const int WIDTH = 60;
        public const int LENGTH = 100;
        public const float CELL = 0.6f;
        public const float MIN_X = -18f;
        public const float MAX_Z = 60f;
        private static Settings _settings = new Settings();

        [Serializable]
        public sealed class Settings
        {
            [Header("Terrain slopes")]
            [SerializeField, Min(0f)] private float _villageSlope = 0.02f;
            [SerializeField, Min(0f)] private float _middleSlope = 0.12f;
            [SerializeField, Min(0f)] private float _sourceSlope = 0.36f;
            [SerializeField, Min(0f)] private float _valleyDepth = 0.8f;
            [SerializeField, Min(0.1f)] private float _valleyWidth = 3f;
            public float VillageSlope => _villageSlope;
            public float MiddleSlope => _middleSlope;
            public float SourceSlope => _sourceSlope;
            public float ValleyDepth => _valleyDepth;
            public float ValleyWidth => _valleyWidth;
        }

        /// <summary>Uses scene-authored terrain parameters for collision, rendering and snow calculations.</summary>
        public static void Configure(Settings settings) => _settings = settings;

        /// <summary>Returns the world-space bed elevation, including the slope and side valleys.</summary>
        public static float Height(float x, float z)
        {
            float height = _settings.VillageSlope * z + (_settings.MiddleSlope - _settings.VillageSlope) * Ramp(z - 24f)
                + (_settings.SourceSlope - _settings.MiddleSlope) * Ramp(z - 42f);
            float distance = (Mathf.Abs(x) - 11f) / _settings.ValleyWidth;
            float fade = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(4f, 24f, z))
                * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(50f, 60f, z)));
            return height - _settings.ValleyDepth * Mathf.Exp(-distance * distance) * fade;
        }

        /// <summary>Smoothly joins neighbouring terrain slopes over 1.2 metres.</summary>
        private static float Ramp(float distance)
        {
            if (distance <= -0.6f) return 0f;
            if (distance >= 0.6f) return distance;
            return (distance + 0.6f) * (distance + 0.6f) / 2.4f;
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
                Color soil = Color.Lerp(new Color(0.85f, 0.90f, 0.94f), new Color(0.52f, 0.63f, 0.73f), Mathf.InverseLerp(2f, 10f, y));
                float variation = Mathf.PerlinNoise(wx * 0.38f + 20f, wz * 0.38f) * 0.08f;
                colors[i] = soil * (0.94f + variation);
                colors[i].a = 1f;
                if (x == WIDTH || z == LENGTH) continue;
                int t = (z * WIDTH + x) * 6;
                indices[t] = i; indices[t + 1] = i + stride; indices[t + 2] = i + 1;
                indices[t + 3] = i + 1; indices[t + 4] = i + stride; indices[t + 5] = i + stride + 1;
            }
            var mesh = new Mesh { name = "Avalanche mountain terrain", vertices = vertices, colors = colors, triangles = indices };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
