using System;
using System.Threading;
using System.Threading.Tasks;
using NickStrupat;

namespace BenchmarkSuite.ThirdParty;

/// <summary>Adapts Nito.AsyncEx's <c>AsyncLock</c> to <see cref="IAsyncLock"/> using its idiomatic
/// <c>using (await LockAsync())</c> pattern. Not reentrant; waiters are resumed asynchronously.</summary>
public sealed class NitoAsyncLock : IAsyncLock
{
	private readonly Nito.AsyncEx.AsyncLock inner = new();

	public async ValueTask LockAsync(Func<ValueTask> whenLocked, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);
		using (await inner.LockAsync(cancellationToken).ConfigureAwait(false))
			await whenLocked().ConfigureAwait(false);
	}
}
