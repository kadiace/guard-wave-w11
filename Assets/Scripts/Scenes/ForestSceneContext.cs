using UnityEngine;

using GuardWave;

[DefaultExecutionOrder(-100)]
public class ForestSceneContext : MonoBehaviour
{
    [Header("Object rotation")]
    [SerializeField, Min(0f)] private float _rotationSpeed = 90f;

    [Header("Camera")]
    [SerializeField, Min(0f)] private float _cameraYawPerScrollStep = 5f;

    [Header("Destruction boundary")]
    [SerializeField] private float _destructionBoundaryZ = -28.2f;

    [Header("Scene objects")]
    [SerializeField] private GameObject _defenseFlag;

    public float DestructionBoundaryZ => _destructionBoundaryZ;
    public GameObject DefenseFlag => _defenseFlag;
    public GuardWaveHud Hud { get; private set; }

    private void Awake()
    {
        GuardWaveHud hudPrefab = Resources.Load<GuardWaveHud>("Prefabs/UIs/GuardWaveHud");
        if (hudPrefab == null)
            throw new System.InvalidOperationException("Missing Resources/Prefabs/UIs/GuardWaveHud prefab.");
        Hud = Instantiate(hudPrefab);
    }

    private void Start()
    {
        Managers.Input.SetInputMode(InputMode.Player);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        GuardWaveGame game = GetComponent<GuardWaveGame>();
        game.RotationSpeed = _rotationSpeed;
        game.CameraYawPerScrollStep = _cameraYawPerScrollStep;
        Debug.Log("[ForestSceneContext] Game scene initialized.");
    }
}
