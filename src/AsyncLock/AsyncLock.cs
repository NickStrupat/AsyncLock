using System;
using System.Threading;
using System.Threading.Tasks;

namespace NickStrupat;

/// A simple, thread-safe, FIFO async lock. It does not allocate if uncontended. It does not support recursion/reentrancy.
public sealed class AsyncLock : IAsyncLock
{
	// The task that represents the holding of the lock. It is initialized to a completed task to signify that the lock is free.
	private Task task = Task.CompletedTask;

	// A cached TCS that is used to avoid allocations when the lock is uncontended.
	private TaskCompletionSource? cachedTcs;

	public AsyncLock() => cachedTcs = CreateTcs();

	/// <summary>
	/// Asynchronously waits for the lock to be acquired.
	/// When the lock is acquired, the supplied delegate is executed, then the lock is released.
	/// Any exceptions thrown by the delegate are propagated to the caller.
	/// </summary>
	/// <param name="whenLocked">The delegate to execute when the lock is acquired.</param>
	/// <param name="cancellationToken">The cancellation token that can be used to cancel waiting for the lock.</param>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="whenLocked"/> is <see langword="null"/>.</exception>
	public async Task LockAsync(Func<Task> whenLocked, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);

		// Atomically clear the cached TCS and retrieve the previous value. If it was null, create a new one.
		var next = Interlocked.Exchange(ref cachedTcs, null) ?? CreateTcs();

		// Atomically set the task to the next TCS and retrieve the previous task.
		var prev = Interlocked.Exchange(ref task, next.Task);

		try
		{
			// Wait for the previous task to complete. This is where we wait in line for the lock.
			await prev.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// We were canceled while waiting, so configure the previous task to allow the next waiter to proceed when it completes.
			_ = prev.ContinueWith(_ => next.SetResult(), TaskContinuationOptions.ExecuteSynchronously);

			_ = TryPutBackCachedTask(prev, next);
			throw;
		}

		try
		{
			// Execute the delegate. Any exceptions are propagated to the caller.
			await whenLocked().ConfigureAwait(false);
		}
		finally
		{
			// Try to put back the cached TCS. If we fail, it means another waiter is waiting, so we complete our 'next' TCS to allow them to proceed.
			if (!TryPutBackCachedTask(prev, next))
				next.SetResult();
		}
	}

	private Boolean TryPutBackCachedTask(Task prev, TaskCompletionSource next)
	{
		// Check if the task is still the one we set earlier. If true, put the task back to what it was when we entered. This is all done as an atomic operation.
		var original = Interlocked.CompareExchange(ref task, prev, next.Task);

		// If the task was still the one we set earlier, no one else has awaited the lock.
		if (original == next.Task)
		{
			// Because no one else has awaited the lock while we held it, we know that no one is awaiting our 'next' TCS. Atomically put it back in the "cached TCS" field.
			// Technically, there is a race condition here where another thread could have already taken the cached TCS, but the worst that can happen is a redundant allocation next time.
			Interlocked.Exchange(ref cachedTcs, next);
			return true;
		}

		// Otherwise we know that someone else is waiting to enter the lock.
		return false;
	}

	private TaskCompletionSource CreateTcs()
	{
		#if DEBUG
		Interlocked.Increment(ref TcsCtorCount);
		#endif
		return new(TaskCreationOptions.RunContinuationsAsynchronously);
	}
	#if DEBUG
	public UInt64 TcsCtorCount;
	#endif
}