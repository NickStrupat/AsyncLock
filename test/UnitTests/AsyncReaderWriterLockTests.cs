using NickStrupat;
using Xunit;

namespace UnitTests;

public class AsyncReaderWriterLockTests
{
	[Fact]
	public async Task ReadLockAsync_SingleReader_ExecutesSuccessfully()
	{
		// Arrange
		var rwLock = new AsyncReaderWriterLock();
		var executed = false;

		// Act
		await rwLock.ReadLockAsync(async () =>
		{
			executed = true;
			await Task.Yield();
		});

		// Assert
		Assert.True(executed);
	}

	[Fact]
	public async Task WriteLockAsync_SingleWriter_ExecutesSuccessfully()
	{
		// Arrange
		var rwLock = new AsyncReaderWriterLock();
		var executed = false;

		// Act
		await rwLock.WriteLockAsync(async () =>
		{
			executed = true;
			await Task.Yield();
		});

		// Assert
		Assert.True(executed);
	}

	[Fact]
	public async Task ReadLockAsync_WithNullAction_ThrowsArgumentNullException()
	{
		// Arrange
		var rwLock = new AsyncReaderWriterLock();

		// Act & Assert
		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			rwLock.ReadLockAsync(null!).AsTask());
	}

	[Fact]
	public async Task WriteLockAsync_WithNullAction_ThrowsArgumentNullException()
	{
		// Arrange
		var rwLock = new AsyncReaderWriterLock();

		// Act & Assert
		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			rwLock.WriteLockAsync(null!).AsTask());
	}

	[Fact]
	public async Task ReadLockAsync_MultipleConcurrentReaders_ExecuteConcurrently()
	{
		// Arrange
		var rwLock = new AsyncReaderWriterLock();
		var concurrentReaders = 0;
		var maxConcurrentReaders = 0;
		var readerCount = 10;
		var tasks = new List<Task>();

		// Act
		for (int i = 0; i < readerCount; i++)
		{
			tasks.Add(rwLock.ReadLockAsync(async () =>
			{
				var current = Interlocked.Increment(ref concurrentReaders);
				maxConcurrentReaders = Math.Max(maxConcurrentReaders, current);
				await Task.Delay(50);
				Interlocked.Decrement(ref concurrentReaders);
			}).AsTask());
		}

		await Task.WhenAll(tasks);

		// Assert - Multiple readers should be able to execute concurrently
		Assert.True(maxConcurrentReaders > 1, $"Expected multiple concurrent readers, but got {maxConcurrentReaders}");
	}

	[Fact]
	public async Task WriteLockAsync_BlocksOtherWriters()
	{
		// Arrange
		var rwLock = new AsyncReaderWriterLock();
		var firstWriterStarted = new TaskCompletionSource<bool>();
		var firstWriterCanFinish = new TaskCompletionSource<bool>();
		var secondWriterStarted = false;

		// Act
		var firstWriter = rwLock.WriteLockAsync(async () =>
		{
			firstWriterStarted.SetResult(true);
			await firstWriterCanFinish.Task;
		}).AsTask();

		await firstWriterStarted.Task;

		var secondWriter = rwLock.WriteLockAsync(async () =>
		{
			secondWriterStarted = true;
			await Task.Yield();
		}).AsTask();

		// Give second writer a chance to start (it should be blocked)
		await Task.Delay(100);

		// Assert - Second writer should not have started yet
		Assert.False(secondWriterStarted);

		// Release first writer
		firstWriterCanFinish.SetResult(true);
		await firstWriter;
		await secondWriter;

		// Now second writer should have completed
		Assert.True(secondWriterStarted);
	}

	[Fact]
	public async Task WriteLockAsync_BlocksReaders()
	{
		// Arrange
		var rwLock = new AsyncReaderWriterLock();
		var writerStarted = new TaskCompletionSource<bool>();
		var writerCanFinish = new TaskCompletionSource<bool>();
		var readerStarted = false;

		// Act
		var writer = rwLock.WriteLockAsync(async () =>
		{
			writerStarted.SetResult(true);
			await writerCanFinish.Task;
		}).AsTask();

		await writerStarted.Task;

		var reader = rwLock.ReadLockAsync(async () =>
		{
			readerStarted = true;
			await Task.Yield();
		}).AsTask();

		// Give reader a chance to start (it should be blocked)
		await Task.Delay(100);

		// Assert - Reader should not have started yet
		Assert.False(readerStarted);

		// Release writer
		writerCanFinish.SetResult(true);
		await writer;
		await reader;

		// Now reader should have completed
		Assert.True(readerStarted);
	}

	[Fact]
	public async Task ReadLockAsync_WaitsForWriterToComplete()
	{
		// Arrange
		var rwLock = new AsyncReaderWriterLock();
		var writerCompleted = false;
		var readerExecuted = false;

		// Act
		var writer = rwLock.WriteLockAsync(async () =>
		{
			await Task.Delay(100);
			writerCompleted = true;
		}).AsTask();

		await Task.Delay(10); // Let writer start

		var reader = rwLock.ReadLockAsync(async () =>
		{
			// When reader executes, writer should have completed
			Assert.True(writerCompleted);
			readerExecuted = true;
			await Task.Yield();
		}).AsTask();

		await Task.WhenAll(writer, reader);

		// Assert
		Assert.True(readerExecuted);
	}

	[Fact]
	public async Task WriteLockAsync_WaitsForAllReadersToComplete()
	{
		// Arrange
		var rwLock = new AsyncReaderWriterLock();
		var readersStarted = new TaskCompletionSource<bool>();
		var readersCanFinish = new TaskCompletionSource<bool>();
		var completedReaderCount = 0;
		var writerStarted = false;

		// Act
		var readers = new List<Task>();
		for (int i = 0; i < 5; i++)
		{
			var readerIndex = i; // Capture loop variable
			readers.Add(rwLock.ReadLockAsync(async () =>
			{
				if (readerIndex == 0)
					readersStarted.SetResult(true);
				await readersCanFinish.Task;
				Interlocked.Increment(ref completedReaderCount);
			}).AsTask());
		}

		await readersStarted.Task;

		var writer = rwLock.WriteLockAsync(async () =>
		{
			// When writer executes, all readers should have completed
			Assert.Equal(5, completedReaderCount);
			writerStarted = true;
			await Task.Yield();
		}).AsTask();

		// Give writer a chance to start (it should be blocked)
		await Task.Delay(100);

		// Assert - Writer should not have started yet
		Assert.False(writerStarted);

		// Release readers
		readersCanFinish.SetResult(true);
		await Task.WhenAll(readers);
		await writer;

		// Now writer should have completed
		Assert.True(writerStarted);
	}

	[Fact]
	public async Task ReadLockAsync_WithCancellation_CancelsOperation()
	{
		// Arrange
		var rwLock = new AsyncReaderWriterLock();
		var writerStarted = new TaskCompletionSource<bool>();
		using var cts = new CancellationTokenSource();

		// Start a writer to block the reader
		var writer = rwLock.WriteLockAsync(async () =>
		{
			writerStarted.SetResult(true);
			await Task.Delay(1000);
		}).AsTask();

		await writerStarted.Task;

		// Act & Assert
		var reader = rwLock.ReadLockAsync(async () =>
		{
			await Task.Yield();
		}, cts.Token).AsTask();

		await cts.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader);

		// Cleanup
		await writer;
	}

	[Fact]
	public async Task WriteLockAsync_WithCancellation_CancelsOperation()
	{
		// Arrange
		var rwLock = new AsyncReaderWriterLock();
		var firstWriterStarted = new TaskCompletionSource<bool>();
		using var cts = new CancellationTokenSource();

		// Start a writer to block the second writer
		var firstWriter = rwLock.WriteLockAsync(async () =>
		{
			firstWriterStarted.SetResult(true);
			await Task.Delay(1000);
		}).AsTask();

		await firstWriterStarted.Task;

		// Act & Assert
		var secondWriter = rwLock.WriteLockAsync(async () =>
		{
			await Task.Yield();
		}, cts.Token).AsTask();

		cts.Cancel();

		await Assert.ThrowsAsync<OperationCanceledException>(() => secondWriter);

		// Cleanup
		await firstWriter;
	}

	[Fact]
	public async Task ReadLockAsync_WhenActionThrows_ReleasesLockProperly()
	{
		// Arrange
		var rwLock = new AsyncReaderWriterLock();
		var expectedException = new InvalidOperationException("Test exception");

		// Act & Assert - First reader throws
		var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
		{
			await rwLock.ReadLockAsync(async () =>
			{
				await Task.Yield();
				throw expectedException;
			});
		});

		Assert.Same(expectedException, exception);

		// Assert - Lock should be released, so a new operation should work
		var secondReaderExecuted = false;
		await rwLock.ReadLockAsync(async () =>
		{
			secondReaderExecuted = true;
			await Task.Yield();
		});

		Assert.True(secondReaderExecuted);
	}

	[Fact]
	public async Task WriteLockAsync_WhenActionThrows_ReleasesLockProperly()
	{
		// Arrange
		var rwLock = new AsyncReaderWriterLock();
		var expectedException = new InvalidOperationException("Test exception");

		// Act & Assert - First writer throws
		var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
		{
			await rwLock.WriteLockAsync(async () =>
			{
				await Task.Yield();
				throw expectedException;
			});
		});

		Assert.Same(expectedException, exception);

		// Assert - Lock should be released, so a new operation should work
		var secondWriterExecuted = false;
		await rwLock.WriteLockAsync(async () =>
		{
			secondWriterExecuted = true;
			await Task.Yield();
		});

		Assert.True(secondWriterExecuted);
	}

	[Fact]
	public async Task ReadLockAsync_ManyReaders_AllExecuteSuccessfully()
	{
		// Arrange
		var rwLock = new AsyncReaderWriterLock();
		var readerCount = 100;
		var executedCount = 0;

		// Act
		var tasks = Enumerable.Range(0, readerCount)
			.Select(_ => rwLock.ReadLockAsync(async () =>
			{
				Interlocked.Increment(ref executedCount);
				await Task.Delay(10);
			}).AsTask());

		await Task.WhenAll(tasks);

		// Assert
		Assert.Equal(readerCount, executedCount);
	}

	[Fact]
	public async Task AlternatingReadsAndWrites_ExecuteInCorrectOrder()
	{
		// Arrange
		var rwLock = new AsyncReaderWriterLock();
		var operations = new List<string>();
		var operationsLock = new object();

		// Act
		var tasks = new List<Task>();

		// Write 1
		tasks.Add(rwLock.WriteLockAsync(async () =>
		{
			await Task.Delay(50);
			lock (operationsLock)
				operations.Add("Write1");
		}).AsTask());

		await Task.Delay(10);

		// Read 1 & 2
		tasks.Add(rwLock.ReadLockAsync(async () =>
		{
			await Task.Delay(50);
			lock (operationsLock)
				operations.Add("Read1");
		}).AsTask());

		tasks.Add(rwLock.ReadLockAsync(async () =>
		{
			await Task.Delay(50);
			lock (operationsLock)
				operations.Add("Read2");
		}).AsTask());

		await Task.Delay(10);

		// Write 2
		tasks.Add(rwLock.WriteLockAsync(async () =>
		{
			await Task.Delay(50);
			lock (operationsLock)
				operations.Add("Write2");
		}).AsTask());

		await Task.WhenAll(tasks);

		// Assert - Write1 should complete first, then Read1 and Read2 (concurrently), then Write2
		Assert.Equal(4, operations.Count);
		Assert.Equal("Write1", operations[0]);
		var collection = operations.Skip(1).Take(2).ToList();
		Assert.Contains("Read1", collection);
		Assert.Contains("Read2", collection);
		Assert.Equal("Write2", operations[3]);
	}

	[Fact]
	public async Task ReadLockAsync_MultipleReadersWithDifferentDurations_AllComplete()
	{
		// Arrange
		var rwLock = new AsyncReaderWriterLock();
		var completedReaders = 0;

		// Act
		var tasks = new[]
		{
			rwLock.ReadLockAsync(async () =>
			{
				await Task.Delay(100);
				Interlocked.Increment(ref completedReaders);
			}).AsTask(),
			rwLock.ReadLockAsync(async () =>
			{
				await Task.Delay(50);
				Interlocked.Increment(ref completedReaders);
			}).AsTask(),
			rwLock.ReadLockAsync(async () =>
			{
				await Task.Delay(150);
				Interlocked.Increment(ref completedReaders);
			}).AsTask()
		};

		await Task.WhenAll(tasks);

		// Assert
		Assert.Equal(3, completedReaders);
	}
}