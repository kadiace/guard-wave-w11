using System.Collections.Generic;
using UnityEngine;

public class PoolManager
{
    private class Pool
    {
        private readonly Stack<GameObject> _items = new();
        private GameObject _prefab;
        private Transform _root;

        public void Init(string key, GameObject prefab, Transform parent, int preloadCount)
        {
            _prefab = prefab;
            _root = new GameObject($"{key}_Pool").transform;
            _root.SetParent(parent, false);

            for (int i = 0; i < preloadCount; i++)
                Push(Create());
        }

        public GameObject Pop(Transform parent)
        {
            GameObject item = _items.Count > 0 ? _items.Pop() : Create();
            item.transform.SetParent(parent, false);
            item.SetActive(true);
            return item;
        }

        public void Push(GameObject item)
        {
            item.SetActive(false);
            item.transform.SetParent(_root, false);
            _items.Push(item);
        }

        public void Clear()
        {
            Object.Destroy(_root.gameObject);
            _items.Clear();
        }

        private GameObject Create()
        {
            GameObject item = Object.Instantiate(_prefab);
            item.name = _prefab.name;
            return item;
        }
    }

    private readonly Dictionary<string, Pool> _pools = new();
    private readonly Dictionary<GameObject, string> _keys = new();
    private Transform _root;

    public void Init(Transform appRoot)
    {
        GameObject root = new("@Pool_Root");
        root.transform.SetParent(appRoot, false);
        _root = root.transform;
    }

    public GameObject Pop(string prefabPath, Transform parent = null, int preloadCount = 5)
    {
        if (!_pools.TryGetValue(prefabPath, out Pool pool))
        {
            GameObject prefab = Managers.Resource.Load<GameObject>(prefabPath);
            pool = CreatePool(prefabPath, prefab, preloadCount);
        }

        GameObject item = pool.Pop(parent);
        _keys[item] = prefabPath;
        Poolable poolable = item.GetComponent<Poolable>();
        poolable.PoolKey = prefabPath;
        return item;
    }

    public void Push(GameObject item)
    {
        string key = item.GetComponent<Poolable>().PoolKey;
        _pools[key].Push(item);
    }

    public void Clear()
    {
        foreach (Pool pool in _pools.Values)
            pool.Clear();

        _pools.Clear();
        _keys.Clear();
    }

    private Pool CreatePool(string key, GameObject prefab, int preloadCount)
    {
        Pool pool = new();
        pool.Init(key, prefab, _root, preloadCount);
        _pools.Add(key, pool);
        return pool;
    }
}
