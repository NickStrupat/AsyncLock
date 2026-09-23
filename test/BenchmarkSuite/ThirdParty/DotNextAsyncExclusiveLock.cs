using System;
using System.Threading;
using System.Threading.Tasks;
using DotNext.Threading;
using NickStrupat;

namespace BenchmarkSuite.ThirdParty;

/// <summary>Adapts DotNext.Threading's <see cref="AsyncExclusiveLock"/> to <see cref="IAsyncLock"/>.
/// Not reentrant; acquisition returns a pooled <see cref="ValueTask"/>.</summary>
public sealed class DotNextAsyncExclusiveLock : IAsyncLock
{
	private readonly AsyncExclusiveLock inner = new();

	public async ValueTask LockAsync(Func<ValueTask> whenLocked, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);
		await inner.AcquireAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await whenLocked().ConfigureAwait(false);
		}
		finally
		{
			inner.Release();
		}
	}
}
