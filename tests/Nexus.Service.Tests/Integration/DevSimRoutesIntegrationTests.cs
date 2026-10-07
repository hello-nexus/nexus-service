#if DEV_TOOLS
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class DevSimRoutesIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public DevSimRoutesIntegrationTests(NexusAppFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task Clear() => await Client().PostAsync("/dev/sim/clear", null);

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();

    [Fact]
    public async Task TheRoutes_FollowTheContract()
    {
        await Clear();
        var client = Client();

        var catalog = await Json(await client.GetAsync("/dev/sim/events"));
        Assert.Equal(22, catalog.GetProperty("catalog").GetArrayLength());
        Assert.Equal(0, catalog.GetProperty("active").GetArrayLength());

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/dev/sim/events/nope.nothing", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/dev/sim/events/nope.nothing")).StatusCode);

        var started = await Json(await client.PostAsync("/dev/sim/events/guard.watchdogLatched", null));
        var again = await Json(await client.PostAsync("/dev/sim/events/guard.watchdogLatched", null));
        var active = Assert.Single(started.GetProperty("active").EnumerateArray());
        Assert.Equal("guard.watchdogLatched", active.GetProperty("id").GetString());
        Assert.Equal(active.GetProperty("startedAtUtcMs").GetInt64(),
            Assert.Single(again.GetProperty("active").EnumerateArray()).GetProperty("startedAtUtcMs").GetInt64());

        var guard = await Json(await client.GetAsync("/cooling/guard"));
        Assert.True(guard.GetProperty("watchdogLatched").GetBoolean());

        var stopped = await Json(await client.DeleteAsync("/dev/sim/events/guard.watchdogLatched"));
        Assert.Equal(0, stopped.GetProperty("active").GetArrayLength());
        Assert.False((await Json(await client.GetAsync("/cooling/guard"))).GetProperty("watchdogLatched").GetBoolean());

        await client.PostAsync("/dev/sim/events/incident.bsod", null);
        await client.PostAsync("/dev/sim/events/health.fanStall", null);
        var cleared = await Json(await client.PostAsync("/dev/sim/clear", null));
        Assert.Equal(0, cleared.GetProperty("active").GetArrayLength());
    }

    [Fact]
    public async Task AHealthSim_ShowsUpInTheDiagnosticsHealthRoute()
    {
        await Clear();
        var client = Client();
        await client.PostAsync("/dev/sim/events/health.fanStall", null);

        var health = await Json(await client.GetAsync("/diagnostics/health"));

        Assert.Contains(health.GetProperty("components").EnumerateArray(), c => c.GetProperty("id").GetString() == "cooling:sim-fan");
        await Clear();
    }

    [Fact]
    public async Task UndoWithASimulatedPendingHeal_LeavesTheRealPendingHealIntact()
    {
        await Clear();
        Store.Update(s =>
        {
            s.Cooling.HealSnapshot = new List<CurveDocument>();
            s.Cooling.HealedChannels = new List<HealedChannelRecord> { new() { Id = "real:fan", Name = "Real Fan", Hazard = "low-ceiling" } };
        });
        var client = Client();
        await client.PostAsync("/dev/sim/events/guard.pendingHeal", null);

        var heal = await Json(await client.PostAsync("/cooling/heal/undo", null));

        // The sim ended; the real heal state was neither undone nor reported as undone.
        Assert.NotNull(Store.Load().Cooling.HealSnapshot);
        Assert.Single(Store.Load().Cooling.HealedChannels);
        Assert.True(heal.GetProperty("undoAvailable").GetBoolean());
        Assert.Equal(0, (await Json(await client.GetAsync("/dev/sim/events"))).GetProperty("active").GetArrayLength());

        // With the sim gone, the next Undo is the real one.
        await client.PostAsync("/cooling/heal/undo", null);
        Assert.Null(Store.Load().Cooling.HealSnapshot);
    }

    [Fact]
    public async Task KeepWithASimulatedPendingHeal_LeavesTheRealPendingHealIntact()
    {
        await Clear();
        Store.Update(s => s.Cooling.HealSnapshot = new List<CurveDocument>());
        var client = Client();
        await client.PostAsync("/dev/sim/events/guard.pendingHeal", null);

        await client.PostAsync("/cooling/heal/keep", null);
        Assert.NotNull(Store.Load().Cooling.HealSnapshot);

        await client.PostAsync("/cooling/heal/keep", null);
        Assert.Null(Store.Load().Cooling.HealSnapshot);
    }

    [Fact]
    public async Task AcknowledgeWithASimulatedEndedTrip_LeavesTheRealTripUnacknowledged()
    {
        await Clear();
        Store.Update(s => s.Cooling.LastThermalTrip = new ThermalGuardTripRecord
        {
            AtUtcMs = 1_000, EndedAtUtcMs = 2_000, PeakC = 99, Reason = "limit",
        });
        var client = Client();
        await client.PostAsync("/dev/sim/events/guard.endedTrip", null);

        await client.PostAsync("/cooling/guard/trip/acknowledge", null);
        Assert.Null(Store.Load().Cooling.LastThermalTrip!.AcknowledgedAtUtcMs);
        Assert.Equal(0, (await Json(await client.GetAsync("/dev/sim/events"))).GetProperty("active").GetArrayLength());

        await client.PostAsync("/cooling/guard/trip/acknowledge", null);
        Assert.NotNull(Store.Load().Cooling.LastThermalTrip!.AcknowledgedAtUtcMs);
    }
}
#endif
