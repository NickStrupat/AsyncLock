using System;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace NickStrupat.Analyzers;

/// <summary>
/// Reports monitor-based synchronisation applied to an async lock: the <see langword="lock"/>
/// statement (<see cref="Rules.LockStatementId"/>) and the <see cref="System.Threading.Monitor"/>
/// methods (<see cref="Rules.MonitorMethodId"/>).
/// </summary>
/// <remarks>
/// Both compile cleanly because an async lock is an ordinary reference type, and both silently do
/// something other than what the author intended: they take the object's monitor rather than the
/// lock the type implements.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MonitorOnAsyncLockAnalyzer : DiagnosticAnalyzer
{
	/// <summary>
	/// The <c>Monitor</c> members that synchronise on their first argument. <c>IsEntered</c> is
	/// deliberately excluded; it is a query, and is legitimate in assertions and debug checks.
	/// </summary>
	private static readonly ImmutableHashSet<String> MonitorMethodNames = ImmutableHashSet.Create(
		StringComparer.Ordinal,
		[
			nameof(Monitor.Enter),
			nameof(Monitor.TryEnter),
			nameof(Monitor.Exit),
			nameof(Monitor.Wait),
			nameof(Monitor.Pulse),
			nameof(Monitor.PulseAll)
		]);

	/// <inheritdoc/>
	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
		ImmutableArray.Create(Rules.LockStatement, Rules.MonitorMethod);

	/// <inheritdoc/>
	public override void Initialize(AnalysisContext context)
	{
		context.EnableConcurrentExecution();
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.RegisterCompilationStartAction(static compilationStartContext =>
		{
			// Nothing can be reported in a compilation that does not reference any async lock type.
			if (AsyncLockTypes.Create(compilationStartContext.Compilation) is not { } types)
				return;

			compilationStartContext.RegisterOperationAction(c => AnalyzeLockStatement(c, types), OperationKind.Lock);
			if (types.Monitor is not null)
				compilationStartContext.RegisterOperationAction(c => AnalyzeInvocation(c, types), OperationKind.Invocation);
		});
	}

	private static void AnalyzeLockStatement(OperationAnalysisContext context, AsyncLockTypes types)
	{
		var lockedValue = Unwrap(((ILockOperation)context.Operation).LockedValue);
		if (!types.Includes(lockedValue.Type))
			return;

		context.ReportDiagnostic(Diagnostic.Create(
			Rules.LockStatement, lockedValue.Syntax.GetLocation(), Describe(lockedValue.Type)));
	}

	private static void AnalyzeInvocation(OperationAnalysisContext context, AsyncLockTypes types)
	{
		var invocation = (IInvocationOperation)context.Operation;
		var method = invocation.TargetMethod;
		if (!MonitorMethodNames.Contains(method.Name))
			return;
		if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, types.Monitor))
			return;

		// Every Monitor method above takes the object it synchronises on as its first parameter.
		if (FindArgument(invocation, ordinal: 0) is not { } argument)
			return;

		var synchronisedOn = Unwrap(argument.Value);
		if (!types.Includes(synchronisedOn.Type))
			return;

		context.ReportDiagnostic(Diagnostic.Create(
			Rules.MonitorMethod, synchronisedOn.Syntax.GetLocation(), Describe(synchronisedOn.Type), method.Name));
	}

	private static IArgumentOperation? FindArgument(IInvocationOperation invocation, Int32 ordinal)
	{
		foreach (var argument in invocation.Arguments)
			if (argument.Parameter?.Ordinal == ordinal)
				return argument;
		return null;
	}

	/// <summary>
	/// Strips conversions so that the async lock is still recognised behind the boxing conversion a
	/// <c>Monitor</c> call introduces, and behind an explicit cast such as <c>lock ((Object)gate)</c>.
	/// </summary>
	private static IOperation Unwrap(IOperation operation)
	{
		while (operation is IConversionOperation conversion)
			operation = conversion.Operand;
		return operation;
	}

	private static String Describe(ITypeSymbol? type) =>
		type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "?";
}
