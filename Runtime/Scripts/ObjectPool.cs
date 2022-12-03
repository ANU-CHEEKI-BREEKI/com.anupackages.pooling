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
        private Transform _releasedInstancesParent;
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

        private IPoolCollection<GameObject> _releasedInstances;
        private HashSet<int> _releasedInstancesIds = new HashSet<int>();

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
        public Transform ReleasedInstancesParent => _releasedInstancesParent;
        public bool DestroyOnLoad => _destroyOnLoad;

        public int ReleasedInstancesCount => _releasedInstancesIds.Count;
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
                    _releasedInstances = new PoolStack<GameObject>();
                    break;
                case CollectionType.Queue:
                    _releasedInstances = new PoolQueue<GameObject>();
                    break;
                default:
                    Debug.LogException(new NotImplementedException($"{initSettings.collectionType}. Using {CollectionType.Stack} instead."));
                    _releasedInstances = new PoolStack<GameObject>();
                    break;
            }

            _releasedInstancesParent = new GameObject(
                string.IsNullOrEmpty(initSettings.releasedInstancesParent)
                ? $"pool-{(initSettings.destroyPoolOnSceneLoaded ? "t" : "f")}-{prefab.name}"
                : initSettings.releasedInstancesParent
            ).transform;
            if (!initSettings.destroyPoolOnSceneLoaded)
                GameObject.DontDestroyOnLoad(_releasedInstancesParent.gameObject);
            _destroyOnLoad = initSettings.destroyPoolOnSceneLoaded;

            _coroutineHolder = _releasedInstancesParent.gameObject.AddComponent<CoroutineHolder>();

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
                        WithInit(initSettings.initializer)
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
            return _releasedInstancesIds.Contains(instance.GetInstanceID());
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

            if (_releasedInstancesParent != null)
                GameObject.Destroy(_releasedInstancesParent.gameObject);

            _releasedInstances.Clear();
            _releasedInstancesIds.Clear();
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
            if (_releasedInstances.Count > 0)
            {
                instance = _releasedInstances.Pop();
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
                _releasedInstancesIds.Remove(instance.gameObject.GetInstanceID());

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

            _releasedInstances.Push(instance);
            _releasedInstancesIds.Add(instanceId);

            var poolable = instance.GetComponents<IPoolable>();
            for (int i = 0; i < poolable.Length; i++)
                poolable[i].DisposeOnReturnedToPool();
            instance.SetActive(false);
            instance.transform.SetParent(_releasedInstancesParent);

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

            //already in pool
            if (_releasedInstancesIds.Contains(instanceId))
                return false;

            return true;
        }

        /// <summary>
        /// DO NOT cache return value.
        /// It is shared in all ObjectPool calls. And it resets in WithInit call;
        /// </summary>
        /// <param name="preInitializer">calls right before IInitializationSettings.WithPreInitialization</param>
        public IInitializationSettings WithInit(Action<GameObject> preInitializer)
        {
            _initSettings.Reset();
            _initSettings._pool = this;
            _initSettings._prePreInitializer = preInitializer;
            return _initSettings;
        }

        private class InitializationSettings : AInitializationSettings
        {
            public ObjectPool _pool;
            public Action<GameObject> _prePreInitializer;

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
                    _prePreInitializer?.Invoke(args.instance);
                }
                catch (Exception ex) { Debug.LogException(ex); }
                try
                {
                    _persistentPreInitializer?.Invoke(args);
                }
                catch (Exception ex) { Debug.LogException(ex); }
                try
                {
                    _preInitializer?.Invoke(args);
                }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private interface IPoolCollection<T> : IEnumerable<T>, IEnumerable, IReadOnlyCollection<T>, ICollection
        {
            new int Count { get; }
            void Clear();
            void Push(T item);
            bool Contains(T item);
            T[] ToArray();
            T Peek();
            T Pop();
        }
        private class PoolStack<T> : Stack<T>, IPoolCollection<T> { }
        private class PoolQueue<T> : Queue<T>, IPoolCollection<T>
        {
            public T Pop() => this.Dequeue();
            public void Push(T item) => this.Enqueue(item);
        }

        public class PoolInitSettings
        {
            public int initialCapacity = 0;
            public string releasedInstancesParent = null;
            public bool destroyPoolOnSceneLoaded = true;
            public CollectionType collectionType = CollectionType.Stack;
            public Action<GameObject> initializer = null;
        }
    }

    public enum InitializerSetType { Replace, Add }

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
        /// preInitializer calls after all WithXXX methods
        /// but before instance.SetActive(true) and initialization through IPoolable.
        /// 
        /// ---
        /// 
        /// initializers with InitializerSetType.Add and InitializerSetType.Replace sets independently
        /// so if you call this method 4 times - 2 wit Add and 2 with Replace - there will be 3 initializers - two from Add and onr lsat from Replace
        /// 
        /// initializers with InitializerSetType.Add called first.
        /// </summary>
        IInitializationSettings WithPreInitialization(Action<PreInitializationArgs> preInitializer, InitializerSetType setType);

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

        protected Action<PreInitializationArgs> _persistentPreInitializer;
        protected Action<PreInitializationArgs> _preInitializer;

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
            _persistentPreInitializer = null;
            _preInitializer = null;
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

        public IInitializationSettings WithPreInitialization(Action<PreInitializationArgs> preInitializer, InitializerSetType setType)
        {
            if (setType == InitializerSetType.Add)
                _persistentPreInitializer += preInitializer;
            else if (setType == InitializerSetType.Replace)
                _preInitializer = preInitializer;
            else
                throw new System.NotImplementedException(setType.ToString());
            return this;
        }
    }
}