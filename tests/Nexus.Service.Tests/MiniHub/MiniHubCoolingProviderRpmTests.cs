using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Peripherals.Hyte.Np50;
using Xunit;

namespace Nexus.Service.Tests.MiniHub;

public class MiniHubCoolingProviderRpmTests
{
    // A port whose polls have not agreed yet must not show a number; an
    // agreed port shows it. Both flow from the hub state the consensus sets.
    [Fact]
    public void GetFanChannels_follows_the_per_port_validity()
    {
        var hub = NewConnectedHub();
        hub.State.Port1Fans = 1;
        hub.State.Port2Fans = 3;
        hub.State.Port1Rpm = 1063;
        hub.State.Port1RpmValid = true;
        hub.State.Port2Rpm = 0;
        hub.State.Port2RpmValid = false;

        var channels = new MiniHubCoolingProvider(hub).GetFanChannels();

        Assert.Equal(2, channels.Count);
        Assert.False(channels[0].RpmUnavailable);
        Assert.Equal(1063, channels[0].Rpm);
        Assert.True(channels[1].RpmUnavailable);
        Assert.Equal(0, channels[1].Rpm);
    }

    [Fact]
    public void GetAll_reports_rpm_only_for_agreed_ports()
    {
        var hub = NewConnectedHub();
        hub.State.Port1Fans = 1;
        hub.State.Port2Fans = 3;
        hub.State.Port1Rpm = 1063;
        hub.State.Port1RpmValid = true;
        hub.State.Port2RpmValid = false;

        var devices = new MiniHubCoolingProvider(hub).GetAll().SelectMany(c => c.Devices).ToList();

        Assert.Equal(2, devices.Count);
        Assert.Equal(1063, devices[0].Rpm);
        Assert.Null(devices[1].Rpm);
    }

    [Fact]
    public void PollFanSpeeds_publishes_only_after_the_polls_agree()
    {
        var transport = new FakeTransport { Port1Period = 141, Port2Period = 0x03 };
        var hub = new MiniHubHub(new FakeDiscovery(), _ => transport);
        Assert.True(hub.EnsureConnected());
        hub.State.Port1Fans = 1;
        hub.State.Port2Fans = 3;
        var provider = new MiniHubCoolingProvider(hub);

        for (var i = 0; i < MiniHubTachConsensus.MinAgreeing - 1; i++) Assert.True(hub.PollFanSpeeds());
        Assert.True(provider.GetFanChannels()[0].RpmUnavailable);

        Assert.True(hub.PollFanSpeeds());
        var channels = provider.GetFanChannels();
        Assert.False(channels[0].RpmUnavailable);
        Assert.Equal(1063, channels[0].Rpm);
        // 0x03 decodes to 50000 RPM: implausible, so port 2 stays hidden.
        Assert.True(channels[1].RpmUnavailable);
    }

    private static MiniHubHub NewConnectedHub()
    {
        var hub = new MiniHubHub(new FakeDiscovery(), _ => new FakeTransport());
        Assert.True(hub.EnsureConnected());
        return hub;
    }

    private sealed class FakeDiscovery : INp50PortDiscovery
    {
        public IReadOnlyList<Np50PortInfo> Discover()
            => new[] { new Np50PortInfo { PortName = "COM_TEST", Serial = "MHTEST" } };
    }

    private sealed class FakeTransport : INp50Transport
    {
        public byte Port1Period;
        public byte Port2Period;
        private byte[] _lastWrite = Array.Empty<byte>();

        public bool IsOpen => true;
        public string Serial => "MHTEST";
        public void DiscardInput() { }
        public void Write(ReadOnlySpan<byte> data) => _lastWrite = data.ToArray();

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_lastWrite.Length < 3 || _lastWrite[2] != 0x06) return 0;
            var reply = new byte[] { 0xFF, 0xDD, 0x06, 0x01, 0x00, Port1Period, 0x02, 0x00, Port2Period };
            var n = Math.Min(reply.Length, buffer.Length);
            reply.AsSpan(0, n).CopyTo(buffer);
            return n;
        }

        public void Dispose() { }
    }
}
