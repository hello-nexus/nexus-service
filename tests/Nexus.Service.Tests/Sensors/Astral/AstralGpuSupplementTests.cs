using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors.Astral;

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

        public bool TryReadGSensorBlock(int adapterIndex, out byte[] block)
        {
            block = Array.Empty<byte>();
            return false;
        }

        public bool TryReadAstralBlock(int adapterIndex, out byte[] block)
        {
            ReadCalls++;
            block = ReadSucceeds ? Block : Array.Empty<byte>();
            return ReadSucceeds;
        }
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
