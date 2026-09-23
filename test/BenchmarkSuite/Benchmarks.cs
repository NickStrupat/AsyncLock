using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using NickStrupat;

namespace BenchmarkSuite
{
	[MemoryDiagnoser]
	// Three launches because contended results vary 10-25% from one process to the next.
	[SimpleJob(RunStrategy.ColdStart, launchCount: 3, warmupCount: 5, iterationCount: 10)]
	// [GenericTypeArguments(typeof(AsyncLock))]
	// [GenericTypeArguments(typeof(SemaphoreSlimAsyncLock))]
	// Public for BenchmarkDotNet, so it cannot be constrained to the internal IAsyncLock; the cast below
	// fails at construction for a type that does not implement it.
	public class Benchmarks<TAsyncLock>
		where TAsyncLock : new()
	{
		// Sized so every iteration runs for at least 100 ms (BenchmarkDotNet's minimum for a stable measurement);
		// an uncontended acquisition takes tens of nanoseconds, a contended one about a microsecond, and a contended
		// one that awaits while holding the lock a few microseconds.
		private const Int32 ContendedOperations = 1_000_000;
		private const Int32 ContendedWithAwaitOperations = 200_000;
		private const Int32 UncontendedOperations = 10_000_000;

		Int32 count = 0;
		readonly IAsyncLock @lock = (IAsyncLock)new TAsyncLock();
		private readonly Func<ValueTask> increment;
		private readonly Func<Int32, CancellationToken, ValueTask> parallelForEachBody;
		private readonly Func<ValueTask> incrementAndYield;
		private readonly Func<Int32, CancellationToken, ValueTask> parallelForEachBodyWithAwait;

		public Benchmarks()
		{
			increment = () =>
			{
				++count;
				return ValueTask.CompletedTask;
			};
			parallelForEachBody = (_, _) => @lock.LockAsync(increment, CancellationToken.None);
			incrementAndYield = IncrementAndYieldAsync;
			parallelForEachBodyWithAwait = (_, _) => @lock.LockAsync(incrementAndYield, CancellationToken.None);
		}

		// Suspends while the lock is held, so the lock's release path runs after an asynchronous completion. Pooled so
		// the suspension itself allocates nothing and the reported allocation is the lock's own.
		[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
		private async ValueTask IncrementAndYieldAsync()
		{
			++count;
			await Task.Yield();
		}

		[Benchmark(OperationsPerInvoke = ContendedOperations)]
		public async Task FullContention()
		{
			await Parallel.ForAsync(0, ContendedOperations, parallelForEachBody);
		}

		[Benchmark(OperationsPerInvoke = ContendedWithAwaitOperations)]
		public async Task FullContentionWithAwait()
		{
			await Parallel.ForAsync(0, ContendedWithAwaitOperations, parallelForEachBodyWithAwait);
		}

		[Benchmark(OperationsPerInvoke = UncontendedOperations)]
		public async Task NoContention()
		{
			for (var i = 0; i < UncontendedOperations; ++i)
				await @lock.LockAsync(increment, CancellationToken.None);
		}
	}
}