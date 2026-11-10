using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lifecycle;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Np50;

public class Np50LegacyDutyTests
{
    private const string LegacyId = "np50:NPTEST:legacy";

    private static bool IsModeFrame(byte[] w) =>
        w.Length == 15 && w[0] == 0xFF && w[1] == 0xCC && w[2] == 0x02 && w[3] == 0x00;

    private static bool IsLegacyDuty(byte[] w) =>
        w.Length == 12 && w[1] == 0xCC && w[2] == 0x02 && w[3] == 0x00 && w[4] == 0x00;

    [Fact]
    public void Repeated_legacy_writes_send_the_mode_frame_once_and_never_in_front_of_each_duty()
    {
        var (hub, t) = NewHub();
        var provider = new Np50CoolingProvider(hub);

        provider.SetFanSpeed(LegacyId, 30);
        hub.State.HubInfo.CoolingMode = "Software";
        provider.SetFanSpeed(LegacyId, 60);
        provider.SetFanSpeed(LegacyId, 10);

        var kinds = t.Snapshot().Select(w => IsModeFrame(w) ? "M" : IsLegacyDuty(w) ? "D" : "?").ToArray();
        Assert.Equal(new[] { "M", "D", "D", "D" }, kinds);
        Assert.Equal(new[] { 30, 60, 10 }, t.Snapshot().Where(IsLegacyDuty).Select(w => (int)w[5]).ToArray());
    }

    [Fact]
    public void Legacy_write_resends_the_mode_frame_at_most_every_couple_of_seconds_while_the_hub_reports_another_mode()
    {
        var (hub, t) = NewHub();
        var now = 1_000_000L;
        hub.Clock = () => now;
        var provider = new Np50CoolingProvider(hub);
        provider.SetFanSpeed(LegacyId, 30);

        now += 500;
        provider.SetFanSpeed(LegacyId, 60);
        Assert.Equal(1, t.Snapshot().Count(IsModeFrame));

        now += 2_000;
        provider.SetFanSpeed(LegacyId, 70);
        Assert.Equal(2, t.Snapshot().Count(IsModeFrame));
    }

    [Fact]
    public void Unknown_reported_mode_never_triggers_a_resend_and_still_allows_the_reassert()
    {
        var (hub, t) = NewHub();
        var now = 1_000_000L;
        hub.Clock = () => now;
        var provider = new Np50CoolingProvider(hub);
        provider.SetFanSpeed(LegacyId, 30);
        hub.State.HubInfo.CoolingMode = "Unknown";
        t.Clear();

        now += 60_000;
        provider.SetFanSpeed(LegacyId, 40);
        Assert.Empty(t.Snapshot().Where(IsModeFrame));

        now += 60_000;
        Assert.True(hub.ReassertLegacyDutyIfDue());
    }

    [Theory]
    [InlineData(Np50Protocol.ModeMotherboard)]
    [InlineData(Np50Protocol.ModeStatic)]
    public void Leaving_software_mode_drops_the_held_duty(byte mode)
    {
        var (hub, t) = NewHub();
        var clock = 0L;
        hub.Clock = () => clock += 60_000;
        var provider = new Np50CoolingProvider(hub);
        provider.SetFanSpeed(LegacyId, 30);
        hub.SetDesiredCoolingMode(mode);
        hub.SetDesiredCoolingMode(Np50Protocol.ModeSoftware);
        hub.State.HubInfo.CoolingMode = "Software";
        t.Clear();

        Assert.False(hub.ReassertLegacyDutyIfDue());
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Heartbeat_reasserts_the_held_legacy_duty_only_when_due_and_in_software_mode()
    {
        var (hub, t) = NewHub();
        var provider = new Np50CoolingProvider(hub);
        provider.SetFanSpeed(LegacyId, 42);
        t.Clear();
        var now = 1_000_000L;
        hub.Clock = () => now;
        hub.SetLegacyFanSpeed(42);
        t.Clear();

        // Hub not yet reporting Software: nothing to re-assert.
        hub.State.HubInfo.CoolingMode = "Motherboard";
        now += 60_000;
        Assert.False(hub.ReassertLegacyDutyIfDue());
        Assert.Empty(t.Snapshot());

        hub.State.HubInfo.CoolingMode = "Software";
        hub.SetLegacyFanSpeed(42);
        t.Clear();
        now += 2_999;
        Assert.False(hub.ReassertLegacyDutyIfDue());
        now += 1;
        Assert.True(hub.ReassertLegacyDutyIfDue());
        Assert.False(hub.ReassertLegacyDutyIfDue());

        var frame = Assert.Single(t.Snapshot());
        Assert.True(IsLegacyDuty(frame));
        Assert.Equal(42, frame[5]);
    }

    [Fact]
    public void Reassert_waits_for_a_later_tick_after_a_mode_frame()
    {
        var (hub, t) = NewHub();
        var now = 1_000_000L;
        hub.Clock = () => now;
        var provider = new Np50CoolingProvider(hub);
        provider.SetFanSpeed(LegacyId, 42);
        hub.State.HubInfo.CoolingMode = "Software";
        now += 60_000;
        hub.SetCoolingMode(Np50Protocol.ModeSoftware);
        t.Clear();

        Assert.False(hub.ReassertLegacyDutyIfDue());
        Assert.Empty(t.Snapshot());

        now += 1_000;
        Assert.True(hub.ReassertLegacyDutyIfDue());
    }

    [Fact]
    public void Heartbeat_does_not_reassert_after_the_fan_is_released()
    {
        var (hub, t) = NewHub();
        var provider = new Np50CoolingProvider(hub);
        provider.SetFanSpeed(LegacyId, 42);
        provider.ReleaseFan(LegacyId);
        t.Clear();
        hub.State.HubInfo.CoolingMode = "Software";

        hub.Clock = () => long.MaxValue / 2;

        Assert.False(hub.ReassertLegacyDutyIfDue());
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Worker_tick_reasserts_the_legacy_duty()
    {
        var (hub, t) = NewHub(reportSoftware: true);
        var store = new InMemoryConfigStore();
        var worker = new Np50HeartbeatWorker(
            hub, new HardwarePresence(new NoUsb()), new MultiplexHub(), new DeviceControlGate(store), gates: new FeatureGates(store));
        var provider = new Np50CoolingProvider(hub);
        provider.SetFanSpeed(LegacyId, 55);
        t.Clear();
        hub.Clock = () => long.MaxValue / 2;

        worker.Tick();

        Assert.Contains(t.Snapshot(), w => IsLegacyDuty(w) && w[5] == 55);
    }

    [Fact]
    public async Task Duty_write_waits_for_an_in_flight_exchange()
    {
        var (hub, t) = NewHub();
        var inRead = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        t.OnReadBlock = () => { inRead.Set(); release.Wait(TimeSpan.FromSeconds(5)); };

        var poll = Task.Run(() => hub.PollHubInfo());
        Assert.True(inRead.Wait(TimeSpan.FromSeconds(5)));
        t.Clear();

        var started = new ManualResetEventSlim();
        var write = Task.Run(() =>
        {
            started.Set();
            return hub.SetLegacyFanSpeed(20);
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(write.Wait(100));
        Assert.Empty(t.Snapshot());

        release.Set();
        await poll;
        Assert.True(await write);
        Assert.Single(t.Snapshot(), IsLegacyDuty);
    }

    private static (Np50Hub Hub, FakeTransport T) NewHub(bool reportSoftware = false)
    {
        var t = new FakeTransport
        {
            OnRead = request =>
            {
                if (!reportSoftware || request.Length < 3 || request[1] != 0xCC || request[2] != 0x01 || request[3] != 0x00) return Array.Empty<byte>();
                var reply = new byte[20];
                reply[0] = 0xFF; reply[1] = 0xCC; reply[2] = 0x01;
                reply[12] = Np50Protocol.ModeSoftware;
                return reply;
            },
        };
        var hub = new Np50Hub(new OnePort(), _ => t);
        Assert.True(hub.EnsureConnected());
        return (hub, t);
    }

    private sealed class NoUsb : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Enumerate() => new();
    }

    private sealed class OnePort : INp50PortDiscovery
    {
        public IReadOnlyList<Np50PortInfo> Discover()
            => new[] { new Np50PortInfo { PortName = "COM_TEST", Serial = "NPTEST" } };
    }

    private sealed class FakeTransport : INp50Transport
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _writes = new();
        private byte[] _last = Array.Empty<byte>();
        public Func<byte[], byte[]> OnRead = _ => Array.Empty<byte>();
        public Action? OnReadBlock;

        public bool IsOpen => true;
        public string Serial => "NPTEST";
        public void DiscardInput() { }

        public void Write(ReadOnlySpan<byte> data)
        {
            lock (_gate)
            {
                _last = data.ToArray();
                _writes.Add(_last);
            }
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            OnReadBlock?.Invoke();
            byte[] last;
            lock (_gate) last = _last;
            var src = OnRead(last);
            var n = Math.Min(src.Length, buffer.Length);
            src.AsSpan(0, n).CopyTo(buffer);
            return n;
        }

        public byte[][] Snapshot()
        {
            lock (_gate) return _writes.ToArray();
        }

        public void Clear()
        {
            lock (_gate) _writes.Clear();
        }

        public void Dispose() { }
    }
}
