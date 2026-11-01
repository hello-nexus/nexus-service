using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.IoSafety;
using Xunit;

namespace Nexus.Service.Tests.IoSafety;

public class PendingIoTests
{
    private sealed class Resource : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task A_cancelled_operation_that_finishes_settles_and_its_resource_is_disposed()
    {
        using var op = new CancellationTokenSource();
        var io = Task.Delay(Timeout.Infinite, op.Token);
        var resource = new Resource();

        var settled = PendingIo.CancelAndSettle(io, op, settleMs: 2000);

        Assert.True(settled);
        Assert.True(PendingIo.Retire(resource, settled));
        Assert.True(resource.Disposed);
        await Assert.ThrowsAsync<TaskCanceledException>(() => io);
    }

    [Fact]
    public void An_operation_that_ignores_the_cancel_strands_its_resource_instead_of_freeing_it()
    {
        using var op = new CancellationTokenSource();
        var never = new TaskCompletionSource();
        var resource = new Resource();
        var before = PendingIo.StrandedCount;

        var settled = PendingIo.CancelAndSettle(never.Task, op, settleMs: 100);

        Assert.False(settled);
        Assert.True(op.IsCancellationRequested);
        Assert.False(PendingIo.Retire(resource, settled));
        Assert.False(resource.Disposed);
        Assert.Equal(before + 1, PendingIo.StrandedCount);
        never.SetResult();
    }

    [Fact]
    public async Task A_faulted_operation_counts_as_finished_and_its_fault_is_read()
    {
        using var op = new CancellationTokenSource();
        var io = Task.FromException(new InvalidOperationException("device gone"));

        Assert.True(PendingIo.CancelAndSettle(io, op, 500));
        await Task.Delay(50);
        Assert.True(io.Exception is not null);
    }
}
