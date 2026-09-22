using System;
using System.Threading;
using System.Threading.Tasks;

namespace NickStrupat;

internal static class TaskExtensions
{
	public static async Task ThrowIf<TEx>(this Task<Boolean> task, Boolean value = true) where TEx : Exception, new()
	{
		if (await task.ConfigureAwait(false) == value)
			throw new TEx();
	}

	public static async Task Then(this Task task, Func<Task> next)
	{
		await task.ConfigureAwait(false);
		await next().ConfigureAwait(false);
	}

	public static async Task Then(this Task task, Action next)
	{
		await task.ConfigureAwait(false);
		next();
	}

	public static async ValueTask ThenSetResultOn(this Task task, TaskCompletionSource next)
	{
		await task.ConfigureAwait(false);
		next.SetResult();
	}

	public static async Task WhenCancelled(this Task task, Action whenCancelled)
	{
		try
		{
			await task.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (InvokeAndReturnFalse(whenCancelled))
		{
		}

		static Boolean InvokeAndReturnFalse(Action action)
		{
			action();
			return false;
		}
	}

	public static async Task WaitAsync(this Task task, CancellationToken cancellationToken, Action whenCancelled)
	{
		await task
			.WaitAsync(cancellationToken)
			.WhenCancelled(whenCancelled)
			.ConfigureAwait(false);
	}
}