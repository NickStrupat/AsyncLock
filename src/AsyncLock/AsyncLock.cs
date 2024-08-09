using System;
using System.Threading;
using System.Threading.Tasks;

namespace NickStrupat;

public sealed class AsyncLock
{
	#if DEBUG
	public UInt64 TcsCtorCount;
	#endif
	private Task task = Task.CompletedTask;

	public async ValueTask LockAsync(Func<Task> whenLocked)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);
		var next = Get();
		var prev = Interlocked.Exchange(ref task, next.Task);
		await prev.ConfigureAwait(false);
		try
		{
			await whenLocked().ConfigureAwait(false);
		}
		finally
		{
			if (Interlocked.CompareExchange(ref task, prev, next.Task) == next.Task)
				Return(next);
			else
				next.SetResult();
		}
	}

	private TaskCompletionSource? fastTcs;
	private TaskCompletionSource Get()
	{
		var item = fastTcs;
		if (item != null && Interlocked.CompareExchange(ref fastTcs, null, item) == item)
			return item;
		#if DEBUG
		Interlocked.Increment(ref TcsCtorCount);
		#endif
		return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	private void Return(TaskCompletionSource tcs)
	{
		if (fastTcs == null)
			Interlocked.CompareExchange(ref fastTcs, tcs, null);
	}
}