using UnityEngine.SceneManagement;

public class SceneManagerEx
{
    public Scene PendingScene { get; private set; } = Scene.Unknown;
    public Scene CurrentScene => ParseScene(SceneManager.GetActiveScene().name);

    public void Init()
    {
        PendingScene = CurrentScene;
    }

    public void ReloadCurrentScene()
    {
        Load(CurrentScene);
    }

    public void Load(Scene scene)
    {
        PendingScene = scene;
        SceneManager.LoadScene(ToSceneName(scene));
    }

    public bool IsCurrentScene(Scene scene)
    {
        return CurrentScene == scene;
    }

    private static string ToSceneName(Scene scene)
    {
        return scene.ToString();
    }

    private static Scene ParseScene(string sceneName)
    {
        if (System.Enum.TryParse(sceneName, out Scene scene))
            return scene;

        return Scene.Unknown;
    }
}
