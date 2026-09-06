using Nexus.Service.Activity;
using Nexus.Service.Cooling;
using Nexus.Service.DependencyInjection;
using Nexus.Service.Devices;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Nexus.Service.Tests.DependencyInjection;

/// <summary>
/// Smoke tests for the per-domain DI extension methods that replaced the
/// 400-line block in Program.cs. We don't boot the full WebApplication here;
/// we just verify that every interface the extensions promise to register
/// resolves to a non-null implementation. If a typo or rename drops a
/// registration, this fires before the runtime has to.
/// </summary>
public class NexusServiceCollectionExtensionsTests
{
    private static IServiceProvider Build()
    {
        var services = new ServiceCollection();
        services
            .AddNexusCore()
            .AddNexusSensors()
            .AddNexusCooling()
            .AddNexusBenchmarks()
            .AddNexusLighting()
            .AddNexusDevices()
            .AddNexusPeripherals()
            .AddNexusActivity()
            .AddNexusNetwork()
            .AddNexusLifecycle()
            .AddNexusWeather()
            .AddNexusPanel(servicePort: 9400)
            .AddNexusLinuxDBus()
            // Registers HelperRegistry, which the Windows providers
            // (WindowsScreenTimeProvider, HelperMonitorEnumeratorProxy) depend on.
            // Program.cs registers it too; without it the graph is incomplete on Windows.
            .AddNexusHelper();
        services.AddLogging();
        services.AddHttpClient();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Core_resolves_token_and_config_store()
    {
        var sp = Build();
        Assert.NotNull(sp);
        Assert.NotNull(sp.GetRequiredService<Nexus.Service.Auth.TokenService>());
        Assert.NotNull(sp.GetRequiredService<IConfigStore>());
    }

    [Fact]
    public void Sensors_resolves_a_sensor_provider()
    {
        var sp = Build();
        Assert.NotNull(sp!.GetRequiredService<ISensorProvider>());
    }

    [Fact]
    public void Cooling_resolves_fan_control_and_curve_engine()
    {
        var sp = Build();
        Assert.NotNull(sp!.GetRequiredService<IFanControlProvider>());
        Assert.NotNull(sp.GetRequiredService<ICoolingProvider>());
        Assert.NotNull(sp.GetRequiredService<ICurveProvider>());
        Assert.NotNull(sp.GetRequiredService<CurveEngine>());
    }

    [Fact]
    public void Lighting_resolves_engine_and_provider()
    {
        var sp = Build();
        Assert.NotNull(sp!.GetRequiredService<Nexus.Service.Lighting.Engine.LightingEngine>());
        Assert.NotNull(sp.GetRequiredService<ILightingProvider>());
        // Constructs the CompositeLightingDeviceProvider and every backing provider
        // (OpenRGB/stub, NP50, MiniHub, CNVS, Q-series) - guards the composite ctor
        // arg order and that each provider's dependency graph resolves.
        Assert.NotNull(sp.GetRequiredService<Nexus.Service.Devices.ILightingDeviceProvider>());
    }

    /// <summary>
    /// The Stream Deck action executor is registered by an explicit factory
    /// (RgbBridge resolves optionally), so nothing else in the graph would
    /// catch a stale argument list - resolving it here does.
    /// </summary>
    [Fact]
    public void Peripherals_resolves_the_deck_action_executor()
    {
        var sp = Build();
        Assert.NotNull(sp!.GetRequiredService<Nexus.Service.Deck.DeckActionExecutor>());
        Assert.NotNull(sp.GetRequiredService<Nexus.Service.Deck.IDeckActionExecutor>());
    }

    [Fact]
    public void Devices_resolves_manager_and_handlers()
    {
        var sp = Build();
        Assert.NotNull(sp!.GetRequiredService<DeviceManager>());
        var handlers = sp.GetServices<IDeviceHandler>().ToArray();
        // CNVS, QSeries (Q60+Q80 collapsed), Y70, Keeb, IbpKeyboard, IbpMouse, FanHub, AW5,
        // NP50, SmartHub, LianLi, LianLiWireless, LianLiTl, Galahad2, CorsairLink,
        // CorsairLinkLcd, Strimer, Tryx, StreamDeck, Kraken, plus one JpegPanelHandler per
        // JpegPanelModel (Galahad II LCD, Corsair XC7, Corsair Elite Capellix, ID-Cooling FX-LCD).
        // ... plus one BulkPanelHandler per bulk-pipe driver (Thermalright, Ryujin, Screen 8.8).
        Assert.Equal(
            20 + Nexus.Service.Peripherals.JpegPanels.JpegPanelModel.All.Length + 3,
            handlers.Length);
    }

    [Fact]
    public void Activity_resolves_core_providers()
    {
        var sp = Build();
        Assert.NotNull(sp!.GetRequiredService<IScreenTimeProvider>());
        Assert.NotNull(sp.GetRequiredService<IAppDetectionProvider>());
        Assert.NotNull(sp.GetRequiredService<IMediaProvider>());
    }

    [Fact]
    public void Lifecycle_resolves_startup_shutdown_pawnio()
    {
        var sp = Build();
        Assert.NotNull(sp!.GetRequiredService<IStartupProvider>());
        Assert.NotNull(sp.GetRequiredService<IShutdownProvider>());
        Assert.NotNull(sp.GetRequiredService<IPawnIoProvider>());
    }

    [Fact]
    public void Hub_singletons_resolve_once()
    {
        var sp = Build();
        var a = sp!.GetRequiredService<MultiplexHub>();
        var b = sp.GetRequiredService<MultiplexHub>();
        Assert.Same(a, b);
    }

    [Fact]
    public void Monitoring_broadcaster_gets_both_halves_of_the_fan_header_rename_join()
    {
        // Optional constructor parameters: an unregistered dependency binds to null and
        // turns the rename off with nothing failing anywhere else.
        var sp = Build();
        Assert.True(sp!.GetRequiredService<Nexus.Service.Monitoring.MonitoringBroadcaster>().FanHeaderRenamesWired);
    }

    [Fact]
    public void Stream_deck_worker_gets_the_fan_provider_the_rename_join_needs()
    {
        // Constructed by an explicit factory, so a missed argument there disables renamed
        // fan names on physical keys while every other deck test stays green.
        var sp = Build();
        Assert.True(sp!.GetRequiredService<Nexus.Service.Peripherals.StreamDeck.StreamDeckConnectionWorker>()
            .FanHeaderRenamesWired);
    }
}
