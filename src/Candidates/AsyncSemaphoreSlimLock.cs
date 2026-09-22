using System;
using System.Threading;
using System.Threading.Tasks;

namespace NickStrupat;

public sealed class AsyncSemaphoreSlimLock : IAsyncLock<AsyncSemaphoreSlimLock.Releaser>, IAsyncLock
{
	private readonly SemaphoreSlim semaphore = new(1, 1);

	public async ValueTask<Releaser> LockAsync(CancellationToken cancellationToken)
	{
		await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
		return new Releaser(semaphore);
	}

	public readonly struct Releaser : IDisposable
	{
		private readonly SemaphoreSlim semaphore;
		internal Releaser(SemaphoreSlim semaphore) => this.semaphore = semaphore;
		public void Dispose() => semaphore?.Release();
	}

	public async ValueTask LockAsync(Func<ValueTask> whenLocked, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);
		using (await LockAsync(cancellationToken).ConfigureAwait(false))
			await whenLocked().ConfigureAwait(false);
	}
}