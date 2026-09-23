using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using NickStrupat;

namespace BenchmarkSuite
{
	/// <summary>
	/// Contended acquisitions whose waits are cancelled while queued, as when timeouts fire under load. Each round holds
	/// the lock, queues <see cref="WaitersPerRound"/> waiters behind it, cancels every other one and waits for those to
	/// observe it, then releases, so the lock must pass the turn past each cancelled waiter. Results are per waiter.
	/// </summary>
	[MemoryDiagnoser]
	// Three launches because contended results vary 10-25% from one process to the next.
	[SimpleJob(RunStrategy.ColdStart, launchCount: 3, warmupCount: 5, iterationCount: 10)]
	// Public for BenchmarkDotNet, so it cannot be constrained to the internal IAsyncLock; the cast below
	// fails at construction for a type that does not implement it.
	public class CancellationBenchmarks<TAsyncLock>
		where TAsyncLock : new()
	{
		private const Int32 Rounds = 10_000;
		private const Int32 WaitersPerRound = 8;
		private const Int32 CancelledPerRound = WaitersPerRound / 2;

		Int32 count = 0;
		readonly IAsyncLock @lock = (IAsyncLock)new TAsyncLock();
		private readonly CancellationTokenSource neverCancelled = new();
		private readonly ValueTask[] waiters = new ValueTask[WaitersPerRound];
		private readonly Func<ValueTask> increment;
		private readonly Func<ValueTask> holdUntilReleased;
		private TaskCompletionSource release = new();

		public CancellationBenchmarks()
		{
			increment = () =>
			{
				++count;
				return ValueTask.CompletedTask;
			};
			holdUntilReleased = () => new ValueTask(release.Task);
		}

		[GlobalCleanup]
		public void Cleanup() => neverCancelled.Dispose();

		[Benchmark(OperationsPerInvoke = Rounds * WaitersPerRound)]
		public async Task ContendedWithCancelledWaiters()
		{
			for (var round = 0; round < Rounds; ++round)
				await RunRoundAsync();
		}

		// Pooled so the harness's own suspension allocates nothing; each round still allocates its gate and the
		// source it cancels.
		[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
		private async ValueTask RunRoundAsync()
		{
			var countBefore = count;
			release = new TaskCompletionSource();
			using var cancelled = new CancellationTokenSource();
			var holder = @lock.LockAsync(holdUntilReleased, CancellationToken.None);
			for (var i = 0; i < WaitersPerRound; ++i)
				waiters[i] = @lock.LockAsync(increment, i % 2 == 0 ? neverCancelled.Token : cancelled.Token);

#pragma warning disable VSTHRD103 // CancelAsync would only defer the callbacks; the waits below observe them either way
			cancelled.Cancel();
#pragma warning restore VSTHRD103

			// Some locks (SemaphoreSlim among them) dequeue a cancelled waiter only in a later continuation, and a
			// release that reaches it first admits it instead; the waits here let every cancellation land first.
			var cancellations = 0;
			for (var i = 1; i < WaitersPerRound; i += 2)
			{
				try
				{
					await waiters[i];
				}
				catch (OperationCanceledException)
				{
					++cancellations;
				}
			}

			release.SetResult();
			await holder;
			for (var i = 0; i < WaitersPerRound; i += 2)
				await waiters[i];
			if (cancellations != CancelledPerRound || count - countBefore != WaitersPerRound - CancelledPerRound)
				throw new InvalidOperationException(
					$"{typeof(TAsyncLock).Name}: {cancellations} of {CancelledPerRound} waiters cancelled, " +
					$"{count - countBefore} of {WaitersPerRound - CancelledPerRound} admitted");
		}
	}
}
