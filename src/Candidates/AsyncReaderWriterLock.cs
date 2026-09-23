using System;
using System.Threading;
using System.Threading.Tasks;

namespace NickStrupat;

// public sealed class AsyncReaderWriterLock2
// {
// 	private Task task = Task.CompletedTask;
// 	private UInt64 readersActiveCount;
// 	private UInt64 writersWaitingCount;
//
// 	public async ValueTask WriteLockAsync(Func<Task> whenLocked)
// 	{
// 		ArgumentNullException.ThrowIfNull(whenLocked);
// 		Interlocked.Increment(ref writersWaitingCount);
// 		var unwaited = false;
// 		try
// 		{
// 			await writerTask.ConfigureAwait(false);
// 			Interlocked.Decrement(ref writersWaitingCount);
// 			unwaited = true;
// 		}
// 		finally
// 		{
// 			if (!unwaited)
// 				Interlocked.Decrement(ref writersWaitingCount);
// 		}
// 	}
// }

public sealed class AsyncReaderWriterLock
{
	// Not scoped to a single call: the first reader acquires it and the last reader, which may be a different
	// call, releases it.
	private readonly SemaphoreSlim exclusiveWriterSemaphore = new(1, 1);
	private readonly AsyncSemaphoreSlimLock sharedReaderLock = new();
	private UInt64 readerCount;

	public async ValueTask WriteLockAsync(Func<Task> whenLocked, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);

		await exclusiveWriterSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await whenLocked().ConfigureAwait(false);
		}
		finally
		{
			exclusiveWriterSemaphore.Release();
		}
	}

	public async ValueTask ReadLockAsync(Func<Task> whenLocked, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);

		await sharedReaderLock.LockAsync(async () =>
		{
			if (readerCount++ == 0)
				await exclusiveWriterSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
		}, cancellationToken).ConfigureAwait(false);

		try
		{
			await whenLocked().ConfigureAwait(false);
		}
		finally
		{
			await sharedReaderLock.LockAsync(() =>
			{
				if (readerCount-- == 1)
					exclusiveWriterSemaphore.Release();
				return ValueTask.CompletedTask;
			}, cancellationToken).ConfigureAwait(false);
		}
	}
}

public sealed class SharedAsyncLock
{
	private readonly AsyncSemaphoreSlimLock @lock = new();
	private UInt64 sharedCount;

	public async ValueTask ExclusiveLockAsync(Func<Task> whenLocked, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);

		await @lock.LockAsync(async () => await whenLocked().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask SharedLockAsync(Func<Task> whenFirstLocked, Func<Task> whenLastUnlocked, Func<Task> whenLocked, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(whenFirstLocked);
		ArgumentNullException.ThrowIfNull(whenLastUnlocked);

		await @lock.LockAsync(async () =>
		{
			if (sharedCount++ == 0)
			{
				await whenFirstLocked();
			}
		}, cancellationToken).ConfigureAwait(false);

		await whenLocked().ConfigureAwait(false);

		await @lock.LockAsync(async () =>
		{
			if (sharedCount-- == 1)
			{
				await whenLastUnlocked();
			}
		}, cancellationToken).ConfigureAwait(false);
	}
}
