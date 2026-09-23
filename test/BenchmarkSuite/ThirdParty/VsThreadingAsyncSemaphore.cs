using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using NickStrupat;

namespace BenchmarkSuite.ThirdParty;

/// <summary>Adapts Microsoft.VisualStudio.Threading's <see cref="AsyncSemaphore"/> (count 1) to
/// <see cref="IAsyncLock"/>. Not reentrant; acquisition returns a <see cref="Task{TResult}"/>.</summary>
public sealed class VsThreadingAsyncSemaphore : IAsyncLock
{
	private readonly AsyncSemaphore inner = new(1);

	public async ValueTask LockAsync(Func<ValueTask> whenLocked, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);
		using (await inner.EnterAsync(cancellationToken).ConfigureAwait(false))
			await whenLocked().ConfigureAwait(false);
	}
}
