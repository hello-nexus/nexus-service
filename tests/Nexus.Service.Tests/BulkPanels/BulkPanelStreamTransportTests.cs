using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.BulkPanels;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class BulkPanelStreamTransportTests
{
    [Fact]
    public void Brightness_is_sent_once_per_change()
    {
        var pipe = new RecordingPipe();
        using var hub = new BulkPanelHub(new ZMatricesPanelDriver());
        Assert.True(hub.Attach(pipe, null));
        var transport = new BulkPanelStreamTransport(hub, "serial");
        int? wanted = 40;
        transport.BindBrightness(() => wanted);

        transport.ApplyBrightness();
        transport.ApplyBrightness();
        wanted = 70;
        transport.ApplyBrightness();

        var levels = pipe.PipeWrites.Where(w => w.Data[2] == 0x04).Select(w => w.Data[4]).ToArray();
        Assert.Equal(new byte[] { 40, 70 }, levels);
    }

    [Fact]
    public void Panel_without_a_backlight_command_never_gets_one()
    {
        var pipe = new RecordingPipe();
        using var hub = new BulkPanelHub(new UniversalScreen88Driver());
        hub.Attach(pipe, null);
        var transport = new BulkPanelStreamTransport(hub, "serial");

        transport.BindBrightness(() => 40);
        transport.ApplyBrightness();

        Assert.Empty(pipe.PipeWrites);
    }

    [Fact]
    public void Discovery_advertises_brightness_only_for_drivers_that_take_it()
    {
        using var zm = new BulkPanelHub(new ZMatricesPanelDriver());
        zm.Attach(new RecordingPipe(), null);
        using var screen88 = new BulkPanelHub(new UniversalScreen88Driver());
        screen88.Attach(new RecordingPipe(), null);

        Assert.True(new BulkPanelDiscovery(zm).Discover().Single().Profile.SupportsBrightness);
        Assert.False(new BulkPanelDiscovery(screen88).Discover().Single().Profile.SupportsBrightness);
    }

    [Fact]
    public void Secondary_monitor_streams_the_desktop_and_injects_touch()
    {
        var pipe = new RecordingPipe();
        using var hub = new BulkPanelHub(new ZMatricesPanelDriver());
        Assert.True(hub.Attach(pipe, null));
        var host = new FakeMonitorHost();
        using var transport = new BulkPanelStreamTransport(hub, "serial", host);
        bool wanted = true;
        transport.BindSecondaryMonitor(() => wanted);
        pipe.Input.Enqueue(ZMatricesTouchTests.V1(0, TouchPhase.Down, 0, 10));
        pipe.Input.Enqueue(ZMatricesTouchTests.V1(0, TouchPhase.Up, 0, 10));

        transport.ApplySecondaryMonitor();

        Assert.True(SpinWait.SpinUntil(() => transport.SecondaryMonitorState == SecondaryMonitorStates.Active, 5000));
        Assert.Contains(pipe.FrameWrites, w => w.AsSpan().StartsWith("Start"u8));
        Assert.True(SpinWait.SpinUntil(() => host.Monitor!.Touches.Count >= 2, 5000));
        Assert.Equal(new[] { (1u, TouchPhase.Down, 10, 539), (1u, TouchPhase.Up, 10, 539) }, host.Monitor!.Touches.Take(2));

        wanted = false;
        transport.ApplySecondaryMonitor();

        Assert.True(host.Monitor!.Disposed);
        Assert.Null(transport.SecondaryMonitorState);
    }

    [Fact]
    public void Secondary_monitor_reports_a_missing_driver()
    {
        using var hub = new BulkPanelHub(new ZMatricesPanelDriver());
        Assert.True(hub.Attach(new RecordingPipe(), null));
        var host = new FakeMonitorHost { Failure = SecondaryMonitorStates.DriverMissing };
        using var transport = new BulkPanelStreamTransport(hub, "serial", host);
        transport.BindSecondaryMonitor(() => true);

        transport.ApplySecondaryMonitor();

        Assert.True(SpinWait.SpinUntil(() => transport.SecondaryMonitorState == SecondaryMonitorStates.DriverMissing, 5000));
    }

    [Fact]
    public void Discovery_offers_the_monitor_only_with_a_host()
    {
        using var hub = new BulkPanelHub(new ZMatricesPanelDriver());
        hub.Attach(new RecordingPipe(), null);

        Assert.True(new BulkPanelDiscovery(hub, new FakeMonitorHost()).Discover().Single().Profile.SupportsSecondaryMonitor);
        Assert.False(new BulkPanelDiscovery(hub).Discover().Single().Profile.SupportsSecondaryMonitor);
    }

    private sealed class FakeMonitorHost : IVirtualMonitorHost
    {
        public string? Failure { get; init; }
        public FakeMonitor? Monitor { get; private set; }

        public IVirtualMonitor? Create(int width, int height, string instanceKey, CancellationToken ct, out string failureState)
        {
            failureState = Failure ?? SecondaryMonitorStates.Failed;
            if (Failure is not null)
            {
                return null;
            }
            Monitor = new FakeMonitor();
            return Monitor;
        }
    }

    private sealed class FakeMonitor : IVirtualMonitor
    {
        private int _frames;
        public ConcurrentQueue<(uint, TouchPhase, int, int)> Touches { get; } = new();
        public bool Disposed { get; private set; }

        public bool TryReadFrame(byte[] destination, int timeoutMs, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _frames) == 1)
            {
                destination.AsSpan().Fill(0x80);
                return true;
            }
            ct.WaitHandle.WaitOne(timeoutMs);
            return false;
        }

        public bool InjectTouch(uint pointerId, TouchPhase phase, int x, int y)
        {
            Touches.Enqueue((pointerId, phase, x, y));
            return true;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingPipe : IBulkUsbPipe
    {
        public List<(byte Pipe, byte[] Data)> PipeWrites { get; } = new();
        public ConcurrentBag<byte[]> FrameWrites { get; } = new();
        public ConcurrentQueue<byte[]> Input { get; } = new();

        public bool Write(ReadOnlySpan<byte> data)
        {
            FrameWrites.Add(data.ToArray());
            return true;
        }

        public bool Write(byte pipeId, ReadOnlySpan<byte> data)
        {
            PipeWrites.Add((pipeId, data.ToArray()));
            return true;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (Input.TryDequeue(out var packet))
            {
                packet.CopyTo(buffer);
                return packet.Length;
            }
            Thread.Sleep(Math.Min(timeoutMs, 20));
            return 0;
        }

        public void Dispose() { }
    }
}
