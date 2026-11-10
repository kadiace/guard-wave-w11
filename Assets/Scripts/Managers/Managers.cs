using UnityEngine;

public class Managers : MonoBehaviour
{
    private static Managers _instance;
    public static Managers Instance { get { Init(); return _instance; } }

    #region Core
    private readonly ResourceManager _resourceManager = new();
    private readonly PoolManager _poolManager = new();
    private readonly DataManager _dataManager = new();
    private readonly InputManager _inputManager = new();
    private readonly SceneManagerEx _sceneManager = new();
    private readonly SoundManager _soundManager = new();

    public static ResourceManager Resource => Instance._resourceManager;
    public static PoolManager Pool => Instance._poolManager;
    public static DataManager Data => Instance._dataManager;
    public static InputManager Input => Instance._inputManager;
    public static SceneManagerEx Scene => Instance._sceneManager;
    public static SoundManager Sound => Instance._soundManager;
    #endregion

    #region Contents
    private readonly GameStateManager _gameStateManager = new();
    private readonly SpawnManager _spawnManager = new();

    public static GameStateManager GameState => Instance._gameStateManager;
    public static SpawnManager Spawn => Instance._spawnManager;
    #endregion

    void Start()
    {
        Init();
    }

    private void OnDestroy()
    {
        if (_instance != this)
            return;

        _inputManager.Shutdown();
        _instance = null;
    }

    /// <summary>영속 매니저를 한 번 생성하고 각 시스템을 초기화한다.</summary>
    public static void Init()
    {
        if (_instance != null)
            return;

        GameObject go = GameObject.Find("@Manager");
        if (go == null)
        {
            go = new GameObject { name = "@Manager" };
            go.AddComponent<Managers>();
        }
        DontDestroyOnLoad(go);
        _instance = go.GetComponent<Managers>();

        _instance._poolManager.Init(_instance.transform);
        _instance._dataManager.Init();
        _instance._inputManager.Init();
        _instance._sceneManager.Init();
        _instance._soundManager.Init();
        _instance._gameStateManager.Init();
        _instance._spawnManager.Init();

        _instance._resourceManager.Instantiate("Prefabs/UIs/EventSystem");
    }

    public static void Clear()
    {
        Pool.Clear();
        GameState.Clear();
        Spawn.Clear();
    }
}
