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
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed => _disposed.Task.IsCompleted;
        public Task WhenDisposed => _disposed.Task;
        public void Dispose() => _disposed.TrySetResult();
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
    public async Task A_stuck_operation_that_later_faults_leaves_no_unobserved_exception()
    {
        var marker = "late-fault-" + Guid.NewGuid().ToString("N");
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.Flatten().InnerExceptions)
            {
                seen.Enqueue(inner.Message);
            }
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            using var op = new CancellationTokenSource();
            var never = new TaskCompletionSource();
            Assert.False(PendingIo.CancelAndSettle(never.Task, op, settleMs: 50));

            // The device returns an error only after the settle window, as a driver completing a cancelled IRP does.
            never.SetException(new InvalidOperationException(marker));
            never = null!;
            for (var i = 0; i < 3; i++)
            {
                await Task.Delay(50);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        Assert.DoesNotContain(marker, seen);
    }

    [Fact]
    public async Task A_stranded_resource_is_released_when_its_stuck_operation_finishes()
    {
        var stuck = new TaskCompletionSource();
        var resource = new Resource();
        var before = PendingIo.StrandedCount;

        Assert.False(PendingIo.Retire(resource, noOperationPending: false, stuck.Task));
        Assert.False(resource.Disposed);
        Assert.Equal(before + 1, PendingIo.StrandedCount);

        stuck.SetResult();
        await resource.WhenDisposed.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(resource.Disposed);
        Assert.Equal(before, PendingIo.StrandedCount);
    }
}
