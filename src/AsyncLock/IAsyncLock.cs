using System;
using System.Threading;
using System.Threading.Tasks;

namespace NickStrupat;

public interface IAsyncLock<TReleaser> where TReleaser : IDisposable
{
	Task<TReleaser> LockAsync(CancellationToken cancellationToken);
}

public interface IAsyncLock
{
	Task LockAsync(Func<Task> whenLocked, CancellationToken cancellationToken = default);
}