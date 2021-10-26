using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEngine.SceneManagement;
#endif

namespace Pooling
{
    public class ObjectPool
    {
        private GameObject _prefab;
        private Transform _releacedInstancesParent;
        private CoroutineHolder _coroutineHolder;
        private bool _destroyOnLoad;

        private Vector3 _initialLocalPosition;
        private Quaternion _initialLocalRotation;
        private Vector3 _initialLocalScale;

        /// <summary>
        /// костыль чтобы удалялись legacy эффекты
        /// </summary>
        private bool _deleteInstanceIfItCreatedNotFromThisPool = true;

        private InitializationSettings _initSettings;

        private IPoolColliction<GameObject> _releacedInstances;
        private HashSet<int> _releacedInstancesIds = new HashSet<int>();

        /// <summary>
        /// All created by this pool instances.
        /// Need to have this container to be able return instances to pool immediately or destroy it when scene reloaded;
        /// </summary>
        private LinkedList<GameObject> _createdInstances = new LinkedList<GameObject>();
        private HashSet<int> _createdInstancesIds = new HashSet<int>();
        private Dictionary<int, Coroutine> _delayers = new Dictionary<int, Coroutine>();

        public event Action<ObjectPool, GameObject> Got;
        public event Action<ObjectPool, GameObject> Returned;

        public GameObject Prefab => _prefab;
        public Transform ReleacedInstancesParent => _releacedInstancesParent;
        public bool DestroyOnLoad => _destroyOnLoad;

        public int ReleacedInstancesCount => _releacedInstancesIds.Count;
        public int CreatedInstancesCount => _createdInstancesIds.Count;

        public enum CollectionType { Stack, Queue }

        public ObjectPool(GameObject prefab, PoolInitSettings initSettings = null)
        {
            if (prefab == null)
                throw new ArgumentNullException();
            _prefab = prefab;

            if (initSettings == null)
                initSettings = new PoolInitSettings();

            switch (initSettings.collectionType)
            {
                case CollectionType.Stack:
                    _releacedInstances = new PoolStack<GameObject>();
                    break;
                case CollectionType.Queue:
                    _releacedInstances = new PoolQeue<GameObject>();
                    break;
                default:
                    Debug.LogException(new NotImplementedException($"{initSettings.collectionType}. Using {CollectionType.Stack} instead."));
                    _releacedInstances = new PoolStack<GameObject>();
                    break;
            }

            _releacedInstancesParent = new GameObject(
                string.IsNullOrEmpty(initSettings.releacedInstancesParent)
                ? $"pool-{(initSettings.destroyPoolOnSceneLoaded ? "t" : "f")}-{prefab.name}"
                : initSettings.releacedInstancesParent
            ).transform;
            if (!initSettings.destroyPoolOnSceneLoaded)
                GameObject.DontDestroyOnLoad(_releacedInstancesParent.gameObject);
            _destroyOnLoad = initSettings.destroyPoolOnSceneLoaded;

            _coroutineHolder = _releacedInstancesParent.gameObject.AddComponent<CoroutineHolder>();

            var prefabTransform = prefab.transform;
            _initialLocalPosition = prefabTransform.localPosition;
            _initialLocalRotation = prefabTransform.localRotation;
            _initialLocalScale = prefabTransform.localScale;

            _initSettings = new InitializationSettings();

            if (initSettings.initialCapacity > 0)
            {
                var tempKeeper = new LinkedList<GameObject>();

                for (int i = 0; i < initSettings.initialCapacity; i++)
                {
                    tempKeeper.AddLast(
                        WithInit(initSettings.initializator)
                        .GetOrCreate()
                    );
                }

                foreach (var instance in tempKeeper)
                    Return(instance);
            }
        }

        public bool Contains(GameObject instance)
        {
            if (instance == null)
                return false;
            return _releacedInstancesIds.Contains(instance.GetInstanceID());
        }
        public bool IsFromPool(GameObject instance)
        {
            if (instance == null || instance == _prefab)
                return false;
            var instanceId = instance.GetInstanceID();
            return _createdInstancesIds.Contains(instanceId);
        }

        public void Dispose()
        {
            //на всякий случай сначала надо удалить все инстансы
            //(если их родитель как то поменялся)
            foreach (var instance in _createdInstances)
                if (instance != null)
                    GameObject.Destroy(instance);

            if (_releacedInstancesParent != null)
                GameObject.Destroy(_releacedInstancesParent.gameObject);

            _releacedInstances.Clear();
            _releacedInstancesIds.Clear();
            _createdInstances.Clear();
            _createdInstancesIds.Clear();
            _prefab = null;
        }

        public void ForceReturnAllInstancesImmediate()
        {
            foreach (var instance in _createdInstances)
                if (instance != null)
                    Return(instance);
            _coroutineHolder.StopAllCoroutines();
        }

        public GameObject GetOrCreate() => GetOrCreate(null);

        private GameObject GetOrCreate(InitializationSettings settings)
        {
            GameObject instance = null;
            var isNewInstance = false;
            if (_releacedInstances.Count > 0)
            {
                instance = _releacedInstances.Pop();
            }
            else
            {
                instance = GameObject.Instantiate(_prefab);
                _createdInstancesIds.Add(instance.GetInstanceID());
                _createdInstances.AddLast(instance);
                isNewInstance = true;
            }

            if (instance == null)
                instance = GetOrCreate(settings);
            else
                _releacedInstancesIds.Remove(instance.gameObject.GetInstanceID());

            settings?.Init(new PreInitializationArgs(
                instance,
                isNewInstance
            ));

            instance.SetActive(true);
            var poolable = instance.GetComponents<IPoolable>();
            for (int i = 0; i < poolable.Length; i++)
                poolable[i].InitOnGotFromPool();

            try
            {
                Got?.Invoke(this, instance);
            }
            catch (Exception ex) { Debug.LogException(ex); }

            return instance;
        }

        public void Return(GameObject instance)
        {
            if (!CanReturnToPool(instance, out var instanceId))
                return;

            if (_delayers.TryGetValue(instanceId, out var delayer))
            {
                if (delayer != null)
                    _coroutineHolder.StopCoroutine(delayer);
                _delayers[instanceId] = null;
            }

            _releacedInstances.Push(instance);
            _releacedInstancesIds.Add(instanceId);

            var poolable = instance.GetComponents<IPoolable>();
            for (int i = 0; i < poolable.Length; i++)
                poolable[i].DisposeOnReturnedToPool();
            instance.SetActive(false);
            instance.transform.SetParent(_releacedInstancesParent);

            try
            {
                Returned?.Invoke(this, instance);
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }
        public void ReturnDelayed(GameObject instance, float scaledDelay)
        {
            if (!CanReturnToPool(instance, out var instanceId))
                return;

            if (!_delayers.TryGetValue(instanceId, out var delayer) || delayer == null)
                _delayers[instanceId] = _coroutineHolder.InvokeDelayed(() => Return(instance), scaledDelay);
        }
        public void ReturnDelayedUnscaled(GameObject instance, float unscaledDelay)
        {
            if (!CanReturnToPool(instance, out var instanceId))
                return;

            if (!_delayers.TryGetValue(instanceId, out var delayer) || delayer == null)
                _delayers[instanceId] = _coroutineHolder.InvokeDelayedUnscaled(() => Return(instance), unscaledDelay);
        }

        private bool CanReturnToPool(GameObject instance, out int instanceId)
        {
            instanceId = 0;

            if (instance == null || instance == _prefab)
                return false;

            instanceId = instance.GetInstanceID();

            //this object is created not from this pool
            if (!_createdInstancesIds.Contains(instanceId))
            {
                if (_deleteInstanceIfItCreatedNotFromThisPool)
                    GameObject.Destroy(instance);
                return false;
            }

            //olready in pool
            if (_releacedInstancesIds.Contains(instanceId))
                return false;

            return true;
        }

        /// <summary>
        /// DO NOT cache return value.
        /// It is shared in all ObjectPool calls. And it resets in WithInit call;
        /// </summary>
        /// <param name="preInitializator">calls right before IInitializationSettings.WithPreInitialization</param>
        public IInitializationSettings WithInit(Action<GameObject> preInitializator)
        {
            _initSettings.Reset();
            _initSettings._pool = this;
            _initSettings._prePreInitializator = preInitializator;
            return _initSettings;
        }

        private class InitializationSettings : AInitializationSettings
        {
            public ObjectPool _pool;
            public Action<GameObject> _prePreInitializator;

            public override GameObject GetOrCreate() => _pool.GetOrCreate(this);

            public void Init(PreInitializationArgs args)
            {
                var instanceTransform = args.instance.transform;
                instanceTransform.SetParent(_parent);
#if UNITY_EDITOR
                if (_parent == null && !_pool.DestroyOnLoad)
                    SceneManager.MoveGameObjectToScene(args.instance, SceneManager.GetActiveScene());
#endif

                if (_setPosition)
                {
                    if (_isLocalPosition)
                        instanceTransform.localPosition = _position;
                    else
                        instanceTransform.position = _position;
                }
                else
                {
                    instanceTransform.localPosition = _pool._initialLocalPosition;
                }

                if (_setRotation)
                {
                    if (_isLocalRotation)
                        instanceTransform.localRotation = _rotation;
                    else
                        instanceTransform.rotation = _rotation;
                }
                else
                {
                    instanceTransform.localRotation = _pool._initialLocalRotation;
                }

                if (_setLocalScale)
                {
                    if (_isLocalScale)
                        instanceTransform.localScale = _localScale;
                    else
                        instanceTransform.SetLossyScale(_localScale);
                }
                else
                {
                    instanceTransform.localScale = _pool._initialLocalScale;
                }

                if (_autoReturn)
                {
                    if (_useUnscaledDelay)
                        _pool.ReturnDelayedUnscaled(args.instance, _returnDelay);
                    else
                        _pool.ReturnDelayed(args.instance, _returnDelay);
                }

                try
                {
                    _prePreInitializator?.Invoke(args.instance);
                }
                catch (Exception ex) { Debug.LogException(ex); }
                try
                {
                    _persistentPreInitializator?.Invoke(args);
                }
                catch (Exception ex) { Debug.LogException(ex); }
                try
                {
                    _preInitializator?.Invoke(args);
                }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private interface IPoolColliction<T> : IEnumerable<T>, IEnumerable, IReadOnlyCollection<T>, ICollection
        {
            new int Count { get; }
            void Clear();
            void Push(T item);
            bool Contains(T item);
            T[] ToArray();
            T Peek();
            T Pop();
        }
        private class PoolStack<T> : Stack<T>, IPoolColliction<T> { }
        private class PoolQeue<T> : Queue<T>, IPoolColliction<T>
        {
            public T Pop() => this.Dequeue();
            public void Push(T item) => this.Enqueue(item);
        }

        public class PoolInitSettings
        {
            public int initialCapacity = 0;
            public string releacedInstancesParent = null;
            public bool destroyPoolOnSceneLoaded = true;
            public CollectionType collectionType = CollectionType.Stack;
            public Action<GameObject> initializator = null;
        }
    }

    public interface IInitializationSettings
    {
        IInitializationSettings WithParent(Transform parent);

        IInitializationSettings WithPosition(Vector3 position);
        IInitializationSettings WithLocalPosition(Vector3 position);

        IInitializationSettings WithRotation(Quaternion rotation);
        IInitializationSettings WithLocalRotation(Quaternion rotation);

        IInitializationSettings WithLossyScale(Vector3 lossyScale);
        IInitializationSettings WithLocalScale(Vector3 localScale);

        IInitializationSettings WithAutoReturn(float delay, bool unscaled = false);
        /// <summary>
        /// preInitializator calls after all WithXXX methods
        /// but before instance.SetActive(true) and initialization through IPoolable.
        /// persistent called first.
        /// </summary>
        IInitializationSettings WithPreInitialization(Action<PreInitializationArgs> preInitializator, bool persistent);

        GameObject GetOrCreate();
    }

    public struct PreInitializationArgs
    {
        public readonly GameObject instance;
        public readonly bool isNewInstance;

        public PreInitializationArgs(GameObject instance, bool isNewInstance)
        {
            this.instance = instance;
            this.isNewInstance = isNewInstance;
        }
    }

    public abstract class AInitializationSettings : IInitializationSettings
    {
        protected Transform _parent;

        protected Vector3 _position;
        protected bool _setPosition;
        protected bool _isLocalPosition;

        protected Vector3 _localScale;
        protected bool _setLocalScale;
        protected bool _isLocalScale;

        protected Quaternion _rotation;
        protected bool _setRotation;
        protected bool _isLocalRotation;

        protected bool _autoReturn;
        protected float _returnDelay;
        protected bool _useUnscaledDelay;

        protected Action<PreInitializationArgs> _persistentPreInitializator;
        protected Action<PreInitializationArgs> _preInitializator;

        public abstract GameObject GetOrCreate();

        public void Reset()
        {
            _parent = null;
            _setPosition = false;
            _isLocalPosition = false;

            _setRotation = false;
            _isLocalRotation = false;

            _setLocalScale = false;
            _isLocalScale = false;

            _autoReturn = false;
            _persistentPreInitializator = null;
            _preInitializator = null;
        }

        public IInitializationSettings WithAutoReturn(float delay, bool unscaled = false)
        {
            _autoReturn = true;
            _returnDelay = delay;
            _useUnscaledDelay = unscaled;
            return this;
        }

        public IInitializationSettings WithParent(Transform parent)
        {
            _parent = parent;
            return this;
        }

        public IInitializationSettings WithLossyScale(Vector3 lossyScale)
        {
            _setLocalScale = true;
            _isLocalScale = false;
            _localScale = lossyScale;
            return this;
        }
        public IInitializationSettings WithLocalScale(Vector3 localScale)
        {
            _setLocalScale = true;
            _isLocalScale = true;
            _localScale = localScale;
            return this;
        }

        public IInitializationSettings WithPosition(Vector3 position)
        {
            _setPosition = true;
            _isLocalPosition = false;
            _position = position;
            return this;
        }
        public IInitializationSettings WithLocalPosition(Vector3 position)
        {
            _setPosition = true;
            _isLocalPosition = true;
            _position = position;
            return this;
        }

        public IInitializationSettings WithRotation(Quaternion rotation)
        {
            _setRotation = true;
            _isLocalRotation = false;
            _rotation = rotation;
            return this;
        }
        public IInitializationSettings WithLocalRotation(Quaternion rotation)
        {
            _setRotation = true;
            _isLocalRotation = true;
            _rotation = rotation;
            return this;
        }

        public IInitializationSettings WithPreInitialization(Action<PreInitializationArgs> preInitializator, bool persistent = false)
        {
            if (persistent)
                _persistentPreInitializator += preInitializator;
            else
                _preInitializator = preInitializator;
            return this;
        }
    }
}