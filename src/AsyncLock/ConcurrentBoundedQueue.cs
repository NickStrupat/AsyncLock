using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace NickStrupat;

/// <summary>
/// A lock-free, bounded multi-producer/multi-consumer queue based on Dmitry Vyukov's
/// bounded MPMC algorithm.
/// </summary>
/// <remarks>
/// This is a <b>best-effort</b> queue, not a linearizable one. Each operation makes a single
/// attempt: under contention <see cref="TryPush"/> may return <c>false</c> even when the queue
/// is not full, and <see cref="TryPop"/> may return <c>false</c> even when the queue is not
/// empty. It never blocks or retries, and never corrupts state, hands out a slot twice, or
/// loses items it reports as enqueued. It is intended for best-effort pooling, where a spurious
/// failure simply means allocating a new object (on pop) or dropping one to the GC (on push).
/// Do not use it as a general-purpose queue that must reliably accept or yield every item.
/// </remarks>
internal sealed class ConcurrentBoundedQueue<T>
{
	private readonly Cell[] buffer;
	private readonly Int32 mask;
	private PaddedInt head;
	private PaddedInt tail;

	/// <summary>
	/// Creates a bounded queue with capacity at least <paramref name="minimumCapacity"/>.
	/// </summary>
	/// <param name="minimumCapacity">Will be rounded up to the nearest power of two</param>
	/// <exception cref="ArgumentOutOfRangeException">
	/// Thrown when <paramref name="minimumCapacity"/> is less than or equal to zero.
	/// </exception>
	public ConcurrentBoundedQueue(Int32 minimumCapacity)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minimumCapacity, 0);
		minimumCapacity = (Int32)BitOperations.RoundUpToPowerOf2((UInt32)minimumCapacity);
		mask = minimumCapacity - 1;
		buffer = new Cell[minimumCapacity];
		for (var i = 0; i < minimumCapacity; i++)
			buffer[i].Sequence = i;
	}

	public Boolean TryPush(T item)
	{
		var tail = Volatile.Read(ref this.tail.Value);
		ref var cell = ref buffer[tail & mask];
		var diff = Volatile.Read(ref cell.Sequence) - tail;

		if (diff == 0 && Interlocked.CompareExchange(ref this.tail.Value, tail + 1, tail) == tail)
		{
			cell.Data = item;
			Volatile.Write(ref cell.Sequence, tail + 1);
			return true;
		}

		return false;
	}

	public Boolean TryPop([MaybeNullWhen(false)] out T item)
	{
		var head = Volatile.Read(ref this.head.Value);
		ref var cell = ref buffer[head & mask];
		var diff = Volatile.Read(ref cell.Sequence) - (head + 1);

		if (diff == 0 && Interlocked.CompareExchange(ref this.head.Value, head + 1, head) == head)
		{
			item = cell.Data!;
			cell.Data = default;
			Volatile.Write(ref cell.Sequence, head + mask + 1);
			return true;
		}

		item = default;
		return false;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Cell
	{
		public Int32 Sequence;
		public T? Data;
	}
}

// Not nested in the queue: a type nested in a generic type is itself generic, and the runtime rejects
// explicit layout on generic types.
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct PaddedInt
{
	[FieldOffset(0)] public Int32 Value;
}
