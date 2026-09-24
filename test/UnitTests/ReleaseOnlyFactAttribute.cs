using System.Diagnostics;
using System.Reflection;
using NickStrupat;
using Xunit;

namespace UnitTests;

/// <summary>
/// A fact that is skipped when <see cref="AsyncLock"/> was built without optimization. For allocation assertions:
/// in a Debug build the compiler emits async state machines as classes, so every async call allocates one.
/// </summary>
public sealed class ReleaseOnlyFactAttribute : FactAttribute
{
	public ReleaseOnlyFactAttribute()
	{
		var debuggable = typeof(AsyncLock).Assembly.GetCustomAttribute<DebuggableAttribute>();
		if (debuggable?.IsJITOptimizerDisabled == true)
			Skip = "Allocation is only meaningful in an optimized build; run the tests with -c Release.";
	}
}
