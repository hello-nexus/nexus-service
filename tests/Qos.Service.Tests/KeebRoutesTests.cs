using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Qos.Service.Devices;
using Qos.Service.Devices.Detection;
using Qos.Service.Devices.Handlers;
using Qos.Service.Models.Common;
using Qos.Service.Models.Peripherals.Keeb;
using Qos.Service.Peripherals.Keeb;
using Qos.Service.Persistence;
using Qos.Service.Routes;

namespace Qos.Service.Tests;

/// <summary>
/// Route-table tests for /keeb endpoints. Verifies every documented route
/// is wired, method-correct, and resolves the IKeebProvider parameter. The
/// provider behaviour itself is covered by StubKeebProviderTests; here we
/// only care that the HTTP surface is intact and points at the right verb.
/// </summary>
public class KeebRoutesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public KeebRoutesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qos-keeb-routes-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private async Task<WebApplication> BuildApp()
    {
        var builder = WebApplication.CreateSlimBuilder();
        var store = new JsonConfigStore(_settingsPath);
        var manager = new DeviceManager(new IDeviceHandler[] { new KeebHandler() }, new FixedUsbEnumerator());
        var provider = new StubKeebProvider(store, manager);
        builder.Services.AddSingleton<IKeebProvider>(provider);
        var app = builder.Build();
        app.MapKeebEndpoints();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task KeebRoutes_MapsTheFullDocumentedSurface()
    {
        await using var app = await BuildApp();
        // Collect (method, pattern) tuples from the route table — the route
        // template names are the public contract; if these drift, the qos-web
        // api/keeb wrappers stop pointing at the right place.
        var routes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => new
            {
                Pattern = e.RoutePattern.RawText,
                Methods = string.Join(",", e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? new[] { "?" }),
            })
            .ToList();

        Assert.Contains(routes, r => r.Pattern == "/keeb/state" && r.Methods.Contains("GET"));
        Assert.Contains(routes, r => r.Pattern == "/keeb/layer/{layer:int}" && r.Methods.Contains("GET"));
        Assert.Contains(routes, r => r.Pattern == "/keeb/layer/{layer:int}/key" && r.Methods.Contains("POST"));
        Assert.Contains(routes, r => r.Pattern == "/keeb/layer/{layer:int}/reset" && r.Methods.Contains("POST"));
        Assert.Contains(routes, r => r.Pattern == "/keeb/settings" && r.Methods.Contains("GET"));
        Assert.Contains(routes, r => r.Pattern == "/keeb/rotary/functions" && r.Methods.Contains("GET"));
        Assert.Contains(routes, r => r.Pattern == "/keeb/rotary" && r.Methods.Contains("POST"));
        Assert.Contains(routes, r => r.Pattern == "/keeb/rotary/sensitivity" && r.Methods.Contains("POST"));
        Assert.Contains(routes, r => r.Pattern == "/keeb/passive-lighting" && r.Methods.Contains("POST"));
        Assert.Contains(routes, r => r.Pattern == "/keeb/firmware/lighting" && r.Methods.Contains("POST"));
        Assert.Contains(routes, r => r.Pattern == "/keeb/game-mode" && r.Methods.Contains("POST"));
        Assert.Contains(routes, r => r.Pattern == "/keeb/macro/{index}" && r.Methods.Contains("GET"));
        Assert.Contains(routes, r => r.Pattern == "/keeb/macro/{index}" && r.Methods.Contains("POST"));
        // Legacy alias retained for older nexus clients.
        Assert.Contains(routes, r => r.Pattern == "/keeb/key-reactive" && r.Methods.Contains("POST"));

        await app.StopAsync();
    }
}

/// <summary>
/// End-to-end provider behaviour tests covering writes that originate through
/// the route bodies. We don't go through HTTP here (the stub provider is
/// stateless and identical between in-process and HTTP); we just round-trip
/// through the StubKeebProvider and verify the persisted state.
/// </summary>
public class KeebFlowTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly JsonConfigStore _store;
    private readonly StubKeebProvider _provider;

    public KeebFlowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qos-keeb-flow-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
        _store = new JsonConfigStore(_settingsPath);
        var manager = new DeviceManager(new IDeviceHandler[] { new KeebHandler() }, new FixedUsbEnumerator());
        _provider = new StubKeebProvider(_store, manager);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void FirmwareLighting_Persists_Across_Provider_Reads()
    {
        _provider.SetFirmwareLighting(new SetFirmwareLightingBody
        {
            AnimationMode = "Rainbow",
            Speed = "Energetic",
            Direction = "RightToLeft",
            Brightness = 33,
            KeyIndicator = true,
        });
        var got = _provider.GetSettings();
        Assert.Equal("Rainbow", got.AnimationMode);
        Assert.Equal("Energetic", got.Speed);
        Assert.Equal("RightToLeft", got.Direction);
        Assert.Equal(33, got.Brightness);
        Assert.True(got.KeyIndicator);
    }

    [Fact]
    public void PassiveLighting_FlowsThroughGetSettings_SeparatelyFromFirmwareLighting()
    {
        _provider.SetFirmwareLighting(new SetFirmwareLightingBody
        {
            AnimationMode = "Wave",
            Speed = "Standard",
            Direction = "TopToBottom",
            Brightness = 70,
            KeyIndicator = false,
        });
        _provider.SetPassiveLighting(new SetPassiveLightingBody
        {
            KeyReactive = true,
            KeyReactiveMask = true,
            KeyReactiveMode = "Ripple",
            KeyReactiveColor = new RGBA { R = 50, G = 75, B = 100, A = 255 },
        });
        var got = _provider.GetSettings();
        // Firmware-lighting fields untouched by passive write.
        Assert.Equal("Wave", got.AnimationMode);
        Assert.Equal(70, got.Brightness);
        // Passive fields landed.
        Assert.True(got.KeyReactive);
        Assert.True(got.KeyReactiveMask);
        Assert.Equal("Ripple", got.KeyReactiveMode);
        Assert.Equal(50, got.KeyReactiveColor.R);
    }

    [Fact]
    public void GameMode_BitField_RoundTrips()
    {
        _provider.SetGameMode(new SetGameModeBody { AltF4 = true, AltTab = false, ShiftTab = true, WindowsKey = false });
        var got = _provider.GetSettings();
        Assert.True(got.AltF4Disabled);
        Assert.False(got.AltTabDisabled);
        Assert.True(got.ShiftKeyDisabled);
        Assert.False(got.WindowsKeyDisabled);
    }

    [Fact]
    public void RotaryRouting_Stores_LeftRight_AndAppOverrides()
    {
        _provider.SetRotary(new SetRotaryWheelsBody
        {
            Left = "BrightnessAdjustment",
            Right = "AltTab",
            Apps = new List<RotaryAppOverride>
            {
                new() { TargetId = "code.exe", Left = "ScrubAdobeTimeline", Right = "ScrollY" },
            },
        });
        var s = _store.Load();
        Assert.Equal("BrightnessAdjustment", s.Keeb.RotaryLeft);
        Assert.Equal("AltTab", s.Keeb.RotaryRight);
        Assert.Single(s.Keeb.RotaryApps);
        Assert.Equal("code.exe", s.Keeb.RotaryApps[0].TargetId);
    }

    [Fact]
    public void LayerKey_WritesAndReads_LandAtTheSameRowCol()
    {
        _provider.SetLayerKey(0, new SetLayerKeyBody { X = 3, Y = 7, Func = "VolumeUp", Mode = "MediaKey", Input = null });
        var layer = _provider.GetLayer(0);
        Assert.True(layer.Count > 3);
        Assert.True(layer[3].Count > 7);
        Assert.Equal("VolumeUp", layer[3][7].Function);
        Assert.Equal("MediaKey", layer[3][7].Mode);
    }

    [Fact]
    public void LayerReset_ClearsCachedKeys()
    {
        _provider.SetLayerKey(2, new SetLayerKeyBody { X = 0, Y = 0, Func = "A", Mode = "StandardKey" });
        Assert.NotEmpty(_provider.GetLayer(2));
        _provider.ResetLayer(2);
        Assert.Empty(_provider.GetLayer(2));
    }

    [Fact]
    public void MacroRoundTrip_PersistsThroughGetMacro()
    {
        var keys = new List<MacroKey>
        {
            new() { Key = "KeyA", Duration = 50, Type = "Make", Category = "" },
            new() { Key = "KeyA", Duration = 50, Type = "Break", Category = "" },
        };
        _provider.SetMacro(3, new SetMacroBody { Keys = keys });
        var read = _provider.GetMacro(3);
        Assert.Equal(3, read.Index);
        Assert.Equal(2, read.Keys.Count);
        Assert.Equal("KeyA", read.Keys[0].Key);
        Assert.Equal(50, read.Keys[0].Duration);
        Assert.Equal("Make", read.Keys[0].Type);
    }
}
