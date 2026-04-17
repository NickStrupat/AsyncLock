using System;
using System.Threading;
using System.Threading.Tasks;

namespace NickStrupat;

public interface IAsyncLock<TReleaser> where TReleaser : IDisposable
{
	ValueTask<TReleaser> LockAsync(CancellationToken cancellationToken = default);
}

public interface IAsyncLock
{
	ValueTask LockAsync(Func<ValueTask> whenLocked, CancellationToken cancellationToken = default);
}