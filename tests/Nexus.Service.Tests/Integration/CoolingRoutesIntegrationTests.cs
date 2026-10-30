using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// GET /cooling/fans (Role/SeriesId exposure) and POST /cooling/fan/{id}/role
/// over the real request pipeline, with the fan control provider swapped for
/// a fake at the DI seam (mirrors DisplayTopologyRoutesTests).
/// </summary>
public sealed class CoolingRoutesIntegrationTests
{
    private sealed class FakeFanProvider : IFanControlProvider
    {
        public List<FanChannel> Channels = new()
        {
            new() { Id = "fan1", Name = "Fan 1" },
            new() { Id = "fan2", Name = "Fan 2" },
            new() { Id = "gpufan", Name = "GPU Fan", IsGpu = true, DeviceId = "/gpu-nvidia/0", DeviceName = "RTX 3070" },
            new() { Id = "/np50/port-1", Name = "Port 1" },
            new() { Id = "/np50/port-2", Name = "Port 2", DeviceId = "np50:1", DeviceName = "HYTE NP50" },
        };

        // Set to read the settings from another thread inside GetFanChannels, as
        // LhmComputer.Update does under its own lock.
        public IConfigStore? StoreReadDuringFanRead;
        public bool StoreLockedDuringFanRead;

        public IReadOnlyList<FanChannel> GetFanChannels()
        {
            if (StoreReadDuringFanRead is { } store)
            {
                // A dedicated thread, so a starved pool on a busy run cannot fake a held lock.
                var reader = new Thread(() => store.Load());
                reader.Start();
                if (!reader.Join(TimeSpan.FromSeconds(2))) StoreLockedDuringFanRead = true;
            }
            return Channels;
        }
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => new List<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds,
            IProgress<FanCalibrationProgress> progress,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private (WebApplicationFactory<Program> factory, HttpClient client) Boot(FakeFanProvider? fans = null)
    {
        var factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IFanControlProvider>();
                s.AddSingleton<IFanControlProvider>(fans ?? new FakeFanProvider());
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return (factory, client);
    }

    [Fact]
    public async Task GetFans_DefaultsRoleToNone_AndExposesSanitizedSeriesId()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/cooling/fans");
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var channels = doc.RootElement.GetProperty("channels");

            var fan1 = channels.EnumerateArray().First(c => c.GetProperty("id").GetString() == "fan1");
            Assert.Equal("none", fan1.GetProperty("role").GetString());
            Assert.Equal("fan1", fan1.GetProperty("seriesId").GetString());

            // Leading slash stripped, embedded slash becomes a hyphen -
            // matches MetricsHistory.SanitizeId exactly (proves the route
            // never reimplements the sanitize rule independently).
            var hubPort = channels.EnumerateArray().First(c => c.GetProperty("id").GetString() == "/np50/port-1");
            Assert.Equal("np50-port-1", hubPort.GetProperty("seriesId").GetString());
            Assert.Equal(
                Nexus.Service.Monitoring.History.MetricsHistory.SanitizeId("/np50/port-1"),
                hubPort.GetProperty("seriesId").GetString());
        }
    }

    [Fact]
    public async Task SetRole_PersistsAndReflectsOnTheNextGet()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var setRes = await client.PostAsJsonAsync("/cooling/fan/fan1/role", new { role = "cpu" });
            Assert.True(setRes.IsSuccessStatusCode);

            var getRes = await client.GetAsync("/cooling/fans");
            using var doc = JsonDocument.Parse(await getRes.Content.ReadAsStringAsync());
            var fan1 = doc.RootElement.GetProperty("channels").EnumerateArray()
                .First(c => c.GetProperty("id").GetString() == "fan1");
            Assert.Equal("cpu", fan1.GetProperty("role").GetString());

            // "none" clears the assignment back to the default.
            var clearRes = await client.PostAsJsonAsync("/cooling/fan/fan1/role", new { role = "none" });
            Assert.True(clearRes.IsSuccessStatusCode);
            var afterClear = JsonDocument.Parse(await (await client.GetAsync("/cooling/fans")).Content.ReadAsStringAsync())
                .RootElement.GetProperty("channels").EnumerateArray()
                .First(c => c.GetProperty("id").GetString() == "fan1");
            Assert.Equal("none", afterClear.GetProperty("role").GetString());
        }
    }

    [Fact]
    public async Task SetRole_UnknownChannel_Returns400()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsJsonAsync("/cooling/fan/nope/role", new { role = "cpu" });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task SetOffset_PersistsAndReflectsOnTheNextGet()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var setRes = await client.PostAsJsonAsync("/cooling/fan/fan1/offset", new { offset = 7 });
            Assert.True(setRes.IsSuccessStatusCode);

            var fan1 = JsonDocument.Parse(await (await client.GetAsync("/cooling/fans")).Content.ReadAsStringAsync())
                .RootElement.GetProperty("channels").EnumerateArray()
                .First(c => c.GetProperty("id").GetString() == "fan1");
            Assert.Equal(7, fan1.GetProperty("offset").GetInt32());
        }
    }

    [Fact]
    public async Task SetOffset_Zero_ClearsIt()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PostAsJsonAsync("/cooling/fan/fan1/offset", new { offset = -12 });
            var clearRes = await client.PostAsJsonAsync("/cooling/fan/fan1/offset", new { offset = 0 });
            Assert.True(clearRes.IsSuccessStatusCode);

            var fan1 = JsonDocument.Parse(await (await client.GetAsync("/cooling/fans")).Content.ReadAsStringAsync())
                .RootElement.GetProperty("channels").EnumerateArray()
                .First(c => c.GetProperty("id").GetString() == "fan1");
            Assert.Equal(0, fan1.GetProperty("offset").GetInt32());
        }
    }

    [Fact]
    public async Task SetOffset_ClampsToTheDutyRange()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PostAsJsonAsync("/cooling/fan/fan1/offset", new { offset = 5000 });
            var fan1 = JsonDocument.Parse(await (await client.GetAsync("/cooling/fans")).Content.ReadAsStringAsync())
                .RootElement.GetProperty("channels").EnumerateArray()
                .First(c => c.GetProperty("id").GetString() == "fan1");
            Assert.Equal(100, fan1.GetProperty("offset").GetInt32());
        }
    }

    [Fact]
    public async Task SetOffset_UnknownChannel_Returns400()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsJsonAsync("/cooling/fan/nope/offset", new { offset = 5 });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task SetRole_InvalidRole_Returns400()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsJsonAsync("/cooling/fan/fan1/role", new { role = "motherboard" });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task PutFanGroups_PersistsAndRidesTheNextFansGet()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var put = await client.PutAsJsonAsync("/cooling/fan-groups", new
            {
                groups = new[]
                {
                    new { id = "g1", name = "  Radiator  ", members = new[] { "fan1", "fan2" } },
                },
            });
            Assert.True(put.IsSuccessStatusCode);

            using var doc = JsonDocument.Parse(await (await client.GetAsync("/cooling/fans")).Content.ReadAsStringAsync());
            var groups = doc.RootElement.GetProperty("groups").EnumerateArray().ToList();
            Assert.Single(groups);
            Assert.Equal("Radiator", groups[0].GetProperty("name").GetString());
            Assert.Equal(new[] { "fan1", "fan2" },
                groups[0].GetProperty("members").EnumerateArray().Select(m => m.GetString()).ToArray());
        }
    }

    [Fact]
    public async Task PutFanGroups_WithAnEmptyListClearsThem()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PutAsJsonAsync("/cooling/fan-groups", new
            {
                groups = new[] { new { id = "g1", name = "Radiator", members = new[] { "fan1" } } },
            });
            var clear = await client.PutAsJsonAsync("/cooling/fan-groups", new { groups = Array.Empty<object>() });
            Assert.True(clear.IsSuccessStatusCode);

            using var doc = JsonDocument.Parse(await (await client.GetAsync("/cooling/fans")).Content.ReadAsStringAsync());
            Assert.Empty(doc.RootElement.GetProperty("groups").EnumerateArray());
        }
    }

    [Fact]
    public async Task GetFans_KeepsTheHardwareNameARenameReplaced()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PostAsJsonAsync("/cooling/fan/fan1/name", new { name = "Top intake" });

            using var doc = JsonDocument.Parse(await (await client.GetAsync("/cooling/fans")).Content.ReadAsStringAsync());
            var fan1 = doc.RootElement.GetProperty("channels").EnumerateArray()
                .First(c => c.GetProperty("id").GetString() == "fan1");
            Assert.Equal("Top intake", fan1.GetProperty("name").GetString());
            Assert.Equal("Fan 1", fan1.GetProperty("originalName").GetString());
        }
    }

    [Fact]
    public async Task GetFans_CarriesTheBoardBlockRenameOntoItsOwnFansOnly()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PostAsJsonAsync("/cooling/fan/motherboard/name", new { name = "ROG STRIX Z790-E" });

            using var doc = JsonDocument.Parse(await (await client.GetAsync("/cooling/fans")).Content.ReadAsStringAsync());
            var channels = doc.RootElement.GetProperty("channels").EnumerateArray().ToList();
            JsonElement ById(string id) => channels.First(c => c.GetProperty("id").GetString() == id);

            Assert.Equal("ROG STRIX Z790-E", ById("fan1").GetProperty("deviceName").GetString());
            // Anything on a device keeps its own product name, GPU included.
            Assert.Equal("RTX 3070", ById("gpufan").GetProperty("deviceName").GetString());
            Assert.Equal("HYTE NP50", ById("/np50/port-2").GetProperty("deviceName").GetString());
            // Nothing to reset to - the synthetic block has no hardware name.
            Assert.Null(ById("fan1").GetProperty("originalDeviceName").GetString());
        }
    }

    [Fact]
    public async Task GetFans_LeavesBoardFansUnnamedUntilTheBlockIsRenamed()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            using var doc = JsonDocument.Parse(await (await client.GetAsync("/cooling/fans")).Content.ReadAsStringAsync());
            var fan1 = doc.RootElement.GetProperty("channels").EnumerateArray()
                .First(c => c.GetProperty("id").GetString() == "fan1");
            Assert.Null(fan1.GetProperty("deviceName").GetString());
        }
    }

    [Fact]
    public async Task GetFans_RenamesAGpuGroupUnderItsCardId()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PostAsJsonAsync("/cooling/fan/%2Fgpu-nvidia%2F0/name", new { name = "Main card" });

            using var doc = JsonDocument.Parse(await (await client.GetAsync("/cooling/fans")).Content.ReadAsStringAsync());
            var gpu = doc.RootElement.GetProperty("channels").EnumerateArray()
                .First(c => c.GetProperty("id").GetString() == "gpufan");
            Assert.Equal("Main card", gpu.GetProperty("deviceName").GetString());
            Assert.Equal("RTX 3070", gpu.GetProperty("originalDeviceName").GetString());
        }
    }

    [Fact]
    public async Task UpdatePreset_SaveCurrent_ReadsFansOutsideTheSettingsLock()
    {
        var fans = new FakeFanProvider();
        var (factory, client) = Boot(fans);
        using (factory)
        {
            var created = await client.PostAsJsonAsync("/cooling/presets", new { name = "Quiet" });
            using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = doc.RootElement.GetProperty("preset").GetProperty("id").GetString();

            fans.StoreReadDuringFanRead = factory.Services.GetRequiredService<IConfigStore>();
            var res = await client.PutAsJsonAsync($"/cooling/presets/{id}", new { saveCurrent = true });

            Assert.True(res.IsSuccessStatusCode);
            Assert.False(fans.StoreLockedDuringFanRead);
        }
    }

    [Fact]
    public async Task CreatePreset_Fresh_BuildsCleanInstallDefaultsAndActivatesThem()
    {
        var (factory, client) = Boot(new FakeFanProvider());
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s =>
            {
                s.Cooling.ActivePreset = "custom";
                s.Cooling.ManualSpeeds["fan1"] = 60;
                s.Cooling.FanOffsets["fan1"] = 9;
                s.Cooling.GlobalSpeedModifier = 1.4;
                s.Cooling.Presets.Add(new CoolingPreset { Id = "old", Name = "Old" });
                s.Cooling.ActivePresetId = "old";
            });

            var res = await client.PostAsJsonAsync("/cooling/presets", new { name = "Clean", fresh = true });

            Assert.True(res.IsSuccessStatusCode);
            var cooling = store.Load().Cooling;
            var preset = Assert.Single(cooling.Presets, p => p.Id != "old");
            Assert.Equal(Nexus.Service.Defaults.InstallDefaults.Cooling.ActivePreset, preset.Mode);
            Assert.Empty(preset.FanCurveAssignments);
            Assert.Empty(preset.ManualSpeeds);
            Assert.Empty(preset.FanOffsets);
            Assert.Equal(Nexus.Service.Defaults.InstallDefaults.Cooling.GlobalSpeedModifier, preset.GlobalSpeedModifier);
            Assert.Equal(preset.Id, cooling.ActivePresetId);
            // Activation replayed the defaults onto the live state.
            Assert.Equal(Nexus.Service.Defaults.InstallDefaults.Cooling.ActivePreset, cooling.ActivePreset);
            Assert.Empty(cooling.FanOffsets);
            Assert.Equal(Nexus.Service.Defaults.InstallDefaults.Cooling.GlobalSpeedModifier, cooling.GlobalSpeedModifier);
        }
    }

    [Fact]
    public async Task CreatePreset_FreshWithNoActivePreset_SnapshotsTheLiveState()
    {
        var (factory, client) = Boot(new FakeFanProvider());
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s =>
            {
                s.Cooling.ActivePreset = "custom";
                s.Cooling.FanOffsets["fan1"] = 9;
            });

            var res = await client.PostAsJsonAsync("/cooling/presets", new { name = "First", fresh = true });

            Assert.True(res.IsSuccessStatusCode);
            var cooling = store.Load().Cooling;
            Assert.Equal("custom", Assert.Single(cooling.Presets).Mode);
            Assert.Equal(9, cooling.Presets[0].FanOffsets["fan1"]);
            Assert.Equal(9, cooling.FanOffsets["fan1"]);
        }
    }

    [Fact]
    public async Task CreatePreset_FreshWithCoolingGateOff_Returns409AndCreatesNothing()
    {
        var (factory, client) = Boot(new FakeFanProvider());
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s =>
            {
                s.Features.Cooling = false;
                s.Cooling.Presets.Add(new CoolingPreset { Id = "old", Name = "Old" });
                s.Cooling.ActivePresetId = "old";
            });

            var res = await client.PostAsJsonAsync("/cooling/presets", new { name = "Clean", fresh = true });

            Assert.Equal(System.Net.HttpStatusCode.Conflict, res.StatusCode);
            var cooling = store.Load().Cooling;
            Assert.Single(cooling.Presets);
            Assert.Equal("old", cooling.ActivePresetId);
        }
    }

    [Theory]
    [InlineData("""{"name":"Snap"}""")]
    [InlineData("""{"name":"Snap","fresh":false}""")]
    public async Task CreatePreset_FreshAbsentOrFalse_SnapshotsTheLiveState(string json)
    {
        var (factory, client) = Boot(new FakeFanProvider());
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s =>
            {
                s.Cooling.ActivePreset = "custom";
                s.Cooling.ManualSpeeds["fan1"] = 60;
                s.Cooling.FanOffsets["fan1"] = 9;
                s.Cooling.GlobalSpeedModifier = 1.4;
            });

            var res = await client.PostAsync("/cooling/presets", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

            Assert.True(res.IsSuccessStatusCode);
            var cooling = store.Load().Cooling;
            var preset = Assert.Single(cooling.Presets);
            Assert.Equal("custom", preset.Mode);
            Assert.Equal(60, preset.ManualSpeeds["fan1"]);
            Assert.Equal(9, preset.FanOffsets["fan1"]);
            Assert.Equal(1.4, preset.GlobalSpeedModifier);
            // Snapshot create does not replay anything onto the live state.
            Assert.Equal("custom", cooling.ActivePreset);
            Assert.Equal(9, cooling.FanOffsets["fan1"]);
        }
    }
}
