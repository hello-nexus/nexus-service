using System;
using System.Collections.Generic;
using System.Linq;
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
        var hub = new BulkPanelHub(new ZMatricesPanelDriver());
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
        var hub = new BulkPanelHub(new UniversalScreen88Driver());
        hub.Attach(pipe, null);
        var transport = new BulkPanelStreamTransport(hub, "serial");

        transport.BindBrightness(() => 40);
        transport.ApplyBrightness();

        Assert.Empty(pipe.PipeWrites);
    }

    [Fact]
    public void Discovery_advertises_brightness_only_for_drivers_that_take_it()
    {
        var zm = new BulkPanelHub(new ZMatricesPanelDriver());
        zm.Attach(new RecordingPipe(), null);
        var screen88 = new BulkPanelHub(new UniversalScreen88Driver());
        screen88.Attach(new RecordingPipe(), null);

        Assert.True(new BulkPanelDiscovery(zm).Discover().Single().Profile.SupportsBrightness);
        Assert.False(new BulkPanelDiscovery(screen88).Discover().Single().Profile.SupportsBrightness);
    }

    private sealed class RecordingPipe : IBulkUsbPipe
    {
        public List<(byte Pipe, byte[] Data)> PipeWrites { get; } = new();

        public bool Write(ReadOnlySpan<byte> data) => true;

        public bool Write(byte pipeId, ReadOnlySpan<byte> data)
        {
            PipeWrites.Add((pipeId, data.ToArray()));
            return true;
        }

        public int Read(Span<byte> buffer, int timeoutMs) => 0;

        public void Dispose() { }
    }
}
