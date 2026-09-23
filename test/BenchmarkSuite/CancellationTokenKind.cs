namespace BenchmarkSuite
{
	/// <summary>The kind of token passed to every acquisition in a benchmark.</summary>
	public enum CancellationTokenKind
	{
		/// <summary><see cref="System.Threading.CancellationToken.None"/>, which locks can skip registering with.</summary>
		None,

		/// <summary>A token from a source that is never cancelled, so waiting acquisitions must register with it.</summary>
		Cancellable,
	}
}
