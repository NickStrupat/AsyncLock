# AsyncLock
Locks for expressing mutual exclusion in async code

## Installation

## Analyzers

The package ships a Roslyn analyzer that catches monitor-based synchronisation applied to an async
lock. Both forms compile — an async lock is an ordinary reference type — and both silently take the
object's monitor rather than the lock the type implements, leaving the resource unguarded against
callers that correctly `await`.

The rules are reported as errors. To relax one, set its severity in an `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.NSAL0001.severity = warning
```

### NSAL0001

Do not use the `lock` statement on an async lock.

```csharp
private readonly AsyncLock gate = new();

lock (gate) { /* NSAL0001: takes gate's monitor, not the async lock */ }

await gate.LockAsync(async () => { /* correct */ });
```

### NSAL0002

Do not call `Monitor` methods (`Enter`, `TryEnter`, `Exit`, `Wait`, `Pulse`, `PulseAll`) on an async
lock. `Monitor.IsEntered` is not reported, since it only queries the monitor and is useful in
assertions.

```csharp
Monitor.Enter(gate); // NSAL0002
```

### Coverage

The rules apply to `AsyncLock`.

## Why it's fast

- **No internal lock.** `SemaphoreSlim`, DotNext, VS.Threading and Nito each guard their wait queue with an internal
  lock taken on every acquire and every release. `AsyncLock` takes none: while it is free it is a single field,
  acquired with one compare-and-swap and released with another, with no queue node at all. That is the main
  structural difference behind it being about twice as fast uncontended.
- **Nothing to allocate per wait.** A waiter borrows a node from a small pool, and the node is itself the thing
  awaited (an `IValueTaskSource` over a `ManualResetValueTaskSourceCore`), reset and reused afterwards, so waiting
  creates no `Task` or `TaskCompletionSource`. `SemaphoreSlim`, VS.Threading and Nito allocate a task or waiter object
  for every wait that has to queue, and Nito allocates even when the lock is free.
- **The wait happens inside the lock.** `LockAsync` takes the critical section as a delegate, so the caller's own
  method never suspends waiting for the lock; `AsyncLock`'s pooled async method does the waiting. With the
  `using (await lock.LockAsync())` pattern the other locks use, the caller's async method suspends instead and
  allocates its state machine, about 110 B. That accounts for all of DotNext's allocation: its own wait nodes are
  pooled too.

Under contention the difference in speed is small: every one of these locks resumes the next waiter through the
thread pool, and that hop dominates.

## Performance

Time and managed allocation per acquisition, compared with `SemaphoreSlim` and popular community async locks.

With `CancellationToken.None`:

| Lock | Uncontended | Contended | Contended, awaiting inside |
|---|---:|---:|---:|
| **`AsyncLock`** | **14.5 ns** · 0 B | 1,049 ns · **5 B** | 2.40 µs · **17 B** |
| `SemaphoreSlim` (`WaitAsync`/`Release`) | 31.3 ns · 0 B | 1,147 ns · 232 B | 2.16 µs · 244 B |
| DotNext.Threading `AsyncExclusiveLock` | 33.7 ns · 0 B | 1,354 ns · 128 B | 1.11 µs¹ · 128 B |
| Microsoft.VisualStudio.Threading `AsyncSemaphore` | 33.5 ns · 0 B | 1,366 ns · 320 B | 1.88 µs · 331 B |
| Nito.AsyncEx `AsyncLock` | 76.9 ns · 320 B | 1,141 ns · 560 B | 1.94 µs · 570 B |

With a cancellable token (from a `CancellationTokenSource` that is never cancelled), as a caller passing a request's
abort token or a timeout would:

| Lock | Uncontended | Contended | Contended, awaiting inside |
|---|---:|---:|---:|
| **`AsyncLock`** | **14.4 ns** · 0 B | 1,327 ns · **1 B** | 2.30 µs · **14 B** |
| `SemaphoreSlim` (`WaitAsync`/`Release`) | 31.4 ns · 0 B | 1,617 ns · 519 B | 2.05 µs · 538 B |
| DotNext.Threading `AsyncExclusiveLock` | 34.2 ns · 0 B | 1,455 ns · 128 B | 2.68 µs · 138 B |
| Microsoft.VisualStudio.Threading `AsyncSemaphore` | 35.0 ns · 0 B | 1,491 ns · 320 B | 2.63 µs · 334 B |
| Nito.AsyncEx `AsyncLock` | 73.3 ns · 320 B | 1,769 ns · 888 B | 2.83 µs · 899 B |

¹ Bimodal: in each process, iterations fell from about 3 µs to between 0.35 and 1.1 µs (median 0.73 µs), and a
re-run did the same (median 0.92 µs). Earlier runs stayed near 2.3 µs throughout, so treat it as unstable rather than
as a speed.

- **Uncontended**: one caller acquiring and releasing in a loop. A free lock never registers with the token, so the
  token makes no difference here.
- **Contended**: `Parallel.ForAsync` hammering one lock around an increment that completes synchronously. A
  cancellable token makes each waiter register with it, which slows `AsyncLock`, `SemaphoreSlim` and Nito by roughly
  25–55% and DotNext and VS.Threading by under 10%, and adds allocation to `SemaphoreSlim` and Nito; `AsyncLock`
  stays allocation-free.
- **Contended, awaiting inside**: the same, with an `await Task.Yield()` while the lock is held. The yield's
  thread-pool round trip dominates, so the times fall within the measurement error of each other (±0.3–0.5 µs);
  allocation still differs.

With waiters cancelled while queued, as when timeouts fire under load. Each round holds the lock, queues 8 waiters,
cancels every other one, then releases; figures are per waiter:

| Lock | Contended, half the waiters cancelled |
|---|---:|
| **`AsyncLock`** | 5.25 µs · **707 B** |
| `SemaphoreSlim` (`WaitAsync`/`Release`) | 8.25 µs · 1.41 KB |
| DotNext.Threading `AsyncExclusiveLock` | 4.96 µs · 821 B |
| Microsoft.VisualStudio.Threading `AsyncSemaphore` | 4.77 µs · 1,000 B |
| Nito.AsyncEx `AsyncLock` | 5.33 µs · 1.45 KB |

Cancellation dominates this scenario: every cancelled wait throws `OperationCanceledException` (about 200 B per throw,
usually more than one throw per wait) and registers with a token that cannot reuse its registrations (about 100 B).
`AsyncLock`, DotNext, VS.Threading and Nito are level on time (±0.4–0.7 µs), `SemaphoreSlim` is about 60% slower,
and `AsyncLock` allocates the least.

Each figure is the mean of 3 processes × 10 iterations, all from one run. Uncontended times are within ±3 ns;
contended times vary by ±2–10%, so contended differences smaller than that are noise. Measured with
BenchmarkDotNet 0.15.6 on .NET 10.0.12, Apple M1 Max, macOS 27, with an IDE running in the background, against
DotNext.Threading 6.8.0, Microsoft.VisualStudio.Threading 18.7.23 and Nito.AsyncEx.Coordination 5.1.2. Each
community lock is called through its own acquire-and-release API, the way a caller would use it
(`test/BenchmarkSuite/ThirdParty`).

To reproduce (about 20 minutes):

```sh
dotnet run -c Release --project test/BenchmarkSuite
```
