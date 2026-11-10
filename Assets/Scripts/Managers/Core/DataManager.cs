using System.IO;
using UnityEngine;

public class DataManager
{
    private const string DefaultSaveFileName = "savefile.json";

    public void Init()
    {
    }

    public void Save<T>(T data, string fileName = DefaultSaveFileName)
    {
        string json = JsonUtility.ToJson(data, true);
        File.WriteAllText(GetSavePath(fileName), json);
    }

    public T Load<T>(string fileName = DefaultSaveFileName) where T : new()
    {
        string path = GetSavePath(fileName);
        if (!File.Exists(path))
        {
            T newData = new();
            Save(newData, fileName);
            return newData;
        }

        string json = File.ReadAllText(path);
        return JsonUtility.FromJson<T>(json);
    }

    private string GetSavePath(string fileName)
    {
        return Path.Combine(Application.persistentDataPath, fileName);
    }
}
