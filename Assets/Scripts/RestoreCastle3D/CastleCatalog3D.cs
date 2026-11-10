using System;
using System.Collections.Generic;
using UnityEngine;

namespace RestoreCastle3D
{
    [Serializable]
    public sealed class CastleMaterial3D
    {
        public string id;
        public string name;
        public string category;
        public bool startOnly;
        public bool spawnOnly;
        public string[] abilities;
    }

    [Serializable]
    public sealed class CastleMaterialFile3D
    {
        public int schemaVersion;
        public CastleMaterial3D[] materials;
    }

    public sealed class CastleCatalog3D
    {
        private readonly Dictionary<string, CastleMaterial3D> _materials = new Dictionary<string, CastleMaterial3D>();
        public CastleMaterial3D[] All { get; private set; }

        /// <summary>2D 원본에서 이관한 자재 정의를 읽어 ID 조회 테이블을 구성한다.</summary>
        public CastleCatalog3D()
        {
            TextAsset text = Resources.Load<TextAsset>("Data/materials");
            if (text == null) throw new InvalidOperationException("Missing Resources/Data/materials.json");
            CastleMaterialFile3D file = JsonUtility.FromJson<CastleMaterialFile3D>(text.text);
            if (file == null || file.materials == null || file.materials.Length == 0)
                throw new InvalidOperationException("Material catalog is empty");
            All = file.materials;
            foreach (CastleMaterial3D info in All) _materials.Add(info.id, info);
        }

        /// <summary>자재 ID에 일치하는 항목을 반환한다.</summary>
        public CastleMaterial3D Get(string id) => _materials.TryGetValue(id, out CastleMaterial3D info) ? info : null;
    }
}
