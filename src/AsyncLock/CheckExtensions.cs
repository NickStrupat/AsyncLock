using System;
using System.Runtime.CompilerServices;
using static System.ArgumentNullException;

namespace NickStrupat;

public static class CheckExtensions
{
	public static T Check<T>(this T value, Action<Object, String> action, [CallerArgumentExpression(nameof(value))] String paramName = "")
	where T : notnull
	{
		if (!typeof(T).IsValueType)
			ThrowIfNull(value);
		ThrowIfNull(action);
		ThrowIfNull(paramName);
		action(value, paramName);
		return value;
	}
}