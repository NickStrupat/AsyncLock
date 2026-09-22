using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NickStrupat;
using Xunit;

namespace AnalyzerTests;

/// <summary>
/// Compiles a snippet against the real AsyncLock assembly, runs an analyzer over it, and compares the
/// reported diagnostics against the locations marked up in the snippet with <c>[|</c> and <c>|]</c>.
/// </summary>
internal static class AnalyzerVerifier
{
	private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

	/// <summary>
	/// Asserts that running <typeparamref name="TAnalyzer"/> over <paramref name="markup"/> reports
	/// exactly <paramref name="expectedIds"/>, one per marked span, in source order.
	/// </summary>
	/// <param name="markup">The snippet, with each expected diagnostic location wrapped in <c>[| |]</c>.</param>
	/// <param name="expectedIds">The diagnostic ids expected at the marked spans, in source order.</param>
	public static async Task VerifyAsync<TAnalyzer>(String markup, params String[] expectedIds)
		where TAnalyzer : DiagnosticAnalyzer, new()
	{
		var (source, spans) = ParseMarkup(markup);
		Assert.Equal(expectedIds.Length, spans.Length);

		var text = SourceText.From(source);
		var syntaxTree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest));
		var compilation = CSharpCompilation.Create(
			"AnalyzerTestAssembly",
			[syntaxTree],
			References,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

		var compileErrors = compilation.GetDiagnostics()
			.Where(x => x.Severity == DiagnosticSeverity.Error)
			.Select(x => x.ToString())
			.ToArray();
		Assert.True(compileErrors.Length == 0, "The test snippet does not compile:\n" + String.Join("\n", compileErrors));

		var diagnostics = await compilation
			.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new TAnalyzer()))
			.GetAnalyzerDiagnosticsAsync();

		var expected = expectedIds.Zip(spans, (id, span) => Format(id, span, text)).ToArray();
		var actual = diagnostics
			.OrderBy(x => x.Location.SourceSpan.Start)
			.Select(x => Format(x.Id, x.Location.SourceSpan, text))
			.ToArray();
		Assert.Equal(expected, actual);
	}

	private static String Format(String id, TextSpan span, SourceText text)
	{
		var start = text.Lines.GetLinePosition(span.Start);
		return $"{id} at ({start.Line + 1},{start.Character + 1}) on '{text.ToString(span)}'";
	}

	/// <summary>Strips the <c>[| |]</c> markers, returning the real source and the spans they delimited.</summary>
	private static (String Source, ImmutableArray<TextSpan> Spans) ParseMarkup(String markup)
	{
		var source = new StringBuilder(markup.Length);
		var spans = ImmutableArray.CreateBuilder<TextSpan>();
		var starts = new Stack<Int32>();
		for (var i = 0; i < markup.Length; i++)
		{
			if (i + 1 < markup.Length && markup[i] == '[' && markup[i + 1] == '|')
			{
				starts.Push(source.Length);
				i++;
			}
			else if (i + 1 < markup.Length && markup[i] == '|' && markup[i + 1] == ']')
			{
				spans.Add(TextSpan.FromBounds(starts.Pop(), source.Length));
				i++;
			}
			else
				source.Append(markup[i]);
		}

		Assert.Empty(starts);
		spans.Sort((x, y) => x.Start.CompareTo(y.Start));
		return (source.ToString(), spans.ToImmutable());
	}

	/// <summary>
	/// References every assembly the test host itself was loaded with, plus the AsyncLock assembly, so
	/// the snippets see the same types a real consuming project would.
	/// </summary>
	private static ImmutableArray<MetadataReference> BuildReferences()
	{
		var paths = ((String?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
			.Split(Path.PathSeparator)
			.Append(typeof(IAsyncLock).Assembly.Location) // the package
			.Append(typeof(AsyncLock).Assembly.Location)  // the candidate implementations
			.Where(x => x.Length != 0)
			.GroupBy(Path.GetFileNameWithoutExtension)
			.Select(x => x.First());
		return [..paths.Select(x => (MetadataReference)MetadataReference.CreateFromFile(x))];
	}
}
