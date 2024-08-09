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
		Assert.Equal(0ul, asyncLock.TcsCtorCount);
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
		var noOp = () => Task.CompletedTask;
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
		Assert.Fail($"Memory allocation detected during all {noAllocationRetryLimit} iterations");
	}
}