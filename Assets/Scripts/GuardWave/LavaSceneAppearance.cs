using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// Applies volcanic ground tint only to this scene's renderers.
    /// MaterialPropertyBlock prevents mutation of Flood's shared materials.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LavaSceneAppearance : MonoBehaviour
    {
        private static readonly int BASE_COLOR_ID = Shader.PropertyToID("_BaseColor");
        private static readonly int COLOR_ID = Shader.PropertyToID("_Color");

        [Header("Volcanic ground (scene instance only)")]
        [SerializeField] private Color _rockTint = new Color(0.24f, 0.17f, 0.15f, 1f);

        private void Start()
        {
            if (gameObject.scene.name != "Lava") return;

            foreach (GameObject root in gameObject.scene.GetRootGameObjects())
            {
                if (root.name != "GuardWave Creek Environment" &&
                    root.name != "GuardWaveCreekEnvironment")
                    continue;

                MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>(true);
                foreach (MeshRenderer renderer in renderers)
                {
                    if (renderer.gameObject.name.Contains("Water")) continue;

                    MaterialPropertyBlock block = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(block);
                    block.SetColor(BASE_COLOR_ID, _rockTint);
                    block.SetColor(COLOR_ID, _rockTint);
                    renderer.SetPropertyBlock(block);
                }
            }
        }
    }
}
