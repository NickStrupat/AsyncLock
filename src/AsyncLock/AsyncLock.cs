using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace NickStrupat;

/// <summary>
/// A thread-safe, FIFO async lock that does not allocate. It does not support recursion/reentrancy.
/// </summary>
/// <remarks>
/// A hybrid: uncontended, it is a lock word — one CAS to acquire and one to release, with no queue node at
/// all. Only under contention does it fall back to a queue of pooled nodes, handing the lock to each waiter in arrival order, and it returns to the lock word as soon as
/// the queue drains. Cancelled waiters stay in the queue and pass the turn on when it reaches them, so a
/// cancellation never admits anyone early or strands anyone behind it.
/// </remarks>
public sealed class AsyncLock : IAsyncLock
{
	private const Int32 MaxRetainedInPool = 32;

	// tail:  null  - free
	//        Held  - held by a nodeless owner, nobody queued
	//        node  - the queue's tail; that node's owner holds the lock or is waiting for it
	// handoff is the rendezvous between a nodeless owner and the one waiter that queues directly behind
	// it (which necessarily got Held from its exchange, since that owner has no node to wait on):
	//        null     - nothing pending
	//        Released - the nodeless owner released first; the waiter that finds this owns the lock
	//        node     - the waiter published first; the nodeless owner grants that node on release
	// The sentinels are never used as real nodes (and have no owner), so they are shared by every instance.
	private static readonly Node Held = new(null);
	private static readonly Node Released = new(null);

	private readonly ObjectPool<Node, AsyncLock> nodePool;
	private Node? tail;
	private Node? handoff;

	/// <summary>Creates an unlocked lock.</summary>
	public AsyncLock() => nodePool = new(MaxRetainedInPool, static self => new Node(self), this);

	/// <summary>
	/// Asynchronously waits for the lock to be acquired.
	/// When the lock is acquired, the supplied delegate is executed, then the lock is released.
	/// Any exceptions thrown by the delegate are propagated to the caller.
	/// </summary>
	/// <param name="whenLocked">The delegate to execute when the lock is acquired.</param>
	/// <param name="cancellationToken">
	/// The cancellation token that can be used to cancel waiting for the lock. It is only observed while
	/// waiting: an acquisition that finds the lock free takes it without consulting the token.
	/// </param>
	/// <returns>A task that completes once the delegate has run and the lock has been released.</returns>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="whenLocked"/> is <see langword="null"/>.</exception>
	/// <exception cref="OperationCanceledException">Thrown when the wait for the lock is cancelled.</exception>
	public ValueTask LockAsync(Func<ValueTask> whenLocked, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);
		if (cancellationToken.IsCancellationRequested)
			return ValueTask.FromCanceled(cancellationToken);

		// Uncontended: take the lock word. A free lock needs no waiting, so a token is irrelevant here.
		return Interlocked.CompareExchange(ref tail, Held, null) is null
			? RunNodelessAsync(whenLocked)
			: LockQueuedAsync(whenLocked, cancellationToken);
	}

	[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
	private async ValueTask RunNodelessAsync(Func<ValueTask> whenLocked)
	{
		try
		{
			await whenLocked().ConfigureAwait(false);
		}
		finally
		{
			ReleaseNodeless();
		}
	}

	private void ReleaseNodeless()
	{
		// Nobody queued: hand the lock word back and we are done.
		if (Interlocked.CompareExchange(ref tail, null, Held) == Held)
			return;

		// Someone queued behind us, and we have no node for them to wait on, so meet them in handoff.
		// Whoever arrives second completes the handover: if they have not published yet, we leave
		// Released and they take the lock themselves; otherwise we grant the node they left.
		var waiter = Interlocked.CompareExchange(ref handoff, Released, null);
		if (waiter is null)
			return;
		Volatile.Write(ref handoff, null); // clear before granting: once granted, a new generation may begin
		waiter.OnPrevCompleted();
	}

	[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
	private async ValueTask LockQueuedAsync(Func<ValueTask> whenLocked, CancellationToken cancellationToken)
	{
		var next = nodePool.Rent();
		var prev = Interlocked.Exchange(ref tail, next);

		// prev is null if the lock was freed between our failed CAS and the exchange: we hold it.
		Boolean mustWait;
		if (prev == Held)
		{
			mustWait = Interlocked.CompareExchange(ref handoff, next, null) is null;
			if (!mustWait)
				Volatile.Write(ref handoff, null); // the nodeless owner already released: the lock is ours
		}
		else
			mustWait = prev is not null && !prev.IsCompleted;

		var ctr = default(CancellationTokenRegistration);
		var granted = true;
		try
		{
			if (mustWait)
			{
				if (cancellationToken.CanBeCanceled)
				{
					// Park on our own latch, to be granted inline by whoever releases ahead of us: prev's
					// owner through the bridge, or the nodeless owner through handoff. If prev released before
					// we could bridge to it, the lock is already ours and there is nothing to wait for.
					if (prev == Held || prev!.TryBridge(next))
					{
						ctr = cancellationToken.UnsafeRegister(static o => ((Node)o!).OnCancel(), next);
						granted = await next.WaitTask.ConfigureAwait(false);
						cancellationToken.ThrowIfCancellationRequested();
					}
				}
				else if (prev == Held)
					await next.WaitTask.ConfigureAwait(false);
				else
					await prev!.GrantTask.ConfigureAwait(false);
			}
			await whenLocked().ConfigureAwait(false);
		}
		finally
		{
			// Disarm first: everything below can recycle next.
			await ctr.DisposeAsync().ConfigureAwait(false);

			if (granted)
				ReleaseQueued(next);
			else
				next.Finish(); // cancelled: the bridge completes next once our turn genuinely arrives

			if (prev is not null && prev != Held)
				prev.Finish();
		}
	}

	private void ReleaseQueued(Node node)
	{
		// Nobody queued behind us: free the lock outright (back to the lock-word state, so the next
		// acquirer takes the nodeless path). Nobody ever saw node, so it skips the rendezvous.
		if (Volatile.Read(ref tail) == node && Interlocked.CompareExchange(ref tail, null, node) == node)
		{
			ReturnToPool(node);
			return;
		}
		node.Complete(); // hand the lock to whoever queued behind us
		node.Finish();
	}

	private void ReturnToPool(Node node)
	{
		node.Reset();
		nodePool.Return(node);
	}

	/// <summary>
	/// A queue node, playing two roles for the one caller that rents it.
	/// <para>
	/// As a <b>grant signal</b> (<see cref="GrantTask"/>) it is completed by that caller on release and
	/// consumed by the caller queued behind it — awaited directly on the fast path, or, on the
	/// cancellable path, by bridging to it (<see cref="TryBridge"/>) so the release grants inline.
	/// </para>
	/// <para>
	/// As a <b>wait latch</b> (<see cref="WaitTask"/>) it parks its own caller during a cancellable
	/// acquisition and arbitrates the race between being granted the lock (the predecessor completed
	/// prev) and being cancelled. On cancellation it bridges, completing the grant signal only once prev
	/// has actually completed. Keeping the latch here rather than in a per-call object is what makes the
	/// cancellable path allocation-free: the latch is pooled along with the node.
	/// </para>
	/// </summary>
	private sealed class Node(AsyncLock? owner) : IValueTaskSource, IValueTaskSource<Boolean>
	{
		private const Int32 Waiting = 0, Granted = 1, Cancelled = 2;

		/// <summary>See <see cref="Finish"/> for the three parties being counted.</summary>
		private const Int32 Finishers = 3;

		// Grant side: "the caller that owns this node has released it".
		private ManualResetValueTaskSourceCore<Byte> grantCore = new() { RunContinuationsAsynchronously = true };
		private Int32 completed; // 0 = pending, 1 = signalled; guards SetResult to exactly one caller
		private Int32 finishers;

		// Wait side: this node's own caller, parked on a cancellable acquisition.
		private ManualResetValueTaskSourceCore<Boolean> waitCore = new() { RunContinuationsAsynchronously = true };
		private Int32 waitState;

		// The cancellable successor to grant inline when this node completes: null until one bridges,
		// Fired once this node has completed (a later TryBridge then fails, and the caller holds the lock).
		private static readonly Node Fired = new(null);
		private Node? bridge;

		/// <summary>Completes when this node's owner releases the lock. Consumed by the successor.</summary>
		public ValueTask GrantTask => new(this, grantCore.Version);

		/// <summary>True once the grant signal has been completed.</summary>
		public Boolean IsCompleted => Volatile.Read(ref completed) != 0;

		/// <summary>
		/// Completes when this node's owner is either granted the lock (<c>true</c>) or cancelled
		/// (<c>false</c>). Consumed by the owner itself, on the cancellable path only.
		/// </summary>
		public ValueTask<Boolean> WaitTask => new(this, waitCore.Version);

		/// <summary>
		/// Invoked on the releasing thread when prev completes (the predecessor released or bridged its
		/// turn to us). Granting inline, rather than from a pool-scheduled continuation, leaves the waiter's
		/// own resumption as the only thread-pool hop.
		/// </summary>
		public void OnPrevCompleted()
		{
			// A loop, not recursion: a run of already-cancelled waiters passes the turn along one node per
			// iteration, so however many cancelled at once, the releaser's stack stays flat.
			for (var node = this; node is not null;)
				node = node.Grant();
		}

		/// <summary>
		/// Hands the turn to this node: wakes its waiter, or, if that waiter has already cancelled,
		/// completes this node in its place and returns the successor bridged to it (if any) to be granted
		/// next.
		/// </summary>
		private Node? Grant()
		{
			if (Interlocked.CompareExchange(ref waitState, Granted, Waiting) == Waiting)
			{
				waitCore.SetResult(true); // the waiter wins the lock; its finally completes this node
				return null;
			}
			return CompleteCore(); // cancelled: the turn is real now, so pass it on
		}

		/// <summary>Invoked when the wait is cancelled, abandoning it without ever completing prev.</summary>
		public void OnCancel()
		{
			if (Interlocked.CompareExchange(ref waitState, Cancelled, Waiting) == Waiting)
				waitCore.SetResult(false); // resume the waiter so it observes the cancellation
		}

		/// <summary>
		/// Idempotently completes the grant signal, releasing the successor. Called by this node's owner
		/// when it releases the lock, or by its bridge when that owner cancelled; only the first caller
		/// signals, and only that caller counts as the completion finisher.
		/// </summary>
		public void Complete()
		{
			for (var node = CompleteCore(); node is not null;)
				node = node.Grant();
		}

		/// <summary>
		/// Completes the grant signal (first caller only) and returns the successor bridged to this node,
		/// for the caller to grant. The bridge is read before <see cref="Finish"/>, which may recycle this
		/// node; after it, this node is not touched again.
		/// </summary>
		private Node? CompleteCore()
		{
			if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
				return null;
			grantCore.SetResult(0); // for a successor awaiting GrantTask directly (the non-cancellable path)
			var successor = Interlocked.Exchange(ref bridge, Fired);
			Finish();
			return successor;
		}

		/// <summary>
		/// Asks for <paramref name="successor"/> to be granted inline when this node completes. Returns
		/// <c>false</c> if it already has, in which case the caller holds the lock now.
		/// </summary>
		public Boolean TryBridge(Node successor)
			=> Interlocked.CompareExchange(ref bridge, successor, null) is null;

		/// <summary>
		/// Three-party rendezvous, deliberately decoupled from <see cref="Complete"/>. The parties are
		/// this node's <i>completion</i>, its <i>owner</i> (once it has consumed <see cref="WaitTask"/>
		/// and disarmed its cancellation callback) and its <i>successor</i> (once done with
		/// <see cref="GrantTask"/>). The last one in recycles the node, so it is never reused while
		/// either core may still be touched — and never before it has been completed at all, which a
		/// successor that cancels rather than waits would otherwise allow.
		/// </summary>
		public void Finish()
		{
			if (Interlocked.Increment(ref finishers) == Finishers)
				owner!.ReturnToPool(this); // only sentinels lack an owner, and they are never finished
		}

		public void Reset()
		{
			grantCore.Reset();
			waitCore.Reset();
			Volatile.Write(ref completed, 0);
			Volatile.Write(ref finishers, 0);
			Volatile.Write(ref waitState, Waiting);
			Volatile.Write(ref bridge, null);
		}

		void IValueTaskSource.GetResult(Int16 token) => grantCore.GetResult(token);
		ValueTaskSourceStatus IValueTaskSource.GetStatus(Int16 token) => grantCore.GetStatus(token);

		void IValueTaskSource.OnCompleted(Action<Object?> continuation, Object? state, Int16 token,
			ValueTaskSourceOnCompletedFlags flags) => grantCore.OnCompleted(continuation, state, token, flags);

		Boolean IValueTaskSource<Boolean>.GetResult(Int16 token) => waitCore.GetResult(token);
		ValueTaskSourceStatus IValueTaskSource<Boolean>.GetStatus(Int16 token) => waitCore.GetStatus(token);

		void IValueTaskSource<Boolean>.OnCompleted(Action<Object?> continuation, Object? state, Int16 token,
			ValueTaskSourceOnCompletedFlags flags) => waitCore.OnCompleted(continuation, state, token, flags);
	}
}
