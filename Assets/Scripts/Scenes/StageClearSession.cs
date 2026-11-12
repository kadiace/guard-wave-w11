using System.Collections.Generic;
using UnityEngine;

/// <summary>실행 중에만 Forest/Fog 클리어 결과를 보관한다. 파일 및 PlayerPrefs에는 저장하지 않는다.</summary>
public static class StageClearSession
{
    private static readonly HashSet<Scene> ClearedStages = new HashSet<Scene>();
    private static Scene _recentlyCleared = Scene.Unknown;

    public static Scene RecentlyCleared => _recentlyCleared;

    /// <summary>Unity 에디터에서 도메인 리로드 없이 다시 실행해도 새 세션으로 초기화한다.</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void InitializeSession()
    {
        ClearedStages.Clear();
        _recentlyCleared = Scene.Unknown;
    }

    /// <summary>현재 실행에서 해당 스테이지를 클리어했는지 반환한다.</summary>
    public static bool IsCleared(Scene scene) => ClearedStages.Contains(scene);

    /// <summary>깃발을 지킨 스테이지를 클리어 처리하고 복귀 시 연출 대상을 기록한다.</summary>
    public static void MarkCleared(Scene scene)
    {
        if (scene != Scene.Forest && scene != Scene.Fog) return;
        ClearedStages.Add(scene);
        _recentlyCleared = scene;
    }

    /// <summary>Game 씬에서 수상 연출을 마치면 이번 복귀 대상만 지운다.</summary>
    public static void ConsumeRecentClear(Scene scene)
    {
        if (_recentlyCleared == scene) _recentlyCleared = Scene.Unknown;
    }
}
