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
		var noOp = async () => { };
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
				return Task.CompletedTask;
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