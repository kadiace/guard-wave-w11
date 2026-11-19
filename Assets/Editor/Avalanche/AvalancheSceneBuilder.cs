using System;
using System.Collections.Generic;
using System.IO;

using UnityEngine;
using UnityEngine.Rendering;

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering.Universal;

using GuardWave;

namespace Avalanche.Editor
{
    /// <summary>Authors the standalone prototype scene and its dedicated assets without touching Flood.</summary>
    public static class AvalancheSceneBuilder
    {
        private const string SCENE_PATH = "Assets/Scenes/Avalanche.unity";
        private const string ASSET_ROOT = "Assets/Avalanche";

        /// <summary>Opens the existing Avalanche scene or creates it once, preserving any edited scene content.</summary>
        [MenuItem("Tools/Guard Wave/Open Avalanche Prototype")]
        public static void OpenOrCreate()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode before opening Avalanche.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (File.Exists(SCENE_PATH)) { EditorSceneManager.OpenScene(SCENE_PATH); return; }
            Create();
        }

        /// <summary>Creates a village with terrain, dedicated art and a playable stage context.</summary>
        public static void Create()
        {
            if (File.Exists(SCENE_PATH)) throw new InvalidOperationException("Avalanche already exists; creation never overwrites a scene.");
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty)
                throw new InvalidOperationException("Save the current scene before creating Avalanche.");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.7f, 0.78f, 0.8f);
            RenderSettings.fog = false;
            var groundMaterial = Material("Riverbed", new Color(0.6f, 0.7f, 0.6f), Shader.Find("Avalanche/Coloured terrain"));
            var waterMaterial = Material("Water", Color.white, Shader.Find("Avalanche/Flow surface"));
            var panel = Material("Panel", new Color(0.89f, 0.59f, 0.29f));
            var stone = Material("Stone", new Color(0.38f, 0.43f, 0.48f));
            var dark = Material("Structure", new Color(0.14f, 0.24f, 0.28f));
            var cream = Material("Plaster", new Color(0.86f, 0.85f, 0.7f));
            var red = Material("Terracotta", new Color(0.66f, 0.29f, 0.22f));
            var teal = Material("Teal", new Color(0.16f, 0.6f, 0.55f));
            var green = Material("Foliage", new Color(0.29f, 0.46f, 0.35f));
            var line = Material("Adhesive", Color.white, Shader.Find("Universal Render Pipeline/Particles/Unlit"));
            var preview = Material("Placement", new Color(0.25f, 0.9f, 0.7f, 0.45f));
            preview.SetFloat("_Surface", 1f);
            preview.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            preview.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            preview.SetFloat("_ZWrite", 0f);
            preview.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            preview.renderQueue = 3000;
            var friction = new PhysicsMaterial("Avalanche construction friction") { dynamicFriction = 0.7f, staticFriction = 0.85f, bounciness = 0f, frictionCombine = PhysicsMaterialCombine.Maximum };
            AssetDatabase.CreateAsset(friction, ASSET_ROOT + "/Friction.physicMaterial");
            Mesh terrainMesh = AvalancheTerrain.CreateMesh();
            AssetDatabase.CreateAsset(terrainMesh, ASSET_ROOT + "/Terrain.asset");
            var terrain = new GameObject("Terrain - shared water bed");
            terrain.AddComponent<MeshFilter>().sharedMesh = terrainMesh;
            terrain.AddComponent<MeshRenderer>().sharedMaterial = groundMaterial;
            MeshCollider collider = terrain.AddComponent<MeshCollider>();
            collider.sharedMesh = terrainMesh;
            collider.sharedMaterial = friction;
            Box("Diorama base", new Vector3(0, -1.9f, 30), new Vector3(40, 1.8f, 64), dark, null);
            var water = new GameObject("Water - live depth and flow");
            MeshFilter waterFilter = water.AddComponent<MeshFilter>();
            water.AddComponent<MeshRenderer>().sharedMaterial = waterMaterial;
            var previewWater = new AvalancheSnow(new AvalancheSnow.Settings());
            Mesh quietMesh = UnityEngine.Object.Instantiate(previewWater.Surface);
            quietMesh.name = "Avalanche quiet water preview";
            AssetDatabase.CreateAsset(quietMesh, ASSET_ROOT + "/QuietWater.asset");
            waterFilter.sharedMesh = quietMesh;
            UnityEngine.Object.DestroyImmediate(previewWater.Surface);
            var decor = new GameObject("Village scenery - outside simulation banks").transform;
            for (int side = -1; side <= 1; side += 2)
            {
                Box("Bank walkway", new Vector3(side * 19f, 0.02f, 30), new Vector3(2f, 0.3f, 60), cream, decor);
                for (int i = 0; i < 5; i++)
                {
                    float z = 6 + i * 10;
                    Tree(new Vector3(side * 21f, 0f, z), green, panel, decor);
                    if (i % 2 == 0) House(new Vector3(side * 23.6f, 0f, z + 4), cream, i % 4 == 0 ? red : teal, dark, decor);
                }
            }
            Market(new Vector3(-16.6f, 0.15f, 26), panel, red, cream, decor);
            BusStop(new Vector3(16.5f, 0.15f, 14), teal, dark, cream, decor);
            Box("Square bench", new Vector3(0, 0.45f, 12.6f), new Vector3(2.3f, 0.2f, 0.6f), panel, decor);
            for (int z = 7; z < 55; z += 8)
            {
                float branch = z > 34 ? 0f : 7f;
                Arrow(new Vector3(branch, AvalancheTerrain.Height(branch, z) + 0.04f, z), cream, decor);
                if (branch > 0f) Arrow(new Vector3(-branch, AvalancheTerrain.Height(-branch, z) + 0.04f, z), cream, decor);
            }
            var light = new GameObject("Soft morning light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.4f;
            light.color = new Color(1f, 0.93f, 0.8f);
            light.transform.rotation = Quaternion.Euler(48, -38, 0);
            light.shadows = LightShadows.Soft;
            var camera = new GameObject("Avalanche overview camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.orthographic = true;
            camera.orthographicSize = 36;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 220f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.68f, 0.78f, 0.8f);
            camera.transform.rotation = Quaternion.Euler(58, -12, 0);
            camera.transform.position = new Vector3(0, 0, 29) - camera.transform.forward * 90f;
            camera.gameObject.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = false;
            camera.gameObject.AddComponent<AudioListener>();
            camera.gameObject.AddComponent<StageCameraController>();
            var stage = new GameObject("Avalanche - stage context").AddComponent<AvalancheStage>();
            stage.Configure(camera, collider, waterFilter,
                AssetDatabase.LoadAssetAtPath<Font>("Assets/TextMesh Pro/Resources/Fonts & Materials/NotoSansKR.ttf"), panel, stone, preview, line, friction);
            AuthorCitizens(stage);
            EditorSceneManager.SaveScene(scene, SCENE_PATH);
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!scenes.Exists(s => s.path == SCENE_PATH)) scenes.Add(new EditorBuildSettingsScene(SCENE_PATH, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            AssetDatabase.SaveAssets();
            Selection.activeObject = stage.gameObject;
        }

        /// <summary>Updates the existing scene to the authored snow terrain and winter presentation while preserving its scene identity.</summary>
        [MenuItem("Tools/Guard Wave/Rebuild Avalanche Snow Stage")]
        public static void RebuildSnowStage()
        {
            if (EditorApplication.isPlaying) return;
            var current = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (current.name != "Avalanche" && current.isDirty) { Debug.LogWarning("Save the open scene before rebuilding Avalanche."); return; }
            var scene = current.name == "Avalanche" ? current : EditorSceneManager.OpenScene(SCENE_PATH);
            AvalancheStage stage = UnityEngine.Object.FindFirstObjectByType<AvalancheStage>();
            AvalancheTerrain.Configure(stage.TerrainSettings);
            Mesh terrain = AvalancheTerrain.CreateMesh();
            Mesh savedTerrain = AssetDatabase.LoadAssetAtPath<Mesh>(ASSET_ROOT + "/Terrain.asset");
            EditorUtility.CopySerialized(terrain, savedTerrain);
            UnityEngine.Object.DestroyImmediate(terrain);
            MeshCollider ground = stage.Ground;
            ground.sharedMesh = savedTerrain;
            ground.GetComponent<MeshFilter>().sharedMesh = savedTerrain;
            ground.name = "Snow mountain - shared height field";
            foreach (MeshFilter filter in UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
            {
                if (!filter.name.StartsWith("Water -")) continue;
                filter.name = "Snow - moving and deposited layers";
                var snow = new AvalancheSnow(new AvalancheSnow.Settings());
                Mesh saved = AssetDatabase.LoadAssetAtPath<Mesh>(ASSET_ROOT + "/QuietWater.asset");
                EditorUtility.CopySerialized(snow.Surface, saved);
                filter.sharedMesh = saved;
                UnityEngine.Object.DestroyImmediate(snow.Surface);
            }
            Transform oldDecor = GameObject.Find("Village scenery - outside simulation banks")?.transform;
            if (oldDecor != null) UnityEngine.Object.DestroyImmediate(oldDecor.gameObject);
            Transform existing = GameObject.Find("Winter village scenery")?.transform;
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing.gameObject);
            Transform decor = new GameObject("Winter village scenery").transform;
            Material wood = AssetDatabase.LoadAssetAtPath<Material>(ASSET_ROOT + "/Panel.mat");
            Material plaster = AssetDatabase.LoadAssetAtPath<Material>(ASSET_ROOT + "/Plaster.mat");
            Material dark = AssetDatabase.LoadAssetAtPath<Material>(ASSET_ROOT + "/Structure.mat");
            Material green = AssetDatabase.LoadAssetAtPath<Material>(ASSET_ROOT + "/Foliage.mat");
            Material roof = AssetDatabase.LoadAssetAtPath<Material>(ASSET_ROOT + "/Teal.mat");
            for (int side = -1; side <= 1; side += 2)
            {
                for (int i = 0; i < 7; i++)
                {
                    Vector3 p = new Vector3(side * 20f, 0f, 7f + i * 7f);
                    p.y = AvalancheTerrain.Height(p.x, p.z);
                    Tree(p, green, wood, decor);
                }
            }
            House(new Vector3(-7.5f, AvalancheTerrain.Height(-7.5f, 17f), 17f), plaster, roof, dark, decor);
            House(new Vector3(7.5f, AvalancheTerrain.Height(7.5f, 18f), 18f), plaster, roof, dark, decor);
            Box("Village bench", new Vector3(0f, AvalancheTerrain.Height(0f, 11f) + 0.45f, 11f), new Vector3(2.3f, 0.2f, 0.6f), wood, decor);
            AuthorCitizens(stage);
            Camera.main.backgroundColor = new Color(0.57f, 0.66f, 0.76f);
            RenderSettings.ambientLight = new Color(0.72f, 0.79f, 0.88f);
            EditorUtility.SetDirty(savedTerrain);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, SCENE_PATH);
            AssetDatabase.SaveAssets();
            Debug.Log("[Avalanche] Snow stage scene rebuilt.");
        }

        /// <summary>Authors and registers the scene's protected citizens without rebuilding terrain or decoration.</summary>
        [MenuItem("Tools/Guard Wave/Set Up Avalanche Citizens")]
        public static void SetUpCitizens()
        {
            if (EditorApplication.isPlaying) return;
            OpenOrCreate();
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Avalanche") return;
            AvalancheStage stage = UnityEngine.Object.FindFirstObjectByType<AvalancheStage>();
            AvalancheTerrain.Configure(stage.TerrainSettings);
            AuthorCitizens(stage);
            EditorUtility.SetDirty(stage);
            EditorSceneManager.MarkSceneDirty(stage.gameObject.scene);
            EditorSceneManager.SaveScene(stage.gameObject.scene, SCENE_PATH);
            Selection.activeObject = stage.gameObject;
        }

        /// <summary>Preserves existing authored citizens, or creates the six starting objectives in the village.</summary>
        private static void AuthorCitizens(AvalancheStage stage)
        {
            AvalancheCitizen[] citizens = UnityEngine.Object.FindObjectsByType<AvalancheCitizen>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (citizens.Length > 0)
            {
                Array.Sort(citizens, (a, b) => a.ObjectiveId.CompareTo(b.ObjectiveId));
                stage.ConfigureCitizens(citizens);
                return;
            }
            Transform group = new GameObject("Protected citizens").transform;
            Vector2[] positions = { new Vector2(-4f, 14f), new Vector2(-4f, 18f), new Vector2(0f, 14f),
                new Vector2(0f, 18f), new Vector2(4f, 14f), new Vector2(4f, 18f) };
            string[] zones = { "산장", "광장", "정류장" };
            Material skin = AssetDatabase.LoadAssetAtPath<Material>(ASSET_ROOT + "/Plaster.mat");
            Material boots = AssetDatabase.LoadAssetAtPath<Material>(ASSET_ROOT + "/Structure.mat");
            citizens = new AvalancheCitizen[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                Transform root = new GameObject($"Citizen {i + 1:00} - {zones[i / 2]}").transform;
                root.SetParent(group, false);
                root.position = new Vector3(positions[i].x, AvalancheTerrain.Height(positions[i].x, positions[i].y) + 0.035f, positions[i].y);
                Transform visual = new GameObject("Visual").transform;
                visual.SetParent(root, false);
                CitizenVisual("Coat", PrimitiveType.Capsule, visual, Vector3.up * 0.85f, new Vector3(0.55f, 0.5f, 0.42f), stage.PanelMaterial);
                CitizenVisual("Head", PrimitiveType.Sphere, visual, Vector3.up * 1.48f, Vector3.one * 0.36f, skin);
                for (int side = -1; side <= 1; side += 2)
                {
                    CitizenVisual("Boot", PrimitiveType.Cube, visual, new Vector3(side * 0.15f, 0.2f, 0f), new Vector3(0.18f, 0.4f, 0.25f), boots);
                    CitizenVisual("Arm", PrimitiveType.Capsule, visual, new Vector3(side * 0.34f, 0.9f, 0f), new Vector3(0.15f, 0.27f, 0.15f), stage.PanelMaterial);
                }
                Renderer marker = CitizenVisual("Status beacon", PrimitiveType.Sphere, root, Vector3.up * 2.05f, Vector3.one * 0.23f, stage.LineMaterial).GetComponent<Renderer>();
                citizens[i] = root.gameObject.AddComponent<AvalancheCitizen>();
                citizens[i].Configure(i, zones[i / 2], visual, marker);
            }
            stage.ConfigureCitizens(citizens);
        }

        /// <summary>Authors a citizen's visual part without a collider or placement-ray participation.</summary>
        private static GameObject CitizenVisual(string name, PrimitiveType type, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            GameObject part = Primitive(name, type, Vector3.zero, scale, material, parent);
            part.layer = 2;
            part.transform.localPosition = position;
            return part;
        }

        /// <summary>Creates a dedicated material asset with a URP shader and no external dependencies.</summary>
        private static Material Material(string name, Color color, Shader shader = null)
        {
            var material = new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Lit")) { name = "Avalanche " + name };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.22f);
            AssetDatabase.CreateAsset(material, ASSET_ROOT + "/" + name + ".mat");
            return material;
        }

        /// <summary>Creates a purely visual primitive; physical terrain and buildable boxes are separate.</summary>
        private static GameObject Primitive(string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material, Transform parent)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        /// <summary>Creates a decorative box with the requested dimensions.</summary>
        private static GameObject Box(string name, Vector3 position, Vector3 scale, Material material, Transform parent) =>
            Primitive(name, PrimitiveType.Cube, position, scale, material, parent);

        /// <summary>Creates a simple tree outside the fluid domain.</summary>
        private static void Tree(Vector3 position, Material leaves, Material trunk, Transform parent)
        {
            Box("Tree trunk", position + Vector3.up * 1.1f, new Vector3(0.35f, 2.2f, 0.35f), trunk, parent);
            Primitive("Tree canopy", PrimitiveType.Sphere, position + Vector3.up * 3f, new Vector3(2.5f, 3.3f, 2.5f), leaves, parent);
        }

        /// <summary>Creates a compact riverside home outside the simulation banks.</summary>
        private static void House(Vector3 position, Material wall, Material roof, Material window, Transform parent)
        {
            Box("Riverside home", position + Vector3.up * 1.5f, new Vector3(4, 3, 4), wall, parent);
            Box("Roof", position + Vector3.up * 3.15f, new Vector3(4.5f, 0.45f, 4.5f), roof, parent);
            Box("Door", position + new Vector3(0, 0.9f, -2.02f), new Vector3(0.9f, 1.8f, 0.08f), window, parent);
            Box("Window", position + new Vector3(1.25f, 1.8f, -2.03f), new Vector3(0.85f, 0.9f, 0.08f), window, parent);
        }

        /// <summary>Creates a market stall that identifies the western citizen zone.</summary>
        private static void Market(Vector3 p, Material wood, Material awning, Material fabric, Transform parent)
        {
            Box("Market counter", p + Vector3.up * 0.55f, new Vector3(2, 1.1f, 2.6f), wood, parent);
            Box("Market awning", p + Vector3.up * 2.5f, new Vector3(2.7f, 0.18f, 3.2f), awning, parent);
            for (int i = -1; i <= 1; i += 2)
                Box("Canopy pole", p + new Vector3(0, 1.5f, i * 1.2f), new Vector3(0.1f, 2.4f, 0.1f), fabric, parent);
        }

        /// <summary>Creates an open bus shelter identifying the eastern citizen zone.</summary>
        private static void BusStop(Vector3 p, Material roof, Material frame, Material seat, Transform parent)
        {
            Box("Bus shelter roof", p + Vector3.up * 2.6f, new Vector3(2.3f, 0.18f, 3.4f), roof, parent);
            Box("Bus shelter seat", p + Vector3.up * 0.6f, new Vector3(0.8f, 0.18f, 2.8f), seat, parent);
            for (int i = -1; i <= 1; i += 2)
                Box("Shelter post", p + new Vector3(0.9f, 1.3f, i * 1.4f), new Vector3(0.12f, 2.6f, 0.12f), frame, parent);
        }

        /// <summary>Paints a static flow forecast on the channel bed, distinct from animated water.</summary>
        private static void Arrow(Vector3 p, Material material, Transform parent)
        {
            Box("Forecast direction", p, new Vector3(0.1f, 0.025f, 1.3f), material, parent);
            for (int i = -1; i <= 1; i += 2)
                Box("Forecast arrowhead", p + new Vector3(i * 0.22f, 0, -0.5f), new Vector3(0.1f, 0.025f, 0.7f), material, parent).transform.rotation = Quaternion.Euler(0, -i * 45, 0);
        }
    }
}
