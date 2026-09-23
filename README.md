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

Time and managed allocation per acquisition, compared with `SemaphoreSlim` and popular community async locks:

| Lock | Uncontended | Contended | Contended, awaiting inside |
|---|---:|---:|---:|
| **`AsyncLock`** | **16.2 ns** · 0 B | 910 ns · **1 B** | 2.02 µs · **15 B** |
| `SemaphoreSlim` (`WaitAsync`/`Release`) | 31.1 ns · 0 B | 905 ns · 232 B | 1.97 µs · 242 B |
| DotNext.Threading `AsyncExclusiveLock` | 32.4 ns · 0 B | 1,196 ns · 128 B | 2.25 µs · 138 B |
| Microsoft.VisualStudio.Threading `AsyncSemaphore` | 33.5 ns · 0 B | 1,264 ns · 320 B | 2.11 µs · 330 B |
| Nito.AsyncEx `AsyncLock` | 75.2 ns · 320 B | 1,269 ns · 560 B | 2.15 µs · 572 B |

- **Uncontended**: one caller acquiring and releasing in a loop.
- **Contended**: `Parallel.ForAsync` hammering one lock around an increment that completes synchronously.
- **Contended, awaiting inside**: the same, with an `await Task.Yield()` while the lock is held. The yield's thread-pool
  round trip dominates, so the times all fall within the measurement error of each other (about ±0.3 µs);
  allocation still differs.

Each figure is the mean of 3 processes × 10 iterations. Uncontended times are within ±3 ns; contended times vary by
about ±10%, so contended differences under that are noise. Measured with BenchmarkDotNet 0.15.6 on .NET 10.0.12,
Apple M1 Max, macOS 27, against DotNext.Threading 6.8.0, Microsoft.VisualStudio.Threading 18.7.23 and
Nito.AsyncEx.Coordination 5.1.2. Each community lock is called through its own acquire-and-release API, the way a
caller would use it (`test/BenchmarkSuite/ThirdParty`).

To reproduce (about 12 minutes):

```sh
dotnet run -c Release --project test/BenchmarkSuite
```
