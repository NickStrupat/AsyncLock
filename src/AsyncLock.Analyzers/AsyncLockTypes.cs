using System;
using Microsoft.CodeAnalysis;

namespace NickStrupat.Analyzers;

/// <summary>
/// The async lock types visible to a single compilation, resolved once at compilation start so that
/// per-operation analysis is nothing more than a handful of symbol comparisons.
/// </summary>
internal sealed class AsyncLockTypes
{
	/// <summary>
	/// Implementing this marks a type as an async lock. This is how user-defined implementations are
	/// recognised as well as the one in this library.
	/// </summary>
	private const String InterfaceMetadataName = "NickStrupat.IAsyncLock";

	private readonly INamedTypeSymbol asyncLockInterface;

	/// <summary>The <see cref="System.Threading.Monitor"/> symbol, or <see langword="null"/> if it is unavailable.</summary>
	public INamedTypeSymbol? Monitor { get; }

	private AsyncLockTypes(INamedTypeSymbol asyncLockInterface, INamedTypeSymbol? monitor)
	{
		this.asyncLockInterface = asyncLockInterface;
		Monitor = monitor;
	}

	/// <summary>
	/// Resolves the async lock types in <paramref name="compilation"/>, returning <see langword="null"/>
	/// when none are referenced so the analyzer can skip the compilation entirely.
	/// </summary>
	public static AsyncLockTypes? Create(Compilation compilation)
	{
		if (compilation.GetTypeByMetadataName(InterfaceMetadataName) is not { } asyncLockInterface)
			return null;

		return new AsyncLockTypes(asyncLockInterface, compilation.GetTypeByMetadataName("System.Threading.Monitor"));
	}

	/// <summary>Determines whether <paramref name="type"/> is, or implements, an async lock.</summary>
	public Boolean Includes(ITypeSymbol? type)
	{
		if (type is null)
			return false;

		// The static type may be the interface itself, which does not appear in its own AllInterfaces.
		if (Matches(type))
			return true;

		foreach (var implemented in type.AllInterfaces)
			if (Matches(implemented))
				return true;

		return false;
	}

	private Boolean Matches(ITypeSymbol type) =>
		SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, asyncLockInterface);
}
