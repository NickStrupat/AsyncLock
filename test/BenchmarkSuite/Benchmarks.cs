using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using NickStrupat;

namespace BenchmarkSuite
{
	[MemoryDiagnoser]
	[SimpleJob(RunStrategy.ColdStart, launchCount: 1, warmupCount: 5, iterationCount: 10)]
	// [GenericTypeArguments(typeof(AsyncLock))]
	// [GenericTypeArguments(typeof(SemaphoreSlimAsyncLock))]
	// Public for BenchmarkDotNet, so it cannot be constrained to the internal IAsyncLock; the cast below
	// fails at construction for a type that does not implement it.
	public class Benchmarks<TAsyncLock>
		where TAsyncLock : new()
	{
		Int32 count = 0;
		readonly IAsyncLock @lock = (IAsyncLock)new TAsyncLock();
		private readonly Func<ValueTask> increment;
		private readonly Func<Int32, CancellationToken, ValueTask> parallelForEachBody;

		public Benchmarks()
		{
			increment = () =>
			{
				++count;
				return ValueTask.CompletedTask;
			};
			parallelForEachBody = (_, _) => @lock.LockAsync(increment, CancellationToken.None);
		}

		[Benchmark]
		public async Task FullContention()
		{
			await Parallel.ForAsync(0, 1_000_000, parallelForEachBody);
		}

		[Benchmark]
		public async Task NoContention()
		{
			for (var i = 0; i < 1_000_000; ++i)
				await @lock.LockAsync(increment, CancellationToken.None);
		}
	}
}