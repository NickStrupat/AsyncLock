using Xunit;

namespace UnitTests;

/// <summary>
/// For test classes that measure the whole process's memory, which is only meaningful while nothing else is running.
/// Its classes run one at a time, after every parallel test has finished.
/// </summary>
/// <remarks>
/// Without it, such a test and an allocation-heavy one stall each other: the first forces full collections until
/// memory stops changing, which it cannot while the second runs, and the second crawls under those collections.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideMemoryCollection
{
	/// <summary>The collection's name, for <see cref="CollectionAttribute"/>.</summary>
	public const String Name = "Process-wide memory";
}
