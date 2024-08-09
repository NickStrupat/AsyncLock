using System;
using System.Threading.Tasks;

namespace NickStrupat;

public sealed class AsyncLock
{
	#if DEBUG
	public UInt64 TcsCtorCount;
	#endif
	private Interlocked<Task> task = new(Task.CompletedTask);

	public async ValueTask LockAsync(Func<Task> whenLocked)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);
		var next = Get();
		var prev = task.Exchange(next.Task);
		await prev.ConfigureAwait(false);
		try
		{
			await whenLocked().ConfigureAwait(false);
		}
		finally
		{
			if (task.CompareExchange(prev, next.Task) == next.Task)
				Return(next);
			else
				next.SetResult();
		}
	}

	private Interlocked<TaskCompletionSource?> cachedTcs;
	
	private TaskCompletionSource Get()
	{
		var item = cachedTcs.CoreCachedValue;
		if (item != null && cachedTcs.CompareExchange(null, item) == item)
			return item;
		#if DEBUG
		Interlocked.Increment(ref TcsCtorCount);
		#endif
		return new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	private void Return(TaskCompletionSource tcs)
	{
		if (cachedTcs.CoreCachedValue == null)
			cachedTcs.CompareExchange(tcs, null);
	}
}