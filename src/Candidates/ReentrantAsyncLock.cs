// using System;
// using System.Threading;
// using System.Threading.Tasks;
//
// namespace NickStrupat;
//
// public sealed class ReentrantAsyncLock
// {
// 	private readonly AsyncLock rootAsyncLock = new();
// 	private readonly AsyncLocal<AsyncLock?> asyncLock = new();
//
// 	public async ValueTask LockAsync(Func<Task> whenLocked)
// 	{
// 		ArgumentNullException.ThrowIfNull(whenLocked);
// 		var currentAsyncLock = asyncLock.Value ?? rootAsyncLock;
// 		await currentAsyncLock.LockAsync(WhenLocked).ConfigureAwait(false);
//
// 		async Task WhenLocked()
// 		{
// 			var methodAsyncLock = new AsyncLock();
// 			asyncLock.Value = methodAsyncLock;
// 			try { await whenLocked().ConfigureAwait(false); }
// 			finally { await methodAsyncLock.LockAsync(Unlock).ConfigureAwait(false); }
// 			Task Unlock() => Task.FromResult(asyncLock.Value = currentAsyncLock);
// 		}
// 	}
// }