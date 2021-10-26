using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Pooling;

public sealed partial class ObjectPools : Singletone<ObjectPools>
{
    public static event Action<ObjectPool> Created;
    /// <summary>
    /// DO NOT create pools inside Clean event callback
    /// </summary>
    public static event Action<IEnumerable<ObjectPool>> Clean;

    private static Dictionary<int, ObjectPool> _pools = new Dictionary<int, ObjectPool>();
    private static Dictionary<int, GameObject> _prebabByInstance = new Dictionary<int, GameObject>();

    private static ObjectPools _instance;
    private static bool _cleaning = false;
    private Transform _destroyablePoolsParent;

    private Transform DestroyablePoolsParent
    {
        get
        {
            if (_destroyablePoolsParent == null)
                _destroyablePoolsParent = new GameObject(nameof(DestroyablePoolsParent)).transform;

            return _destroyablePoolsParent;
        }
    }

    protected override void Initialize()
    {
        // auto clearing on scene unloaded
        SceneManager.sceneUnloaded += scene =>
        {
            _cleaning = true;

            var toRemove = new LinkedList<int>();

            toRemove.Clear();
            foreach (var pair in _prebabByInstance)
            {
                var prefab = pair.Value;
                var prefabId = prefab.GetInstanceID();
                if (_pools.ContainsKey(prefabId) && _pools[prefabId].DestroyOnLoad)
                    toRemove.AddLast(pair.Key);
            }
            foreach (var key in toRemove)
                _prebabByInstance.Remove(key);

            toRemove.Clear();
            foreach (var pool in _pools)
            {
                if (pool.Value.DestroyOnLoad)
                {
                    pool.Value.Dispose();
                    toRemove.AddLast(pool.Key);
                }
                else
                {
                    pool.Value.ForceReturnAllInstancesImmediate();
                }
            }
            foreach (var key in toRemove)
                _pools.Remove(key);

            if (Instance._destroyablePoolsParent != null)
            {
                Destroy(Instance._destroyablePoolsParent.gameObject);
                Instance._destroyablePoolsParent = null;
            }

            try
            {
                Clean?.Invoke(_pools.Values);
            }
            catch (Exception ex) { Debug.LogException(ex); }

            _cleaning = false;
        };
    }

    /// <summary>
    /// Create pool if it not exists already and fill it with initial count of released instances;
    /// 
    /// DO NOT create pools inside Clean event callback
    /// </summary>
    public static void InitializePool(GameObject prefab, ObjectPool.PoolInitSettings initSettings)
    {
        if (prefab == null)
            throw new ArgumentNullException(nameof(prefab));

        if (HasPoolFor(prefab))
        {
            Debug.LogWarning(
                $"Pool for this prefab already ititialized and can not be reinitialized again.",
                _pools[prefab.GetInstanceID()].ReleacedInstancesParent
            );
            return;
        }

        CreatePool(prefab, initSettings);
    }

    public static bool HasPoolFor(GameObject prefab)
    {
        if (prefab == null)
            throw new ArgumentNullException(nameof(prefab));
        var prefabId = prefab.GetInstanceID();
        return _pools.ContainsKey(prefabId);
    }

    public static bool IsInPool(GameObject instance)
    {
        if (instance == null)
            return false;
        var instanceId = instance.GetInstanceID();
        if (!_prebabByInstance.ContainsKey(instanceId))
            return false;

        var prefabId = _prebabByInstance[instanceId].GetInstanceID();
        if (!_pools.ContainsKey(prefabId))
            return false;

        var pool = _pools[prefabId];
        return pool.Contains(instance);
    }
    public static bool IsFromPool(GameObject instance)
    {
        var pool = GetPoolByInstance(instance);
        if (pool == null)
            return false;
        return pool.IsFromPool(instance);
    }

    public static bool IsInPool(Component instance)
    {
        if (instance == null)
            return false;
        return IsInPool(instance.gameObject);
    }
    public static bool IsFromPool(Component instance)
    {
        if (instance == null)
            return false;
        return IsFromPool(instance.gameObject);
    }

    /// <summary>
    /// DO NOT create pools inside Clean event callback
    /// </summary>
    public static GameObject GetOrCreate(GameObject prefab) => WithInit(prefab).GetOrCreate();
    /// <summary>
    /// DO NOT create pools inside Clean event callback
    /// </summary>
    public static T GetOrCreate<T>(T prefab) where T : Component
    {
        if (prefab == null)
            throw new ArgumentNullException();

        var instance = GetOrCreate(prefab.gameObject);
        if (instance == null)
            return null;

        var instanceComponent = instance.GetComponent<T>();
        return instanceComponent;
    }

    public static void Return(GameObject instance)
    {
        var pool = GetPoolByInstance(instance);
        if (pool != null)
            pool.Return(instance);
    }
    public static void ReturnDelayed(GameObject instance, float scaledDelay)
    {
        var pool = GetPoolByInstance(instance);
        if (pool != null)
            pool.ReturnDelayed(instance, scaledDelay);
    }
    public static void ReturnDelayedUnscaled(GameObject instance, float unscaledDelay)
    {
        var pool = GetPoolByInstance(instance);
        if (pool != null)
            pool.ReturnDelayed(instance, unscaledDelay);
    }

    public static void Return(Component instance)
    {
        if (instance == null)
            return;
        Return(instance.gameObject);
    }
    public static void ReturnDelayed(Component instance, float scaledDelay)
    {
        if (instance == null)
            return;
        ReturnDelayed(instance.gameObject, scaledDelay);
    }
    public static void ReturnDelayedUnscaled(Component instance, float unscaledDelay)
    {
        if (instance == null)
            return;
        ReturnDelayedUnscaled(instance.gameObject, unscaledDelay);
    }

    private static ObjectPool GetPool(GameObject prefab)
    {
        var id = prefab.GetInstanceID();
        if (!_pools.ContainsKey(id))
            CreatePool(prefab);

        return _pools[id];
    }
    private static ObjectPool CreatePool(GameObject prefab, ObjectPool.PoolInitSettings initSettings = null)
    {
        if (_cleaning)
            throw new InvalidOperationException($"Do not create pools inside {nameof(Clean)} event.");

        var id = prefab.GetInstanceID();
        var newPool = new ObjectPool(prefab, initSettings);
        _pools.Add(id, newPool);

        if (newPool.DestroyOnLoad)
            newPool.ReleacedInstancesParent.parent = Instance.DestroyablePoolsParent;
        else
            newPool.ReleacedInstancesParent.parent = Instance.transform;

        try
        {
            Created?.Invoke(newPool);
        }
        catch (Exception ex) { Debug.LogException(ex); }

        return newPool;
    }
    private static ObjectPool GetPoolByInstance(GameObject instance)
    {
        if (instance == null)
            return null;

        var instanceId = instance.gameObject.GetInstanceID();
        if (!_prebabByInstance.ContainsKey(instanceId))
            return null;
        else
            return GetPool(_prebabByInstance[instanceId]);
    }

    /// <summary>
    /// DO NOT cache return value.
    /// It is shared in all ObjectPools calls. And it resets in WithInit call;
    /// </summary>
    public static IInitializationSettings WithInit(GameObject prefab)
    {
        if (prefab == null)
            throw new ArgumentNullException();

        //check if it instance of any another prefab
        //and take those prefab insted
        var prefabId = prefab.gameObject.GetInstanceID();
        if (_prebabByInstance.ContainsKey(prefabId))
            prefab = _prebabByInstance[prefabId];

        var pool = GetPool(prefab);
        return pool.WithInit((instance) =>
        {
            var instanceId = instance.gameObject.GetInstanceID();
            //cahce prefab for this instance to be able get it in Retuen method
            if (!_prebabByInstance.ContainsKey(instanceId))
                _prebabByInstance.Add(instanceId, prefab);
        });
    }
    /// <summary>
    /// DO NOT cache return value.
    /// It is shared in all ObjectPools calls. And it resets in WithInit call;
    /// </summary>
    public static IInitializationSettings WithInit(GameObject prefab, Transform parent, Vector3? position = null, Quaternion? rotation = null)
    {
        var settings = WithInit(prefab);
        settings.WithParent(parent);
        if (position.HasValue)
            settings.WithPosition(position.Value);
        if (rotation.HasValue)
            settings.WithRotation(rotation.Value);
        return settings;
    }
}