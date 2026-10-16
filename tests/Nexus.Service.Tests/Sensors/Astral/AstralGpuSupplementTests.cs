using System.Text.Json;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors.Astral;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Sensors.Astral;

public class AstralGpuSupplementTests
{
    private const string HwIdentifier = "/gpu-nvidia/0";
    private const string HwName = "NVIDIA GeForce RTX 5080";

    private sealed class FakeClient : IAstralNvApiClient
    {
        public uint SubSystemId;
        public bool HasSubSystemId = true;
        public bool ReadSucceeds;
        public byte[] Block = ValidBlock();
        public int SubsystemCalls;
        public int ReadCalls;
        public bool GSensorSucceeds;
        public byte[] GSensorBlock = GBlock(0, 0, 4096);
        public int GSensorReadCalls;

        // Every word = 12000 -> each pin reads 12.0 V / 12.0 A, so it passes
        // AstralTelemetryParser's 12V-rail plausibility band.
        private static byte[] ValidBlock()
        {
            var block = new byte[AstralTelemetryParser.BlockSize];
            for (var i = 0; i < AstralTelemetryParser.BlockSize; i += 2)
            {
                block[i] = 0x2E;
                block[i + 1] = 0xE0;
            }
            return block;
        }

        public bool TryGetPciSubsystemId(int adapterIndex, out uint subSystemId)
        {
            SubsystemCalls++;
            subSystemId = SubSystemId;
            return HasSubSystemId;
        }

        public bool TryReadAstralBlock(int adapterIndex, out byte[] block)
        {
            ReadCalls++;
            block = ReadSucceeds ? Block : Array.Empty<byte>();
            return ReadSucceeds;
        }

        public bool TryReadGSensorBlock(int adapterIndex, out byte[] block)
        {
            GSensorReadCalls++;
            block = GSensorSucceeds ? GSensorBlock : Array.Empty<byte>();
            return GSensorSucceeds;
        }
    }

    // Big-endian X, Y, Z words plus the status byte, as registers 0x70-0x76 hold them.
    private static byte[] GBlock(short x, short y, short z) =>
        new[] { (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, (byte)(z >> 8), (byte)z, (byte)0 };

    private const uint AstralSubSystemId = 0x89E31043;
    private static readonly string LevelSenseKey = AstralGpuSupplement.LevelSenseKey(0, AstralSubSystemId);

    private static List<HardwareSensor> Poll(AstralGpuSupplement supplement, ManualTimeProvider time)
    {
        var result = new List<HardwareSensor>();
        supplement.AppendSensors(HwIdentifier, HwIdentifier, HwName, result);
        time.Advance(TimeSpan.FromSeconds(1));
        return result;
    }

    private static HardwareSensor? LevelX(List<HardwareSensor> result) => result.Find(s => s.Name == "GPU Level Sense X");
    private static HardwareSensor? LevelY(List<HardwareSensor> result) => result.Find(s => s.Name == "GPU Level Sense Y");
    private static HardwareSensor? Sag(List<HardwareSensor> result) => result.Find(s => s.Name == "GPU Level Sense Sag");
    private static HardwareSensor? Lean(List<HardwareSensor> result) => result.Find(s => s.Name == "GPU Level Sense Lean");

    [Fact]
    public void AppendSensors_calibrates_level_sense_on_the_first_readings_and_saves_it()
    {
        var client = new FakeClient { SubSystemId = AstralSubSystemId, ReadSucceeds = true, GSensorSucceeds = true };
        var time = new ManualTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
        var store = new InMemoryConfigStore();
        var supplement = new AstralGpuSupplement(client, time, TimeSpan.FromSeconds(1), store);

        for (var i = 1; i < AstralLevelSense.CalibrationSamples; i++)
        {
            Assert.Null(LevelX(Poll(supplement, time)));
        }
        Assert.Empty(store.Load().AstralLevelSense);

        var calibratedAt = time.GetUtcNow().ToUnixTimeMilliseconds();
        var result = Poll(supplement, time);

        Assert.Equal(0f, LevelX(result)!.Value, 0.0001f);
        Assert.Equal(0f, LevelY(result)!.Value, 0.0001f);
        Assert.Equal("Angle", LevelX(result)!.Type);
        var saved = store.Load().AstralLevelSense[LevelSenseKey];
        Assert.Equal(0f, saved.X, 0.0001f);
        Assert.Equal(0f, saved.Y, 0.0001f);
        Assert.Equal(0f, saved.Z!.Value, 0.0001f);
        Assert.Equal(calibratedAt, saved.CalibratedAtMs);
    }

    [Fact]
    public void AppendSensors_measures_level_against_a_saved_calibration()
    {
        // About 2 degrees of tilt on Y (143 / 4096 = sin 2 degrees).
        var client = new FakeClient
        {
            SubSystemId = AstralSubSystemId, ReadSucceeds = true, GSensorSucceeds = true, GSensorBlock = GBlock(0, 143, 4094),
        };
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryConfigStore();
        store.Update(s => s.AstralLevelSense[LevelSenseKey] = new AstralLevelSenseCalibration { X = 0f, Y = 0f, Z = 0f, CalibratedAtMs = 42 });
        var supplement = new AstralGpuSupplement(client, time, TimeSpan.FromSeconds(1), store);

        var result = Poll(supplement, time);

        Assert.Equal(0f, LevelX(result)!.Value, 0.001f);
        Assert.Equal(2f, LevelY(result)!.Value, 0.001f);
        Assert.Equal(2f, Sag(result)!.Value, 0.001f);
        Assert.Equal(0f, Lean(result)!.Value, 0.001f);
        Assert.Equal(42, store.Load().AstralLevelSense[LevelSenseKey].CalibratedAtMs);
    }

    [Fact]
    public void AppendSensors_takes_again_a_calibration_saved_without_z()
    {
        var client = new FakeClient { SubSystemId = AstralSubSystemId, ReadSucceeds = true, GSensorSucceeds = true };
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryConfigStore();
        store.Update(s => s.AstralLevelSense[LevelSenseKey] = new AstralLevelSenseCalibration { X = 0f, Y = 0f, CalibratedAtMs = 42 });
        var supplement = new AstralGpuSupplement(client, time, TimeSpan.FromSeconds(1), store);

        for (var i = 1; i < AstralLevelSense.CalibrationSamples; i++)
        {
            Assert.Null(LevelX(Poll(supplement, time)));
        }
        Assert.NotNull(Sag(Poll(supplement, time)));
        Assert.NotNull(store.Load().AstralLevelSense[LevelSenseKey].Z);
    }

    [Fact]
    public void AppendSensors_ignores_a_calibration_saved_for_another_card()
    {
        var client = new FakeClient { SubSystemId = AstralSubSystemId, ReadSucceeds = true, GSensorSucceeds = true };
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryConfigStore();
        store.Update(s => s.AstralLevelSense[AstralGpuSupplement.LevelSenseKey(0, 0x89DE1043)] = new AstralLevelSenseCalibration());
        var supplement = new AstralGpuSupplement(client, time, TimeSpan.FromSeconds(1), store);

        Assert.Null(LevelX(Poll(supplement, time)));
    }

    [Fact]
    public void AppendSensors_reads_no_g_sensor_until_the_card_is_confirmed_as_an_astral()
    {
        var client = new FakeClient { SubSystemId = AstralSubSystemId, ReadSucceeds = false, GSensorSucceeds = true };
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var supplement = new AstralGpuSupplement(client, time, TimeSpan.FromSeconds(1), new InMemoryConfigStore());

        for (var i = 0; i < AstralLevelSense.CalibrationSamples; i++)
        {
            Poll(supplement, time);
        }

        Assert.Equal(0, client.GSensorReadCalls);
    }

    [Fact]
    public void AppendSensors_stops_asking_a_card_whose_g_sensor_never_answers()
    {
        var client = new FakeClient { SubSystemId = AstralSubSystemId, ReadSucceeds = true, GSensorSucceeds = false };
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var supplement = new AstralGpuSupplement(client, time, TimeSpan.FromSeconds(1), new InMemoryConfigStore());

        for (var i = 0; i < 15; i++)
        {
            Poll(supplement, time);
        }

        Assert.Equal(10, client.GSensorReadCalls);
        Assert.Equal(15, client.ReadCalls);
    }

    [Fact]
    public void RecalibrateLevelSense_keeps_the_old_level_until_the_new_one_is_saved()
    {
        var client = new FakeClient { SubSystemId = AstralSubSystemId, ReadSucceeds = true, GSensorSucceeds = true };
        var time = new ManualTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
        var store = new InMemoryConfigStore();
        var supplement = new AstralGpuSupplement(client, time, TimeSpan.FromSeconds(1), store);
        for (var i = 0; i < AstralLevelSense.CalibrationSamples; i++)
        {
            Poll(supplement, time);
        }
        var firstCalibration = store.Load().AstralLevelSense[LevelSenseKey].CalibratedAtMs;

        Assert.Equal(new[] { HwIdentifier }, supplement.RecalibrateLevelSense(HwIdentifier));

        for (var i = 1; i < AstralLevelSense.CalibrationSamples; i++)
        {
            Assert.NotNull(LevelX(Poll(supplement, time)));
        }
        Assert.Equal(firstCalibration, store.Load().AstralLevelSense[LevelSenseKey].CalibratedAtMs);

        Poll(supplement, time);
        Assert.True(store.Load().AstralLevelSense[LevelSenseKey].CalibratedAtMs > firstCalibration);
    }

    [Fact]
    public void Saved_calibrations_survive_the_settings_round_trip()
    {
        // The store serializes through PersistenceJsonContext, where a source-gen
        // gap would write the section back empty without erroring.
        var settings = new NexusSettings();
        settings.AstralLevelSense[LevelSenseKey] = new AstralLevelSenseCalibration { X = 88.54f, Y = 1.2f, Z = 90.76f, CalibratedAtMs = 1_760_000_000_000 };

        var json = JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings);
        var back = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings)!;

        var saved = Assert.Contains(LevelSenseKey, back.AstralLevelSense);
        Assert.Equal(88.54f, saved.X);
        Assert.Equal(1.2f, saved.Y);
        Assert.Equal(90.76f, saved.Z);
        Assert.Equal(1_760_000_000_000, saved.CalibratedAtMs);
    }

    [Fact]
    public void GetLevelSenseStates_shows_a_calibration_in_progress_then_when_it_was_saved()
    {
        var client = new FakeClient { SubSystemId = AstralSubSystemId, ReadSucceeds = true, GSensorSucceeds = true };
        var time = new ManualTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
        var store = new InMemoryConfigStore();
        var supplement = new AstralGpuSupplement(client, time, TimeSpan.FromSeconds(1), store);
        Assert.Empty(supplement.GetLevelSenseStates());

        Poll(supplement, time);
        var during = Assert.Single(supplement.GetLevelSenseStates());
        Assert.Equal(HwIdentifier, during.GpuId);
        Assert.True(during.Calibrating);
        Assert.Null(during.CalibratedAtMs);

        for (var i = 1; i < AstralLevelSense.CalibrationSamples; i++)
        {
            Poll(supplement, time);
        }
        var after = Assert.Single(supplement.GetLevelSenseStates());
        Assert.False(after.Calibrating);
        Assert.Equal(store.Load().AstralLevelSense[LevelSenseKey].CalibratedAtMs, after.CalibratedAtMs);
    }

    [Fact]
    public void ReadPinCurrents_knows_a_card_only_after_the_gpus_were_read()
    {
        var client = new FakeClient { SubSystemId = AstralSubSystemId, ReadSucceeds = true };
        var time = new ManualTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
        var supplement = new AstralGpuSupplement(client, time, TimeSpan.FromSeconds(1));
        Assert.Empty(supplement.ReadPinCurrents());

        var readAt = time.GetUtcNow().ToUnixTimeMilliseconds();
        Poll(supplement, time);
        var card = Assert.Single(supplement.ReadPinCurrents());

        Assert.Equal(HwIdentifier, card.GpuId);
        Assert.Contains("Astral", card.Name);
        Assert.Equal(new[] { 12f, 12f, 12f, 12f, 12f, 12f }, card.PinAmps);
        Assert.Equal(time.GetUtcNow().ToUnixTimeMilliseconds(), card.ReadAtMs);
        Assert.Equal(2, client.ReadCalls);
        Assert.True(card.ReadAtMs > readAt);
    }

    [Fact]
    public void ReadPinCurrents_shares_the_probe_interval_with_the_gpu_list()
    {
        var client = new FakeClient { SubSystemId = AstralSubSystemId, ReadSucceeds = true };
        var time = new ManualTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
        var supplement = new AstralGpuSupplement(client, time, TimeSpan.FromSeconds(1));
        supplement.AppendSensors(HwIdentifier, HwIdentifier, HwName, new List<HardwareSensor>());

        supplement.ReadPinCurrents();
        Assert.Equal(1, client.ReadCalls);

        time.Advance(TimeSpan.FromSeconds(1));
        var card = Assert.Single(supplement.ReadPinCurrents());
        Assert.Equal(2, client.ReadCalls);
        Assert.Equal(time.GetUtcNow().ToUnixTimeMilliseconds(), card.ReadAtMs);

        supplement.AppendSensors(HwIdentifier, HwIdentifier, HwName, new List<HardwareSensor>());
        Assert.Equal(2, client.ReadCalls);
    }

    [Fact]
    public void ReadPinCurrents_keeps_the_time_of_the_last_good_reading()
    {
        var client = new FakeClient { SubSystemId = AstralSubSystemId, ReadSucceeds = true };
        var time = new ManualTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
        var supplement = new AstralGpuSupplement(client, time, TimeSpan.FromSeconds(1));
        supplement.AppendSensors(HwIdentifier, HwIdentifier, HwName, new List<HardwareSensor>());
        var goodAt = time.GetUtcNow().ToUnixTimeMilliseconds();

        client.ReadSucceeds = false;
        time.Advance(TimeSpan.FromSeconds(5));
        var card = Assert.Single(supplement.ReadPinCurrents());

        Assert.Equal(2, client.ReadCalls);
        Assert.Equal(goodAt, card.ReadAtMs);
    }

    [Fact]
    public void GetLevelSenseStates_carries_the_same_reading_as_the_sensors_once_calibrated()
    {
        var client = new FakeClient { SubSystemId = AstralSubSystemId, ReadSucceeds = true, GSensorSucceeds = true };
        var time = new ManualTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
        var supplement = new AstralGpuSupplement(client, time, TimeSpan.FromSeconds(1), new InMemoryConfigStore());

        Poll(supplement, time);
        var during = Assert.Single(supplement.GetLevelSenseStates());
        Assert.Null(during.Sag);
        Assert.Null(during.Lean);
        Assert.Null(during.X);
        Assert.Null(during.Y);

        for (var i = 1; i < AstralLevelSense.CalibrationSamples; i++)
        {
            Poll(supplement, time);
        }
        client.GSensorBlock = GBlock(40, -30, 4090);
        var sensors = Poll(supplement, time);

        var reading = Assert.Single(supplement.GetLevelSenseStates());
        Assert.Equal((float?)Sag(sensors)!.Value, reading.Sag);
        Assert.Equal((float?)Lean(sensors)!.Value, reading.Lean);
        Assert.Equal((float?)LevelX(sensors)!.Value, reading.X);
        Assert.Equal((float?)LevelY(sensors)!.Value, reading.Y);
    }

    [Fact]
    public void RecalibrateLevelSense_skips_a_gpu_without_a_g_sensor()
    {
        var client = new FakeClient { SubSystemId = AstralSubSystemId, ReadSucceeds = true, GSensorSucceeds = false };
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var supplement = new AstralGpuSupplement(client, time, TimeSpan.FromSeconds(1), new InMemoryConfigStore());
        Poll(supplement, time);

        Assert.Empty(supplement.RecalibrateLevelSense(null));
        Assert.Empty(supplement.RecalibrateLevelSense(HwIdentifier));
    }

    [Fact]
    public void AppendSensors_probes_and_emits_sensors_for_asus_vendor()
    {
        var client = new FakeClient { SubSystemId = 0x89DE1043, ReadSucceeds = true };
        var supplement = new AstralGpuSupplement(client, TimeProvider.System, TimeSpan.FromSeconds(1));
        var result = new List<HardwareSensor>();

        supplement.AppendSensors(HwIdentifier, HwIdentifier, HwName, result);

        Assert.Equal(1, client.ReadCalls);
        Assert.Contains(result, s => s.Name == "GPU 12VHPWR Pin 1 Voltage");
        Assert.Contains(result, s => s.Name == "GPU 12VHPWR Connector Power");
    }

    [Fact]
    public void AppendSensors_never_probes_a_non_asus_vendor()
    {
        var client = new FakeClient { SubSystemId = 0x12341462 }; // MSI
        var supplement = new AstralGpuSupplement(client, TimeProvider.System, TimeSpan.FromSeconds(1));
        var result = new List<HardwareSensor>();

        supplement.AppendSensors(HwIdentifier, HwIdentifier, HwName, result);

        Assert.Equal(0, client.ReadCalls);
        Assert.Empty(result);
    }

    [Fact]
    public void AppendSensors_emits_nothing_when_the_probe_fails()
    {
        var client = new FakeClient { SubSystemId = 0x89DE1043, ReadSucceeds = false };
        var supplement = new AstralGpuSupplement(client, TimeProvider.System, TimeSpan.FromSeconds(1));
        var result = new List<HardwareSensor>();

        supplement.AppendSensors(HwIdentifier, HwIdentifier, HwName, result);

        Assert.Equal(1, client.ReadCalls);
        Assert.Empty(result);
    }

    [Fact]
    public void AppendSensors_skips_the_gpu_when_the_subsystem_id_cannot_be_read()
    {
        var client = new FakeClient { HasSubSystemId = false };
        var supplement = new AstralGpuSupplement(client, TimeProvider.System, TimeSpan.FromSeconds(1));
        var result = new List<HardwareSensor>();

        supplement.AppendSensors(HwIdentifier, HwIdentifier, HwName, result);

        Assert.Equal(0, client.ReadCalls);
        Assert.Empty(result);
    }

    [Fact]
    public void AppendSensors_respects_the_probe_interval()
    {
        var client = new FakeClient { SubSystemId = 0x89DE1043, ReadSucceeds = true };
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var supplement = new AstralGpuSupplement(client, time, TimeSpan.FromSeconds(1));

        supplement.AppendSensors(HwIdentifier, HwIdentifier, HwName, new List<HardwareSensor>());
        supplement.AppendSensors(HwIdentifier, HwIdentifier, HwName, new List<HardwareSensor>());
        Assert.Equal(1, client.ReadCalls);

        time.Advance(TimeSpan.FromSeconds(2));
        supplement.AppendSensors(HwIdentifier, HwIdentifier, HwName, new List<HardwareSensor>());
        Assert.Equal(2, client.ReadCalls);
    }

    [Fact]
    public void AppendSensors_keeps_the_last_good_reading_after_a_later_probe_fails()
    {
        var client = new FakeClient { SubSystemId = 0x89DE1043, ReadSucceeds = true };
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var supplement = new AstralGpuSupplement(client, time, TimeSpan.FromSeconds(1));

        supplement.AppendSensors(HwIdentifier, HwIdentifier, HwName, new List<HardwareSensor>());

        client.ReadSucceeds = false;
        time.Advance(TimeSpan.FromSeconds(2));
        var result = new List<HardwareSensor>();
        supplement.AppendSensors(HwIdentifier, HwIdentifier, HwName, result);

        Assert.Equal(2, client.ReadCalls);
        Assert.Contains(result, s => s.Name == "GPU 12VHPWR Pin 1 Voltage");
    }

    [Fact]
    public void EnrichName_reflects_astral_confirmation_only_after_a_successful_probe()
    {
        var client = new FakeClient { SubSystemId = 0x89DE1043, ReadSucceeds = false };
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var supplement = new AstralGpuSupplement(client, time, TimeSpan.FromSeconds(1));

        supplement.AppendSensors(HwIdentifier, HwIdentifier, HwName, new List<HardwareSensor>());
        Assert.Equal("ASUS GeForce RTX 5080", supplement.EnrichName(HwIdentifier, HwName));

        client.ReadSucceeds = true;
        time.Advance(TimeSpan.FromSeconds(2));
        supplement.AppendSensors(HwIdentifier, HwIdentifier, HwName, new List<HardwareSensor>());
        Assert.Equal("ASUS ROG Astral RTX 5080", supplement.EnrichName(HwIdentifier, HwName));
    }

    [Fact]
    public void EnrichName_returns_original_name_for_an_unparseable_identifier()
    {
        var client = new FakeClient();
        var supplement = new AstralGpuSupplement(client, TimeProvider.System, TimeSpan.FromSeconds(1));

        Assert.Equal(HwName, supplement.EnrichName("not-an-adapter-identifier", HwName));
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan amount) => _utcNow += amount;
}
