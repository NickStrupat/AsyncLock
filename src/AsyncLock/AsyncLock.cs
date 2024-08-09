using System;
using System.Threading;
using System.Threading.Tasks;

namespace NickStrupat;

/// A simple, thread-safe, FIFO async lock. It does not allocate if uncontended. It does not support recursion/reentrancy.
public sealed class AsyncLock
{
	private Interlocked<Task> task = new(Task.CompletedTask);
	private Interlocked<TaskCompletionSource?> cachedTcs;

	public async ValueTask LockAsync(Func<Task> whenLocked)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);
		var next = cachedTcs.Exchange(null) ?? CreateTcs(); // try to get a cached TCS; if there isn't one, create a new one
		var prev = task.Exchange(next.Task); // set the task to the next TCS and get the previous task
		await prev.ConfigureAwait(false); // wait for the previous task to complete
		try
		{
			await whenLocked().ConfigureAwait(false);
		}
		finally
		{
			// if the task is still the one we set earlier, no one else has taken the lock, so let's put the task back like it was when we entered
			if (task.CompareExchange(prev, next.Task) == next.Task)
				cachedTcs.Exchange(next); // we also know that we didn't use our 'next' TCS, so we can put it back in the cached TCS
			else
				next.SetResult(); // otherwise, we know that someone else has taken the lock, so we can safely set the result, allowing the next waiter to proceed into the lock
		}
	}
	
	private TaskCompletionSource CreateTcs()
	{
		#if DEBUG
		Interlocked.Increment(ref TcsCtorCount);
		#endif
		return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
	}
	#if DEBUG
	public UInt64 TcsCtorCount;
	#endif
}