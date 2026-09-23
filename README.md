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

## Performance

Time and managed allocation per acquisition, compared with `SemaphoreSlim` and popular community async locks.

With `CancellationToken.None`:

| Lock | Uncontended | Contended | Contended, awaiting inside |
|---|---:|---:|---:|
| **`AsyncLock`** | **14.5 ns** · 0 B | 957 ns · **5 B** | 2.11 µs · **16 B** |
| `SemaphoreSlim` (`WaitAsync`/`Release`) | 31.2 ns · 0 B | 1,020 ns · 232 B | 1.93 µs · 242 B |
| DotNext.Threading `AsyncExclusiveLock` | 32.3 ns · 0 B | 1,251 ns · 128 B | 2.30 µs · 137 B |
| Microsoft.VisualStudio.Threading `AsyncSemaphore` | 33.1 ns · 0 B | 1,288 ns · 320 B | 2.12 µs · 329 B |
| Nito.AsyncEx `AsyncLock` | 77.8 ns · 320 B | 1,232 ns · 560 B | 2.30 µs · 570 B |

With a cancellable token (from a `CancellationTokenSource` that is never cancelled), as a caller passing a request's
abort token or a timeout would:

| Lock | Uncontended | Contended | Contended, awaiting inside |
|---|---:|---:|---:|
| **`AsyncLock`** | **14.6 ns** · 0 B | 1,251 ns · **1 B** | 1.91 µs · **8 B** |
| `SemaphoreSlim` (`WaitAsync`/`Release`) | 31.3 ns · 0 B | 1,644 ns · 520 B | 2.42 µs · 543 B |
| DotNext.Threading `AsyncExclusiveLock` | 34.0 ns · 0 B | 1,351 ns · 128 B | 2.39 µs · 138 B |
| Microsoft.VisualStudio.Threading `AsyncSemaphore` | 46.7 ns¹ · 0 B | 1,654 ns · 320 B | 3.04 µs · 333 B |
| Nito.AsyncEx `AsyncLock` | 78.9 ns · 320 B | 1,811 ns · 888 B | 2.84 µs · 898 B |

¹ A noisy measurement (±18 ns; median 34.8 ns), in line with the `None` figure.

- **Uncontended**: one caller acquiring and releasing in a loop. A free lock never registers with the token, so the
  token makes no difference here.
- **Contended**: `Parallel.ForAsync` hammering one lock around an increment that completes synchronously. A
  cancellable token makes each waiter register with it, which slows most locks by roughly 30–60% (DotNext barely
  changes) and adds allocation to `SemaphoreSlim` and Nito; `AsyncLock` stays allocation-free.
- **Contended, awaiting inside**: the same, with an `await Task.Yield()` while the lock is held. The yield's
  thread-pool round trip dominates, so the times fall within the measurement error of each other (±0.3–0.7 µs);
  allocation still differs.

Each figure is the mean of 3 processes × 10 iterations, all from one run. Uncontended times are within ±4 ns except
where marked; contended times vary by ±4–15%, so contended differences smaller than that are noise. Measured with
BenchmarkDotNet 0.15.6 on .NET 10.0.12, Apple M1 Max, macOS 27, against DotNext.Threading 6.8.0,
Microsoft.VisualStudio.Threading 18.7.23 and Nito.AsyncEx.Coordination 5.1.2. Each community lock is called through
its own acquire-and-release API, the way a caller would use it (`test/BenchmarkSuite/ThirdParty`).

To reproduce (about 50 minutes):

```sh
dotnet run -c Release --project test/BenchmarkSuite
```
