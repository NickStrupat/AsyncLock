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
	private readonly AsyncSemaphoreSlimLock exclusiveWriterLock = new();
	private readonly AsyncSemaphoreSlimLock sharedReaderLock = new();
	private UInt64 readerCount;

	public async ValueTask WriteLockAsync(Func<Task> whenLocked, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);

		using (await exclusiveWriterLock.LockAsync(cancellationToken).ConfigureAwait(false))
			await whenLocked().ConfigureAwait(false);
	}

	public async ValueTask ReadLockAsync(Func<Task> whenLocked, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);

		AsyncSemaphoreSlimLock.Releaser writeLockReleaser = default;
		try
		{
			using (await sharedReaderLock.LockAsync(cancellationToken).ConfigureAwait(false))
			{
				if (readerCount++ == 0)
				{
					writeLockReleaser = await exclusiveWriterLock.LockAsync(cancellationToken).ConfigureAwait(false);
				}
			}

			await whenLocked().ConfigureAwait(false);
		}
		finally
		{
			using (await sharedReaderLock.LockAsync(cancellationToken).ConfigureAwait(false))
			{
				if (readerCount-- == 1)
				{
					writeLockReleaser.Dispose();
				}
			}
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

		using (await @lock.LockAsync(cancellationToken).ConfigureAwait(false))
			await whenLocked().ConfigureAwait(false);
	}

	public async ValueTask SharedLockAsync(Func<Task> whenFirstLocked, Func<Task> whenLastUnlocked, Func<Task> whenLocked, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(whenFirstLocked);
		ArgumentNullException.ThrowIfNull(whenLastUnlocked);

		using (await @lock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			if (sharedCount++ == 0)
			{
				await whenFirstLocked();
			}
		}

		await whenLocked().ConfigureAwait(false);

		using (await @lock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			if (sharedCount-- == 1)
			{
				await whenLastUnlocked();
			}
		}
	}
}
