using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace NickStrupat.Analyzers;

/// <summary>
/// The async lock types visible to a single compilation, resolved once at compilation start so that
/// per-operation analysis is nothing more than a handful of symbol comparisons.
/// </summary>
internal sealed class AsyncLockTypes
{
	/// <summary>
	/// Implementing one of these marks a type as an async lock. This is how user-defined
	/// implementations are recognised as well as the ones in this library.
	/// </summary>
	private static readonly String[] InterfaceMetadataNames =
	[
		"NickStrupat.IAsyncLock",
	];

	/// <summary>
	/// Async lock types that do not implement one of the interfaces above. Extend this list when a
	/// new such type is added to the library.
	/// </summary>
	private static readonly String[] StandaloneMetadataNames =
	[
		"NickStrupat.AsyncReaderWriterLock",
		"NickStrupat.SharedAsyncLock",
	];

	private readonly ImmutableArray<INamedTypeSymbol> interfaces;
	private readonly ImmutableArray<INamedTypeSymbol> standaloneTypes;

	/// <summary>The <see cref="System.Threading.Monitor"/> symbol, or <see langword="null"/> if it is unavailable.</summary>
	public INamedTypeSymbol? Monitor { get; }

	private AsyncLockTypes(
		ImmutableArray<INamedTypeSymbol> interfaces,
		ImmutableArray<INamedTypeSymbol> standaloneTypes,
		INamedTypeSymbol? monitor)
	{
		this.interfaces = interfaces;
		this.standaloneTypes = standaloneTypes;
		Monitor = monitor;
	}

	/// <summary>
	/// Resolves the async lock types in <paramref name="compilation"/>, returning <see langword="null"/>
	/// when none are referenced so the analyzer can skip the compilation entirely.
	/// </summary>
	public static AsyncLockTypes? Create(Compilation compilation)
	{
		var interfaces = Resolve(compilation, InterfaceMetadataNames);
		var standaloneTypes = Resolve(compilation, StandaloneMetadataNames);
		if (interfaces.IsEmpty && standaloneTypes.IsEmpty)
			return null;

		return new AsyncLockTypes(interfaces, standaloneTypes, compilation.GetTypeByMetadataName("System.Threading.Monitor"));
	}

	private static ImmutableArray<INamedTypeSymbol> Resolve(Compilation compilation, String[] metadataNames)
	{
		var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>(metadataNames.Length);
		foreach (var metadataName in metadataNames)
			if (compilation.GetTypeByMetadataName(metadataName) is { } type)
				builder.Add(type);
		return builder.ToImmutable();
	}

	/// <summary>Determines whether <paramref name="type"/> is, or implements, an async lock.</summary>
	public Boolean Includes(ITypeSymbol? type)
	{
		if (type is null)
			return false;

		// The static type may be the interface itself, which does not appear in its own AllInterfaces.
		if (Matches(type, interfaces) || Matches(type, standaloneTypes))
			return true;

		foreach (var implemented in type.AllInterfaces)
			if (Matches(implemented, interfaces))
				return true;

		return false;
	}

	private static Boolean Matches(ITypeSymbol type, ImmutableArray<INamedTypeSymbol> candidates)
	{
		foreach (var candidate in candidates)
			if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, candidate))
				return true;
		return false;
	}
}
