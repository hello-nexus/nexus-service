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

        public IReadOnlyList<FanChannel> GetFanChannels() => Channels;
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

    private (WebApplicationFactory<Program> factory, HttpClient client) Boot()
    {
        var factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IFanControlProvider>();
                s.AddSingleton<IFanControlProvider>(new FakeFanProvider());
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
}
