using System.Text.Json;
using Nexus.Service.Games;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Games;

public class FpsUploadWorkerTests : IDisposable
{
    private readonly string _dir;

    public FpsUploadWorkerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-fpsupload-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeFpsUploadTransport : IFpsUploadTransport
    {
        public List<FpsUploadPayload> Sent { get; } = new();
        public FpsUploadResult Respond { get; set; } = FpsUploadResult.Success;

        public Task<FpsUploadResult> SendAsync(FpsUploadPayload payload, CancellationToken ct)
        {
            Sent.Add(payload);
            return Task.FromResult(Respond);
        }
    }

    /// <summary>Minimal ISensorProvider double; every non-configured member returns an empty placeholder.</summary>
    private sealed class StubSensors : ISensorProvider
    {
        public string Cpu { get; init; } = "Test CPU";
        public IReadOnlyList<string> GpuModels { get; init; } = new[] { "Test GPU" };
        public IReadOnlyList<GpuReadout> Gpus { get; init; } = Array.Empty<GpuReadout>();
        public string MemoryFormatted { get; init; } = "32 GB";
        public string Motherboard { get; init; } = "Test Board";

        public string GetCpuModel() => Cpu;
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => Array.Empty<HardwareSensor>();
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 0f);
        public IReadOnlyList<string> GetGpuModels() => GpuModels;
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Gpus;
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => Array.Empty<HardwareSensor>();
        public string GetMemoryTotalFormatted() => MemoryFormatted;
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => new Dictionary<string, StorageComponent>();
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => Array.Empty<HardwareSensor>();
        public string GetMotherboardModel() => Motherboard;
        public SensorExtras GetSensorExtras() => new();
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static FpsSessionRecord Session(
        string gameKey = "steam:1091500", string name = "Cyberpunk 2077", string store = "steam",
        long? startedUtcMs = null, int focusedSec = 600, int validSec = 590, long frames = 590 * 60,
        int minFps = 30, int maxFps = 144, int dispW = 2560, int dispH = 1440, int refreshHz = 144,
        int winW = 2560, int winH = 1440, bool fullscreen = true, bool capped = false, int capValue = 0)
    {
        var started = startedUtcMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var hist = new uint[FpsHistogram.BucketCount];
        FpsHistogram.AddSample(hist, 60);
        return new FpsSessionRecord(
            Guid.NewGuid(), gameKey, name, store, started, started + focusedSec * 1000L, focusedSec, validSec, frames,
            minFps, maxFps, hist, dispW, dispH, refreshHz, winW, winH, fullscreen, capped, capValue,
            HardwareHash: 12345, FpsUploadState.Pending);
    }

    private static FpsUploadWorker MakeWorker(
        InMemoryConfigStore store, BinaryFpsSessionStore sessions, FakeFpsUploadTransport transport, StubSensors? sensors = null) =>
        new(store, sessions, transport, new SystemSpecsCollector(sensors ?? new StubSensors()));

    private static InMemoryConfigStore OptedInStore()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Telemetry.CollectAnonymousData = true);
        return store;
    }

    [Fact]
    public void Payload_serializes_with_the_exact_wire_field_names_the_api_expects()
    {
        var payload = new FpsUploadPayload
        {
            InstallId = "install-1",
            ClientVersion = "1.0.0",
            Os = "win",
            OsVersion = "Windows 11",
            Arch = "x64",
            Hardware = new FpsUploadHardware { Cpu = "CPU", Gpu = "GPU", RamBytes = 100, Motherboard = "Board" },
            Sessions = new List<FpsUploadSession>
            {
                new()
                {
                    Id = Guid.NewGuid().ToString(), GameKey = "steam:1", GameName = "Game", Store = "steam",
                    FocusedSec = 600, ValidSec = 590, Frames = 35400, MinFps = 30, MaxFps = 144,
                    Hist = new int[FpsHistogram.BucketCount], HistSchema = 1,
                    DispW = 1920, DispH = 1080, RefreshHz = 144, WinW = 1920, WinH = 1080,
                    Fullscreen = true, Capped = false, CapValue = 0,
                },
            },
        };

        var json = JsonSerializer.Serialize(payload, AppJsonContext.Default.FpsUploadPayload);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        foreach (var field in new[] { "installId", "clientVersion", "os", "osVersion", "arch", "hardware", "sessions" })
        {
            Assert.True(root.TryGetProperty(field, out _), $"missing top-level field '{field}'");
        }

        var hardware = root.GetProperty("hardware");
        foreach (var field in new[] { "cpu", "gpu", "ramBytes", "motherboard" })
        {
            Assert.True(hardware.TryGetProperty(field, out _), $"missing hardware field '{field}'");
        }

        var session = root.GetProperty("sessions")[0];
        foreach (var field in new[]
        {
            "id", "gameKey", "gameName", "store", "validSec", "focusedSec", "frames", "minFps", "maxFps",
            "hist", "histSchema", "dispW", "dispH", "refreshHz", "winW", "winH", "fullscreen", "capped", "capValue",
        })
        {
            Assert.True(session.TryGetProperty(field, out _), $"missing session field '{field}'");
        }

        Assert.Equal(FpsHistogram.BucketCount, session.GetProperty("hist").GetArrayLength());
        Assert.Equal(1, session.GetProperty("histSchema").GetInt32());
    }

    [Fact]
    public async Task RunPendingUploadsAsync_MapsRecordFieldsOntoTheUploadSession()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var record = Session();
        store.Append(record);
        var config = OptedInStore();
        var transport = new FakeFpsUploadTransport();
        var worker = MakeWorker(config, store, transport);

        await worker.RunPendingUploadsAsync(CancellationToken.None);

        var sent = Assert.Single(transport.Sent);
        var session = Assert.Single(sent.Sessions);
        Assert.Equal(record.Id.ToString(), session.Id);
        Assert.Equal(record.GameKey, session.GameKey);
        Assert.Equal(record.GameName, session.GameName);
        Assert.Equal(record.Store, session.Store);
        Assert.Equal(record.FocusedSec, session.FocusedSec);
        Assert.Equal(record.ValidSec, session.ValidSec);
        Assert.Equal(record.Frames, session.Frames);
        Assert.Equal(record.MinFps, session.MinFps);
        Assert.Equal(record.MaxFps, session.MaxFps);
        Assert.Equal(Array.ConvertAll(record.Hist, h => (int)h), session.Hist);
        Assert.Equal(1, session.HistSchema);
        Assert.Equal(record.DispW, session.DispW);
        Assert.Equal(record.DispH, session.DispH);
        Assert.Equal(record.RefreshHz, session.RefreshHz);
        Assert.Equal(record.WinW, session.WinW);
        Assert.Equal(record.WinH, session.WinH);
        Assert.Equal(record.Fullscreen, session.Fullscreen);
        Assert.Equal(record.Capped, session.Capped);
        Assert.Equal(record.CapValue, session.CapValue);
        Assert.Equal("Test CPU", sent.Hardware.Cpu);
        Assert.Equal("Test GPU", sent.Hardware.Gpu);
        Assert.Equal("Test Board", sent.Hardware.Motherboard);
        Assert.Equal(32L * 1024 * 1024 * 1024, sent.Hardware.RamBytes);
    }

    [Fact]
    public async Task RunPendingUploadsAsync_IgpuFirstRig_UploadsTheDiscreteCard()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        store.Append(Session());
        var config = OptedInStore();
        var transport = new FakeFpsUploadTransport();
        var sensors = new StubSensors
        {
            GpuModels = new[] { "AMD Radeon Graphics", "AMD Radeon RX 7700 XT" },
            Gpus = new[]
            {
                new GpuReadout { Id = "gpu/0", Name = "AMD Radeon Graphics", Vendor = "AMD", Integrated = true },
                new GpuReadout { Id = "gpu/1", Name = "AMD Radeon RX 7700 XT", Vendor = "AMD", Integrated = false },
            },
        };
        var worker = MakeWorker(config, store, transport, sensors);

        await worker.RunPendingUploadsAsync(CancellationToken.None);

        var sent = Assert.Single(transport.Sent);
        Assert.Equal("AMD Radeon RX 7700 XT", sent.Hardware.Gpu);
    }

    [Fact]
    public async Task RunPendingUploadsAsync_NoPendingSessions_DoesNotCallTheTransport()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var config = OptedInStore();
        var transport = new FakeFpsUploadTransport();
        var worker = MakeWorker(config, store, transport);

        await worker.RunPendingUploadsAsync(CancellationToken.None);

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task RunPendingUploadsAsync_OptedOut_NeverCallsTheTransport()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        store.Append(Session());
        var config = new InMemoryConfigStore(); // CollectAnonymousData defaults to false
        var transport = new FakeFpsUploadTransport();
        var worker = MakeWorker(config, store, transport);

        await worker.RunPendingUploadsAsync(CancellationToken.None);

        Assert.Empty(transport.Sent);
        Assert.Single(store.QueryPendingForUpload(50)); // left untouched, not marked sent or rejected
    }

    [Fact]
    public async Task RunPendingUploadsAsync_FpsTrackingDisabled_NeverCallsTheTransport()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        store.Append(Session());
        var config = OptedInStore();
        config.Update(s => s.Fps.TrackingEnabled = false);
        var transport = new FakeFpsUploadTransport();
        var worker = MakeWorker(config, store, transport);

        await worker.RunPendingUploadsAsync(CancellationToken.None);

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task RunPendingUploadsAsync_Success_MarksTheBatchSent()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var record = Session();
        store.Append(record);
        var config = OptedInStore();
        var transport = new FakeFpsUploadTransport { Respond = FpsUploadResult.Success };
        var worker = MakeWorker(config, store, transport);

        await worker.RunPendingUploadsAsync(CancellationToken.None);

        Assert.Empty(store.QueryPendingForUpload(50));
        var stored = Assert.Single(store.QuerySessions(record.GameKey, 10));
        Assert.Equal(FpsUploadState.Sent, stored.UploadState);
    }

    [Fact]
    public async Task RunPendingUploadsAsync_Rejected_MarksTheBatchRejectedSoItIsNotRetried()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var record = Session();
        store.Append(record);
        var config = OptedInStore();
        var transport = new FakeFpsUploadTransport { Respond = FpsUploadResult.Rejected };
        var worker = MakeWorker(config, store, transport);

        await worker.RunPendingUploadsAsync(CancellationToken.None);

        Assert.Empty(store.QueryPendingForUpload(50));
        var stored = Assert.Single(store.QuerySessions(record.GameKey, 10));
        Assert.Equal(FpsUploadState.Rejected, stored.UploadState);

        // A later pass must not resend it.
        await worker.RunPendingUploadsAsync(CancellationToken.None);
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task RunPendingUploadsAsync_TransportFailure_LeavesTheBatchPendingForRetry()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var record = Session();
        store.Append(record);
        var config = OptedInStore();
        var transport = new FakeFpsUploadTransport { Respond = FpsUploadResult.Failed };
        var worker = MakeWorker(config, store, transport);

        await worker.RunPendingUploadsAsync(CancellationToken.None);

        Assert.Single(store.QueryPendingForUpload(50));
        var stored = Assert.Single(store.QuerySessions(record.GameKey, 10));
        Assert.Equal(FpsUploadState.Pending, stored.UploadState);

        // A later pass retries the same session.
        await worker.RunPendingUploadsAsync(CancellationToken.None);
        Assert.Equal(2, transport.Sent.Count);
    }

    [Fact]
    public async Task RunPendingUploadsAsync_MoreThanFiftyPending_SendsOnlyOneBatchOfFifty()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        for (var i = 0; i < 55; i++)
        {
            store.Append(Session(gameKey: $"steam:{i}", startedUtcMs: DateTimeOffset.UtcNow.AddSeconds(i).ToUnixTimeMilliseconds()));
        }
        var config = OptedInStore();
        var transport = new FakeFpsUploadTransport();
        var worker = MakeWorker(config, store, transport);

        await worker.RunPendingUploadsAsync(CancellationToken.None);

        var sent = Assert.Single(transport.Sent);
        Assert.Equal(50, sent.Sessions.Count);
        Assert.Equal(5, store.QueryPendingForUpload(50).Count);
    }

    [Fact]
    public async Task RunPendingUploadsAsync_ZeroDisplayDimensions_RejectsLocallyWithoutCallingTheTransport()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var record = Session(dispW: 0, dispH: 0, refreshHz: 0);
        store.Append(record);
        var config = OptedInStore();
        var transport = new FakeFpsUploadTransport();
        var worker = MakeWorker(config, store, transport);

        await worker.RunPendingUploadsAsync(CancellationToken.None);

        Assert.Empty(transport.Sent);
        var stored = Assert.Single(store.QuerySessions(record.GameKey, 10));
        Assert.Equal(FpsUploadState.Rejected, stored.UploadState);
    }

    [Fact]
    public async Task RunPendingUploadsAsync_FocusedSecOverTheApiCeiling_RejectsLocallyWithoutCallingTheTransport()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var record = Session(focusedSec: 90_000, validSec: 90_000, frames: 90_000 * 60);
        store.Append(record);
        var config = OptedInStore();
        var transport = new FakeFpsUploadTransport();
        var worker = MakeWorker(config, store, transport);

        await worker.RunPendingUploadsAsync(CancellationToken.None);

        Assert.Empty(transport.Sent);
        var stored = Assert.Single(store.QuerySessions(record.GameKey, 10));
        Assert.Equal(FpsUploadState.Rejected, stored.UploadState);
    }

    [Fact]
    public async Task RunPendingUploadsAsync_MixedBatch_SendsOnlyTheStructurallyValidSessions()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var invalid = Session(gameKey: "steam:bad", dispW: 0, dispH: 0, refreshHz: 0);
        var valid = Session(gameKey: "steam:good");
        store.Append(invalid);
        store.Append(valid);
        var config = OptedInStore();
        var transport = new FakeFpsUploadTransport();
        var worker = MakeWorker(config, store, transport);

        await worker.RunPendingUploadsAsync(CancellationToken.None);

        var sent = Assert.Single(transport.Sent);
        var sentSession = Assert.Single(sent.Sessions);
        Assert.Equal(valid.Id.ToString(), sentSession.Id);
        Assert.Equal(FpsUploadState.Rejected, Assert.Single(store.QuerySessions("steam:bad", 10)).UploadState);
        Assert.Equal(FpsUploadState.Sent, Assert.Single(store.QuerySessions("steam:good", 10)).UploadState);
    }
}
