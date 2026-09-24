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
/// A cooling reset restores the default preset ("off" - every fan on hardware
/// control), which is a hardware state no engine re-apply covers, so the route
/// releases the fans itself. Drives the real routes over the request pipeline
/// with the fan provider swapped at the DI seam.
/// </summary>
public sealed class ProfileResetCoolingTests
{
    private sealed class RecordingFanProvider : IFanControlProvider
    {
        public int ReleaseAllCount;

        public IReadOnlyList<FanChannel> GetFanChannels() => new List<FanChannel>
        {
            new() { Id = "fan1", Name = "Fan 1" },
            new() { Id = "fan2", Name = "Fan 2" },
        };
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => new List<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() => Interlocked.Increment(ref ReleaseAllCount);
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds,
            IProgress<FanCalibrationProgress> progress,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private static (WebApplicationFactory<Program> factory, HttpClient client, RecordingFanProvider fans) Boot()
    {
        var fans = new RecordingFanProvider();
        var factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IFanControlProvider>();
                s.AddSingleton<IFanControlProvider>(fans);
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return (factory, client, fans);
    }

    // Program.cs gates AppBootstrap.InitializeProfiles behind !testHost, so the
    // manifest is empty (and ActiveProfileId blank) until the test seeds it.
    private static async Task<string> ActiveProfileId(WebApplicationFactory<Program> factory, HttpClient client)
    {
        factory.Services.GetRequiredService<Nexus.Service.Persistence.ProfileManager>().Initialize();
        using var doc = JsonDocument.Parse(await (await client.GetAsync("/profiles")).Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("activeId").GetString()!;
    }

    private static async Task<string> CreateProfile(HttpClient client, string name)
    {
        var res = await client.PostAsJsonAsync("/profiles/create", new { name });
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("profile").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task ResetCooling_ReleasesEveryFanToHardware()
    {
        var (factory, client, fans) = Boot();
        using (factory)
        {
            var id = await ActiveProfileId(factory, client);

            var res = await client.PostAsJsonAsync($"/profiles/{id}/reset/cooling", new { });

            Assert.True(res.IsSuccessStatusCode);
            Assert.Equal(1, fans.ReleaseAllCount);
        }
    }

    [Fact]
    public async Task ResetWholeProfile_ReleasesEveryFanToHardware()
    {
        var (factory, client, fans) = Boot();
        using (factory)
        {
            var id = await ActiveProfileId(factory, client);

            var res = await client.PostAsJsonAsync($"/profiles/{id}/reset", new { });

            Assert.True(res.IsSuccessStatusCode);
            Assert.Equal(1, fans.ReleaseAllCount);
        }
    }

    [Fact]
    public async Task SharedCooling_ResetOnAnotherProfile_StillReleases()
    {
        var (factory, client, fans) = Boot();
        using (factory)
        {
            await ActiveProfileId(factory, client);
            // Shared cooling writes through to the active profile's in-memory
            // state whichever profile the reset names, so the fans must go too.
            Assert.True((await client.PutAsJsonAsync("/profiles/sharing/categories",
                new { category = "cooling", shared = true })).IsSuccessStatusCode);
            var other = await CreateProfile(client, "Second");

            var res = await client.PostAsJsonAsync($"/profiles/{other}/reset/cooling", new { });

            Assert.True(res.IsSuccessStatusCode);
            Assert.Equal(1, fans.ReleaseAllCount);
        }
    }

    [Fact]
    public async Task SharedCooling_WholeProfileReset_LeavesFansAlone()
    {
        var (factory, client, fans) = Boot();
        using (factory)
        {
            var id = await ActiveProfileId(factory, client);
            Assert.True((await client.PutAsJsonAsync("/profiles/sharing/categories",
                new { category = "cooling", shared = true })).IsSuccessStatusCode);

            // A whole-profile reset skips shared categories, so cooling is
            // untouched and the fans keep whatever drives them.
            var res = await client.PostAsJsonAsync($"/profiles/{id}/reset", new { });

            Assert.True(res.IsSuccessStatusCode);
            Assert.Equal(0, fans.ReleaseAllCount);
        }
    }

    private static readonly string[] PresetCurveIds =
        { "preset-silent", "preset-balanced", "preset-turbo", "preset-max" };

    private static IReadOnlyList<string> CurveIds(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<Nexus.Service.Persistence.IConfigStore>()
            .Load().Cooling.Curves.Select(c => c.Id).ToList();

    // A reset must land on the same four preset curves a clean install boots
    // with, not on an empty list that only refills after a restart.
    [Fact]
    public async Task ResetCooling_SeedsTheDefaultPresetCurves()
    {
        var (factory, client, _) = Boot();
        using (factory)
        {
            var id = await ActiveProfileId(factory, client);
            Assert.True(FanProfiles.SeedDefaultPresetCurves(
                factory.Services.GetRequiredService<IFanControlProvider>(),
                factory.Services.GetRequiredService<Nexus.Service.Persistence.IConfigStore>()));

            var res = await client.PostAsJsonAsync($"/profiles/{id}/reset/cooling", new { });

            Assert.True(res.IsSuccessStatusCode);
            Assert.Equal(PresetCurveIds, CurveIds(factory));
        }
    }

    [Fact]
    public async Task ResetWholeProfile_SeedsTheDefaultPresetCurves()
    {
        var (factory, client, _) = Boot();
        using (factory)
        {
            var id = await ActiveProfileId(factory, client);

            var res = await client.PostAsJsonAsync($"/profiles/{id}/reset", new { });

            Assert.True(res.IsSuccessStatusCode);
            Assert.Equal(PresetCurveIds, CurveIds(factory));
        }
    }

    [Fact]
    public async Task SharedCooling_ResetOnAnotherProfile_SeedsTheActiveProfile()
    {
        var (factory, client, _) = Boot();
        using (factory)
        {
            var first = await ActiveProfileId(factory, client);
            Assert.True((await client.PutAsJsonAsync("/profiles/sharing/categories",
                new { category = "cooling", shared = true })).IsSuccessStatusCode);
            var other = await CreateProfile(client, "Second");
            // Creating a profile activates it; switch back so the reset names
            // an inactive profile and goes through the shared branch.
            Assert.True((await client.PostAsJsonAsync($"/profiles/{first}/switch", new { })).IsSuccessStatusCode);

            var res = await client.PostAsJsonAsync($"/profiles/{other}/reset/cooling", new { });

            Assert.True(res.IsSuccessStatusCode);
            Assert.Equal(PresetCurveIds, CurveIds(factory));
        }
    }

    [Fact]
    public async Task ResetLighting_LeavesFansAlone()
    {
        var (factory, client, fans) = Boot();
        using (factory)
        {
            var id = await ActiveProfileId(factory, client);

            var res = await client.PostAsJsonAsync($"/profiles/{id}/reset/lighting", new { });

            Assert.True(res.IsSuccessStatusCode);
            Assert.Equal(0, fans.ReleaseAllCount);
        }
    }
}
