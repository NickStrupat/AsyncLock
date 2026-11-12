using NickStrupat;
using Xunit;

namespace UnitTests;

// Measures the whole process's live memory, so nothing else may be running.
[Collection(ProcessWideMemoryCollection.Name)]
public class AsyncLockMemoryTests
{
	[Fact]
	public async Task CancelledWaitersKeepOnlyTheirQueueNodeUntilTheHolderReleases()
	{
		// A cancelled waiter's node stays queued until the turn reaches it, which is not before the holder releases.
		// Meanwhile it must hold nothing else: in particular not the waiter's async state machine, which is larger
		// than the node. All the waiters wait at once, so their state machines cannot be pooled and reused.
		const Int32 cancelledWaiters = 20_000;
		const Int32 maxBytesPerCancelledWaiter = 200; // a node is about 130 bytes; with its state machine, about 340
		var asyncLock = new AsyncLock();

		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var holder = asyncLock.LockAsync(async () =>
		{
			held.SetResult();
			await release.Task;
		}).AsTask();
		await held.Task;

		var before = LiveBytes();
		await QueueAndCancelAsync(asyncLock, cancelledWaiters);
		var bytesPerCancelledWaiter = (LiveBytes() - before) / cancelledWaiters;

		Assert.InRange(bytesPerCancelledWaiter, 1, maxBytesPerCancelledWaiter);

		release.SetResult();
		await holder;
		await asyncLock.LockAsync(() => ValueTask.CompletedTask); // the turn has passed through every cancelled node

		var bytesPerWaiterAfterRelease = (LiveBytes() - before) / cancelledWaiters;
		Assert.InRange(bytesPerWaiterAfterRelease, Int64.MinValue, 10);
	}

	// A separate method so that nothing it creates is still referenced from the caller's locals when it returns.
	private static async Task QueueAndCancelAsync(AsyncLock asyncLock, Int32 count)
	{
		using var cts = new CancellationTokenSource();
		var waiters = new Task[count];
		for (var i = 0; i < waiters.Length; i++)
			waiters[i] = asyncLock.LockAsync(() => ValueTask.CompletedTask, cts.Token).AsTask();

		await cts.CancelAsync();
		foreach (var waiter in waiters)
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
	}

	/// <summary>The size of the managed heap after a full, blocking, compacting collection.</summary>
	private static Int64 LiveBytes()
	{
		const Int32 passes = 2; // the second pass collects what the first pass's finalizers released
		for (var i = 0; i < passes; i++)
		{
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
			GC.WaitForPendingFinalizers();
		}

		return GC.GetGCMemoryInfo(GCKind.FullBlocking).GenerationInfo.ToArray().Sum(x => x.SizeAfterBytes);
	}
}
