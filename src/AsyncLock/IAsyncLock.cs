using System;
using System.Threading;
using System.Threading.Tasks;

namespace NickStrupat;

internal interface IAsyncLock
{
	ValueTask LockAsync(Func<ValueTask> whenLocked, CancellationToken cancellationToken = default);
}