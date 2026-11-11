using UnityEngine;

using GuardWave;

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
    [SerializeField] private GuardWaveHud _hud;
    [SerializeField] private MeshFilter _floodMesh;

    public float DestructionBoundaryZ => _destructionBoundaryZ;
    public GameObject DefenseFlag => _defenseFlag;
    public GuardWaveHud Hud => _hud;
    public MeshFilter FloodMesh => _floodMesh;

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
