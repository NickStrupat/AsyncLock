using System.Threading;

namespace NickStrupat;

public struct Interlocked<T>(T value) where T : class?
{
	private T value = value;

	public T CoreCachedValue
	{
		get => value;
		set => this.value = value;
	}
	
	public T Exchange(T newValue) => Interlocked.Exchange(ref value, newValue);
	
	public T CompareExchange(T newValue, T comparand) => Interlocked.CompareExchange(ref value!, newValue, comparand);

	public T Read() => CompareExchange(null!, null!);
}