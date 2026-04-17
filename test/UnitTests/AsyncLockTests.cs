using System.Diagnostics;
using NickStrupat;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests;

public class AsyncLockTests(ITestOutputHelper output)
{
	[Fact]
	public async Task ProvideMutualExclusion()
	{
		var asyncLock = new AsyncLock();
		var inGuardedSection = false;
		Task GenerateTask() => Task.Run(async () =>
		{
			await asyncLock.LockAsync(async () =>
			{
				Assert.False(inGuardedSection);
				inGuardedSection = true;
				SynchronizationContext.SetSynchronizationContext(null);
				await Task.Yield(); // Return to the task pool
				inGuardedSection = false;
			});
		});
		for (var i = 0; i < 1000; ++i)
		{
			await Task.WhenAll(
				GenerateTask(),
				GenerateTask(),
				GenerateTask(),
				GenerateTask(),
				GenerateTask()
			);
		}
	}

	[Fact]
	public async Task ProvideMutualExclusionOfNestedAsyncCode()
	{
		var asyncLock = new AsyncLock();
		var raceConditionDetector = 0;
		async Task GenerateTask()
		{
			await asyncLock.LockAsync(async () =>
			{
				await Task.Run(() => ++raceConditionDetector);
			});
		}
		for (var i = 0; i < 1000; ++i)
		{
			await Task.WhenAll(
				GenerateTask(),
				GenerateTask(),
				GenerateTask(),
				GenerateTask(),
				GenerateTask()
			);
		}
		Assert.Equal(5000, raceConditionDetector);
	}

	#if DEBUG
	[Fact]
	public async Task ReusesCachedTaskCompletionSourceWhenNotContended()
	{
		var asyncLock = new AsyncLock();
		for (var i = 0; i < 1000; ++i)
		{
			await asyncLock.LockAsync(async () => await Task.Yield());
		}
		Assert.Equal(1ul, asyncLock.TcsCtorCount);
	}

	[Fact]
	public async Task DoesNotReuseTaskCompletionSourceWhenContended()
	{

		var asyncLock = new AsyncLock();
		var inGuardedSection = false;
		Task GenerateTask() => Task.Run(async () =>
		{
			await asyncLock.LockAsync(async () =>
			{
				Assert.False(inGuardedSection);
				inGuardedSection = true;
				SynchronizationContext.SetSynchronizationContext(null);
				await Task.Yield(); // Return to the task pool
				inGuardedSection = false;
			});
		});
		for (var i = 0; i < 1000; ++i)
		{
			await Task.WhenAll(
				GenerateTask(),
				GenerateTask(),
				GenerateTask(),
				GenerateTask(),
				GenerateTask()
			);
		}
		Assert.NotEqual(5000ul, asyncLock.TcsCtorCount);
		Assert.NotEqual(0ul, asyncLock.TcsCtorCount);
		Assert.NotEqual(1ul, asyncLock.TcsCtorCount);
		output.WriteLine(asyncLock.TcsCtorCount.ToString());
	}
	#endif

	[Fact]
	public async Task DoesNotAllocateWhenNotContended()
	{
		Func<ValueTask> noOp = () => ValueTask.CompletedTask;
		var asyncLock = new AsyncLock();
		const int noAllocationRetryLimit = 10_000;
		for(var x = 0; x != noAllocationRetryLimit; ++x)
		{
			var mem = GC.GetTotalMemory(true);
			for (var i = 0; i < 1000; ++i)
			{
				await asyncLock.LockAsync(noOp);
			}
			var mem2 = GC.GetTotalMemory(true);
			if (mem == mem2)
				return;
		}
		Assert.Fail($"Memory allocation detected during all {noAllocationRetryLimit:N0} iterations");
	}

	sealed class TestTask
	{
		private readonly TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Task WaitAsync() => signal.Task;
		public void SetAsCompleted() => signal.SetResult();
	}

	[Fact]
	public async Task SupportsCancellation()
	{
		var asyncLock = new AsyncLock();

		var releaseFirstLock = new TaskCompletionSource();
		var firstLockTaken = new TaskCompletionSource();
		var firstLockReleased = new TaskCompletionSource();
		var firstLockTask = asyncLock.LockAsync(
			async () =>
			{
				firstLockTaken.SetResult();
				try { await releaseFirstLock.Task; }
				finally { firstLockReleased.SetResult(); }
			},
			CancellationToken.None
		);
		await firstLockTaken.Task;

		using var secondLockCancellationTokenSource = new CancellationTokenSource();
		var releaseSecondLock = new TaskCompletionSource();
		var secondLockTaken = new TaskCompletionSource();
		var secondLockReleased = new TaskCompletionSource();
		var secondLockTask = asyncLock.LockAsync(
			async () =>
			{
				secondLockTaken.SetResult();
				try { await releaseSecondLock.Task; }
				finally { secondLockReleased.SetResult(); }
			},
			secondLockCancellationTokenSource.Token
		);

		var thirdLockTaken = new TaskCompletionSource();
		var thirdLockTask = asyncLock.LockAsync(() =>
			{
				thirdLockTaken.SetResult();
				return ValueTask.CompletedTask;
			},
			CancellationToken.None
		);

		// Cancel the second lock attempt
		await secondLockCancellationTokenSource.CancelAsync();
		await Assert.ThrowsAsync<TaskCanceledException>(async () => await secondLockTask);

		// Release the first lock
		releaseFirstLock.SetResult();
		await firstLockReleased.Task;
		await thirdLockTask; // Should complete now
		await thirdLockTaken.Task;

		// Verify that the second lock was never taken
		Assert.Equal(TaskStatus.WaitingForActivation, secondLockTaken.Task.Status);
	}

	[Fact]
	public async Task CancellationDoesNotBreakMutualExclusion()
	{
		// This test reproduces a race where a cancelled waiter's ContinueWith
		// completes a TCS that gets reused by a later waiter, allowing two
		// callers into the critical section simultaneously.
		//
		// Sequence:
		// 1. A holds the lock (swapped in tcs0)
		// 2. B queues behind A (swapped in tcs1, awaits tcs0.Task)
		// 3. B is cancelled — sets continuation: tcs0.Task → tcs1.SetResult()
		//    TryPutBackCachedTask succeeds, caching tcs1
		// 4. C queues — grabs tcs1 from cache, awaits tcs0.Task
		// 5. D queues behind C — awaits tcs1.Task
		// 6. A finishes — calls tcs0.SetResult()
		//    → C wakes up (correct)
		//    → rogue continuation fires tcs1.SetResult() → D wakes up (BUG!)

		var asyncLock = new AsyncLock();

		// A acquires and holds the lock
		var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var aAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var taskA = asyncLock.LockAsync(async () =>
		{
			aAcquired.SetResult();
			await releaseA.Task;
		});
		await aAcquired.Task;

		// B queues behind A (LockAsync runs synchronously until the await, so B is queued when it returns)
		using var bCts = new CancellationTokenSource();
		var taskB = asyncLock.LockAsync(() => ValueTask.CompletedTask, bCts.Token);

		// Cancel B — this sets up the rogue continuation on A's underlying task
		await bCts.CancelAsync();
		await Assert.ThrowsAsync<TaskCanceledException>(taskB.AsTask);

		// C queues — grabs B's cached TCS, awaits A's task
		var releaseC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var cAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var taskC = asyncLock.LockAsync(async () =>
		{
			cAcquired.SetResult();
			await releaseC.Task;
		});

		// D queues behind C — awaits the TCS that has the rogue continuation
		var dAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseD = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var taskD = asyncLock.LockAsync(async () =>
		{
			dAcquired.SetResult();
			await releaseD.Task;
		});

		// Release A — C should acquire, but the rogue continuation also releases D
		releaseA.SetResult();
		await taskA;
		await cAcquired.Task;

		// D should NOT have acquired the lock — C is still holding it
		await Task.Delay(200);
		Assert.False(dAcquired.Task.IsCompleted,
			"Mutual exclusion violated: D entered the critical section while C still holds the lock. " +
			"The cancelled waiter's ContinueWith prematurely completed the reused TCS.");

		// Clean up
		releaseC.SetResult();
		await taskC;
		await dAcquired.Task;
		releaseD.SetResult();
		await taskD;
	}

	[Fact]
	public async Task ReusesCachedTaskCompletionSourceWhenCancelledButNotContended()
	{
		var asyncLock = new AsyncLock();

		var releaseFirstLock = new TaskCompletionSource();
		var firstLockTaken = new TaskCompletionSource();
		var firstLockReleased = new TaskCompletionSource();
		var firstLockTask = asyncLock.LockAsync(
			async () =>
			{
				firstLockTaken.SetResult();
				try { await releaseFirstLock.Task; }
				finally { firstLockReleased.SetResult(); }
			},
			CancellationToken.None
		);
		await firstLockTaken.Task;

		// Attempt and cancel multiple lock attempts while the first lock is held
		for (var i = 0; i != 10; i++)
		{
			using var secondLockCancellationTokenSource = new CancellationTokenSource();
			var releaseSecondLock = new TaskCompletionSource();
			var secondLockTaken = new TaskCompletionSource();
			var secondLockReleased = new TaskCompletionSource();
			var secondLockTask = asyncLock.LockAsync(
				async () =>
				{
					secondLockTaken.SetResult();
					try
					{
						await releaseSecondLock.Task;
					}
					finally
					{
						secondLockReleased.SetResult();
					}
				},
				secondLockCancellationTokenSource.Token
			);

			// Cancel the second lock attempt
			await secondLockCancellationTokenSource.CancelAsync();
			await Assert.ThrowsAsync<TaskCanceledException>(async () => await secondLockTask);
		}

		// Release the first lock
		releaseFirstLock.SetResult();
		await firstLockReleased.Task;

		// Verify that only two TaskCompletionSources were created: one for the first lock,
		// and one for the cancelled lock attempts that were never contended
		Assert.Equal(2ul, asyncLock.TcsCtorCount);
	}
}