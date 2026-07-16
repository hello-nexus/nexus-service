using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// GET /displays/topology + the monitor promote/demote endpoints over the
/// real request pipeline, with the platform topology provider swapped for a
/// fake at the DI seam. Hosting support is force-enabled so the suite runs
/// identically on every dev/CI OS.
/// </summary>
[Collection("NexusHost")]
public sealed class DisplayTopologyRoutesTests
{
    private const string MonitorId = "DEL41B7-5-abc-UID12345";
    private const string Y70Id = "RTK0004-5-def-UID67890";

    private sealed class FakeTopologyProvider : IDisplayTopologyProvider
    {
        public List<RawDisplayInfo>? Displays = DefaultDisplays();
        public bool PositionsAvailable => true;
        public IReadOnlyList<RawDisplayInfo>? Enumerate() => Displays;

        public static List<RawDisplayInfo> DefaultDisplays() => new()
        {
            new RawDisplayInfo
            {
                Id = MonitorId,
                Number = 1,
                Name = "DEL 41B7",
                Manufacturer = "DEL",
                Model = "41B7",
                X = 0, Y = 0, Width = 2560, Height = 1440,
                ResolutionWidth = 3840, ResolutionHeight = 2160,
                Scale = 1.5,
                IsPrimary = true,
                IsTouch = true,
                Orientation = "Landscape",
                RawHardwareId = @"\\?\DISPLAY#DEL41B7#5&abc&0&UID12345#{guid}",
            },
            new RawDisplayInfo
            {
                Id = Y70Id,
                Number = 2,
                Name = "RTK 0004",
                Manufacturer = "RTK",
                Model = "0004",
                X = 2560, Y = 0, Width = 1100, Height = 3840,
                ResolutionWidth = 1100, ResolutionHeight = 3840,
                Scale = 1.5,
                RawHardwareId = @"\\?\DISPLAY#RTK0004#5&def&0&UID67890#{guid}",
            },
        };
    }

    private readonly FakeTopologyProvider _provider = new();

    private (WebApplicationFactory<Program> factory, HttpClient client) Boot()
    {
        var factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IDisplayTopologyProvider>();
                s.AddSingleton<IDisplayTopologyProvider>(_provider);
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return (factory, client);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Topology_reports_monitors_with_y70_flagged()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/displays/topology");
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            Assert.True(root.GetProperty("positionsAvailable").GetBoolean());
            var displays = root.GetProperty("displays");
            Assert.Equal(2, displays.GetArrayLength());

            var first = displays[0];
            Assert.Equal(MonitorId, first.GetProperty("id").GetString());
            Assert.Equal(0, first.GetProperty("bounds").GetProperty("x").GetInt32());
            Assert.Equal(2560, first.GetProperty("bounds").GetProperty("width").GetInt32());
            Assert.Equal(3840, first.GetProperty("resolution").GetProperty("width").GetInt32());
            Assert.Equal(1.5, first.GetProperty("scaleFactor").GetDouble());
            Assert.True(first.GetProperty("isPrimary").GetBoolean());
            Assert.False(first.GetProperty("isY70").GetBoolean());

            var second = displays[1];
            Assert.True(second.GetProperty("isY70").GetBoolean());
            Assert.False(second.GetProperty("hostingSupported").GetBoolean());
        }
    }

    [Fact]
    public async Task Promote_creates_a_bound_monitor_record_and_broadcasts()
    {
        DisplayTopologyService.HostingSupportedOverrideForTests = true;
        try
        {
            var (factory, client) = Boot();
            using (factory)
            {
                var hub = factory.Services.GetRequiredService<MultiplexHub>();
                var wsClient = factory.Server.CreateWebSocketClient();
                var token = factory.Services.GetRequiredService<TokenService>().Token;
                wsClient.ConfigureRequest = req => req.Headers["Authorization"] = "Bearer " + token;
                using var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
                await ws.SendAsync(Encoding.UTF8.GetBytes("{\"sub\":[\"displays\"]}"),
                    WebSocketMessageType.Text, true, CancellationToken.None);
                await WaitFor(() => hub.TopicSubscriberCount(PanelTopics.Displays) == 1, "displays subscribed");

                var res = await client.PostAsync($"/displays/{MonitorId}/panel", Json("{}"));
                Assert.True(res.IsSuccessStatusCode);
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var record = doc.RootElement;
                var recordId = record.GetProperty("id").GetString();

                Assert.False(string.IsNullOrEmpty(recordId));
                Assert.Equal(MonitorId, record.GetProperty("displayId").GetString());
                Assert.Equal("DEL 41B7", record.GetProperty("displayName").GetString());
                var caps = record.GetProperty("capabilities");
                Assert.Equal(PanelSurfaces.Monitor, caps.GetProperty("surface").GetString());
                // Touch rides the pointer-device association from topology.
                Assert.True(caps.GetProperty("touch").GetBoolean());
                Assert.Equal(2560, caps.GetProperty("cssWidth").GetInt32());
                Assert.Equal(1440, caps.GetProperty("cssHeight").GetInt32());
                Assert.Equal(1.5, caps.GetProperty("dpr").GetDouble());

                // The displays topic observed the assignment change.
                var frame = await ReceiveText(ws);
                Assert.Contains("\"displays\"", frame);

                // Topology now reports the binding.
                var topoRes = await client.GetAsync("/displays/topology");
                using var topoDoc = JsonDocument.Parse(await topoRes.Content.ReadAsStringAsync());
                var entry = topoDoc.RootElement.GetProperty("displays")[0];
                Assert.Equal(recordId, entry.GetProperty("assignedPanelDeviceId").GetString());

                // The registry persisted the binding (settings round-trip).
                var registry = factory.Services.GetRequiredService<PanelDeviceRegistry>();
                Assert.Equal(MonitorId, registry.Get(recordId!)?.DisplayId);

                // /panel/devices stamps displayAttached for the bound record.
                var devicesRes = await client.GetAsync("/panel/devices");
                using var devicesDoc = JsonDocument.Parse(await devicesRes.Content.ReadAsStringAsync());
                var found = false;
                foreach (var device in devicesDoc.RootElement.GetProperty("devices").EnumerateArray())
                {
                    if (device.GetProperty("id").GetString() != recordId) continue;
                    found = true;
                    Assert.True(device.GetProperty("displayAttached").GetBoolean());
                }
                Assert.True(found);
            }
        }
        finally
        {
            DisplayTopologyService.HostingSupportedOverrideForTests = null;
        }
    }

    [Fact]
    public async Task Promote_rejects_y70_unknown_and_double_promotion()
    {
        DisplayTopologyService.HostingSupportedOverrideForTests = true;
        try
        {
            var (factory, client) = Boot();
            using (factory)
            {
                Assert.Equal(HttpStatusCode.Conflict,
                    (await client.PostAsync($"/displays/{Y70Id}/panel", Json("{}"))).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound,
                    (await client.PostAsync("/displays/nope/panel", Json("{}"))).StatusCode);

                Assert.True((await client.PostAsync($"/displays/{MonitorId}/panel", Json("{}"))).IsSuccessStatusCode);
                Assert.Equal(HttpStatusCode.Conflict,
                    (await client.PostAsync($"/displays/{MonitorId}/panel", Json("{}"))).StatusCode);
            }
        }
        finally
        {
            DisplayTopologyService.HostingSupportedOverrideForTests = null;
        }
    }

    [Fact]
    public async Task Promote_requires_hosting_support_and_token()
    {
        DisplayTopologyService.HostingSupportedOverrideForTests = false;
        try
        {
            var (factory, client) = Boot();
            using (factory)
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity,
                    (await client.PostAsync($"/displays/{MonitorId}/panel", Json("{}"))).StatusCode);

                var anonymous = factory.CreateClient();
                Assert.Equal(HttpStatusCode.Unauthorized,
                    (await anonymous.PostAsync($"/displays/{MonitorId}/panel", Json("{}"))).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized,
                    (await anonymous.DeleteAsync($"/displays/{MonitorId}/panel")).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized,
                    (await anonymous.GetAsync("/displays/assignments")).StatusCode);
            }
        }
        finally
        {
            DisplayTopologyService.HostingSupportedOverrideForTests = null;
        }
    }

    [Fact]
    public async Task Demote_turns_the_panel_off_but_keeps_its_config()
    {
        DisplayTopologyService.HostingSupportedOverrideForTests = true;
        try
        {
            var (factory, client) = Boot();
            using (factory)
            {
                Assert.Equal(HttpStatusCode.NotFound,
                    (await client.DeleteAsync($"/displays/{MonitorId}/panel")).StatusCode);

                var promote = await client.PostAsync($"/displays/{MonitorId}/panel", Json("{}"));
                using var doc = JsonDocument.Parse(await promote.Content.ReadAsStringAsync());
                var recordId = doc.RootElement.GetProperty("id").GetString();

                // Persist a layout + a setting so the off/on cycle can prove
                // the config survives.
                Assert.True((await client.PostAsync($"/panel/devices/{recordId}",
                    Json("{\"displayName\":\"Desk monitor\",\"reserveMonitor\":false}"))).IsSuccessStatusCode);

                var assignments = await client.GetStringAsync("/displays/assignments");
                Assert.Contains(MonitorId, assignments);

                // Off: kiosk assignment gone, record still there with config.
                Assert.True((await client.DeleteAsync($"/displays/{MonitorId}/panel")).IsSuccessStatusCode);
                Assert.DoesNotContain(MonitorId, await client.GetStringAsync("/displays/assignments"));
                var registry = factory.Services.GetRequiredService<PanelDeviceRegistry>();
                var off = registry.Get(recordId!);
                Assert.NotNull(off);
                Assert.False(off!.Enabled);
                Assert.Equal("Desk monitor", off.DisplayName);

                // Topology reads the display as unassigned while off.
                using (var topo = JsonDocument.Parse(await client.GetStringAsync("/displays/topology")))
                {
                    Assert.Equal(JsonValueKind.Null,
                        topo.RootElement.GetProperty("displays")[0].GetProperty("assignedPanelDeviceId").ValueKind);
                }

                // Second off is a no-op 404 (nothing active to turn off).
                Assert.Equal(HttpStatusCode.NotFound,
                    (await client.DeleteAsync($"/displays/{MonitorId}/panel")).StatusCode);

                // On again: SAME record, settings intact, assignment restored.
                var repromote = await client.PostAsync($"/displays/{MonitorId}/panel", Json("{}"));
                Assert.True(repromote.IsSuccessStatusCode);
                using var redoc = JsonDocument.Parse(await repromote.Content.ReadAsStringAsync());
                Assert.Equal(recordId, redoc.RootElement.GetProperty("id").GetString());
                Assert.Equal("Desk monitor", redoc.RootElement.GetProperty("displayName").GetString());
                Assert.False(redoc.RootElement.GetProperty("reserveMonitor").GetBoolean());
                Assert.Contains(MonitorId, await client.GetStringAsync("/displays/assignments"));
            }
        }
        finally
        {
            DisplayTopologyService.HostingSupportedOverrideForTests = null;
        }
    }

    [Fact]
    public async Task Reserve_monitor_patch_flows_into_assignments()
    {
        DisplayTopologyService.HostingSupportedOverrideForTests = true;
        try
        {
            var (factory, client) = Boot();
            using (factory)
            {
                var promote = await client.PostAsync($"/displays/{MonitorId}/panel", Json("{}"));
                using var doc = JsonDocument.Parse(await promote.Content.ReadAsStringAsync());
                var recordId = doc.RootElement.GetProperty("id").GetString();

                // Default: reserved.
                using (var assignments = JsonDocument.Parse(await client.GetStringAsync("/displays/assignments")))
                {
                    Assert.True(assignments.RootElement.GetProperty("assignments")[0]
                        .GetProperty("reserveMonitor").GetBoolean());
                }

                var patch = await client.PostAsync($"/panel/devices/{recordId}",
                    Json("{\"reserveMonitor\":false}"));
                Assert.True(patch.IsSuccessStatusCode);

                using (var assignments = JsonDocument.Parse(await client.GetStringAsync("/displays/assignments")))
                {
                    Assert.False(assignments.RootElement.GetProperty("assignments")[0]
                        .GetProperty("reserveMonitor").GetBoolean());
                }
            }
        }
        finally
        {
            DisplayTopologyService.HostingSupportedOverrideForTests = null;
        }
    }

    private sealed class FakeOrientationProvider : IDisplayOrientationProvider
    {
        public string? LastDisplayId;
        public string? LastOrientation;
        public string? LastCoverColorHex;
        public (bool Ok, string Error) SetY70Orientation(string orientation) => (true, "");
        public (bool Ok, string Error) SetDisplayOrientation(string displayId, string orientation, string coverColorHex)
        {
            LastDisplayId = displayId;
            LastOrientation = orientation;
            LastCoverColorHex = coverColorHex;
            return (true, "");
        }
    }

    [Fact]
    public async Task Rotation_endpoint_validates_and_dispatches()
    {
        var fakeOrientation = new FakeOrientationProvider();
        var factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IDisplayTopologyProvider>();
                s.AddSingleton<IDisplayTopologyProvider>(_provider);
                s.RemoveAll<IDisplayOrientationProvider>();
                s.AddSingleton<IDisplayOrientationProvider>(fakeOrientation);
            }));
        using (factory)
        {
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);

            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.PostAsync($"/displays/{MonitorId}/rotation", Json("{\"orientation\":\"Sideways\"}"))).StatusCode);

            var anonymous = factory.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await anonymous.PostAsync($"/displays/{MonitorId}/rotation", Json("{\"orientation\":\"Portrait\"}"))).StatusCode);

            var ok = await client.PostAsync($"/displays/{MonitorId}/rotation", Json("{\"orientation\":\"Portrait\"}"));
            Assert.True(ok.IsSuccessStatusCode);
            Assert.Equal(MonitorId, fakeOrientation.LastDisplayId);
            Assert.Equal("Portrait", fakeOrientation.LastOrientation);
            // No promoted panel for this display: nothing to resolve a colour from.
            Assert.Equal("", fakeOrientation.LastCoverColorHex);
        }
    }

    [Fact]
    public async Task Rotation_endpoint_threads_the_promoted_panels_background_colour_as_the_cover_colour()
    {
        DisplayTopologyService.HostingSupportedOverrideForTests = true;
        try
        {
            var fakeOrientation = new FakeOrientationProvider();
            var factory = new NexusAppFactory().WithWebHostBuilder(b =>
                b.ConfigureTestServices(s =>
                {
                    s.RemoveAll<IDisplayTopologyProvider>();
                    s.AddSingleton<IDisplayTopologyProvider>(_provider);
                    s.RemoveAll<IDisplayOrientationProvider>();
                    s.AddSingleton<IDisplayOrientationProvider>(fakeOrientation);
                }));
            using (factory)
            {
                var client = factory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);

                var promote = await client.PostAsync($"/displays/{MonitorId}/panel", Json("{}"));
                using var doc = JsonDocument.Parse(await promote.Content.ReadAsStringAsync());
                var recordId = doc.RootElement.GetProperty("id").GetString();
                var patch = await client.PostAsync($"/panel/devices/{recordId}", Json("{\"backgroundColor\":\"#2c0d0d\"}"));
                Assert.True(patch.IsSuccessStatusCode);

                var ok = await client.PostAsync($"/displays/{MonitorId}/rotation", Json("{\"orientation\":\"Portrait\"}"));

                Assert.True(ok.IsSuccessStatusCode);
                Assert.Equal("#2c0d0d", fakeOrientation.LastCoverColorHex);
            }
        }
        finally
        {
            DisplayTopologyService.HostingSupportedOverrideForTests = null;
        }
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (cts.IsCancellationRequested)
                throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private static async Task<string> ReceiveText(WebSocket ws, int timeoutMs = 2000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[8192];
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}
