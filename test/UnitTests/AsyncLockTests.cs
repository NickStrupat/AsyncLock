using NickStrupat;
using Xunit;

namespace UnitTests;

public class AsyncLockTests
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
	public async Task ReusesTaskCompletionSourceWhenNotContended()
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
	}
	#endif
	
	[Fact]
	public async Task DoesNotAllocateWhenNotContendedAndDelegateDoesNotAllocate()
	{
		var noOp = () => Task.CompletedTask;
		var asyncLock = new AsyncLock();
		for(var x = 0; x != 10_000; ++x)
		{
			var mem = GC.GetTotalMemory(true);
			for (var i = 0; i < 1000; ++i)
			{
				await asyncLock.LockAsync(noOp);
			}
			var mem2 = GC.GetTotalMemory(true);
			if (mem == mem2)
				break;
		}
	}
}