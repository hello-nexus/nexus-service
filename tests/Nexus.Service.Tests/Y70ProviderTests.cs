using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Displays;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Effective-orientation logic for the Y70: ForceOrientation always drives the
/// hardware to PortraitFlipped regardless of the stored preference, the
/// stored preference applies unchanged when the flag is off, and SetOrientation
/// / SetForceOrientation persist only (the caller applies once via
/// ApplyEffectiveOrientation, so a combined update never double-writes).
/// </summary>
public class Y70ProviderTests
{
    private static Y70Provider Build(InMemoryY70ConfigStore store, FakeY70OrientationProvider orientation)
        => new(
            new Y70DisplayHub(
                new StubY70DisplayPortDiscovery(),
                _ => throw new InvalidOperationException("Y70 transport is not expected in these tests")),
            store,
            orientation,
            new StubDisplayBrightnessProvider());

    [Fact]
    public void SetOrientation_persists_without_applying_to_hardware()
    {
        var store = new InMemoryY70ConfigStore();
        var orientation = new FakeY70OrientationProvider();
        var provider = Build(store, orientation);

        provider.SetOrientation("Landscape");

        Assert.Equal("Landscape", provider.GetOrientation());
        Assert.Null(orientation.LastApplied);
    }

    [Fact]
    public void SetForceOrientation_persists_without_applying_to_hardware()
    {
        var store = new InMemoryY70ConfigStore();
        var orientation = new FakeY70OrientationProvider();
        var provider = Build(store, orientation);

        provider.SetForceOrientation(true);

        Assert.True(provider.GetForceOrientation());
        Assert.Null(orientation.LastApplied);
    }

    [Fact]
    public void ApplyEffectiveOrientation_uses_stored_preference_when_force_is_off()
    {
        var store = new InMemoryY70ConfigStore();
        var orientation = new FakeY70OrientationProvider();
        var provider = Build(store, orientation);

        provider.SetOrientation("Landscape");
        provider.SetForceOrientation(false);
        provider.ApplyEffectiveOrientation();

        Assert.Equal("Landscape", orientation.LastApplied);
    }

    [Fact]
    public void ApplyEffectiveOrientation_forces_PortraitFlipped_when_force_is_on()
    {
        var store = new InMemoryY70ConfigStore();
        var orientation = new FakeY70OrientationProvider();
        var provider = Build(store, orientation);

        provider.SetOrientation("Landscape");
        provider.SetForceOrientation(true);
        provider.ApplyEffectiveOrientation();

        Assert.Equal("Landscape", provider.GetOrientation());
        Assert.Equal("PortraitFlipped", orientation.LastApplied);
    }

    [Fact]
    public void ApplyEffectiveOrientation_reflects_current_force_flag()
    {
        var store = new InMemoryY70ConfigStore();
        store.Load().Y70.Orientation = "Portrait";
        store.Load().Y70.ForceOrientation = true;
        var orientation = new FakeY70OrientationProvider();
        var provider = Build(store, orientation);

        provider.ApplyEffectiveOrientation();

        Assert.Equal("PortraitFlipped", orientation.LastApplied);
    }

    [Fact]
    public void Combined_update_applies_exactly_once()
    {
        var store = new InMemoryY70ConfigStore();
        var orientation = new FakeY70OrientationProvider();
        var provider = Build(store, orientation);

        // Mirrors the POST /y70/rotation route: persist whichever fields are
        // present, then apply once.
        provider.SetOrientation("Landscape");
        provider.SetForceOrientation(false);
        provider.ApplyEffectiveOrientation();

        Assert.Equal(1, orientation.ApplyCount);
        Assert.Equal("Landscape", orientation.LastApplied);
    }

    // ── Brightness/power transport routing ──
    // A Truly exposes the same CDC port as the serial models (the shared
    // FF DD version query answers, so the hub connects) but takes display
    // control only via DDC/CI; a connected hub must not select serial for it.

    private static (Y70Provider Provider, FakeNp50Transport Serial, FakeDdcBrightnessProvider Ddc, InMemoryY70ConfigStore Store)
        BuildConnected(string variant)
    {
        var (provider, serial, ddc, store, _, _) = BuildConnectedWithHub(variant);
        return (provider, serial, ddc, store);
    }

    private static (Y70Provider Provider, FakeNp50Transport Serial, FakeDdcBrightnessProvider Ddc, InMemoryY70ConfigStore Store, Y70DisplayHub Hub, FakeY70PortDiscovery Discovery)
        BuildConnectedWithHub(string variant)
    {
        var transport = new FakeNp50Transport();
        var discovery = new FakeY70PortDiscovery(variant);
        var hub = new Y70DisplayHub(discovery, _ => transport);
        Assert.True(hub.EnsureConnected());
        var ddc = new FakeDdcBrightnessProvider();
        var store = new InMemoryY70ConfigStore();
        var provider = new Y70Provider(hub, store, new FakeY70OrientationProvider(), ddc);
        return (provider, transport, ddc, store, hub, discovery);
    }

    [Fact]
    public void SetBrightness_on_serial_variant_writes_serial_frame_only()
    {
        var (provider, serial, ddc, _) = BuildConnected(Y70DisplayProtocol.VariantInfinite);

        provider.SetBrightness(50);

        var frame = Assert.Single(serial.Writes);
        Assert.Equal(Y70DisplayProtocol.BuildSetBrightnessPower(true, 50), frame);
        Assert.Empty(ddc.VcpWrites);
    }

    [Fact]
    public void SetBrightness_on_truly_uses_ddc_even_with_serial_connected()
    {
        var (provider, serial, ddc, _) = BuildConnected(Y70DisplayProtocol.VariantTruly);

        provider.SetBrightness(50);

        Assert.Empty(serial.Writes);
        // Reference Y70TouchDdcciDeviceBase writes brightness alone - no
        // power write rides along with a brightness change.
        var write = Assert.Single(ddc.VcpWrites);
        Assert.Equal((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpBrightness, 50), write);
    }

    [Fact]
    public void SetToggle_off_on_truly_writes_standby_and_skips_brightness()
    {
        var (provider, serial, ddc, _) = BuildConnected(Y70DisplayProtocol.VariantTruly);

        provider.SetToggle(true);

        Assert.Empty(serial.Writes);
        var write = Assert.Single(ddc.VcpWrites);
        Assert.Equal((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpPower, Y70DisplayProtocol.VcpPowerStandby), write);
        Assert.True(provider.GetToggle());
    }

    [Fact]
    public void SetToggle_on_on_truly_writes_power_on_only()
    {
        var (provider, _, ddc, store) = BuildConnected(Y70DisplayProtocol.VariantTruly);
        store.Load().Y70.ScreenOff = true;

        provider.SetToggle(false);

        var write = Assert.Single(ddc.VcpWrites);
        Assert.Equal((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpPower, Y70DisplayProtocol.VcpPowerOn), write);
        Assert.False(provider.GetToggle());
    }

    [Fact]
    public void SetBrightness_on_truly_writes_raw_percent_without_serial_floor()
    {
        var (provider, _, ddc, _) = BuildConnected(Y70DisplayProtocol.VariantTruly);

        provider.SetBrightness(5);

        var write = Assert.Single(ddc.VcpWrites);
        Assert.Equal((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpBrightness, 5), write);
        Assert.Equal(5, provider.GetBrightness());
    }

    [Fact]
    public void SetBrightness_on_serial_variant_floors_to_firmware_minimum()
    {
        var (provider, serial, _, _) = BuildConnected(Y70DisplayProtocol.VariantInfinite);

        provider.SetBrightness(5);

        var frame = Assert.Single(serial.Writes);
        Assert.Equal(Y70DisplayProtocol.BuildSetBrightnessPower(true, Y70DisplayProtocol.MinBrightnessOnPercent), frame);
        Assert.Equal(5, provider.GetBrightness());
    }

    [Fact]
    public void Rapid_truly_brightness_sets_coalesce_to_latest_value()
    {
        var (provider, _, ddc, _) = BuildConnected(Y70DisplayProtocol.VariantTruly);

        provider.SetBrightness(30);
        provider.SetBrightness(60);

        // First write is immediate; the in-window second collapses to a
        // trailing write of the latest value (reference debounce, 100ms).
        Assert.Equal((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpBrightness, 30), ddc.VcpWrites[0]);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ddc.VcpWrites.Count < 2 && DateTime.UtcNow < deadline)
        {
            System.Threading.Thread.Sleep(10);
        }
        Assert.Equal(2, ddc.VcpWrites.Count);
        Assert.Equal((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpBrightness, 60), ddc.VcpWrites[1]);
        Assert.Equal(60, provider.GetBrightness());
    }

    [Fact]
    public void SetBrightness_on_truly_without_ddc_display_persists_store_only()
    {
        var (provider, serial, ddc, store) = BuildConnected(Y70DisplayProtocol.VariantTruly);
        ddc.DisplayId = null;

        provider.SetBrightness(70);

        Assert.Empty(serial.Writes);
        Assert.Empty(ddc.VcpWrites);
        Assert.Equal(70, store.Load().Y70.Brightness);
    }

    // ── Original Touch (0x0C00): hybrid serial-PWM + DDC RGB-gain path ──

    /// <summary>FF CC 02 reply: header echo, screen-on flag at [4], brightness at [5].</summary>
    private static byte[] ScreenInfoReply(bool screenOn, int brightness) =>
        new byte[] { 0xFF, 0xCC, 0x00, 0x00, (byte)(screenOn ? 1 : 0), (byte)brightness };

    [Fact]
    public void SetBrightness_on_touch_preps_then_writes_rgb_gains()
    {
        var (provider, serial, ddc, _) = BuildConnected(Y70DisplayProtocol.VariantTouch);
        serial.NextReadResponse = ScreenInfoReply(screenOn: true, brightness: 50);

        provider.SetBrightness(50);

        // Prep: UserDefine3 unlock, then the PWM pin (screen on below full).
        var gain = Y70DisplayProtocol.TouchRgbGainForPercent(50);
        Assert.Equal(new[]
        {
            (FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpColorPresetMode, Y70DisplayProtocol.VcpColorPresetUserDefine3),
            (FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpVideoGainRed, gain),
            (FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpVideoGainGreen, gain),
            (FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpVideoGainBlue, gain),
        }, ddc.VcpWrites);
        // Serial saw the screen-info request plus exactly one control frame:
        // the PWM pin at 100 - never a dimming FF CC 01.
        Assert.Contains(Y70DisplayProtocol.BuildGetScreenInfo(), serial.Writes);
        var controlFrames = serial.Writes.Where(w => w.Length >= 3 && w[2] == 0x01).ToList();
        var pin = Assert.Single(controlFrames);
        Assert.Equal(Y70DisplayProtocol.BuildSetBrightnessPower(true, 100), pin);
    }

    [Fact]
    public void Touch_prep_skips_pwm_pin_when_screen_reads_off()
    {
        var (provider, serial, ddc, _) = BuildConnected(Y70DisplayProtocol.VariantTouch);
        serial.NextReadResponse = ScreenInfoReply(screenOn: false, brightness: 0);

        provider.SetBrightness(40);

        Assert.DoesNotContain(serial.Writes, w => w.Length >= 3 && w[2] == 0x01);
        Assert.Contains((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpVideoGainRed, Y70DisplayProtocol.TouchRgbGainForPercent(40)), ddc.VcpWrites);
    }

    [Fact]
    public void Touch_prep_runs_once_per_connection()
    {
        var (provider, _, ddc, _) = BuildConnected(Y70DisplayProtocol.VariantTouch);

        provider.SetBrightness(30);
        provider.SetBrightness(60);

        // Second set is in-window and lands via the trailing flush.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline
               && !ddc.VcpWrites.Contains((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpVideoGainBlue, Y70DisplayProtocol.TouchRgbGainForPercent(60))))
        {
            System.Threading.Thread.Sleep(10);
        }
        Assert.Equal(1, ddc.VcpWrites.Count(w => w.Code == Y70DisplayProtocol.VcpColorPresetMode));
        Assert.Contains((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpVideoGainBlue, Y70DisplayProtocol.TouchRgbGainForPercent(60)), ddc.VcpWrites);
    }

    [Fact]
    public void SetToggle_off_on_touch_sends_serial_zero_and_no_ddc_power()
    {
        var (provider, serial, ddc, _) = BuildConnected(Y70DisplayProtocol.VariantTouch);

        provider.SetToggle(true);

        var frame = Assert.Single(serial.Writes);
        Assert.Equal(Y70DisplayProtocol.BuildSetBrightnessPower(false, 0), frame);
        Assert.DoesNotContain(ddc.VcpWrites, w => w.Code == Y70DisplayProtocol.VcpPower);
        Assert.True(provider.GetToggle());
    }

    [Fact]
    public void SetToggle_on_on_touch_pins_pwm_and_restores_floored_gains()
    {
        var (provider, serial, ddc, store) = BuildConnected(Y70DisplayProtocol.VariantTouch);
        store.Load().Y70.ScreenOff = true;
        store.Load().Y70.Brightness = 5;

        provider.SetToggle(false);

        // Reference restore: PWM back to 100, gains at the floored percent.
        var controlFrames = serial.Writes.Where(w => w.Length >= 3 && w[2] == 0x01).ToList();
        Assert.Contains(Y70DisplayProtocol.BuildSetBrightnessPower(true, 100), controlFrames);
        var gain = Y70DisplayProtocol.TouchRgbGainForPercent(Y70DisplayProtocol.MinBrightnessOnPercent);
        Assert.Contains((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpVideoGainRed, gain), ddc.VcpWrites);
        Assert.DoesNotContain(ddc.VcpWrites, w => w.Code == Y70DisplayProtocol.VcpPower);
        // Stored preference survives the floored hardware restore.
        Assert.Equal(5, store.Load().Y70.Brightness);
        Assert.False(provider.GetToggle());
    }

    [Fact]
    public void Touch_brightness_with_serial_down_writes_gains_never_vcp_brightness()
    {
        var (provider, _, ddc, _, hub, discovery) = BuildConnectedWithHub(Y70DisplayProtocol.VariantTouch);
        discovery.PortAvailable = false;
        hub.Disconnect();

        provider.SetBrightness(50);

        // A raw VCP 0x10 write on a Touch lowers the scaler luminance
        // underneath the gain path and nothing restores it; the register-set
        // choice must key on the identified variant, not the link state.
        Assert.DoesNotContain(ddc.VcpWrites, w => w.Code == Y70DisplayProtocol.VcpBrightness);
        Assert.Contains((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpVideoGainRed, Y70DisplayProtocol.TouchRgbGainForPercent(50)), ddc.VcpWrites);
    }

    [Fact]
    public void Touch_toggle_with_serial_down_falls_back_to_ddc_power_and_wakes_after_reconnect()
    {
        var (provider, _, ddc, _, hub, discovery) = BuildConnectedWithHub(Y70DisplayProtocol.VariantTouch);
        discovery.PortAvailable = false;
        hub.Disconnect();

        provider.SetToggle(true);

        Assert.Contains((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpPower, Y70DisplayProtocol.VcpPowerStandby), ddc.VcpWrites);

        // Link back: the serial power-on cannot wake the monitor from the
        // DDC standby the fallback set, so an explicit DDC On must follow.
        discovery.PortAvailable = true;
        provider.SetToggle(false);

        Assert.Contains((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpPower, Y70DisplayProtocol.VcpPowerOn), ddc.VcpWrites);
        Assert.False(provider.GetToggle());
    }

    [Fact]
    public void Touch_preset_retries_after_failed_vcp_write()
    {
        var (provider, _, ddc, _) = BuildConnected(Y70DisplayProtocol.VariantTouch);
        ddc.FailVcpWrites = true;

        provider.SetBrightness(30);

        Assert.Equal(1, ddc.VcpWrites.Count(w => w.Code == Y70DisplayProtocol.VcpColorPresetMode));

        // SetVcp reports failure as false (helper proxy), so the preset must
        // not latch; the in-window follow-up's trailing flush retries it.
        ddc.FailVcpWrites = false;
        provider.SetBrightness(60);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline
               && ddc.VcpWrites.Count(w => w.Code == Y70DisplayProtocol.VcpColorPresetMode) < 2)
        {
            System.Threading.Thread.Sleep(10);
        }
        Assert.Equal(2, ddc.VcpWrites.Count(w => w.Code == Y70DisplayProtocol.VcpColorPresetMode));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(20, 18)]
    [InlineData(50, 30)]
    [InlineData(100, 50)]
    public void TouchRgbGain_matches_reference_mapping(int percent, int expectedGain)
    {
        Assert.Equal(expectedGain, Y70DisplayProtocol.TouchRgbGainForPercent(percent));
    }

    private sealed class FakeY70PortDiscovery : IY70DisplayPortDiscovery
    {
        private readonly string _variant;
        /// <summary>False simulates the serial link staying down (unplugged CDC port).</summary>
        public bool PortAvailable = true;
        public FakeY70PortDiscovery(string variant) => _variant = variant;
        public IReadOnlyList<Y70DisplayPort> Discover() => PortAvailable
            ? new[] { new Y70DisplayPort { PortName = "COM9", Serial = "TESTSER", Variant = _variant } }
            : Array.Empty<Y70DisplayPort>();
    }

    private sealed class FakeNp50Transport : INp50Transport
    {
        public readonly List<byte[]> Writes = new();
        /// <summary>Served once by the next Read (the Touch prep's FF CC 02 reply), then cleared.</summary>
        public byte[]? NextReadResponse;
        public bool IsOpen => true;
        public string Serial => "TESTSER";
        public void Write(ReadOnlySpan<byte> data) => Writes.Add(data.ToArray());
        public void DiscardInput() { }
        public int Read(Span<byte> buffer, int timeoutMs)
        {
            var response = NextReadResponse;
            NextReadResponse = null;
            if (response is null) return 0;
            var n = Math.Min(buffer.Length, response.Length);
            response.AsSpan(0, n).CopyTo(buffer);
            return n;
        }
        public void Dispose() { }
    }

    private sealed class FakeDdcBrightnessProvider : IDisplayBrightnessProvider
    {
        public const string Id = "display-rtk409a";
        public string? DisplayId = Id;
        /// <summary>True makes SetVcp record the attempt but return false (helper-proxy failure shape).</summary>
        public bool FailVcpWrites;
        // The provider's trailing coalesced write lands from a timer thread;
        // snapshot under a lock so test asserts never race an Add.
        private readonly object _lock = new();
        private readonly List<(string Id, byte Code, int Value)> _vcpWrites = new();

        public IReadOnlyList<(string Id, byte Code, int Value)> VcpWrites
        {
            get { lock (_lock) return _vcpWrites.ToArray(); }
        }

        public string Hint => "";
        public IReadOnlyList<DisplayDto> Enumerate() => Array.Empty<DisplayDto>();
        public int? GetBrightness(string id) => null;
        public DisplayBrightnessDto SetBrightness(string id, int percent) => new() { Id = id };
        public DisplayBrightnessWritePolicy GetBrightnessWritePolicy(string id) => new();
        public DisplayVcpDto? GetVcp(string id, byte code) => null;
        public bool SetVcp(string id, byte code, int value)
        {
            lock (_lock) _vcpWrites.Add((id, code, value));
            return !FailVcpWrites;
        }
        public string? FindDisplayIdByHardwareName(IReadOnlyList<string> nameFragments) => DisplayId;
    }

    private sealed class FakeY70OrientationProvider : IDisplayOrientationProvider
    {
        public string? LastApplied;
        public int ApplyCount;

        public (bool Ok, string Error) SetY70Orientation(string orientation)
        {
            LastApplied = orientation;
            ApplyCount++;
            return (true, "");
        }

        public (bool Ok, string Error) SetDisplayOrientation(string displayId, string orientation, string coverColorHex) => (true, "");
    }

    /// <summary>In-memory IConfigStore for unit tests - no disk I/O.</summary>
    private sealed class InMemoryY70ConfigStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();

        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }
}
