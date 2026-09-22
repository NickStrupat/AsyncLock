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

A type is recognised as an async lock when it implements `IAsyncLock` or `IAsyncLock<TReleaser>`,
which includes your own implementations. The library's `AsyncReaderWriterLock` and `SharedAsyncLock`
implement neither, so they are matched by name; that list lives in
`src/AsyncLock.Analyzers/AsyncLockTypes.cs` and needs extending when a similar type is added.
