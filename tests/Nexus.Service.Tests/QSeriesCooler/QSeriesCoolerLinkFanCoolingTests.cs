using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Peripherals.Hyte.Np50;          // INp50Transport
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Persistence;
using Nexus.Service.Tests;
using RgbColor = Nexus.Service.Peripherals.Hyte.MiniHub.RgbColor;

namespace Nexus.Service.Tests.QSeriesCooler;

/// <summary>
/// Covers the per-fan cooling channels enumerated from a Nexus Link channel's device
/// chain: one <see cref="Nexus.Service.Models.Cooling.FanChannel"/> per FP12 slot (no
/// aggregate ":fans" channel), driving one fan resends the whole physical channel's
/// frame at its own chain position, the shared hub-wide mode across pump + fans, and the
/// legacy aggregate id's assignment migrating onto the new per-fan ids.
/// </summary>
public class QSeriesCoolerLinkFanCoolingTests
{
    private static readonly int[] FixtureRpm = { 621, 705, 715, 731, 727 };

    private static QSeriesCoolerHub NewConnectedHub(out ScriptedTransport transport)
    {
        var t = new ScriptedTransport();
        transport = t;
        var discovery = new FakeDiscovery(new QSeriesCoolerPort
        {
            PortName = "COM_TEST", Serial = "QTEST123", Variant = QSeriesCoolerProtocol.VariantQ60,
        });
        var hub = new QSeriesCoolerHub(discovery, _ => t);
        // Lighting is the cheapest path that opens the transport.
        hub.WriteLighting(new[] { new RgbColor(1, 2, 3) });
        var devices = new List<QSeriesLinkDevice>();
        for (var i = 0; i < 5; i++)
        {
            devices.Add(new QSeriesLinkDevice
            {
                Slot = i + 1,
                Model = "FP12",
                FanCount = 1,
                FanRpm = new[] { FixtureRpm[i] },
                LedCount = 0,
            });
        }
        hub.State.Channel2Devices = devices;
        return hub;
    }

    [Fact]
    public void GetFanChannels_yields_pump_and_five_FP12_channels_no_aggregate_fans_channel()
    {
        var hub = NewConnectedHub(out _);
        var provider = new QSeriesCoolerCoolingProvider(hub, new InMemoryConfigStore());

        var channels = provider.GetFanChannels();

        Assert.Contains(channels, c => c.Id == "qseries:QTEST123:pump");
        Assert.DoesNotContain(channels, c => c.Id.EndsWith(":fans", StringComparison.Ordinal));
        for (var i = 0; i < 5; i++)
        {
            var slot = i + 1;
            var ch = Assert.Single(channels, c => c.Id == $"qseries:QTEST123:p2:{slot}");
            Assert.Equal($"FP12 (Port 2 #{slot})", ch.Name);
            Assert.Equal(Nexus.Service.Models.Cooling.FanKinds.Fan, ch.Kind);
            Assert.Equal("Port 2", ch.PortLabel);
            Assert.Equal("FP12", ch.FanModel);
            Assert.Equal(FixtureRpm[i], ch.Rpm);
            Assert.Equal("qseries:QTEST123", ch.DeviceId);
        }
    }

    [Fact]
    public void Driving_one_fan_resends_channel_2_frame_preserving_a_previously_driven_slots_duty()
    {
        var hub = NewConnectedHub(out var t);
        var provider = new QSeriesCoolerCoolingProvider(hub, new InMemoryConfigStore());

        // Drive slot 1 first, THEN slot 3 - this is the only way to prove the resend
        // PRESERVES a sibling slot's duty rather than merely defaulting untouched
        // slots to 0 (which a single-write test can't distinguish from "always zero").
        provider.SetFanSpeed("qseries:QTEST123:p2:1", 55);
        provider.SetFanSpeed("qseries:QTEST123:p2:3", 70);

        var frame = t.Writes.Last(w =>
            w.Length >= 4 && w[1] == 0xCC && w[2] == 0x02 && w[3] == QSeriesCoolerProtocol.FanChannel);
        // Slot 1 is block index 0, slot 3 is block index 2 (0-based): header at 4 + i*9.
        Assert.Equal(55, frame[4 + 0 * 9 + 2]);
        Assert.Equal(70, frame[4 + 2 * 9 + 2]);
        // Every other slot's fan1% byte is still 0 - never driven.
        foreach (var block in new[] { 1, 3, 4 })
        {
            Assert.Equal(0, frame[4 + block * 9 + 2]);
        }
    }

    [Fact]
    public void Driving_a_fan_behind_a_light_strip_lands_in_its_own_chain_position_not_the_strips()
    {
        var hub = NewConnectedHub(out var t);
        hub.State.Channel1Devices = new List<QSeriesLinkDevice>
        {
            new() { Slot = 1, Model = "LS10", LedCount = 20, FanCount = 0 },
            new() { Slot = 2, Model = "FP12", LedCount = 0, FanCount = 1, FanRpm = new[] { 700 } },
        };
        var provider = new QSeriesCoolerCoolingProvider(hub, new InMemoryConfigStore());

        provider.SetFanSpeed("qseries:QTEST123:p1:2", 80);

        var frame = Assert.Single(t.Writes, w =>
            w.Length >= 4 && w[1] == 0xCC && w[2] == 0x02 && w[3] == QSeriesCoolerProtocol.LinkChannel1);
        // LS10 (slot 1) occupies block 0 and carries no fan duty.
        Assert.Equal(0, frame[4 + 0 * 9 + 2]);
        // FP12 (slot 2) occupies block 1 - BuildSetChannelFanSpeeds maps slotDuties[i] to
        // wire block i by CHAIN POSITION, so a compacted "fans only" index would have
        // wrongly placed this duty in block 0 (LS10's block) instead.
        Assert.Equal(80, frame[4 + 1 * 9 + 2]);
    }

    [Fact]
    public void Pinned_non_software_mode_swallows_a_fan_duty_write()
    {
        var hub = NewConnectedHub(out var t);
        var provider = new QSeriesCoolerCoolingProvider(hub, new InMemoryConfigStore());
        hub.MarkDesiredControlMode(QSeriesCoolerProtocol.ControlModeMotherboard);

        provider.SetFanSpeed("qseries:QTEST123:p2:1", 80);

        Assert.DoesNotContain(t.Writes, w => w.Length >= 4 && w[1] == 0xCC && w[2] == 0x02 && w[3] == QSeriesCoolerProtocol.FanChannel);
        var channel = provider.GetFanChannels().Single(c => c.Id == "qseries:QTEST123:p2:1");
        Assert.Equal(Nexus.Service.Models.Cooling.FanModes.Auto, channel.Mode);
    }

    [Fact]
    public void ReleaseFan_reverts_to_motherboard_only_once_the_pump_and_every_driven_fan_are_released()
    {
        var hub = NewConnectedHub(out var t);
        var provider = new QSeriesCoolerCoolingProvider(hub, new InMemoryConfigStore());

        provider.SetFanSpeed("qseries:QTEST123:pump", 50);
        provider.SetFanSpeed("qseries:QTEST123:p2:1", 60);
        provider.SetFanSpeed("qseries:QTEST123:p2:2", 70);

        provider.ReleaseFan("qseries:QTEST123:p2:1");
        Assert.DoesNotContain(t.Writes, IsMotherboardControlFrame);

        provider.ReleaseFan("qseries:QTEST123:pump");
        Assert.DoesNotContain(t.Writes, IsMotherboardControlFrame);

        provider.ReleaseFan("qseries:QTEST123:p2:2");
        Assert.Contains(t.Writes, IsMotherboardControlFrame);
    }

    private static bool IsMotherboardControlFrame(byte[] w) =>
        w.Length >= 5 && w[1] == 0xCC && w[2] == 0x02 && w[3] == 0x00 && w[4] == QSeriesCoolerProtocol.ControlModeMotherboard;

    [Fact]
    public void Legacy_fans_assignment_migrates_across_every_binding_and_removes_the_old_key()
    {
        var hub = NewConnectedHub(out _);
        var store = new InMemoryConfigStore();
        const string legacy = "qseries:QTEST123:fans";
        store.Update(s =>
        {
            s.Cooling.ManualSpeeds[legacy] = 55;
            s.Cooling.CustomManualSpeeds[legacy] = 45;
            s.Cooling.FanOffsets[legacy] = 5;
            s.Cooling.FanLockOverrides[legacy] = true;
            s.Cooling.FanRoles[legacy] = Nexus.Service.Models.Cooling.FanRoleKind.Gpu;
            s.Cooling.CustomFanCurveAssignments[legacy] = "curve1";
            s.Cooling.UncontrolledFanChannels.Add(legacy);
            s.Cooling.FanChannelOrder = new List<string> { "qseries:QTEST123:pump", legacy };
            s.Cooling.FanNames[legacy] = "My Radiator Fans";
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "curve1",
                Outputs = { new CurveOutputDocument { Id = legacy, Type = "fan" } },
            });
            s.Cooling.Presets.Add(new CoolingPreset
            {
                Id = "preset1",
                FanCurveAssignments = { [legacy] = "curve1" },
                ManualSpeeds = { [legacy] = 33 },
                FanOffsets = { [legacy] = 3 },
            });
        });
        var provider = new QSeriesCoolerCoolingProvider(hub, store);

        provider.GetFanChannels();

        var cooling = store.Load().Cooling;
        var newIds = Enumerable.Range(1, 5).Select(slot => $"qseries:QTEST123:p2:{slot}").ToArray();

        Assert.DoesNotContain(legacy, cooling.ManualSpeeds.Keys);
        Assert.DoesNotContain(legacy, cooling.CustomManualSpeeds.Keys);
        Assert.DoesNotContain(legacy, cooling.FanOffsets.Keys);
        Assert.DoesNotContain(legacy, cooling.FanLockOverrides.Keys);
        Assert.DoesNotContain(legacy, cooling.FanRoles.Keys);
        Assert.DoesNotContain(legacy, cooling.CustomFanCurveAssignments.Keys);
        Assert.DoesNotContain(legacy, cooling.UncontrolledFanChannels);
        Assert.DoesNotContain(legacy, cooling.FanChannelOrder!);
        // The aggregate's user-given name must not be stamped onto five fans.
        Assert.DoesNotContain(legacy, cooling.FanNames.Keys);
        Assert.DoesNotContain(newIds, id => cooling.FanNames.ContainsKey(id));

        var curve = cooling.Curves.Single(c => c.Id == "curve1");
        Assert.DoesNotContain(curve.Outputs, o => o.Id == legacy);
        var preset = cooling.Presets.Single(p => p.Id == "preset1");

        foreach (var id in newIds)
        {
            Assert.Equal(55, cooling.ManualSpeeds[id]);
            Assert.Equal(45, cooling.CustomManualSpeeds[id]);
            Assert.Equal(5, cooling.FanOffsets[id]);
            Assert.True(cooling.FanLockOverrides[id]);
            Assert.Equal(Nexus.Service.Models.Cooling.FanRoleKind.Gpu, cooling.FanRoles[id]);
            Assert.Equal("curve1", cooling.CustomFanCurveAssignments[id]);
            Assert.Contains(id, cooling.UncontrolledFanChannels);
            Assert.Contains(id, cooling.FanChannelOrder!);
            Assert.Contains(curve.Outputs, o => o.Id == id && o.Type == "fan");
            Assert.Equal("curve1", preset.FanCurveAssignments[id]);
            Assert.Equal(33, preset.ManualSpeeds[id]);
            Assert.Equal(3, preset.FanOffsets[id]);
        }
        // Order preserved: the legacy id's old position now holds the 5 new ids in place.
        Assert.Equal(new[] { "qseries:QTEST123:pump" }.Concat(newIds), cooling.FanChannelOrder!);

        // A second enumeration is a no-op: nothing left to migrate, no duplicate outputs.
        provider.GetFanChannels();
        Assert.Equal(5, store.Load().Cooling.Curves.Single(c => c.Id == "curve1").Outputs.Count);
    }

    private sealed class FakeDiscovery : IQSeriesCoolerPortDiscovery
    {
        private readonly QSeriesCoolerPort[] _ports;
        public FakeDiscovery(params QSeriesCoolerPort[] ports) => _ports = ports;
        public IReadOnlyList<QSeriesCoolerPort> Discover() => _ports;
    }

    // Answers Port-0 queries as already in software mode with turbo on, so a set-fan
    // write never needs to prepend a mode-switch frame the test would have to skip past.
    private sealed class ScriptedTransport : INp50Transport
    {
        public readonly List<byte[]> Writes = new();
        private byte[]? _pending;
        public bool IsOpen => true;
        public string Serial => "QTEST123";

        public void Write(ReadOnlySpan<byte> data)
        {
            Writes.Add(data.ToArray());
            var isPort0 = data.Length >= 4 && data[0] == 0xFF && data[1] == 0xCC && data[2] == 0x01 && data[3] == 0x00;
            _pending = isPort0 ? BuildPort0() : null;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_pending is null) return 0;
            var n = Math.Min(buffer.Length, _pending.Length);
            _pending.AsSpan(0, n).CopyTo(buffer);
            _pending = null;
            return n;
        }

        public void DiscardInput() { }
        public void Dispose() { }

        private static byte[] BuildPort0()
        {
            var p = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
            p[0] = 0xFF; p[1] = 0xCC;
            p[9] = 1; p[10] = 0; // non-zero pump tach so TryParsePort0PumpRpm succeeds
            p[12] = QSeriesCoolerProtocol.ControlModeSoftware;
            p[14] = QSeriesCoolerProtocol.TurboOnByte;
            return p;
        }
    }
}
