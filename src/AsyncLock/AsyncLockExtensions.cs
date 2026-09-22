using System;
using System.Threading;
using System.Threading.Tasks;

namespace NickStrupat;

public static class AsyncLockExtensions
{
	public static async Task LockAsync<TReleaser>(this IAsyncLock<TReleaser> asyncLock,
		Func<Task> whenLocked,
		CancellationToken cancellationToken = default)
		where TReleaser : IDisposable
	{
		cancellationToken.ThrowIfCancellationRequested();
		using (await asyncLock.LockAsync(cancellationToken))
			await whenLocked();
	}
}