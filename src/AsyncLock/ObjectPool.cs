using System;

namespace NickStrupat;

/// <summary>A best-effort pool: see <see cref="ConcurrentBoundedQueue{T}"/> for what "best-effort" means.</summary>
internal sealed class ObjectPool<T, TState>(Int32 capacity, Func<TState, T> factory, TState state)
	where T : class
{
	private readonly ConcurrentBoundedQueue<T> queue = new(capacity);

	public T Rent()
	{
		if (queue.TryPop(out var item))
			return item;
		return factory(state);
	}

	public void Return(T item)
	{
		queue.TryPush(item);
	}
}
