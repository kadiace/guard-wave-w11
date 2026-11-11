using System;
using System.Collections.Generic;
using UnityEngine;

namespace GuardWave
{
    [Serializable]
    public sealed class GuardWaveMaterial
    {
        public string id;
        public string name;
        public string category;
        public bool startOnly;
        public bool spawnOnly;
        public string[] abilities;
    }

    [Serializable]
    public sealed class GuardWaveMaterialFile
    {
        public int schemaVersion;
        public GuardWaveMaterial[] materials;
    }

    public sealed class GuardWaveCatalog
    {
        private readonly Dictionary<string, GuardWaveMaterial> _materials = new Dictionary<string, GuardWaveMaterial>();
        public GuardWaveMaterial[] All { get; private set; }

        /// <summary>2D 원본에서 이관한 자재 정의를 읽어 ID 조회 테이블을 구성한다.</summary>
        public GuardWaveCatalog()
        {
            TextAsset text = Resources.Load<TextAsset>("Data/materials");
            if (text == null) throw new InvalidOperationException("Missing Resources/Data/materials.json");
            GuardWaveMaterialFile file = JsonUtility.FromJson<GuardWaveMaterialFile>(text.text);
            if (file == null || file.materials == null || file.materials.Length == 0)
                throw new InvalidOperationException("Material catalog is empty");
            All = file.materials;
            foreach (GuardWaveMaterial info in All) _materials.Add(info.id, info);
        }

        /// <summary>자재 ID에 일치하는 항목을 반환한다.</summary>
        public GuardWaveMaterial Get(string id) => _materials.TryGetValue(id, out GuardWaveMaterial info) ? info : null;
    }
}
