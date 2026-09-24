using System;
using System.Collections.Generic;
using Nexus.Service.Platform;
using Xunit;

namespace Nexus.Service.Tests.Platform;

public class LargeBlockMemoryPoolTests
{
    [Fact]
    public void Rent_returns_a_full_block_and_reuses_it_after_dispose()
    {
        using var pool = new LargeBlockMemoryPool();
        var first = pool.Rent(4096);
        Assert.Equal(LargeBlockMemoryPool.BlockSize, first.Memory.Length);
        var memory = first.Memory;
        first.Dispose();
        using var second = pool.Rent();
        Assert.True(second.Memory.Span == memory.Span);
    }

    [Fact]
    public void Double_dispose_returns_the_block_once()
    {
        using var pool = new LargeBlockMemoryPool();
        var block = pool.Rent();
        block.Dispose();
        block.Dispose();
        using var a = pool.Rent();
        using var b = pool.Rent();
        Assert.False(a.Memory.Span == b.Memory.Span);
    }

    [Fact]
    public void Rent_larger_than_a_block_throws()
    {
        using var pool = new LargeBlockMemoryPool();
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(LargeBlockMemoryPool.BlockSize + 1));
    }

    [Fact]
    public void Retains_at_most_the_cap_of_idle_blocks()
    {
        using var pool = new LargeBlockMemoryPool();
        var rented = new List<IDisposable>();
        for (int i = 0; i < LargeBlockMemoryPool.MaxRetainedBlocks + 8; i++) rented.Add(pool.Rent());
        foreach (var r in rented) r.Dispose();
        Assert.Equal(LargeBlockMemoryPool.MaxRetainedBlocks, pool.RetainedCount);
        using var one = pool.Rent();
        Assert.Equal(LargeBlockMemoryPool.MaxRetainedBlocks - 1, pool.RetainedCount);
    }

    [Fact]
    public void Trim_releases_only_blocks_that_stayed_idle_for_the_interval()
    {
        using var pool = new LargeBlockMemoryPool();
        var rented = new List<IDisposable>();
        for (int i = 0; i < 10; i++) rented.Add(pool.Rent());
        foreach (var r in rented) r.Dispose();
        pool.Trim();
        // The first trim only starts the window: all ten sat idle from here.
        Assert.Equal(10, pool.RetainedCount);

        using (pool.Rent()) using (pool.Rent()) using (pool.Rent()) { }
        pool.Trim();
        // Three were taken during the window, so seven never moved.
        Assert.Equal(3, pool.RetainedCount);

        pool.Trim();
        Assert.Equal(0, pool.RetainedCount);
    }
}
