using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// Per-segment composition. With the default keeb partition the output must
/// be byte-identical to the legacy writer's per-card FillZone (same
/// brightness rounding, same disabled blackout, same identify flash); with
/// custom partitions the slices land at their segment offsets.
/// </summary>
public class SegmentFrameComposerTests
{
    private const string HubId = "keeb:SER123";

    private static DeviceFrame Frame(string id, int ledCount, byte seed)
    {
        var frame = new DeviceFrame(0, id, ledCount);
        for (int i = 0; i < ledCount; i++)
            frame.SetLed(i, (byte)(seed + i), (byte)(i * 2), (byte)(255 - i));
        return frame;
    }

    private static RgbColor[][] Buffers(DeviceStructure structure)
    {
        var buffers = Array.Empty<RgbColor[]>();
        SegmentFrameComposer.EnsureBuffers(structure, ref buffers);
        return buffers;
    }

    /// <summary>Legacy KeebLightingFrameWriter.FillZone math, kept verbatim as the comparison oracle.</summary>
    private static RgbColor[] LegacyFillZone(DeviceFrame frame, int dstLen, double mul)
    {
        var dst = new RgbColor[dstLen];
        var ledCount = Math.Min(dst.Length, frame.LedCount);
        var src = frame.LedBytes;
        for (var i = 0; i < ledCount; i++)
        {
            var off = i * 3;
            if (off + 2 >= src.Length) { dst[i] = default; continue; }
            if (mul >= 0.999)
                dst[i] = new RgbColor(src[off], src[off + 1], src[off + 2]);
            else if (mul <= 0.0)
                dst[i] = default;
            else
                dst[i] = new RgbColor((byte)(src[off] * mul), (byte)(src[off + 1] * mul), (byte)(src[off + 2] * mul));
        }
        return dst;
    }

    [Fact]
    public void Default_partition_is_byte_identical_to_legacy_writer()
    {
        var structure = KeebZoneSupport.BuildStructure(HubId);
        var zones = ZoneResolution.Resolve(structure, new NexusSettings());
        var keys = Frame(HubId + ":keys", KeebLayout.KeyLedCount, seed: 10);
        var underglow = Frame(HubId + ":underglow", KeebLayout.SurroundLedCount, seed: 90);
        var buffers = Buffers(structure);

        var touched = SegmentFrameComposer.Compose(
            structure, zones, new[] { keys, underglow },
            disabled: new List<string>(),
            uncontrolled: new List<string>(),
            prefs: new Dictionary<string, LightingDevicePreference>(),
            globalBrightness: 1f, masterMul: 1.0, nowTicks: DateTime.UtcNow.Ticks, identify: null, buffers);

        Assert.True(touched[0]);
        Assert.True(touched[1]);
        Assert.Equal(LegacyFillZone(keys, KeebLayout.KeyLedCount, 1.0), buffers[0]);
        Assert.Equal(LegacyFillZone(underglow, KeebLayout.SurroundLedCount, 1.0), buffers[1]);
    }

    [Fact]
    public void Master_brightness_caps_per_zone_software_brightness()
    {
        var structure = KeebZoneSupport.BuildStructure(HubId);
        var zones = ZoneResolution.Resolve(structure, new NexusSettings());
        var keys = Frame(HubId + ":keys", KeebLayout.KeyLedCount, seed: 33);
        var underglow = Frame(HubId + ":underglow", KeebLayout.SurroundLedCount, seed: 7);
        var prefs = new Dictionary<string, LightingDevicePreference>
        {
            [HubId + ":keys"] = new() { Brightness = 37 },
        };
        var buffers = Buffers(structure);

        SegmentFrameComposer.Compose(structure, zones, new[] { keys, underglow },
            new List<string>(), new List<string>(), prefs, globalBrightness: 0.8f, masterMul: 1.0,
            nowTicks: DateTime.UtcNow.Ticks, identify: null, buffers);

        // Master caps rather than scales: keys (37%) stay below the 80% master,
        // underglow (100%) is capped to it.
        var mulKeys = Math.Min(37 / 100.0, 0.8f);
        var mulGlow = Math.Min(100 / 100.0, 0.8f);
        Assert.Equal(LegacyFillZone(keys, KeebLayout.KeyLedCount, mulKeys), buffers[0]);
        Assert.Equal(LegacyFillZone(underglow, KeebLayout.SurroundLedCount, mulGlow), buffers[1]);
    }

    [Fact]
    public void Master_multiplies_the_per_zone_software_brightness()
    {
        var structure = KeebZoneSupport.BuildStructure(HubId);
        var zones = ZoneResolution.Resolve(structure, new NexusSettings());
        var keys = Frame(HubId + ":keys", KeebLayout.KeyLedCount, seed: 60);
        var underglow = Frame(HubId + ":underglow", KeebLayout.SurroundLedCount, seed: 20);
        var prefs = new Dictionary<string, LightingDevicePreference>
        {
            [HubId + ":keys"] = new() { Brightness = 50 },
        };
        var buffers = Buffers(structure);

        // master 0.5 over per-zone keys 50% / underglow default 100%, global 1.0:
        // effective = global * master * zone.
        SegmentFrameComposer.Compose(structure, zones, new[] { keys, underglow },
            new List<string>(), new List<string>(), prefs, globalBrightness: 1f, masterMul: 0.5,
            nowTicks: DateTime.UtcNow.Ticks, identify: null, buffers);

        Assert.Equal(LegacyFillZone(keys, KeebLayout.KeyLedCount, (1f * 50 / 100.0) * 0.5), buffers[0]);
        Assert.Equal(LegacyFillZone(underglow, KeebLayout.SurroundLedCount, (1f * 100 / 100.0) * 0.5), buffers[1]);
    }

    [Fact]
    public void Disabled_zone_goes_black()
    {
        var structure = KeebZoneSupport.BuildStructure(HubId);
        var zones = ZoneResolution.Resolve(structure, new NexusSettings());
        var keys = Frame(HubId + ":keys", KeebLayout.KeyLedCount, seed: 50);
        var buffers = Buffers(structure);

        var touched = SegmentFrameComposer.Compose(structure, zones, new[] { keys },
            new List<string> { HubId + ":keys" },
            new List<string>(),
            new Dictionary<string, LightingDevicePreference>(),
            1f, 1.0, DateTime.UtcNow.Ticks, null, buffers);

        Assert.True(touched[0]);
        // Underglow frame absent: segment untouched, writer skips it.
        Assert.False(touched[1]);
        Assert.All(buffers[0], c => Assert.Equal(default, c));
    }

    [Fact]
    public void Uncontrolled_zone_goes_black()
    {
        var structure = KeebZoneSupport.BuildStructure(HubId);
        var zones = ZoneResolution.Resolve(structure, new NexusSettings());
        var keys = Frame(HubId + ":keys", KeebLayout.KeyLedCount, seed: 50);
        var buffers = Buffers(structure);

        var touched = SegmentFrameComposer.Compose(structure, zones, new[] { keys },
            new List<string>(),
            new List<string> { HubId + ":keys" },
            new Dictionary<string, LightingDevicePreference>(),
            1f, 1.0, DateTime.UtcNow.Ticks, null, buffers);

        Assert.True(touched[0]);
        Assert.All(buffers[0], c => Assert.Equal(default, c));
    }

    [Fact]
    public void Identify_flashes_the_zone_white_on_the_on_phase()
    {
        var structure = KeebZoneSupport.BuildStructure(HubId);
        var zones = ZoneResolution.Resolve(structure, new NexusSettings());
        var keys = Frame(HubId + ":keys", KeebLayout.KeyLedCount, seed: 50);
        var identify = new Np50IdentifyTracker();
        identify.Schedule(HubId + ":keys", durationMs: 5000);
        var buffers = Buffers(structure);

        SegmentFrameComposer.Compose(structure, zones, new[] { keys },
            new List<string>(), new List<string>(), new Dictionary<string, LightingDevicePreference>(),
            1f, 1.0, DateTime.UtcNow.Ticks, identify, buffers);

        // Scheduled just now: the flash starts in its on half-period.
        Assert.All(buffers[0], c => Assert.Equal(new RgbColor(255, 255, 255), c));
    }

    [Fact]
    public void Custom_spanning_zone_lands_slices_at_segment_offsets()
    {
        var structure = KeebZoneSupport.BuildStructure(HubId);
        var settings = new NexusSettings();
        settings.Devices.ZonePartitions[HubId] = new List<ZoneDef>
        {
            new() { Name = "Head", Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 90 } } },
            new()
            {
                Name = "Span",
                Slices =
                {
                    new ZoneSlice { Segment = 0, Start = 90, Count = KeebLayout.KeyLedCount - 90 },
                    new ZoneSlice { Segment = 1, Start = 0, Count = 5 },
                },
            },
            new()
            {
                Name = "Tail",
                Slices = { new ZoneSlice { Segment = 1, Start = 5, Count = KeebLayout.SurroundLedCount - 5 } },
            },
        };
        var zones = ZoneResolution.Resolve(structure, settings);
        Assert.False(zones[0].IsDefault);

        var spanLen = (KeebLayout.KeyLedCount - 90) + 5;
        var span = Frame($"{HubId}:z1", spanLen, seed: 100);
        var buffers = Buffers(structure);

        var touched = SegmentFrameComposer.Compose(structure, zones, new[] { span },
            new List<string>(), new List<string>(), new Dictionary<string, LightingDevicePreference>(),
            1f, 1.0, DateTime.UtcNow.Ticks, null, buffers);

        // The span zone touches both hardware segments.
        Assert.True(touched[0]);
        Assert.True(touched[1]);

        var src = span.LedBytes;
        // Keys segment: zone-local run starts at segment-local offset 90.
        for (int i = 0; i < KeebLayout.KeyLedCount - 90; i++)
        {
            var off = i * 3;
            Assert.Equal(new RgbColor(src[off], src[off + 1], src[off + 2]), buffers[0][90 + i]);
        }
        // Leading keys remain black (their zone has no frame this tick).
        Assert.Equal(default, buffers[0][0]);
        // Underglow segment: remaining zone-local LEDs land at its start.
        for (int i = 0; i < 5; i++)
        {
            var off = (KeebLayout.KeyLedCount - 90 + i) * 3;
            Assert.Equal(new RgbColor(src[off], src[off + 1], src[off + 2]), buffers[1][i]);
        }
        Assert.Equal(default, buffers[1][5]);
    }
}
