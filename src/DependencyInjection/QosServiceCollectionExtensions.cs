using System.Runtime.InteropServices;
using Qos.Service.Activity;
using Qos.Service.Auth;
using Qos.Service.Cooling;
using Qos.Service.Devices;
using Qos.Service.Discord;
using Qos.Service.Fps;
using Qos.Service.Lifecycle;
using Qos.Service.Lighting;
using Qos.Service.Lighting.Engine;
using Qos.Service.Obs;
using Qos.Service.Peripherals.Keeb;
using Qos.Service.Peripherals.QSeries;
using Qos.Service.Peripherals.Y70;
using Qos.Service.Persistence;
using Qos.Service.Platform;
using Qos.Service.Sensors;
using Qos.Service.Sockets;
using Qos.Service.Steam;
using Microsoft.Extensions.DependencyInjection;

namespace Qos.Service.DependencyInjection;

/// <summary>
/// Per-domain DI extension methods. Program.cs composes them into a single
/// fluent chain instead of inlining ~400 lines of platform-conditional
/// AddSingleton blocks. Each method keeps its own #if WINDOWS / runtime
/// platform checks so the AOT trim model is unchanged.
/// </summary>
public static class QosServiceCollectionExtensions
{
    public static IServiceCollection AddQosCore(this IServiceCollection services)
    {
#if WINDOWS
        services.AddSingleton<LhmComputer>();
        services.AddSingleton<IPerformanceProvider, Qos.Service.Platform.Windows.WindowsPerformanceProvider>();
#else
        services.AddSingleton<IPerformanceProvider>(_ => PerformanceProviderFactory.Create());
#endif
        services.AddSingleton<IConfigStore, JsonConfigStore>();
        services.AddSingleton<TokenService>();

#if WINDOWS
        services.AddSingleton<IFpsProvider, WindowsFpsProvider>();
#else
        services.AddSingleton<IFpsProvider, StubFpsProvider>();
#endif
        services.AddSingleton<MultiplexHub>();
        services.AddSingleton<LightingOutputHub>();
        services.AddSingleton<Qos.Service.Monitoring.MonitoringBroadcaster>();
        services.AddHostedService(sp => sp.GetRequiredService<Qos.Service.Monitoring.MonitoringBroadcaster>());
        // ConflictWatcher polls the running process list against
        // ConflictAppCatalog and publishes to the "conflicts" multiplex
        // topic. OpenRgbProcessManager is only registered on Win/Mac, so
        // we resolve it as optional so the watcher can ignore the bundled
        // child OpenRGB process where present.
        services.AddSingleton<Qos.Service.Conflicts.ConflictWatcher>(sp =>
            new Qos.Service.Conflicts.ConflictWatcher(
                sp.GetRequiredService<MultiplexHub>(),
                sp.GetService<Qos.Service.Lighting.Rgb.OpenRgbProcessManager>()));
        services.AddHostedService(sp => sp.GetRequiredService<Qos.Service.Conflicts.ConflictWatcher>());
        return services;
    }

    public static IServiceCollection AddQosSensors(this IServiceCollection services)
    {
#if WINDOWS
        services.AddSingleton<ISensorProvider, LibreHardwareSensorProvider>();
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            services.AddSingleton<ISensorProvider, LinuxSensorProvider>();
        else
            services.AddSingleton<ISensorProvider, MacSensorProvider>();
#endif
        services.AddSingleton<ProcessMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<ProcessMonitor>());
        return services;
    }

    public static IServiceCollection AddQosCooling(this IServiceCollection services)
    {
        services.AddSingleton<StubCoolingProvider>();
        // Pick the motherboard-side provider per platform, registered under
        // the concrete type. The public IFanControlProvider / ICoolingProvider
        // bindings below resolve to CompositeFanControlProvider so the curve
        // engine and routes see motherboard + NP50 channels through one shape.
#if WINDOWS
        services.AddSingleton<WindowsFanControlProvider>();
        services.AddSingleton<IFanControlProvider>(sp => new CompositeFanControlProvider(
            sp.GetRequiredService<WindowsFanControlProvider>(),
            sp.GetRequiredService<Np50CoolingProvider>(),
            sp.GetRequiredService<MiniHubCoolingProvider>()));
        services.AddSingleton<ICoolingProvider>(sp => (ICoolingProvider)sp.GetRequiredService<IFanControlProvider>());
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<MacFanControlProvider>();
            services.AddSingleton<IFanControlProvider>(sp => new CompositeFanControlProvider(
                sp.GetRequiredService<MacFanControlProvider>(),
                sp.GetRequiredService<Np50CoolingProvider>(),
                sp.GetRequiredService<MiniHubCoolingProvider>()));
            services.AddSingleton<ICoolingProvider>(sp => (ICoolingProvider)sp.GetRequiredService<IFanControlProvider>());
        }
        else
        {
            services.AddSingleton<IFanControlProvider>(sp => new CompositeFanControlProvider(
                sp.GetRequiredService<StubCoolingProvider>(),
                sp.GetRequiredService<Np50CoolingProvider>(),
                sp.GetRequiredService<MiniHubCoolingProvider>()));
            services.AddSingleton<ICoolingProvider>(sp => (ICoolingProvider)sp.GetRequiredService<IFanControlProvider>());
        }
#endif
        services.AddSingleton<Np50CoolingProvider>();
        services.AddSingleton<MiniHubCoolingProvider>();
        services.AddSingleton<ICurveProvider>(sp => sp.GetRequiredService<StubCoolingProvider>());
        services.AddSingleton<CurveEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<CurveEngine>());
        services.AddSingleton<CalibrationRunner>();
        return services;
    }

    public static IServiceCollection AddQosLighting(this IServiceCollection services)
    {
        services.AddSingleton<LightingEngine>();
        services.AddSingleton(_ => new Qos.Service.Lighting.Engine.Gpu.GpuContext(160, 90));
        services.AddSingleton<ILightingProvider, LightingProvider>();
        services.AddSingleton<IObsProvider, ObsProvider>();
        services.AddSingleton<ISteamProvider, SteamProvider>();
        services.AddSingleton<IDiscordProvider, DiscordProvider>();

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            services.AddSingleton<Qos.Service.Lighting.Rgb.OpenRgbProcessManager>();
            services.AddSingleton<Qos.Service.Lighting.Rgb.IRgbController>(_ =>
                new Qos.Service.Lighting.Rgb.OpenRgbController());
            services.AddSingleton<Qos.Service.Lighting.Rgb.RgbBridge>();
        }
        else
        {
            services.AddSingleton<Qos.Service.Lighting.Rgb.IRgbController, Qos.Service.Lighting.Rgb.NoOpRgbController>();
        }

        if (OperatingSystem.IsWindows())
            services.AddHostedService<Qos.Service.Lighting.Rgb.PowerEventListener>();

        return services;
    }

    public static IServiceCollection AddQosDevices(this IServiceCollection services)
    {
        services.AddSingleton<StubDeviceProvider>();
        services.AddSingleton<IDeviceProvider>(sp => sp.GetRequiredService<StubDeviceProvider>());

        // Lighting provider composition: OpenRGB (motherboard / RAM / AIO /
        // etc.) + NP50 hub (LS10 / LS30 / FP12 daisy-chained off Nexus Link
        // ports). The composite routes by id prefix so existing
        // /devices/lighting-devices/* routes don't change shape.
        // Np50LightingDeviceProvider doubles as an ILightingFrameContributor
        // so the RgbBridge can include NP50 zones in the engine's DeviceFrame
        // array and the engine's per-tick OnFrame fires for them. The
        // Np50LightingFrameWriter hosted service consumes those frames and
        // pushes per-port LED buffers to the hub.
        services.AddSingleton<Qos.Service.Lighting.Np50IdentifyTracker>();
        services.AddSingleton<Qos.Service.Lighting.Np50LightingDeviceProvider>();
        services.AddSingleton<Qos.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Qos.Service.Lighting.Np50LightingDeviceProvider>());
        services.AddSingleton<Qos.Service.Lighting.Np50LightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Qos.Service.Lighting.Np50LightingFrameWriter>());

        // MiniHub: lighting-only v1, mirrors the NP50 stack with a separate
        // hub coordinator + heartbeat + frame writer. Composite lighting
        // provider routes between OpenRGB / NP50 / MiniHub by id prefix.
        services.AddSingleton<Qos.Service.Lighting.MiniHubLightingDeviceProvider>();
        services.AddSingleton<Qos.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Qos.Service.Lighting.MiniHubLightingDeviceProvider>());
        services.AddSingleton<Qos.Service.Lighting.MiniHubLightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Qos.Service.Lighting.MiniHubLightingFrameWriter>());

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            services.AddSingleton<Qos.Service.Lighting.Rgb.OpenRgbLightingDeviceProvider>();
            services.AddSingleton<ILightingDeviceProvider>(sp => new Qos.Service.Lighting.CompositeLightingDeviceProvider(
                sp.GetRequiredService<Qos.Service.Lighting.Rgb.OpenRgbLightingDeviceProvider>(),
                sp.GetRequiredService<Qos.Service.Lighting.Np50LightingDeviceProvider>(),
                sp.GetRequiredService<Qos.Service.Lighting.MiniHubLightingDeviceProvider>()));
        }
        else
        {
            services.AddSingleton<ILightingDeviceProvider>(sp => new Qos.Service.Lighting.CompositeLightingDeviceProvider(
                sp.GetRequiredService<StubDeviceProvider>(),
                sp.GetRequiredService<Qos.Service.Lighting.Np50LightingDeviceProvider>(),
                sp.GetRequiredService<Qos.Service.Lighting.MiniHubLightingDeviceProvider>()));
        }

        services.AddSingleton<IDeviceHandler, Qos.Service.Devices.Handlers.CnvsHandler>();
        services.AddSingleton<IDeviceHandler, Qos.Service.Devices.Handlers.QSeriesHandler>();
        services.AddSingleton<IDeviceHandler, Qos.Service.Devices.Handlers.Y70Handler>();
        services.AddSingleton<IDeviceHandler, Qos.Service.Devices.Handlers.KeebHandler>();
        services.AddSingleton<IDeviceHandler, Qos.Service.Devices.Handlers.FanHubHandler>();
        services.AddSingleton<IDeviceHandler, Qos.Service.Devices.Handlers.Np50Handler>();

        // NP50 hub: serial port discovery + transport factory + singleton hub +
        // 2-second heartbeat poller. Discovery is Windows-only for now; non-
        // Windows builds get a stub that finds nothing (the hub silently stays
        // disconnected, which keeps the rest of the service composing cleanly).
#if WINDOWS
        services.AddSingleton<Qos.Service.Peripherals.Hyte.Np50.INp50PortDiscovery,
                              Qos.Service.Peripherals.Hyte.Np50.WindowsNp50PortDiscovery>();
#else
        services.AddSingleton<Qos.Service.Peripherals.Hyte.Np50.INp50PortDiscovery,
                              Qos.Service.Peripherals.Hyte.Np50.StubNp50PortDiscovery>();
#endif
        services.AddSingleton<Qos.Service.Peripherals.Hyte.Np50.Np50Hub>(sp =>
            new Qos.Service.Peripherals.Hyte.Np50.Np50Hub(
                sp.GetRequiredService<Qos.Service.Peripherals.Hyte.Np50.INp50PortDiscovery>(),
                port => new Qos.Service.Peripherals.Hyte.Np50.Np50SerialTransport(port.PortName, port.Serial)));
        services.AddSingleton<Qos.Service.Peripherals.Hyte.Np50.Np50HeartbeatWorker>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Qos.Service.Peripherals.Hyte.Np50.Np50HeartbeatWorker>());

        // MiniHub hub: own port-discovery instance (we don't bind it to
        // INp50PortDiscovery in DI because that interface is already taken
        // by the NP50 binding — instead the hub factory below constructs
        // the MiniHub-specific discovery inline). Transport factory reuses
        // the generic serial-port wrapper since it's product-agnostic.
        services.AddSingleton<Qos.Service.Peripherals.Hyte.MiniHub.MiniHubHub>(sp =>
        {
            Qos.Service.Peripherals.Hyte.Np50.INp50PortDiscovery discovery;
#if WINDOWS
            discovery = new Qos.Service.Peripherals.Hyte.MiniHub.WindowsMiniHubPortDiscovery();
#else
            discovery = new Qos.Service.Peripherals.Hyte.MiniHub.StubMiniHubPortDiscovery();
#endif
            return new Qos.Service.Peripherals.Hyte.MiniHub.MiniHubHub(
                discovery,
                port => new Qos.Service.Peripherals.Hyte.Np50.Np50SerialTransport(port.PortName, port.Serial));
        });
        services.AddSingleton<Qos.Service.Peripherals.Hyte.MiniHub.MiniHubHeartbeatWorker>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Qos.Service.Peripherals.Hyte.MiniHub.MiniHubHeartbeatWorker>());

#if WINDOWS
        services.AddSingleton<Qos.Service.Devices.Detection.WindowsUsbEnumerator>();
        services.AddSingleton<Qos.Service.Devices.Detection.IUsbEnumerator>(sp =>
            new Qos.Service.Devices.Detection.CachingUsbEnumerator(
                sp.GetRequiredService<Qos.Service.Devices.Detection.WindowsUsbEnumerator>()));
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<Qos.Service.Devices.Detection.MacUsbEnumerator>();
            services.AddSingleton<Qos.Service.Devices.Detection.IUsbEnumerator>(sp =>
                new Qos.Service.Devices.Detection.CachingUsbEnumerator(
                    sp.GetRequiredService<Qos.Service.Devices.Detection.MacUsbEnumerator>()));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<Qos.Service.Devices.Detection.LinuxUsbEnumerator>();
            services.AddSingleton<Qos.Service.Devices.Detection.IUsbEnumerator>(sp =>
                new Qos.Service.Devices.Detection.CachingUsbEnumerator(
                    sp.GetRequiredService<Qos.Service.Devices.Detection.LinuxUsbEnumerator>()));
        }
        else
        {
            services.AddSingleton<Qos.Service.Devices.Detection.StubUsbEnumerator>();
            services.AddSingleton<Qos.Service.Devices.Detection.IUsbEnumerator>(sp =>
                new Qos.Service.Devices.Detection.CachingUsbEnumerator(
                    sp.GetRequiredService<Qos.Service.Devices.Detection.StubUsbEnumerator>()));
        }
#endif
        services.AddSingleton<DeviceManager>();
        services.AddSingleton<Qos.Service.Devices.DeviceBroadcaster>();
        services.AddHostedService(sp => sp.GetRequiredService<Qos.Service.Devices.DeviceBroadcaster>());
        return services;
    }

    public static IServiceCollection AddQosPeripherals(this IServiceCollection services)
    {
#if WINDOWS
        services.AddSingleton<Qos.Service.Peripherals.Hid.IHidEnumerator, Qos.Service.Peripherals.Hid.WindowsHidEnumerator>();
#else
        services.AddSingleton<Qos.Service.Peripherals.Hid.IHidEnumerator, Qos.Service.Peripherals.Hid.StubHidEnumerator>();
#endif
        services.AddSingleton<Qos.Service.Peripherals.PeripheralRegistry>();

        services.AddSingleton<StubKeebProvider>();
        services.AddSingleton<Qos.Service.Peripherals.Keeb.KeebSession>();
        services.AddSingleton<Qos.Service.Peripherals.Keeb.HidKeebProvider>();
        services.AddSingleton<IKeebProvider>(sp => sp.GetRequiredService<Qos.Service.Peripherals.Keeb.HidKeebProvider>());
        services.AddHostedService<Qos.Service.Peripherals.Keeb.KeebHotswapHost>();
#if WINDOWS
        services.AddSingleton<IInputterProvider, WindowsInputter>();
#else
        services.AddSingleton<IInputterProvider>(sp => sp.GetRequiredService<StubKeebProvider>());
#endif

        services.AddSingleton<IY70Provider, StubY70Provider>();
        services.AddSingleton<IQSeriesProvider, StubQSeriesProvider>();

#if WINDOWS
        // Brightness proxies through the user-session helper - DDC/CI and
        // laptop-panel APIs are unreliable from Session 0.
        services.AddSingleton<Qos.Service.Platform.Displays.IDisplayBrightnessProvider,
            Qos.Service.Platform.Displays.HelperDisplayBrightnessProxy>();
        // Y70 display rotation also routes through the helper: Session 0
        // cannot ChangeDisplaySettingsEx against the user's monitors.
        services.AddSingleton<Qos.Service.Platform.Displays.IDisplayOrientationProvider,
            Qos.Service.Platform.Displays.HelperDisplayOrientationProxy>();
        // Monitor enumeration follows the same Session 0 limitation: DXGI
        // EnumOutputs returns nothing under LocalSystem, so the helper does
        // the enumeration and we proxy.
        services.AddSingleton<Qos.Service.Platform.IMonitorEnumerator,
            Qos.Service.Platform.Displays.HelperMonitorEnumeratorProxy>();
        // Screen-mirror frames also flow through the helper - DXGI desktop
        // duplication is Session 0-blind, so the helper captures + downsamples
        // and pushes canvas-resolution RGB24 over the pipe.
        services.AddSingleton<Qos.Service.Lighting.Capture.IScreenFrameSource,
            Qos.Service.Lighting.Capture.HelperScreenFrameSource>();
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<Qos.Service.Platform.Displays.IDisplayBrightnessProvider,
                Qos.Service.Platform.Displays.MacDisplayBrightnessProvider>();
        }
        else
        {
            services.AddSingleton<Qos.Service.Platform.Displays.IDisplayBrightnessProvider,
                Qos.Service.Platform.Displays.StubDisplayBrightnessProvider>();
        }
        services.AddSingleton<Qos.Service.Platform.IMonitorEnumerator,
            Qos.Service.Platform.DefaultMonitorEnumerator>();
        services.AddSingleton<Qos.Service.Platform.Displays.IDisplayOrientationProvider,
            Qos.Service.Platform.Displays.NoopDisplayOrientationProvider>();
#endif
        services.AddSingleton<Qos.Service.Platform.Displays.DisplayBrightnessController>();
        return services;
    }

    public static IServiceCollection AddQosActivity(this IServiceCollection services)
    {
        services.AddSingleton<Qos.Service.Activity.Storage.IScreenTimeStore>(_ =>
        {
            try
            {
                return new Qos.Service.Activity.Storage.SqliteScreenTimeStore();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[screentime-store] sqlite unavailable, using in-memory: {ex.Message}");
                return new Qos.Service.Activity.Storage.InMemoryScreenTimeStore();
            }
        });

#if WINDOWS
        services.AddSingleton<IScreenTimeProvider, WindowsScreenTimeProvider>();
        services.AddSingleton<IAppDetectionProvider, StubAppDetectionProvider>();
        services.AddSingleton<IShortcutsProvider, WindowsShortcutsProvider>();
        services.AddSingleton<IMediaProvider, WindowsMediaProvider>();
        services.AddSingleton<IVolumeProvider, WindowsVolumeProvider>();
        services.AddSingleton<IBeatsProvider, WasapiLoopbackBeatsProvider>();
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<IScreenTimeProvider, MacScreenTimeProvider>();
            services.AddSingleton<IAppDetectionProvider, MacAppDetectionProvider>();
            services.AddSingleton<IShortcutsProvider, MacShortcutsProvider>();
            services.AddSingleton<IMediaProvider, MacMediaProvider>();
            services.AddSingleton<IVolumeProvider, MacVolumeProvider>();
            services.AddSingleton<IBeatsProvider, MacAudioBeatsProvider>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<Qos.Service.Activity.LinuxScreenTimeProvider>();
            services.AddHostedService(sp => sp.GetRequiredService<Qos.Service.Activity.LinuxScreenTimeProvider>());
            services.AddSingleton<IScreenTimeProvider>(sp => sp.GetRequiredService<Qos.Service.Activity.LinuxScreenTimeProvider>());
            services.AddSingleton<IAppDetectionProvider, StubAppDetectionProvider>();
            services.AddSingleton<IShortcutsProvider, LinuxShortcutsProvider>();
            services.AddSingleton<IMediaProvider, StubMediaProvider>();
            services.AddSingleton<IVolumeProvider, StubVolumeProvider>();
            services.AddSingleton<IBeatsProvider, BeatsProvider>();
        }
        else
        {
            services.AddSingleton<IScreenTimeProvider, StubScreenTimeProvider>();
            services.AddSingleton<IAppDetectionProvider, StubAppDetectionProvider>();
            services.AddSingleton<IShortcutsProvider, StubShortcutsProvider>();
            services.AddSingleton<IMediaProvider, StubMediaProvider>();
            services.AddSingleton<IVolumeProvider, StubVolumeProvider>();
            services.AddSingleton<IBeatsProvider, StubBeatsProvider>();
        }
#endif
        return services;
    }

    public static IServiceCollection AddQosNetwork(this IServiceCollection services)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<MacNetworkProvider>();
            services.AddHostedService(sp => sp.GetRequiredService<MacNetworkProvider>());
            services.AddSingleton<INetworkProvider>(sp => sp.GetRequiredService<MacNetworkProvider>());
        }
#if WINDOWS
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            services.AddSingleton<WindowsNetworkProvider>();
            services.AddHostedService(sp => sp.GetRequiredService<WindowsNetworkProvider>());
            services.AddSingleton<INetworkProvider>(sp => sp.GetRequiredService<WindowsNetworkProvider>());
        }
#endif
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<LinuxNetworkProvider>();
            services.AddHostedService(sp => sp.GetRequiredService<LinuxNetworkProvider>());
            services.AddSingleton<INetworkProvider>(sp => sp.GetRequiredService<LinuxNetworkProvider>());
        }
        else
        {
            services.AddSingleton<INetworkProvider, StubNetworkProvider>();
        }
        return services;
    }

    public static IServiceCollection AddQosLifecycle(this IServiceCollection services)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            services.AddSingleton<IStartupProvider, MacStartupProvider>();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            services.AddSingleton<IStartupProvider, WindowsStartupProvider>();
        else
            services.AddSingleton<IStartupProvider, StubStartupProvider>();

        services.AddSingleton<IShutdownProvider, StubShutdownProvider>();
#if WINDOWS
        services.AddSingleton<IPawnIoProvider, PawnIoProvider>();
#else
        services.AddSingleton<IPawnIoProvider, StubPawnIoProvider>();
#endif

        // Replay persisted lighting + cooling state to hardware on startup.
        // Lives in Lifecycle because it doesn't belong to a single domain.
        services.AddHostedService<AutoRestoreOnStart>();
        return services;
    }

    public static IServiceCollection AddQosBenchmarks(this IServiceCollection services)
    {
        services.AddSingleton<Qos.Service.Benchmarks.IBenchmarkProvider, Qos.Service.Benchmarks.Providers.DefaultBenchmarkProvider>();
        services.AddSingleton<Qos.Service.Benchmarks.BenchmarkRunner>();
        services.AddSingleton<ProfileManager>();
        services.AddSingleton<Qos.Service.Media.MediaLibrary>();
        return services;
    }

    public static IServiceCollection AddQosWeather(this IServiceCollection services)
    {
        services.AddSingleton<Qos.Service.Platform.Weather.IWeatherProvider, Qos.Service.Platform.Weather.OpenMeteoWeatherProvider>();
        return services;
    }

    /// <summary>
    /// Widget runtime. Phase 0 only registers discovery; serving routes are
    /// wired in <see cref="Qos.Service.Routes.WidgetRoutes.MapWidgetEndpoints"/>
    /// in Program.cs.
    /// </summary>
    public static IServiceCollection AddQosWidgets(this IServiceCollection services)
    {
        services.AddSingleton<Qos.Service.Widgets.WidgetRegistry>();
        services.AddSingleton<Qos.Service.Widgets.WidgetSettingsService>();
        services.AddSingleton<Qos.Service.Widgets.WidgetProxyService>();
        services.AddSingleton<Qos.Service.Widgets.WidgetInstaller>();
        services.AddSingleton<Qos.Service.Widgets.WidgetCodeSessionService>();
        services.AddSingleton<Qos.Service.Widgets.WidgetActionRegistry>(sp =>
        {
            var registry = new Qos.Service.Widgets.WidgetActionRegistry();
            // First-party action modules. Each registers its own actions
            // by name; the manifest's capabilities.dispatch allowlist
            // gates per-widget access.
            Qos.Service.Widgets.WidgetActions.DisplayActions.RegisterAll(registry);
            Qos.Service.Widgets.WidgetActions.ScreentimeActions.RegisterAll(registry);
            Qos.Service.Widgets.WidgetActions.MacroActions.RegisterAll(registry);
            return registry;
        });
        return services;
    }

    public static IServiceCollection AddQosPanel(this IServiceCollection services, int servicePort)
    {
        // QSeriesPortWatcher keeps `adb reverse tcp:{servicePort}` alive
        // while a HYTE Q60 / Q80 USB display is attached. Without it,
        // every time Y70's adb-server restarts the panel's multiplex
        // WebSocket on the Q-series silently freezes. Windows-only — the
        // Q-series host stack lives on the Y70 PC.
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<Qos.Service.QSeries.QSeriesPortWatcher>(
                _ => new Qos.Service.QSeries.QSeriesPortWatcher(servicePort));
            services.AddHostedService(sp =>
                sp.GetRequiredService<Qos.Service.QSeries.QSeriesPortWatcher>());
        }

        services.AddSingleton<Qos.Service.Panel.PanelKioskLauncher>();
        services.AddSingleton<Qos.Service.Panel.PanelOverlayHostLauncher>();
        // IOverlayHost picks the right impl per OS. Mac spawns the Swift
        // sidecar qos-overlay-helper (transparent NSWindow + WKWebView
        // per NSScreen). Windows spawns qos-overlay.exe (WinForms +
        // WebView2). Linux is a no-op until an X11/Wayland surface is added.
        if (OperatingSystem.IsMacOS())
        {
            Qos.Service.Platform.Mac.MacOverlayHostLauncher.Configure(servicePort);
            services.AddSingleton<Qos.Service.Panel.IOverlayHost, Qos.Service.Platform.Mac.MacOverlayHostLauncher>();
        }
        else if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<Qos.Service.Panel.IOverlayHost>(sp =>
                sp.GetRequiredService<Qos.Service.Panel.PanelOverlayHostLauncher>());
        }
        else
        {
            services.AddSingleton<Qos.Service.Panel.IOverlayHost, Qos.Service.Panel.NoopOverlayHost>();
        }
        services.AddSingleton<Qos.Service.Panel.PanelPhonePairingService>();
        services.AddSingleton<Qos.Service.Panel.PanelDeviceRegistry>();
        return services;
    }

    public static IServiceCollection AddQosLinuxDBus(this IServiceCollection services)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<Qos.Service.Platform.Linux.DBus.DBusConnection>();
            services.AddHostedService<Qos.Service.Platform.Linux.LinuxTrayService>();
        }
        return services;
    }

    /// <summary>
    /// Windows user-session helper IPC. The named-pipe server, registry, and
    /// typed command client. Other platforms run their providers natively in
    /// the user-context daemon, so no helper subsystem is registered.
    /// </summary>
    public static IServiceCollection AddQosHelper(this IServiceCollection services)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<Qos.Service.Helper.HelperRegistry>();
            services.AddSingleton<Qos.Service.Helper.HelperPipeServer>();
            services.AddHostedService(sp => sp.GetRequiredService<Qos.Service.Helper.HelperPipeServer>());
        }
#endif
        return services;
    }
}
