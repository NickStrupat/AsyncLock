// using NickStrupat;
// using Xunit;
//
// namespace UnitTests;
//
// public class ReentrantAsyncLockTests
// {
// 	private sealed class AsyncTaskCompletionSource() : TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
// 	private sealed class AsyncTaskCompletionSource<T>() : TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
//
// 	[Fact]
// 	public async Task ReentrantAsyncLock_Unlocked_SynchronouslyPermitsLock()
// 	{
// 		var mutex = new ReentrantAsyncLock();
//
// 		var lockTask = mutex.LockAsync(() => Task.CompletedTask).AsTask();
//
// 		Assert.True(lockTask.IsCompleted);
// 		Assert.False(lockTask.IsFaulted);
// 		Assert.False(lockTask.IsCanceled);
// 	}
//
// 	[Fact]
// 	public async Task ReentrantAsyncLock_Locked_PreventsLockUntilUnlocked()
// 	{
// 		var mutex = new ReentrantAsyncLock();
// 		var task1HasLock = new AsyncTaskCompletionSource<Object?>();
// 		var task1Continue = new AsyncTaskCompletionSource<Object?>();
//
// 		var task1 = Task.Run(async () =>
// 		{
// 			await mutex.LockAsync(async () =>
// 			{
// 				task1HasLock.SetResult(null);
// 				await task1Continue.Task;
// 			});
// 		});
// 		await task1HasLock.Task;
//
// 		var task2 = Task.Run(async () =>
// 		{
// 			await mutex.LockAsync(() => Task.CompletedTask);
// 		});
//
// 		Assert.False(task2.IsCompleted);
// 		task1Continue.SetResult(null);
// 		await task2;
// 	}
//
// 	[Fact]
// 	public async Task ReentrantAsyncLock_WhenLocked_IsReentrant()
// 	{
// 		var mutex = new ReentrantAsyncLock();
// 		var reentrant = 0;
// 		await mutex.LockAsync(async () =>
// 			await mutex.LockAsync(async () =>
// 			{
// 				await Task.Yield();
// 				await mutex.LockAsync(async () =>
// 					await mutex.LockAsync(async () =>
// 						reentrant++
// 					).ConfigureAwait(false)
// 				);
// 			})
// 		);
// 		Assert.Equal(1, reentrant);
// 	}
//
// 	[Fact]
// 	public async Task ReentrantAsyncLock_WhenLockedRecursivelyAndSequentially_IsReentrant()
// 	{
// 		var asyncLock = new ReentrantAsyncLock();
// 		var raceCondition = 0;
// 		// You can acquire the lock asynchronously
// 		await asyncLock.LockAsync(async () =>
// 		{
// 			await Task.WhenAll(
// 				Task.Run(async () =>
// 				{
// 					// The lock is reentrant
// 					await asyncLock.LockAsync(async () =>
// 					{
// 						// The lock provides mutual exclusion
// 						raceCondition++;
// 					});
// 				}),
// 				Task.Run(async () =>
// 				{
// 					await asyncLock.LockAsync(async () =>
// 					{
// 						raceCondition++;
// 					});
// 				})
// 			);
// 		});
// 		Assert.Equal(2, raceCondition);
// 	}
// }