using System;
using System.Threading;
using System.Threading.Tasks;

namespace NickStrupat;

public sealed class AsyncSemaphoreSlimLock : IAsyncLock
{
	private readonly SemaphoreSlim semaphore = new(1, 1);

	public async ValueTask LockAsync(Func<ValueTask> whenLocked, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);
		await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await whenLocked().ConfigureAwait(false);
		}
		finally
		{
			semaphore.Release();
		}
	}
}