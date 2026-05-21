using Qos.Service.Activity;
using Qos.Service.Cooling;
using Qos.Service.DependencyInjection;
using Qos.Service.Devices;
using Qos.Service.Lifecycle;
using Qos.Service.Lighting;
using Qos.Service.Persistence;
using Qos.Service.Sensors;
using Qos.Service.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Qos.Service.Tests.DependencyInjection;

/// <summary>
/// Smoke tests for the per-domain DI extension methods that replaced the
/// 400-line block in Program.cs. We don't boot the full WebApplication here;
/// we just verify that every interface the extensions promise to register
/// resolves to a non-null implementation. If a typo or rename drops a
/// registration, this fires before the runtime has to.
/// </summary>
public class QosServiceCollectionExtensionsTests
{
    private static IServiceProvider Build()
    {
        var services = new ServiceCollection();
        services
            .AddQosCore()
            .AddQosSensors()
            .AddQosCooling()
            .AddQosBenchmarks()
            .AddQosLighting()
            .AddQosDevices()
            .AddQosPeripherals()
            .AddQosActivity()
            .AddQosNetwork()
            .AddQosLifecycle()
            .AddQosWeather()
            .AddQosPanel(servicePort: 9400);
        services.AddLogging();
        services.AddHttpClient();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Core_resolves_token_and_config_store()
    {
        var sp = Build();
        Assert.NotNull(sp);
        Assert.NotNull(sp.GetRequiredService<Qos.Service.Auth.TokenService>());
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
        Assert.NotNull(sp!.GetRequiredService<Qos.Service.Lighting.Engine.LightingEngine>());
        Assert.NotNull(sp.GetRequiredService<ILightingProvider>());
    }

    [Fact]
    public void Devices_resolves_manager_and_handlers()
    {
        var sp = Build();
        Assert.NotNull(sp!.GetRequiredService<DeviceManager>());
        var handlers = sp.GetServices<IDeviceHandler>().ToArray();
        // CNVS, QSeries (Q60+Q80 collapsed), Y70, Keeb, FanHub, NP50
        Assert.Equal(6, handlers.Length);
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
}
