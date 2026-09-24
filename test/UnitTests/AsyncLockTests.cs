using System.Runtime.CompilerServices;
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
				await Task.Yield();
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
	public async Task CancellationDoesNotBreakMutualExclusion()
	{
		// Reproduces the recycle-while-held race: a contended waiter (B) is cancelled
		// while its predecessor (A) still holds the lock. The cancellation path resets
		// and returns B's turnstile node to the pool even though A has not yet completed
		// it. A later acquisition (C) rents that recycled node, a further waiter (D) ends
		// up awaiting it, and when A finally releases it completes the recycled node —
		// admitting D into the critical section while C still holds it.

		var asyncLock = new AsyncLock();

		// A acquires and holds the lock.
		var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var aAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var taskA = asyncLock.LockAsync(async () =>
		{
			aAcquired.SetResult();
			await releaseA.Task;
		}).AsTask();
		await aAcquired.Task;

		// B queues behind A, then is cancelled while still waiting.
		using var bCts = new CancellationTokenSource();
		var taskB = asyncLock.LockAsync(() => ValueTask.CompletedTask, bCts.Token).AsTask();
		await bCts.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskB);

		// C queues — on the buggy impl it rents B's prematurely-recycled node — and holds.
		var releaseC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var cAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var taskC = asyncLock.LockAsync(async () =>
		{
			cAcquired.SetResult();
			await releaseC.Task;
		}).AsTask();

		// D queues behind C — on the buggy impl it awaits the recycled node.
		var dAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseD = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var taskD = asyncLock.LockAsync(async () =>
		{
			dAcquired.SetResult();
			await releaseD.Task;
		}).AsTask();

		// Release A — C should acquire; D must not.
		releaseA.SetResult();
		await taskA;
		await cAcquired.Task;

		await Task.Delay(200);
		Assert.False(dAcquired.Task.IsCompleted,
			"Mutual exclusion violated: D entered the critical section while C still holds the lock. " +
			"A cancelled waiter recycled a turnstile node that its predecessor later completed.");

		// Clean up.
		releaseC.SetResult();
		await taskC;
		await dAcquired.Task;
		releaseD.SetResult();
		await taskD;
	}

	[Fact]
	public async Task CancellingContendedWaiterUnblocksPromptlyWithoutFaulting()
	{
		var asyncLock = new AsyncLock();

		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var holder = asyncLock.LockAsync(async () =>
		{
			held.SetResult();
			await release.Task;
		}).AsTask();
		await held.Task;

		// Queue a waiter behind the holder and cancel it, repeatedly. The cancellation must surface as
		// an OperationCanceledException (never an InvalidOperationException from a double completion or a
		// stale source-token check), and must resolve while the holder is STILL holding — i.e. the wait
		// is genuinely cancelled, not merely released when the holder finishes.
		for (var i = 0; i < 20; i++)
		{
			using var cts = new CancellationTokenSource();
			var entered = false;
			var waiter = asyncLock.LockAsync(
				() => { entered = true; return ValueTask.CompletedTask; },
				cts.Token).AsTask();

			await cts.CancelAsync();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

			Assert.False(entered, "a cancelled waiter must never enter the critical section");
			Assert.False(holder.IsCompleted, "the holder must still hold the lock when the waiter cancels");
		}

		release.SetResult();
		await holder;
	}

	[Fact]
	public async Task SupportsCancellation()
	{
		var asyncLock = new AsyncLock();

		var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var firstTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var firstReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var firstTask = asyncLock.LockAsync(async () =>
		{
			firstTaken.SetResult();
			try { await releaseFirst.Task; }
			finally { firstReleased.SetResult(); }
		}).AsTask();
		await firstTaken.Task;

		// Second attempt queues behind the first and is cancelled while waiting.
		using var secondCts = new CancellationTokenSource();
		var secondTaken = false;
		var secondTask = asyncLock.LockAsync(
			() => { secondTaken = true; return ValueTask.CompletedTask; },
			secondCts.Token).AsTask();

		// Third attempt queues behind the second.
		var thirdTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var thirdTask = asyncLock.LockAsync(() =>
		{
			thirdTaken.SetResult();
			return ValueTask.CompletedTask;
		}).AsTask();

		// Cancel the second attempt — it must fault promptly, before the first lock is released.
		await secondCts.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondTask);
		Assert.False(thirdTaken.Task.IsCompleted, "third must still wait behind the holder");

		// Release the first; the third proceeds through the bridge left by the cancelled second.
		releaseFirst.SetResult();
		await firstReleased.Task;
		await thirdTask;
		await thirdTaken.Task;

		Assert.False(secondTaken, "the cancelled attempt must never enter the critical section");
	}

	[Fact]
	public async Task ConcurrentCancellationPreservesMutualExclusion()
	{
		var asyncLock = new AsyncLock();
		var active = 0;
		var violations = 0;

		async Task Worker(Int32 seed)
		{
			var rng = new Random(seed);
			for (var i = 0; i < 250; i++)
			{
				using var cts = new CancellationTokenSource();
				if (rng.Next(2) == 0)
					cts.CancelAfter(rng.Next(0, 3)); // race a cancellation against acquisition
				try
				{
					await asyncLock.LockAsync(async () =>
					{
						if (Interlocked.Increment(ref active) != 1)
							Interlocked.Increment(ref violations);
						await Task.Yield(); // widen the critical-section window
						Interlocked.Decrement(ref active);
					}, cts.Token);
				}
				catch (OperationCanceledException) { }
			}
		}

		var workers = Enumerable.Range(0, 8).Select(s => Task.Run(() => Worker(s))).ToArray();
		await Task.WhenAll(workers);

		Assert.Equal(0, Volatile.Read(ref violations));
		Assert.Equal(0, Volatile.Read(ref active));
	}

	[Fact]
	public async Task MassCancellationPassesTheTurnThroughToTheNextLiveWaiter()
	{
		// The turn passes through cancelled waiters on the releasing thread. Queue a very long run of them
		// and cancel them all at once: walking that chain must neither overflow the releaser's stack (the
		// walk is a loop, not recursion) nor drop the turn before it reaches the live waiter behind them.
		const Int32 cancelledWaiters = 100_000;
		var asyncLock = new AsyncLock();

		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var holder = asyncLock.LockAsync(async () =>
		{
			held.SetResult();
			await release.Task;
		}).AsTask();
		await held.Task;

		using var cts = new CancellationTokenSource();
		var cancelledEntered = 0;
		var waiters = new Task[cancelledWaiters];
		for (var i = 0; i < waiters.Length; i++)
			waiters[i] = asyncLock.LockAsync(
				() => { Interlocked.Increment(ref cancelledEntered); return ValueTask.CompletedTask; },
				cts.Token).AsTask();

		var liveEntered = false;
		var live = asyncLock.LockAsync(() => { liveEntered = true; return ValueTask.CompletedTask; }).AsTask();

		await cts.CancelAsync();
		foreach (var waiter in waiters)
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
		Assert.False(live.IsCompleted, "the live waiter must still wait behind the holder");

		release.SetResult();
		await holder;
		await live.WaitAsync(TimeSpan.FromSeconds(30)); // a dropped turn surfaces as a TimeoutException

		Assert.True(liveEntered, "the turn must reach the live waiter through the whole cancelled chain");
		Assert.Equal(0, Volatile.Read(ref cancelledEntered));
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public async Task DeepQueueOfSynchronousWaitersDoesNotOverflowTheStack(Boolean cancellable, Boolean withResult)
	{
		// Each waiter's critical section completes synchronously, so it releases the moment it is granted. If a
		// grant resumed the waiter inline on the releasing thread (a synchronous continuation), every waiter would
		// run inside its predecessor's release and a long queue would overflow the stack, killing the process.
		// Granting asynchronously keeps each waiter on its own stack. Run with and without a cancellable token (the
		// two resume through different signals) and through both the plain and the result-returning overloads.
		const Int32 queuedWaiters = 100_000;
		var asyncLock = new AsyncLock();
		using var cts = new CancellationTokenSource();
		var token = cancellable ? cts.Token : CancellationToken.None;

		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var holder = asyncLock.LockAsync(async () =>
		{
			held.SetResult();
			await release.Task;
		}).AsTask();
		await held.Task;

		var entered = 0;
		var outOfOrder = 0;
		var waiters = new Task[queuedWaiters];
		for (var i = 0; i < waiters.Length; i++)
		{
			var position = i;
			waiters[i] = withResult
				? asyncLock.LockAsync(() =>
				{
					if (entered++ != position)
						outOfOrder++;
					return new ValueTask<Int32>(position);
				}, token).AsTask()
				: asyncLock.LockAsync(() =>
				{
					if (entered++ != position)
						outOfOrder++;
					return ValueTask.CompletedTask;
				}, token).AsTask();
		}

		release.SetResult();
		await holder;
		await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(30));

		Assert.Equal(queuedWaiters, entered);
		Assert.Equal(0, outOfOrder);
	}

	[Fact]
	public async Task StateOverloadPassesTheStateToTheCriticalSection()
	{
		var asyncLock = new AsyncLock();
		var counter = new StrongBox<Int32>();

		await asyncLock.LockAsync(counter, static c => { c.Value += 2; return ValueTask.CompletedTask; });
		var result = await asyncLock.LockAsync(counter, static c => new ValueTask<Int32>(c.Value * 10));

		Assert.Equal(2, counter.Value);
		Assert.Equal(20, result);
	}

	[Fact]
	public async Task ResultOverloadReturnsTheCriticalSectionsResult()
	{
		var asyncLock = new AsyncLock();

		var immediate = await asyncLock.LockAsync(() => new ValueTask<String>("done"));
		var awaited = await asyncLock.LockAsync(async () =>
		{
			await Task.Yield();
			return 42;
		});

		Assert.Equal("done", immediate);
		Assert.Equal(42, awaited);
	}

	[ReleaseOnlyFact]
	public void StateAndResultOverloadsDoNotAllocateWhenUncontended()
	{
		// With a static lambda and the state passed explicitly there is no closure, and a free lock takes the lock
		// word without a queue node, so an uncontended acquisition allocates nothing. Everything here completes
		// synchronously on this thread, which is what makes the per-thread allocation counter exact.
		var asyncLock = new AsyncLock();
		var counter = new StrongBox<Int32>();
		const Int32 acquisitions = 1_000;

		for (var i = 0; i < acquisitions; i++) // warm up: JIT and the pooled async method builders
			AcquireBoth();
		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var i = 0; i < acquisitions; i++)
			AcquireBoth();
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		Assert.Equal(0, allocated);
		Assert.Equal(4 * acquisitions, counter.Value);

		void AcquireBoth()
		{
			var plain = asyncLock.LockAsync(counter, static c => { c.Value++; return ValueTask.CompletedTask; });
			var withResult = asyncLock.LockAsync(counter, static c => new ValueTask<Int32>(++c.Value));
			Assert.True(plain.IsCompletedSuccessfully && withResult.IsCompletedSuccessfully);
		}
	}

	[Fact]
	public async Task ResultOverloadProvidesMutualExclusion()
	{
		var asyncLock = new AsyncLock();
		var inGuardedSection = false;
		var next = 0;
		Task<Int32> Acquire() => Task.Run(async () => await asyncLock.LockAsync(async () =>
		{
			Assert.False(inGuardedSection);
			inGuardedSection = true;
			var value = next++;
			await Task.Yield();
			inGuardedSection = false;
			return value;
		}));

		var results = await Task.WhenAll(Enumerable.Range(0, 5_000).Select(_ => Acquire()));

		Assert.Equal(Enumerable.Range(0, 5_000), results.Order());
	}

	[Fact]
	public async Task ResultOverloadSupportsCancellationAndPropagatesExceptions()
	{
		var asyncLock = new AsyncLock();
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var holder = asyncLock.LockAsync(async () =>
		{
			held.SetResult();
			await release.Task;
			return 1;
		}).AsTask();
		await held.Task;

		using var cts = new CancellationTokenSource();
		var cancelledEntered = false;
		var cancelled = asyncLock.LockAsync(() => { cancelledEntered = true; return new ValueTask<Int32>(2); },
			cts.Token).AsTask();
		var faulting = asyncLock.LockAsync<Int32>(() => throw new InvalidOperationException("inside the lock")).AsTask();

		await cts.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
		release.SetResult();

		Assert.Equal(1, await holder);
		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => faulting);
		Assert.Equal("inside the lock", exception.Message);
		Assert.False(cancelledEntered, "the cancelled attempt must never enter the critical section");
		Assert.Equal(3, await asyncLock.LockAsync(() => new ValueTask<Int32>(3))); // the lock is free again
	}
}
