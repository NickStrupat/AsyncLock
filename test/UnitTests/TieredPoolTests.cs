using NickStrupat;
using Xunit;

namespace UnitTests;

public class TieredPoolTests
{
	[Fact]
	public void EmptyPoolHasNothingToRent()
	{
		var pool = new TieredPool<Object>();

		Assert.Null(pool.TryRent());
	}

	[Fact]
	public void ReturnedItemIsRentedOnce()
	{
		var pool = new TieredPool<Object>();
		var item = new Object();

		pool.Return(item);

		Assert.Same(item, pool.TryRent());
		Assert.Null(pool.TryRent());
	}

	[Fact]
	public void ItemsReturnedWhileTheSlotIsFullAreKeptInTheOverflowQueue()
	{
		var pool = new TieredPool<Object>();
		var first = new Object();
		var second = new Object();
		var third = new Object();

		pool.Return(first);
		pool.Return(second);
		pool.Return(third);

		// The slot is rented first, then the queue in the order it was filled.
		Assert.Same(first, pool.TryRent());
		Assert.Same(second, pool.TryRent());
		Assert.Same(third, pool.TryRent());
		Assert.Null(pool.TryRent());
	}

	[Fact]
	public void KeepsNoMoreThanTheSlotAndAFullOverflowQueue()
	{
		const Int32 capacity = 1 + TieredPool<Object>.OverflowCapacity;
		var pool = new TieredPool<Object>();

		for (var i = 0; i < capacity + 10; i++)
			pool.Return(new Object()); // the last ten find no room and are dropped

		var rented = 0;
		while (pool.TryRent() is not null)
			rented++;
		Assert.Equal(capacity, rented);
	}

	[Fact]
	public void AllocatesNothingUntilASecondItemIsIdle()
	{
		// The point of the two tiers: a pool that only ever holds one idle item never builds its queue. Everything
		// here runs on this thread, which is what makes the per-thread allocation counter exact.
		var first = new Object();
		var second = new Object();
		var third = new Object();
		Exercise(new TieredPool<Object>(), out _, out _, out _); // warm up: JIT and type loading
		Exercise(new TieredPool<Object>(), out var oneIdle, out var secondIdle, out var afterwards);

		Assert.Equal(0, oneIdle);
		Assert.True(secondIdle > 0, "the overflow queue is created when an item is returned to a full slot");
		Assert.Equal(0, afterwards);

		void Exercise(TieredPool<Object> pool, out Int64 oneIdleBytes, out Int64 secondIdleBytes, out Int64 afterwardsBytes)
		{
			var start = GC.GetAllocatedBytesForCurrentThread();
			for (var i = 0; i < 100; i++)
			{
				pool.Return(first);
				Assert.Same(first, pool.TryRent());
			}
			pool.Return(first);
			var afterOneIdle = GC.GetAllocatedBytesForCurrentThread();
			pool.Return(second);
			var afterSecondIdle = GC.GetAllocatedBytesForCurrentThread();
			for (var i = 0; i < 100; i++)
			{
				pool.Return(third);
				Assert.NotNull(pool.TryRent());
			}
			var end = GC.GetAllocatedBytesForCurrentThread();

			oneIdleBytes = afterOneIdle - start;
			secondIdleBytes = afterSecondIdle - afterOneIdle;
			afterwardsBytes = end - afterSecondIdle;
		}
	}

	[Fact]
	public async Task ConcurrentRentersNeverShareAnItem()
	{
		// Each worker rents (or creates) an item, claims it, releases the claim and returns it. An item handed to two
		// renters at once shows up as a claim that was already taken.
		const Int32 workers = 8;
		const Int32 iterations = 200_000;
		var holder = new PoolHolder();
		var doubleRents = 0;

		await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
		{
			for (var i = 0; i < iterations; i++)
			{
				var item = holder.Pool.TryRent() ?? new Claim();
				if (Interlocked.Exchange(ref item.Taken, 1) != 0)
					Interlocked.Increment(ref doubleRents);
				Volatile.Write(ref item.Taken, 0);
				holder.Pool.Return(item);
			}
		})));

		Assert.Equal(0, Volatile.Read(ref doubleRents));
	}

	private sealed class Claim
	{
		public Int32 Taken;
	}

	/// <summary>Holds the pool in a field, as its real owner does, so that every worker shares the one instance.</summary>
	private sealed class PoolHolder
	{
		public TieredPool<Claim> Pool;
	}
}
