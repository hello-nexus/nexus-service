using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Tests.Np50;

/// <summary>
/// A released NP50 runs the firmware's standalone mode from its EEPROM defaults
/// (what the cooling page's FW Control hands it to), not bare motherboard PWM.
/// </summary>
public class Np50CoolingProviderHandBackTests
{
    private const byte StaticSetpoint = 37;

    [Fact]
    public void ReleaseAll_hands_the_hub_to_its_eeprom_default_static_mode_at_the_stored_setpoint()
    {
        var hub = NewConnectedHub(defaultMode: Np50Protocol.DefaultModeStatic, out var t);
        var provider = new Np50CoolingProvider(hub);

        provider.ReleaseAll();

        var frame = Assert.Single(t.Writes, IsCoolingModeFrame);
        Assert.Equal(Np50Protocol.ModeStatic, frame[4]);
        Assert.Equal(StaticSetpoint, frame[5]);
        Assert.Equal(Np50Protocol.ModeStatic, hub.DesiredCoolingMode);
    }

    [Fact]
    public void ReleaseFan_hands_back_to_motherboard_when_that_is_the_eeprom_default()
    {
        var hub = NewConnectedHub(defaultMode: Np50Protocol.DefaultModeMotherboard, out var t);
        var provider = new Np50CoolingProvider(hub);

        provider.DriveFanSpeed(Fp12, 50);
        provider.ReleaseFan(Fp12);

        var frame = Assert.Single(t.Writes.Where(IsCoolingModeFrame).Skip(1));
        Assert.Equal(Np50Protocol.ModeMotherboard, frame[4]);
    }

    [Fact]
    public void Releasing_an_undriven_legacy_channel_before_the_first_fp12_write_keeps_fp12_writes_applied()
    {
        var hub = NewConnectedHub(defaultMode: Np50Protocol.DefaultModeStatic, out var t);
        var provider = new Np50CoolingProvider(hub);

        provider.ReleaseFan("np50:NPTEST:legacy");
        provider.DriveFanSpeed(Fp12, 60);

        Assert.Equal(Np50Protocol.ModeSoftware, hub.DesiredCoolingMode);
        Assert.Contains(t.Writes, w => IsCoolingModeFrame(w) && w[4] == Np50Protocol.ModeSoftware);
        var duty = Assert.Single(t.Writes, IsPortDutyFrame);
        Assert.Equal(1, duty[3]);
        Assert.Equal(60, duty[6]);
    }

    [Fact]
    public void Releasing_the_last_driven_channel_hands_back()
    {
        var hub = NewConnectedHub(defaultMode: Np50Protocol.DefaultModeStatic, out _);
        var provider = new Np50CoolingProvider(hub);

        provider.DriveFanSpeed(Fp12, 60);
        provider.ReleaseFan(Fp12);

        Assert.Equal(Np50Protocol.ModeStatic, hub.DesiredCoolingMode);
    }

    [Fact]
    public void Releasing_a_driven_legacy_channel_still_hands_back()
    {
        var hub = NewConnectedHub(defaultMode: Np50Protocol.DefaultModeStatic, out _);
        var provider = new Np50CoolingProvider(hub);

        provider.DriveFanSpeed("np50:NPTEST:legacy", 40);
        provider.ReleaseFan("np50:NPTEST:legacy");

        Assert.Equal(Np50Protocol.ModeStatic, hub.DesiredCoolingMode);
    }

    [Fact]
    public void Releasing_a_non_last_driven_channel_does_not_hand_back()
    {
        var hub = NewConnectedHub(defaultMode: Np50Protocol.DefaultModeStatic, out _);
        var provider = new Np50CoolingProvider(hub);

        provider.DriveFanSpeed(Fp12, 60);
        provider.DriveFanSpeed("np50:NPTEST:port1:dev2", 60);
        provider.ReleaseFan(Fp12);

        Assert.Equal(Np50Protocol.ModeSoftware, hub.DesiredCoolingMode);
    }

    [Fact]
    public void A_release_while_the_hub_is_offline_still_hands_back_after_reconnect()
    {
        var hub = NewConnectedHub(defaultMode: Np50Protocol.DefaultModeStatic, out var t);
        var provider = new Np50CoolingProvider(hub);

        provider.DriveFanSpeed(Fp12, 60);
        t.Open = false;
        provider.ReleaseFan(Fp12);
        Assert.Equal(Np50Protocol.ModeSoftware, hub.DesiredCoolingMode);

        t.Open = true;
        provider.ReleaseFan(Fp12);
        Assert.Equal(Np50Protocol.ModeStatic, hub.DesiredCoolingMode);
    }

    [Fact]
    public void A_user_pinned_firmware_mode_swallows_curve_writes_without_a_port_duty_frame()
    {
        var hub = NewConnectedHub(defaultMode: Np50Protocol.DefaultModeStatic, out var t);
        var provider = new Np50CoolingProvider(hub);

        hub.SetDesiredCoolingMode(Np50Protocol.ModeStatic);
        provider.DriveFanSpeed(Fp12, 60);

        Assert.Equal(Np50Protocol.ModeStatic, hub.DesiredCoolingMode);
        Assert.DoesNotContain(t.Writes, IsPortDutyFrame);
    }

    // Header, port byte, then one fixed-size record per device slot.
    private static bool IsPortDutyFrame(byte[] w) =>
        w.Length == 4 + Np50Protocol.MaxDevicesPerPort * 9 && w[0] == 0xFF && w[1] == 0xCC && w[2] == 0x02;

    private const string Fp12 = "np50:NPTEST:port1:dev1";

    [Fact]
    public void ReleaseAll_falls_back_to_motherboard_when_the_eeprom_read_fails()
    {
        var hub = NewConnectedHub(defaultMode: null, out var t);
        var provider = new Np50CoolingProvider(hub);

        provider.ReleaseAll();

        var frame = Assert.Single(t.Writes, IsCoolingModeFrame);
        Assert.Equal(Np50Protocol.ModeMotherboard, frame[4]);
    }

    // FF CC 02 00 <mode> <static%> ... (15 bytes).
    private static bool IsCoolingModeFrame(byte[] w) =>
        w.Length == 15 && w[0] == 0xFF && w[1] == 0xCC && w[2] == 0x02 && w[3] == 0x00;

    // Null defaultMode: the EEPROM read answers with a short reply, so
    // GetFirmwareDefaults returns null.
    private static Np50Hub NewConnectedHub(byte? defaultMode, out FakeTransport transport)
    {
        var t = new FakeTransport
        {
            OnRead = request =>
            {
                var isDefaultsQuery = request.Length >= 3 && request[1] == 0xCC && request[2] == 0x04;
                if (!isDefaultsQuery || defaultMode is not byte mode) return Array.Empty<byte>();
                var reply = new byte[17];
                reply[0] = 0xFF; reply[1] = 0xCC; reply[2] = 0x04; reply[3] = 0x00;
                reply[4] = mode;
                reply[5] = StaticSetpoint;
                return reply;
            },
        };
        transport = t;
        var hub = new Np50Hub(new FakeDiscovery(), _ => t);
        Assert.True(hub.EnsureConnected());
        return hub;
    }

    private sealed class FakeDiscovery : INp50PortDiscovery
    {
        public IReadOnlyList<Np50PortInfo> Discover()
            => new[] { new Np50PortInfo { PortName = "COM_TEST", Serial = "NPTEST" } };
    }

    private sealed class FakeTransport : INp50Transport
    {
        public readonly List<byte[]> Writes = new();
        public Func<byte[], byte[]> OnRead = _ => Array.Empty<byte>();
        private byte[] _lastWrite = Array.Empty<byte>();

        public bool Open = true;
        public bool IsOpen => Open;
        public string Serial => "NPTEST";
        public void DiscardInput() { }

        public void Write(ReadOnlySpan<byte> data)
        {
            _lastWrite = data.ToArray();
            Writes.Add(_lastWrite);
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            var src = OnRead(_lastWrite);
            var n = Math.Min(src.Length, buffer.Length);
            src.AsSpan(0, n).CopyTo(buffer);
            return n;
        }

        public void Dispose() { }
    }
}
