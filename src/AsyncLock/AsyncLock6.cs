using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace NickStrupat;

public sealed class AsyncLock6 : IAsyncLock
{
	private const Int32 MaxRetainedInPool = 32;

	private readonly ObjectPool<ValueTaskCompletionSource> vtcsPool;
	private ValueTaskCompletionSource vtcs;

	public AsyncLock6()
	{
		vtcsPool = new(MaxRetainedInPool);
		vtcs = vtcsPool.Rent();
		vtcs.SetResult();
	}

	public async ValueTask LockAsync(Func<ValueTask> whenLocked, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);
		cancellationToken.ThrowIfCancellationRequested();

		var next = vtcsPool.Rent();
		var prev = Interlocked.Exchange(ref this.vtcs, next);
		var ctr = cancellationToken.UnsafeRegister(static o =>
		{

		}, );
		try
		{
			await prev.ValueTask.ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			await whenLocked().ConfigureAwait(false);
		}
		finally
		{
			next.SetResult();
			await ctr.DisposeAsync();
			prev.Reset();
			vtcsPool.Return(prev);
		}
	}

	private sealed class ValueTaskCompletionSource : IValueTaskSource
	{
		private ManualResetValueTaskSourceCore<Byte> core = new() { RunContinuationsAsynchronously = true };

		public ValueTask ValueTask => new(this, core.Version);

		public void SetResult() => core.SetResult(0);
		public void GetResult(Int16 token) => core.GetResult(token);
		public ValueTaskSourceStatus GetStatus(Int16 token) => core.GetStatus(token);
		public void Reset() => core.Reset();

		public void OnCompleted(Action<Object?> continuation, Object? state, Int16 token,
			ValueTaskSourceOnCompletedFlags flags) => core.OnCompleted(continuation, state, token, flags);
	}

	[StructLayout(LayoutKind.Explicit, Size = 128)]
	private struct PaddedInt
	{
		[FieldOffset(0)] public Int32 Value;
	}

	private sealed class ConcurrentBoundedQueue<T>
	{
		private readonly Cell[] buffer;
		private readonly Int32 mask;
		private PaddedInt head;
		private PaddedInt tail;

		/// <summary>
		///
		/// </summary>
		/// <param name="minimumCapacity">Will be rounded up to the nearest power of two</param>
		/// <exception cref="ArgumentOutOfRangeException"></exception>
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

	private sealed class ObjectPool<T>(Int32 capacity)
		where T : class, new()
	{
		private readonly ConcurrentBoundedQueue<T> queue = new(capacity);

		public T Rent()
		{
			if (queue.TryPop(out var item))
				return item;
			return new();
		}

		public void Return(T item)
		{
			queue.TryPush(item);
		}
	}
}
