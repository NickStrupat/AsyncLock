using System;
using System.Threading;

namespace NickStrupat;

/// <summary>
/// A best-effort pool that costs its owner two references until it is used. The first idle item is kept in a single
/// slot; the bounded queue that holds any more is only created once an item is returned while that slot is occupied.
/// </summary>
/// <remarks>
/// A mutable struct, so that its owner carries it inline rather than pointing at a separate object: keep it in a
/// field that is not <see langword="readonly"/>, and never copy it. Like <see cref="ConcurrentBoundedQueue{T}"/> it
/// is best-effort: under contention a rent may find nothing while an item is idle, and a return may drop its item to
/// the GC.
/// </remarks>
internal struct TieredPool<T> where T : class
{
	/// <summary>How many idle items the overflow queue holds, in addition to the one in the slot.</summary>
	public const Int32 OverflowCapacity = 32;

	private T? spare;
	private ConcurrentBoundedQueue<T>? overflow;

	/// <summary>Takes an idle item out of the pool.</summary>
	/// <returns>The item, or <see langword="null"/> if the pool has none to hand out.</returns>
	public T? TryRent()
	{
		// Read first: an empty slot is then left alone, rather than written to on every rent.
		var item = Volatile.Read(ref spare);
		if (item is not null && Interlocked.CompareExchange(ref spare, null, item) == item)
			return item;
		return Volatile.Read(ref overflow) is { } queue && queue.TryPop(out item) ? item : null;
	}

	/// <summary>Makes <paramref name="item"/> available to rent again, or drops it if there is no room for it.</summary>
	/// <param name="item">The item to return. The caller must not use it afterwards.</param>
	public void Return(T item)
	{
		if (Interlocked.CompareExchange(ref spare, item, null) is null)
			return;
		(Volatile.Read(ref overflow) ?? CreateOverflow()).TryPush(item);
	}

	private ConcurrentBoundedQueue<T> CreateOverflow()
	{
		// If two returns race to create the queue, both use the one that was published first.
		var created = new ConcurrentBoundedQueue<T>(OverflowCapacity);
		return Interlocked.CompareExchange(ref overflow, created, null) ?? created;
	}
}
