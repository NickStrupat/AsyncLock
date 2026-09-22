using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NickStrupat;

AsyncSemaphore asyncSemaphore = new(10);
;

public sealed class AsyncSemaphore
{
	private InterlockedCounter counter;
	private readonly CacheLineAlignedArray<Interlocked<Task>> slots;

	public AsyncSemaphore(Int32 slotCount)
	{
		slots = new CacheLineAlignedArray<Interlocked<Task>>(slotCount);
		for (var i = 0; i < slotCount; ++i)
			slots[i] = new Interlocked<Task>(Task.CompletedTask);
	}

	public async Task WaitAsync()
	{
		var slot = counter.Increment() % (UInt64)slots.Length;
		await slots[(Int32)slot].Exchange(Task.CompletedTask);
	}

	private readonly struct CacheLineAlignedArray<T>(Int32 size)
	{
		private readonly T[] buffer = new T[Multiplier * size];
		public Int32 Length => size;
		public ref T this[Int32 index] => ref buffer[Multiplier * index];
		private static readonly Int32 Multiplier = CacheLine.Size / Unsafe.SizeOf<T>();
	}
}

public struct InterlockedCounter
{
	private UInt64 value;
	public UInt64 Increment() => Interlocked.Increment(ref value);
	public UInt64 Decrement() => Interlocked.Decrement(ref value);
}

public struct Interlocked<T>(T value) where T : class
{
	private T value = value;
	public T Exchange(T newValue) => Interlocked.Exchange(ref value, newValue);
	public T CompareExchange(T newValue, T comparand) => Interlocked.CompareExchange(ref value, newValue, comparand);
}