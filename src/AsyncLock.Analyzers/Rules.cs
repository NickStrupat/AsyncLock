using System;
using Microsoft.CodeAnalysis;

namespace NickStrupat.Analyzers;

/// <summary>The diagnostics reported by <see cref="MonitorOnAsyncLockAnalyzer"/>.</summary>
public static class Rules
{
	/// <summary>The id of the diagnostic reported for a <see langword="lock"/> statement on an async lock.</summary>
	public const String LockStatementId = "NSAL0001";

	/// <summary>The id of the diagnostic reported for a <c>Monitor</c> call on an async lock.</summary>
	public const String MonitorMethodId = "NSAL0002";

	private const String Category = "Usage";
	private const String HelpLinkPrefix = "https://github.com/NickStrupat/AsyncLock#";

	internal static readonly DiagnosticDescriptor LockStatement = new(
		LockStatementId,
		"Do not use the 'lock' statement on an async lock",
		"'{0}' is an async lock; 'lock' enters an unrelated monitor on it instead of acquiring the lock. Use 'await ...LockAsync(...)'.",
		Category,
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description:
			"The 'lock' statement enters the object's monitor, which is entirely separate from the mutual " +
			"exclusion an async lock implements. Threads blocking on the monitor are not serialised against " +
			"callers awaiting the async lock, so the resource the lock is meant to protect is left unguarded. " +
			"Await the type's LockAsync method instead.",
		helpLinkUri: HelpLinkPrefix + LockStatementId.ToLowerInvariant());

	internal static readonly DiagnosticDescriptor MonitorMethod = new(
		MonitorMethodId,
		"Do not call 'Monitor' methods on an async lock",
		"'{0}' is an async lock; 'Monitor.{1}' operates on an unrelated monitor on it instead of acquiring the lock. Use 'await ...LockAsync(...)'.",
		Category,
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description:
			"The Monitor class operates on the object's monitor, which is entirely separate from the mutual " +
			"exclusion an async lock implements. Threads blocking on the monitor are not serialised against " +
			"callers awaiting the async lock, so the resource the lock is meant to protect is left unguarded. " +
			"Await the type's LockAsync method instead.",
		helpLinkUri: HelpLinkPrefix + MonitorMethodId.ToLowerInvariant());
}
