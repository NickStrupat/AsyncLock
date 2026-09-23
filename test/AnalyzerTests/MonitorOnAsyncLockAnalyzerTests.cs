using NickStrupat.Analyzers;
using Xunit;

namespace AnalyzerTests;

public class MonitorOnAsyncLockAnalyzerTests
{
	private const String Usings = """
		using System;
		using System.Threading;
		using System.Threading.Tasks;
		using NickStrupat;

		""";

	private static Task VerifyAsync(String body, params String[] expectedIds) =>
		AnalyzerVerifier.VerifyAsync<MonitorOnAsyncLockAnalyzer>(Usings + body, expectedIds);

	[Fact]
	public Task AsyncLock_WhenLockStatementUsed_IsReported() => VerifyAsync("""
		class C
		{
			private readonly AsyncLock gate = new();
			public void M() { lock ([|gate|]) { } }
		}
		""", Rules.LockStatementId);

	[Fact]
	public Task AsyncLock6_WhenLockStatementUsed_IsReported() => VerifyAsync("""
		class C
		{
			private readonly AsyncLock6 gate = new();
			public void M() { lock ([|gate|]) { } }
		}
		""", Rules.LockStatementId);

	[Fact]
	public Task IAsyncLockInterface_WhenLockStatementUsed_IsReported() => VerifyAsync("""
		class C
		{
			public void M(IAsyncLock gate) { lock ([|gate|]) { } }
		}
		""", Rules.LockStatementId);

	[Fact]
	public Task AsyncSemaphoreSlimLock_WhenLockStatementUsed_IsReported() => VerifyAsync("""
		class C
		{
			private readonly AsyncSemaphoreSlimLock gate = new();
			public void M() { lock ([|gate|]) { } }
		}
		""", Rules.LockStatementId);

	/// <summary>A cast to Object hides the type from the reader, but not from the analyzer.</summary>
	[Fact]
	public Task AsyncLock_WhenLockedThroughAnObjectCast_IsReported() => VerifyAsync("""
		class C
		{
			private readonly AsyncLock gate = new();
			public void M() { lock ((Object)[|gate|]) { } }
		}
		""", Rules.LockStatementId);

	[Fact]
	public Task AsyncLock_WhenPropertyOrArrayElementIsLocked_IsReported() => VerifyAsync("""
		class C
		{
			private readonly AsyncLock[] gates = new AsyncLock[1];
			private AsyncLock Gate => gates[0];
			public void M()
			{
				lock ([|Gate|]) { }
				lock ([|gates[0]|]) { }
			}
		}
		""", Rules.LockStatementId, Rules.LockStatementId);

	[Theory]
	[InlineData("Monitor.Enter([|gate|]);")]
	[InlineData("Boolean taken = false; Monitor.Enter([|gate|], ref taken);")]
	[InlineData("Monitor.TryEnter([|gate|]);")]
	[InlineData("Monitor.TryEnter([|gate|], 10);")]
	[InlineData("Monitor.Exit([|gate|]);")]
	[InlineData("Monitor.Wait([|gate|]);")]
	[InlineData("Monitor.Pulse([|gate|]);")]
	[InlineData("Monitor.PulseAll([|gate|]);")]
	public Task AsyncLock_WhenPassedToASynchronisingMonitorMethod_IsReported(String statement) => VerifyAsync($$"""
		class C
		{
			private readonly AsyncLock gate = new();
			public void M() { {{statement}} }
		}
		""", Rules.MonitorMethodId);

	/// <summary>Named arguments must not let a Monitor call slip past the first-parameter lookup.</summary>
	[Fact]
	public Task AsyncLock_WhenPassedToMonitorByName_IsReported() => VerifyAsync("""
		class C
		{
			private readonly AsyncLock gate = new();
			public void M() { Monitor.TryEnter(millisecondsTimeout: 10, obj: [|gate|]); }
		}
		""", Rules.MonitorMethodId);

	/// <summary>Monitor.IsEntered only queries the monitor, so it is left alone for use in assertions.</summary>
	[Fact]
	public Task AsyncLock_WhenPassedToMonitorIsEntered_IsNotReported() => VerifyAsync("""
		class C
		{
			private readonly AsyncLock gate = new();
			public Boolean M() => Monitor.IsEntered(gate);
		}
		""");

	[Fact]
	public Task PlainObject_WhenLockedInATypeThatAlsoHoldsAnAsyncLock_IsNotReported() => VerifyAsync("""
		class C
		{
			private readonly AsyncLock gate = new();
			private readonly Object sync = new();
			public void M()
			{
				lock (sync) { }
				Monitor.Enter(sync);
				Monitor.Exit(sync);
			}
		}
		""");

	[Fact]
	public Task AsyncLock_WhenAwaitedCorrectly_IsNotReported() => VerifyAsync("""
		class C
		{
			private readonly AsyncLock gate = new();
			private readonly Object sync = new();
			private Int32 count;
			public async ValueTask M() => await gate.LockAsync(() =>
			{
				lock (sync) { count++; }
				return ValueTask.CompletedTask;
			});
		}
		""");
}
