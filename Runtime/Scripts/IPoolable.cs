using System;
using UnityEngine;

public interface IPoolable
{
    event Action Got;
    event Action Returned;

    void InitOnGotFromPool();
    void DisposeOnReturnedToPool();
}

public static class PoolableExtensions
{
    public static void Release(this IPoolable poolable) => ObjectPools.Return(poolable as Component);
    public static bool IsInPool(this IPoolable poolable) => ObjectPools.IsInPool(poolable as Component);
}