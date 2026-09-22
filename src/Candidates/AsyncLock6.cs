using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace NickStrupat;

public sealed class AsyncLock6 : IAsyncLock
{
	private const Int32 MaxRetainedInPool = 32;

	private readonly ObjectPool<Node, AsyncLock6> nodePool;
	private Node tail;

	public AsyncLock6()
	{
		nodePool = new(MaxRetainedInPool, static self => new Node(self), this);
		tail = nodePool.Rent();
		tail.Complete(); // the sentinel starts completed so the first waiter proceeds immediately
		tail.Finish();   // stand in for the sentinel's absent owner so the first waiter recycles it
	}

	public ValueTask LockAsync(Func<ValueTask> whenLocked, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(whenLocked);
		if (cancellationToken.IsCancellationRequested)
			return ValueTask.FromCanceled(cancellationToken);

		// Append ourselves to the queue: publish our node as the new tail and take the previous tail
		// as the node whose completion grants us the lock.
		var next = nodePool.Rent();
		var prev = Interlocked.Exchange(ref this.tail, next);

		// The uncontended/non-cancellable path can simply await prev's grant signal. Only a real token
		// needs the latch half of our own node.
		return cancellationToken.CanBeCanceled
			? LockSlowAsync(prev, next, whenLocked, cancellationToken)
			: LockFastAsync(prev, next, whenLocked);
	}

	[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
	private async ValueTask LockFastAsync(Node prev, Node next, Func<ValueTask> whenLocked)
	{
		try
		{
			await prev.GrantTask.ConfigureAwait(false);
			await whenLocked().ConfigureAwait(false);
		}
		finally
		{
			next.Complete(); // hand the lock to whoever queued behind us
			next.Finish();
			prev.Finish();
		}
	}

	[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
	private async ValueTask LockSlowAsync(
		Node prev, Node next, Func<ValueTask> whenLocked, CancellationToken cancellationToken)
	{
		// Our own node doubles as the cancellable wait latch, so a cancellable acquisition needs no
		// per-call state object at all. Crucially the latch never completes prev: it only *observes*
		// prev's completion. That keeps prev owned by the predecessor, so cancelling our wait cannot
		// destroy the signal our successor depends on.
		prev.RegisterContinuation(static o => ((Node)o!).OnPrevCompleted(), next);
		var ctr = cancellationToken.UnsafeRegister(static o => ((Node)o!).OnCancel(), next);
		var granted = false;
		try
		{
			granted = await next.WaitTask.ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			await whenLocked().ConfigureAwait(false);
		}
		finally
		{
			// Disarm first. Finish can put next straight back in the pool, and a callback still armed
			// on a re-rented node would complete a different caller's latch — handing it a lock it was
			// never granted.
			await ctr.DisposeAsync().ConfigureAwait(false);

			// Hand the lock on only if we actually acquired it. If we cancelled, prev has not completed
			// yet, so the turn is not ours to give; the bridge (Node.OnPrevCompleted) completes next
			// once prev genuinely completes, and the successor keeps waiting for the real holder.
			if (granted)
				next.Complete();

			// Either way we are now done touching next: latch consumed, callback disarmed. Doing this
			// any earlier would let next be recycled out from under our own await of WaitTask.
			next.Finish();
			prev.Finish();
		}
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
	/// consumed by the caller queued behind it — awaited directly on the fast path, or observed through
	/// <see cref="RegisterContinuation"/> on the cancellable path.
	/// </para>
	/// <para>
	/// As a <b>wait latch</b> (<see cref="WaitTask"/>) it parks its own caller during a cancellable
	/// acquisition and arbitrates the race between being granted the lock (the predecessor completed
	/// prev) and being cancelled. On cancellation it bridges, completing the grant signal only once prev
	/// has actually completed. Keeping the latch here rather than in a per-call object is what makes the
	/// cancellable path allocation-free: the latch is pooled along with the node.
	/// </para>
	/// </summary>
	private sealed class Node(AsyncLock6 owner) : IValueTaskSource, IValueTaskSource<Boolean>
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

		/// <summary>Completes when this node's owner releases the lock. Consumed by the successor.</summary>
		public ValueTask GrantTask => new(this, grantCore.Version);

		/// <summary>
		/// Completes when this node's owner is either granted the lock (<c>true</c>) or cancelled
		/// (<c>false</c>). Consumed by the owner itself, on the cancellable path only.
		/// </summary>
		public ValueTask<Boolean> WaitTask => new(this, waitCore.Version);

		/// <summary>Invoked when prev completes (the predecessor released or bridged its turn to us).</summary>
		public void OnPrevCompleted()
		{
			if (Interlocked.CompareExchange(ref waitState, Granted, Waiting) == Waiting)
				waitCore.SetResult(true); // we win the lock; the waiter's finally completes our node
			else
				Complete();               // we had already cancelled; the turn is real now, so pass it on
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
			if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
				return;
			grantCore.SetResult(0);
			Finish();
		}

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
				owner.ReturnToPool(this);
		}

		/// <summary>
		/// Registers a one-shot continuation that runs when the grant signal completes, without consuming
		/// it through an await. Used by the cancellable path to observe prev's completion (grant or
		/// bridge) while leaving prev owned by its predecessor.
		/// </summary>
		public void RegisterContinuation(Action<Object?> continuation, Object? state)
			=> grantCore.OnCompleted(continuation, state, grantCore.Version, ValueTaskSourceOnCompletedFlags.None);

		public void Reset()
		{
			grantCore.Reset();
			waitCore.Reset();
			Volatile.Write(ref completed, 0);
			Volatile.Write(ref finishers, 0);
			Volatile.Write(ref waitState, Waiting);
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
