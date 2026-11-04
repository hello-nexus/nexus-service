using System;
using System.Collections.Concurrent;
using System.Threading;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.BulkPanels;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class BulkPanelTouchReaderTests
{
    [Fact]
    public void A_successor_waits_for_a_stalled_predecessor_before_it_reads()
    {
        var pipe = new StallingPipe();
        using var hub = new BulkPanelHub(new ZMatricesPanelDriver());
        Assert.True(hub.Attach(pipe, null));
        using var stop = new CancellationTokenSource();
        var contacts = new ConcurrentQueue<TouchContactTracker.Injection>();

        using var first = new BulkPanelTouchReader(hub, 1120, 540, () => (false, false), i => { contacts.Enqueue(i); return true; }, "t", stop.Token);
        first.Start();
        Assert.True(pipe.FirstReadEntered.Wait(5000));
        stop.Cancel();

        using var second = new BulkPanelTouchReader(hub, 1120, 540, () => (false, false), i => { contacts.Enqueue(i); return true; }, "t");
        second.Start();
        Assert.True(SpinWait.SpinUntil(() => hub.InputReadersWaiting == 1, 5000));
        Assert.Equal(1, pipe.ReadCalls);

        pipe.Release.Set();
        pipe.Input.Enqueue(ZMatricesTouchTests.V1(0, TouchPhase.Down, 0, 10));
        Assert.True(SpinWait.SpinUntil(() => !contacts.IsEmpty, 5000));
        Assert.Equal(1, pipe.MaxConcurrent);
    }

    [Fact]
    public void Disposing_a_reader_that_never_started_does_not_throw()
    {
        using var hub = new BulkPanelHub(new ZMatricesPanelDriver());
        var reader = new BulkPanelTouchReader(hub, 1120, 540, () => (false, false), _ => true, "t");
        reader.Dispose();
        reader.Start();
        reader.Dispose();
    }

    private sealed class StallingPipe : IBulkUsbPipe
    {
        private int _concurrent;
        public int ReadCalls;
        public int MaxConcurrent;
        public readonly ManualResetEventSlim FirstReadEntered = new();
        public readonly ManualResetEventSlim Release = new();
        public readonly ConcurrentQueue<byte[]> Input = new();

        public bool Write(ReadOnlySpan<byte> data) => true;
        public bool Write(byte pipeId, ReadOnlySpan<byte> data) => true;

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            int now = Interlocked.Increment(ref _concurrent);
            int calls = Interlocked.Increment(ref ReadCalls);
            int seen;
            while (now > (seen = Volatile.Read(ref MaxConcurrent)) && Interlocked.CompareExchange(ref MaxConcurrent, now, seen) != seen)
            {
            }
            try
            {
                if (calls == 1)
                {
                    FirstReadEntered.Set();
                    Release.Wait(10000);
                    return 0;
                }
                if (Input.TryDequeue(out var packet))
                {
                    packet.CopyTo(buffer);
                    return packet.Length;
                }
                Thread.Sleep(Math.Min(timeoutMs, 20));
                return 0;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        public void Dispose() { }
    }
}
