using Nexus.Service.Activity;
using Nexus.Service.Auth;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Discord;
using Nexus.Service.Fps;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Obs;
using Nexus.Service.Peripherals.Keeb;
using Nexus.Service.Peripherals.QSeries;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Plugins;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;
using Nexus.Service.Steam;
using Microsoft.Extensions.DependencyInjection;

namespace Nexus.Service.DependencyInjection;

/// <summary>
/// Per-domain DI extension methods. Program.cs composes them into a single
/// fluent chain instead of inlining ~400 lines of platform-conditional
/// AddSingleton blocks. Each method keeps its own #if WINDOWS / runtime
/// platform checks so the AOT trim model is unchanged.
/// </summary>
public static class NexusServiceCollectionExtensions
{
    public static IServiceCollection AddNexusCore(this IServiceCollection services)
    {
#if WINDOWS
        services.AddSingleton<LhmComputer>();
        services.AddSingleton<IPerformanceProvider, Nexus.Service.Platform.Windows.WindowsPerformanceProvider>();
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
        services.AddSingleton<Nexus.Service.Monitoring.MonitoringBroadcaster>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Monitoring.MonitoringBroadcaster>());
        // ConflictWatcher polls the running process list against
        // ConflictAppCatalog and publishes to the "conflicts" multiplex
        // topic. OpenRgbProcessManager is only registered on Win/Mac, so
        // we resolve it as optional so the watcher can ignore the bundled
        // child OpenRGB process where present.
        services.AddSingleton<Nexus.Service.Conflicts.ConflictWatcher>(sp =>
            new Nexus.Service.Conflicts.ConflictWatcher(
                sp.GetRequiredService<MultiplexHub>(),
                sp.GetService<Nexus.Service.Lighting.Rgb.OpenRgbProcessManager>()));
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Conflicts.ConflictWatcher>());
        return services;
    }

    public static IServiceCollection AddNexusSensors(this IServiceCollection services)
    {
#if WINDOWS
        services.AddSingleton<ISensorProvider, LibreHardwareSensorProvider>();
#elif MACOS
        services.AddSingleton<ISensorProvider, MacSensorProvider>();
#elif LINUX
        services.AddSingleton<ISensorProvider, LinuxSensorProvider>();
#else
#error No ISensorProvider for this target — wire one when adding a platform.
#endif
        services.AddSingleton<ProcessMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<ProcessMonitor>());
        services.AddSingleton<Nexus.Service.Activity.GpuProcessMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Activity.GpuProcessMonitor>());
        services.AddSingleton<SystemSpecsCollector>();
        // Pre-warms the specs cache in the background after host start so the
        // first Devices → System Specs request doesn't pay a cold PowerShell
        // spawn. Hard rule: this MUST stay off the startup critical path —
        // see SystemSpecsPrewarmService.ExecuteAsync.
        services.AddHostedService<SystemSpecsPrewarmService>();
        // Pushes a pairing-QR-refresh nudge to the dashboard when the host IP
        // changes (VPN/Wi-Fi↔wired/DHCP), so a displayed QR doesn't keep
        // embedding a stale LAN address until its TTL. Off the critical path —
        // it only subscribes to NetworkChange.NetworkAddressChanged.
        services.AddHostedService<Nexus.Service.Net.NetworkAddressChangeListener>();
        // One-time hardware/specs snapshot to service.log after discovery
        // settles, so a tester's log opens with the full detected picture.
        // Off the critical path; see StartupDiagnosticsDumpService.ExecuteAsync.
        services.AddHostedService<Nexus.Service.Diagnostics.StartupDiagnosticsDumpService>();
        // Anonymous fleet heartbeat — post-boot, off the critical path, gated by
        // the collect-anonymous-data setting (default on).
        services.AddHostedService<Nexus.Service.Telemetry.HeartbeatService>();
        // Product telemetry — anonymous events to PostHog, same opt-out + install
        // id as the heartbeat. Inject ITelemetry and call Capture(...). The flush
        // worker stays dormant until a PostHog key is configured (PostHogOptions).
        services.AddSingleton<Nexus.Service.Telemetry.TelemetryClient>();
        services.AddSingleton<Nexus.Service.Telemetry.ITelemetry>(
            sp => sp.GetRequiredService<Nexus.Service.Telemetry.TelemetryClient>());
        services.AddSingleton<Nexus.Service.Telemetry.PostHogOptions>();
        services.AddSingleton<Nexus.Service.Telemetry.ITelemetrySink, Nexus.Service.Telemetry.PostHogSink>();
        services.AddHostedService<Nexus.Service.Telemetry.TelemetryFlushService>();
        // Attaches the hardware/system profile (specs + recognized connected
        // devices) to the anonymous person via $set. Post-boot, refreshed.
        services.AddHostedService<Nexus.Service.Telemetry.SystemProfileService>();
#if WINDOWS
        // Triggers the IFanControlProvider singleton ctor (which transitively
        // constructs LhmComputer + kicks off its background Open()) right
        // after host start. Same "warmup off critical path" pattern as
        // SystemSpecsPrewarmService. Windows-only because LhmComputer is the
        // only IFanControlProvider implementation that needs prewarming.
        services.AddHostedService<LhmWarmupService>();
#endif
        return services;
    }

    public static IServiceCollection AddNexusCooling(this IServiceCollection services)
    {
        // The runtime plugin-provider registry — the single seam the cooling /
        // sensor / device / DFU composites read so a plugin can add a source
        // without a rebuild. Empty until the broker (Phase 2) registers one.
        services.AddSingleton<PluginProviderRegistry>();
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
            sp.GetRequiredService<MiniHubCoolingProvider>(),
            sp.GetRequiredService<PluginProviderRegistry>(),
            new CompositeFanControlProvider.FanSource(
                SmartHubCoolingProvider.IsSmartHubId, sp.GetRequiredService<SmartHubCoolingProvider>())));
        services.AddSingleton<ICoolingProvider>(sp => (ICoolingProvider)sp.GetRequiredService<IFanControlProvider>());
#elif MACOS
        services.AddSingleton<MacFanControlProvider>();
        services.AddSingleton<IFanControlProvider>(sp => new CompositeFanControlProvider(
            sp.GetRequiredService<MacFanControlProvider>(),
            sp.GetRequiredService<Np50CoolingProvider>(),
            sp.GetRequiredService<MiniHubCoolingProvider>(),
            sp.GetRequiredService<PluginProviderRegistry>(),
            new CompositeFanControlProvider.FanSource(
                SmartHubCoolingProvider.IsSmartHubId, sp.GetRequiredService<SmartHubCoolingProvider>())));
        services.AddSingleton<ICoolingProvider>(sp => (ICoolingProvider)sp.GetRequiredService<IFanControlProvider>());
#elif LINUX
        // hwmon (motherboard + AMD GPU via amdgpu) + liquidctl USB coolers
        // + NVIDIA GPU fans, layered onto the same composite the routes see.
        services.AddSingleton<LinuxFanControlProvider>();
        services.AddSingleton<LinuxLiquidctlProvider>();
        services.AddSingleton<LinuxNvidiaFanProvider>();
        services.AddSingleton<IFanControlProvider>(sp => new CompositeFanControlProvider(
            sp.GetRequiredService<LinuxFanControlProvider>(),
            sp.GetRequiredService<Np50CoolingProvider>(),
            sp.GetRequiredService<MiniHubCoolingProvider>(),
            sp.GetRequiredService<PluginProviderRegistry>(),
            new CompositeFanControlProvider.FanSource(
                SmartHubCoolingProvider.IsSmartHubId, sp.GetRequiredService<SmartHubCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                LinuxLiquidctlProvider.IsLiquidctlId, sp.GetRequiredService<LinuxLiquidctlProvider>()),
            new CompositeFanControlProvider.FanSource(
                LinuxNvidiaFanProvider.IsNvidiaId, sp.GetRequiredService<LinuxNvidiaFanProvider>())));
        services.AddSingleton<ICoolingProvider>(sp => (ICoolingProvider)sp.GetRequiredService<IFanControlProvider>());
#else
        services.AddSingleton<IFanControlProvider>(sp => new CompositeFanControlProvider(
            sp.GetRequiredService<StubCoolingProvider>(),
            sp.GetRequiredService<Np50CoolingProvider>(),
            sp.GetRequiredService<MiniHubCoolingProvider>(),
            sp.GetRequiredService<PluginProviderRegistry>(),
            new CompositeFanControlProvider.FanSource(
                SmartHubCoolingProvider.IsSmartHubId, sp.GetRequiredService<SmartHubCoolingProvider>())));
        services.AddSingleton<ICoolingProvider>(sp => (ICoolingProvider)sp.GetRequiredService<IFanControlProvider>());
#endif
        services.AddSingleton<Np50CoolingProvider>();
        services.AddSingleton<MiniHubCoolingProvider>();
        services.AddSingleton<SmartHubCoolingProvider>();
        services.AddSingleton<ICurveProvider>(sp => sp.GetRequiredService<StubCoolingProvider>());
        services.AddSingleton<CurveEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<CurveEngine>());
        services.AddSingleton<CalibrationRunner>();
        return services;
    }

    public static IServiceCollection AddNexusLighting(this IServiceCollection services)
    {
        services.AddSingleton<LightingEngine>();
        // Community LED mappings: resolver state for contributor frames, the
        // registry client (disk-cached, offline-tolerant), the apply
        // orchestrator shared by routes + auto-apply, and the first-seen
        // auto-apply worker.
        services.AddSingleton<Nexus.Service.Lighting.Mappings.ContributorFrameLayouts>();
        services.AddSingleton<Nexus.Service.Lighting.Mappings.MappingCloudClient>();
        services.AddSingleton<Nexus.Service.Lighting.Mappings.MappingApplyService>();
        // Zones model: aggregates every provider's partitionable-device
        // structures and resolves any card id to its zone + layout.
        services.AddSingleton<Nexus.Service.Lighting.Zones.ZoneTopology>();
        services.AddHostedService<Nexus.Service.Lighting.Mappings.MappingAutoApplyService>();
        services.AddSingleton(sp => new Nexus.Service.Lighting.Engine.Gpu.GpuContext(
            160, 90, sp.GetService<Nexus.Service.Persistence.IConfigStore>()));
        services.AddSingleton<ILightingProvider, LightingProvider>();
        services.AddSingleton<IObsProvider, ObsProvider>();
        services.AddSingleton<ISteamProvider, SteamProvider>();
        services.AddSingleton<IDiscordProvider, DiscordProvider>();

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            services.AddSingleton<Nexus.Service.Lighting.Rgb.OpenRgbProcessManager>();
            services.AddSingleton<Nexus.Service.Lighting.Rgb.IRgbController>(_ =>
                new Nexus.Service.Lighting.Rgb.OpenRgbController());
            services.AddSingleton<Nexus.Service.Lighting.Rgb.RgbBridge>();
        }
        else
        {
            services.AddSingleton<Nexus.Service.Lighting.Rgb.IRgbController, Nexus.Service.Lighting.Rgb.NoOpRgbController>();
        }

        if (OperatingSystem.IsWindows())
            services.AddHostedService<Nexus.Service.Lighting.Rgb.PowerEventListener>();

        return services;
    }

    public static IServiceCollection AddNexusWebcam(this IServiceCollection services)
    {
        // Linux writes MJPEG passthrough into v4l2loopback (optional env
        // override pins an explicit device node instead of the sysfs name
        // scan); Windows decodes H.264/MJPEG via Media Foundation into the
        // bundled NexusVCam MF virtual camera; macOS pipes encoded frames to
        // the bundled camera helper, which decodes and feeds the CMIO camera
        // extension's sink stream.
        if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<Nexus.Service.Webcam.IVirtualCamera>(_ =>
                new Nexus.Service.Webcam.Linux.V4l2LoopbackCamera(
                    Environment.GetEnvironmentVariable("NEXUS_WEBCAM_DEVICE")));
        }
        else if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<Nexus.Service.Webcam.IVirtualCamera>(_ =>
                new Nexus.Service.Webcam.Windows.WindowsVirtualCamera());
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<Nexus.Service.Webcam.IVirtualCamera>(_ =>
                new Nexus.Service.Webcam.Mac.CmioCamera());
        }
        else
        {
            services.AddSingleton<Nexus.Service.Webcam.IVirtualCamera, Nexus.Service.Webcam.NullVirtualCamera>();
        }
        services.AddSingleton<Nexus.Service.Webcam.WebcamSessionManager>();
        return services;
    }

    public static IServiceCollection AddNexusDevices(this IServiceCollection services)
    {
        // CNVS hub: serial-port discovery + hub singleton + connection
        // worker that grabs COM7 at startup before OpenRGB-headless can
        // claim it (whoever opens the COM port first wins on Windows
        // serial — same race-and-hold pattern that lets NP50 and MiniHub
        // coexist with OpenRGB). Windows-only discovery; non-Windows gets
        // a stub that never finds anything.
#if WINDOWS
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Cnvs.ICnvsPortDiscovery,
                              Nexus.Service.Peripherals.Hyte.Cnvs.WindowsCnvsPortDiscovery>();
#elif LINUX
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Cnvs.ICnvsPortDiscovery,
                              Nexus.Service.Peripherals.Hyte.Cnvs.LinuxCnvsPortDiscovery>();
#else
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Cnvs.ICnvsPortDiscovery,
                              Nexus.Service.Peripherals.Hyte.Cnvs.StubCnvsPortDiscovery>();
#endif
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Cnvs.CnvsHub>();
        services.AddHostedService<Nexus.Service.Peripherals.Hyte.Cnvs.CnvsConnectionWorker>();

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
        services.AddSingleton<Nexus.Service.Lighting.Np50IdentifyTracker>();
        services.AddSingleton<Nexus.Service.Lighting.Np50LightingDeviceProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.Np50LightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.Np50LightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.Np50LightingFrameWriter>());

        // MiniHub: lighting-only v1, mirrors the NP50 stack with a separate
        // hub coordinator + heartbeat + frame writer. Composite lighting
        // provider routes between OpenRGB / NP50 / MiniHub / CNVS by id prefix.
        services.AddSingleton<Nexus.Service.Lighting.MiniHubLightingDeviceProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.MiniHubLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.MiniHubLightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.MiniHubLightingFrameWriter>());

        // Smart Hub: ARGB + PWM-fan hub, same engine→writer pipeline as the
        // MiniHub. Surfaces four resizable ARGB ports; the writer streams them
        // at 30 Hz. Cooling side lives in SmartHubCoolingProvider.
        services.AddSingleton<Nexus.Service.Lighting.SmartHubLightingDeviceProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.SmartHubLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.SmartHubLightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.SmartHubLightingFrameWriter>());

        // CNVS lighting: CnvsHub owns COM7 (not OpenRGB). This provider
        // surfaces the 50-LED zone to the lighting engine and the writer pushes
        // 30 Hz LED frames to the hub. Reuses Np50IdentifyTracker (shared
        // identify-flash state).
        services.AddSingleton<Nexus.Service.Lighting.CnvsLightingDeviceProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.CnvsLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.CnvsLightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.CnvsLightingFrameWriter>());

        // Q-series cooler lighting: QSeriesCoolerHub owns the cooler's serial port
        // (OpenRGB does not drive 1st-party HYTE devices), so this provider
        // surfaces the cooler LEDs and the writer pushes 30 Hz frames. Reuses
        // Np50IdentifyTracker.
        services.AddSingleton<Nexus.Service.Lighting.QSeriesLightingDeviceProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.QSeriesLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.QSeriesLightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.QSeriesLightingFrameWriter>());

        // Keeb TKL lighting: KeebHub owns the keyboard's vendor HID interface
        // (OpenRGB's "HYTE Keeb TKL" detector is disabled), this provider
        // surfaces the keys + underglow zones, and the writer streams 30 Hz
        // RGB frames directly over HID. Reuses Np50IdentifyTracker for the
        // shared identify-flash state.
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Keeb.KeebHub>();
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Keeb.KeebSettingsApplier>();
        services.AddSingleton<Nexus.Service.Lighting.KeebLightingDeviceProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.KeebLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.Zones.IDeviceStructureSource>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.KeebLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.KeebLightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.KeebLightingFrameWriter>());
        services.AddHostedService(sp => new Nexus.Service.Peripherals.Hyte.Keeb.KeebConnectionWorker(
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.Keeb.KeebHub>(),
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.Keeb.KeebSettingsApplier>(),
            sp.GetRequiredService<Nexus.Service.Devices.Detection.HardwarePresence>(),
            sp.GetRequiredService<Nexus.Service.Lighting.KeebLightingDeviceProvider>()));
        services.AddHostedService<Nexus.Service.Peripherals.Hyte.Keeb.KeebInputWorker>();

        // Smart (network) lights — Philips Hue, Nanoleaf, Govee today; WLED /
        // LIFX / Twinkly / WiZ / Yeelight / Elgato next. One brand-neutral
        // provider + frame writer + send throttle; per-brand behavior is an
        // ILightDriver. Joins the composite by id prefix ("hue:", …).
        // Cross-platform (pure sockets), so it runs on macOS/Linux too. See
        // plans/smart-lights-integration.md.
        services.AddSingleton<Nexus.Service.Lighting.Smart.Discovery.MdnsQuery>();
        services.AddSingleton<Nexus.Service.Lighting.Smart.Discovery.LanDiscovery>();
        services.AddSingleton<Nexus.Service.Lighting.Smart.NetworkSendThrottle>();
        services.AddSingleton<Nexus.Service.Lighting.Smart.Drivers.Hue.HueBridgeClient>();
        services.AddSingleton<Nexus.Service.Lighting.Smart.Drivers.Hue.HueDriver>();
        services.AddSingleton<Nexus.Service.Lighting.Smart.ILightDriver>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.Smart.Drivers.Hue.HueDriver>());
        services.AddSingleton<Nexus.Service.Lighting.Smart.Drivers.Nanoleaf.NanoleafClient>();
        services.AddSingleton(sp => new Nexus.Service.Lighting.Smart.Drivers.Nanoleaf.NanoleafDriver(
            sp.GetRequiredService<Nexus.Service.Lighting.Smart.Drivers.Nanoleaf.NanoleafClient>(),
            sp.GetRequiredService<Nexus.Service.Lighting.Smart.Discovery.LanDiscovery>(),
            sp.GetRequiredService<Nexus.Service.Persistence.IConfigStore>()));
        services.AddSingleton<Nexus.Service.Lighting.Smart.ILightDriver>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.Smart.Drivers.Nanoleaf.NanoleafDriver>());
        services.AddSingleton(_ => new Nexus.Service.Lighting.Smart.Drivers.Govee.GoveeLanClient());
        services.AddSingleton<Nexus.Service.Lighting.Smart.Drivers.Govee.GoveeDriver>();
        services.AddSingleton<Nexus.Service.Lighting.Smart.ILightDriver>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.Smart.Drivers.Govee.GoveeDriver>());
        services.AddSingleton<Nexus.Service.Lighting.Smart.SmartLightProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.Smart.SmartLightProvider>());
        services.AddSingleton<Nexus.Service.Lighting.Zones.IDeviceStructureSource>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.Smart.SmartLightProvider>());
        services.AddSingleton<Nexus.Service.Lighting.Smart.SmartLightFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.Smart.SmartLightFrameWriter>());

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            services.AddSingleton<Nexus.Service.Lighting.Rgb.OpenRgbLightingDeviceProvider>();
            services.AddSingleton<Nexus.Service.Lighting.Zones.IDeviceStructureSource>(
                sp => sp.GetRequiredService<Nexus.Service.Lighting.Rgb.OpenRgbLightingDeviceProvider>());
            services.AddSingleton<ILightingDeviceProvider>(sp => new Nexus.Service.Lighting.CompositeLightingDeviceProvider(
                sp.GetRequiredService<Nexus.Service.Lighting.Rgb.OpenRgbLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.Np50LightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.MiniHubLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.SmartHubLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.CnvsLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.QSeriesLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.KeebLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.Smart.SmartLightProvider>(),
                sp.GetRequiredService<Nexus.Service.Persistence.IConfigStore>(),
                sp.GetRequiredService<Nexus.Service.Lighting.Engine.LightingEngine>()));
        }
        else
        {
            services.AddSingleton<ILightingDeviceProvider>(sp => new Nexus.Service.Lighting.CompositeLightingDeviceProvider(
                sp.GetRequiredService<StubDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.Np50LightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.MiniHubLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.SmartHubLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.CnvsLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.QSeriesLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.KeebLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.Smart.SmartLightProvider>(),
                sp.GetRequiredService<Nexus.Service.Persistence.IConfigStore>(),
                sp.GetRequiredService<Nexus.Service.Lighting.Engine.LightingEngine>()));
        }

        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.CnvsHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.QSeriesHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.Y70Handler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.KeebHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.FanHubHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.Np50Handler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.SmartHubHandler>();

        // Read-only catalog of firmware images embedded in this build. Backs
        // the Firmware Updates page's "available version" column.
        services.AddSingleton<Nexus.Service.Devices.Firmware.BundledFirmwareCatalog>();

        // Firmware flasher: dfu-util wrapper + WinUSB installer + orchestrator.
        // CnvsHub doubles as a DFU flash target (it owns the CNVS serial port and
        // can drop the device into the bootloader).
        services.AddSingleton<Nexus.Service.Devices.Firmware.DfuUtil>(_ =>
            new Nexus.Service.Devices.Firmware.DfuUtil(Nexus.Service.Devices.Firmware.DfuUtil.ResolveDefaultPath()));
        services.AddSingleton<Nexus.Service.Devices.Firmware.WinUsbDriverInstaller>();
        services.AddSingleton<Nexus.Service.Devices.Firmware.IDfuFlashTarget>(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.Cnvs.CnvsHub>());
        services.AddSingleton<Nexus.Service.Devices.Firmware.IDfuFlashTarget>(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.Np50.Np50Hub>());
        services.AddSingleton<Nexus.Service.Devices.Firmware.IDfuFlashTarget>(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHub>());
        services.AddSingleton<Nexus.Service.Devices.Firmware.IDfuFlashTarget>(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.SmartHub.SmartHubHub>());
        services.AddSingleton<Nexus.Service.Devices.Firmware.IDfuFlashTarget>(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.QSeriesCooler.QSeriesCoolerHub>());
        services.AddSingleton<Nexus.Service.Devices.Firmware.IDfuFlashTarget>(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.Y70Display.Y70DisplayHub>());
        services.AddSingleton<Nexus.Service.Devices.Firmware.FirmwareFlasher>();

        // NP50 hub: serial port discovery + transport factory + singleton hub +
        // 2-second heartbeat poller. Windows and Linux have real port discovery;
        // other platforms get a stub that finds nothing (the hub stays
        // disconnected).
#if WINDOWS
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Np50.INp50PortDiscovery,
                              Nexus.Service.Peripherals.Hyte.Np50.WindowsNp50PortDiscovery>();
#elif LINUX
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Np50.INp50PortDiscovery,
                              Nexus.Service.Peripherals.Hyte.Np50.LinuxNp50PortDiscovery>();
#else
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Np50.INp50PortDiscovery,
                              Nexus.Service.Peripherals.Hyte.Np50.StubNp50PortDiscovery>();
#endif
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Np50.Np50Hub>(sp =>
            new Nexus.Service.Peripherals.Hyte.Np50.Np50Hub(
                sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.Np50.INp50PortDiscovery>(),
                port => new Nexus.Service.Peripherals.Hyte.Np50.Np50SerialTransport(port.PortName, port.Serial)));
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Np50.Np50HeartbeatWorker>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.Np50.Np50HeartbeatWorker>());

        // MiniHub hub: own port-discovery instance (we don't bind it to
        // INp50PortDiscovery in DI because that interface is already taken
        // by the NP50 binding — instead the hub factory below constructs
        // the MiniHub-specific discovery inline). Transport factory reuses
        // the generic serial-port wrapper since it's product-agnostic.
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHub>(sp =>
        {
            Nexus.Service.Peripherals.Hyte.Np50.INp50PortDiscovery discovery;
#if WINDOWS
            discovery = new Nexus.Service.Peripherals.Hyte.MiniHub.WindowsMiniHubPortDiscovery();
#elif LINUX
            discovery = new Nexus.Service.Peripherals.Hyte.MiniHub.LinuxMiniHubPortDiscovery();
#else
            discovery = new Nexus.Service.Peripherals.Hyte.MiniHub.StubMiniHubPortDiscovery();
#endif
            return new Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHub(
                discovery,
                port => new Nexus.Service.Peripherals.Hyte.Np50.Np50SerialTransport(port.PortName, port.Serial));
        });
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHeartbeatWorker>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHeartbeatWorker>());

        // Smart Hub: ARGB + PWM-fan hub. Own port-discovery instance (the
        // INp50PortDiscovery binding is already taken by NP50), constructed
        // inline; reuses the product-agnostic Np50SerialTransport. Same
        // heartbeat shape as the MiniHub.
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.SmartHub.SmartHubHub>(sp =>
        {
            Nexus.Service.Peripherals.Hyte.Np50.INp50PortDiscovery discovery;
#if WINDOWS
            discovery = new Nexus.Service.Peripherals.Hyte.SmartHub.WindowsSmartHubPortDiscovery();
#elif LINUX
            discovery = new Nexus.Service.Peripherals.Hyte.SmartHub.LinuxSmartHubPortDiscovery();
#else
            discovery = new Nexus.Service.Peripherals.Hyte.SmartHub.StubSmartHubPortDiscovery();
#endif
            return new Nexus.Service.Peripherals.Hyte.SmartHub.SmartHubHub(
                discovery,
                port => new Nexus.Service.Peripherals.Hyte.Np50.Np50SerialTransport(port.PortName, port.Serial));
        });
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.SmartHub.SmartHubHeartbeatWorker>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.SmartHub.SmartHubHeartbeatWorker>());

        // Q-series cooler controller (Q60 / Q80): serial-over-USB hub mirroring
        // the MiniHub stack. Reads firmware version + variant so the Firmware
        // Updates page can show current-vs-available. Reuses the product-agnostic
        // Np50SerialTransport; discovery is Windows-only (stub elsewhere).
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.QSeriesCooler.QSeriesCoolerHub>(sp =>
        {
            Nexus.Service.Peripherals.Hyte.QSeriesCooler.IQSeriesCoolerPortDiscovery discovery;
#if WINDOWS
            discovery = new Nexus.Service.Peripherals.Hyte.QSeriesCooler.WindowsQSeriesCoolerPortDiscovery();
#elif LINUX
            discovery = new Nexus.Service.Peripherals.Hyte.QSeriesCooler.LinuxQSeriesCoolerPortDiscovery();
#else
            discovery = new Nexus.Service.Peripherals.Hyte.QSeriesCooler.StubQSeriesCoolerPortDiscovery();
#endif
            return new Nexus.Service.Peripherals.Hyte.QSeriesCooler.QSeriesCoolerHub(
                discovery,
                port => new Nexus.Service.Peripherals.Hyte.Np50.Np50SerialTransport(port.PortName, port.Serial));
        });
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.QSeriesCooler.QSeriesCoolerHeartbeatWorker>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.QSeriesCooler.QSeriesCoolerHeartbeatWorker>());

        // Y70 Touch display controller (Touch / Infinite / Truly): serial-over-USB
        // hub mirroring the Q-series stack. Reads firmware version + variant for the
        // Firmware Updates page. Reuses Np50SerialTransport; discovery is Windows-only.
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Y70Display.Y70DisplayHub>(sp =>
        {
            Nexus.Service.Peripherals.Hyte.Y70Display.IY70DisplayPortDiscovery discovery;
#if WINDOWS
            discovery = new Nexus.Service.Peripherals.Hyte.Y70Display.WindowsY70DisplayPortDiscovery();
#elif LINUX
            discovery = new Nexus.Service.Peripherals.Hyte.Y70Display.LinuxY70DisplayPortDiscovery();
#else
            discovery = new Nexus.Service.Peripherals.Hyte.Y70Display.StubY70DisplayPortDiscovery();
#endif
            return new Nexus.Service.Peripherals.Hyte.Y70Display.Y70DisplayHub(
                discovery,
                port => new Nexus.Service.Peripherals.Hyte.Np50.Np50SerialTransport(port.PortName, port.Serial));
        });
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Y70Display.Y70DisplayHeartbeatWorker>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.Y70Display.Y70DisplayHeartbeatWorker>());

#if WINDOWS
        services.AddSingleton<Nexus.Service.Devices.Detection.WindowsUsbEnumerator>();
        services.AddSingleton<Nexus.Service.Devices.Detection.IUsbEnumerator>(sp =>
            new Nexus.Service.Devices.Detection.CachingUsbEnumerator(
                sp.GetRequiredService<Nexus.Service.Devices.Detection.WindowsUsbEnumerator>()));
#elif MACOS
        services.AddSingleton<Nexus.Service.Devices.Detection.MacUsbEnumerator>();
        services.AddSingleton<Nexus.Service.Devices.Detection.IUsbEnumerator>(sp =>
            new Nexus.Service.Devices.Detection.CachingUsbEnumerator(
                sp.GetRequiredService<Nexus.Service.Devices.Detection.MacUsbEnumerator>()));
#elif LINUX
        services.AddSingleton<Nexus.Service.Devices.Detection.LinuxUsbEnumerator>();
        services.AddSingleton<Nexus.Service.Devices.Detection.IUsbEnumerator>(sp =>
            new Nexus.Service.Devices.Detection.CachingUsbEnumerator(
                sp.GetRequiredService<Nexus.Service.Devices.Detection.LinuxUsbEnumerator>()));
#else
        services.AddSingleton<Nexus.Service.Devices.Detection.StubUsbEnumerator>();
        services.AddSingleton<Nexus.Service.Devices.Detection.IUsbEnumerator>(sp =>
            new Nexus.Service.Devices.Detection.CachingUsbEnumerator(
                sp.GetRequiredService<Nexus.Service.Devices.Detection.StubUsbEnumerator>()));
#endif
        // Shared "is this device on the bus" check over the cached USB enumerator,
        // used to gate per-device heartbeat workers so they stay silent on hosts
        // where their hardware isn't attached.
        services.AddSingleton<Nexus.Service.Devices.Detection.HardwarePresence>();
        services.AddSingleton<DeviceManager>();
        services.AddSingleton<Nexus.Service.Devices.DeviceBroadcaster>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Devices.DeviceBroadcaster>());
        return services;
    }

    public static IServiceCollection AddNexusPeripherals(this IServiceCollection services)
    {
#if WINDOWS
        services.AddSingleton<Nexus.Service.Peripherals.Hid.IHidEnumerator, Nexus.Service.Peripherals.Hid.WindowsHidEnumerator>();
#elif LINUX
        services.AddSingleton<Nexus.Service.Peripherals.Hid.IHidEnumerator, Nexus.Service.Peripherals.Hid.LinuxHidEnumerator>();
#else
        services.AddSingleton<Nexus.Service.Peripherals.Hid.IHidEnumerator, Nexus.Service.Peripherals.Hid.StubHidEnumerator>();
#endif
        services.AddSingleton<Nexus.Service.Peripherals.PeripheralRegistry>();

        // Real HID-backed keeb provider. StubKeebProvider stays registered only
        // as the IInputterProvider fallback on platforms without a native inputter.
        services.AddSingleton<StubKeebProvider>();
        services.AddSingleton<RealKeebProvider>();
        services.AddSingleton<IKeebProvider>(sp => sp.GetRequiredService<RealKeebProvider>());
#if WINDOWS
        services.AddSingleton<IInputterProvider, WindowsInputter>();
        services.AddSingleton<Nexus.Service.Platform.Clipboard.IClipboardProvider>(
            sp => new Nexus.Service.Platform.Clipboard.HelperRoutedClipboardProvider(sp));
#elif MACOS
        services.AddSingleton<IInputterProvider, MacInputter>();
        services.AddSingleton<Nexus.Service.Platform.Clipboard.IClipboardProvider, Nexus.Service.Platform.Clipboard.MacClipboardProvider>();
#elif LINUX
        services.AddSingleton<IInputterProvider, LinuxInputter>();
        services.AddSingleton<Nexus.Service.Platform.Clipboard.IClipboardProvider, Nexus.Service.Platform.Clipboard.LinuxClipboardProvider>();
#else
        services.AddSingleton<IInputterProvider>(sp => sp.GetRequiredService<StubKeebProvider>());
        services.AddSingleton<Nexus.Service.Platform.Clipboard.IClipboardProvider, Nexus.Service.Platform.Clipboard.StubClipboardProvider>();
#endif

        services.AddSingleton<Nexus.Service.Transfer.TransferInbox>();

        // Real Y70 control (serial brightness/power + DDC/CI fallback). Degrades
        // to persist-only when no panel is attached (hub disconnected + no DDC
        // match), so it works cross-platform.
        services.AddSingleton<IY70Provider, Y70Provider>();
        services.AddSingleton<IQSeriesProvider, StubQSeriesProvider>();

#if WINDOWS
        // Brightness proxies through the user-session helper - DDC/CI and
        // laptop-panel APIs are unreliable from Session 0.
        services.AddSingleton<Nexus.Service.Platform.Displays.IDisplayBrightnessProvider,
            Nexus.Service.Platform.Displays.HelperDisplayBrightnessProxy>();
        // Y70 display rotation also routes through the helper: Session 0
        // cannot ChangeDisplaySettingsEx against the user's monitors.
        services.AddSingleton<Nexus.Service.Platform.Displays.IDisplayOrientationProvider,
            Nexus.Service.Platform.Displays.HelperDisplayOrientationProxy>();
        // Monitor enumeration follows the same Session 0 limitation: DXGI
        // EnumOutputs returns nothing under LocalSystem, so the helper does
        // the enumeration and we proxy.
        services.AddSingleton<Nexus.Service.Platform.IMonitorEnumerator,
            Nexus.Service.Platform.Displays.HelperMonitorEnumeratorProxy>();
        // Display topology (positions/modes/scale) is the same Session 0
        // story: the helper runs the real provider in the user session.
        services.AddSingleton<Nexus.Service.Platform.Displays.IDisplayTopologyProvider,
            Nexus.Service.Platform.Displays.HelperDisplayTopologyProxy>();
        services.AddSingleton<Nexus.Service.Platform.Displays.DisplayTopologyWatcher>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Platform.Displays.DisplayTopologyWatcher>());
        // Screen-mirror frames also flow through the helper - DXGI desktop
        // duplication is Session 0-blind, so the helper captures + downsamples
        // and pushes canvas-resolution RGB24 over the pipe.
        services.AddSingleton<Nexus.Service.Lighting.Capture.IScreenFrameSource,
            Nexus.Service.Lighting.Capture.HelperScreenFrameSource>();
#elif MACOS
        services.AddSingleton<Nexus.Service.Platform.Displays.IDisplayBrightnessProvider,
            Nexus.Service.Platform.Displays.MacDisplayBrightnessProvider>();
        services.AddSingleton<Nexus.Service.Platform.Displays.IDisplayTopologyProvider,
            Nexus.Service.Platform.Displays.MacDisplayTopologyProvider>();
#elif LINUX
        services.AddSingleton<Nexus.Service.Platform.Displays.IDisplayBrightnessProvider,
            Nexus.Service.Platform.Displays.LinuxDisplayBrightnessProvider>();
        services.AddSingleton<Nexus.Service.Platform.Displays.IDisplayTopologyProvider,
            Nexus.Service.Platform.Displays.LinuxDisplayTopologyProvider>();
        // Screen-mirror frames come from the xdg-desktop-portal ScreenCast
        // portal (PipeWire), consumed by a gst-launch reader. The portal
        // handshake rides the session D-Bus connection (AddNexusLinuxDBus);
        // without this binding the effect's IScreenFrameSource stays null and
        // screen-mirror renders nothing on Linux.
        services.AddSingleton<Nexus.Service.Lighting.Capture.IScreenFrameSource,
            Nexus.Service.Lighting.Capture.LinuxScreenFrameSource>();
#else
        services.AddSingleton<Nexus.Service.Platform.Displays.IDisplayBrightnessProvider,
            Nexus.Service.Platform.Displays.StubDisplayBrightnessProvider>();
        services.AddSingleton<Nexus.Service.Platform.Displays.IDisplayTopologyProvider,
            Nexus.Service.Platform.Displays.StubDisplayTopologyProvider>();
#endif
#if !WINDOWS
        // Non-Windows monitor enumeration + display orientation are platform-agnostic.
        services.AddSingleton<Nexus.Service.Platform.IMonitorEnumerator,
            Nexus.Service.Platform.DefaultMonitorEnumerator>();
        services.AddSingleton<Nexus.Service.Platform.Displays.IDisplayOrientationProvider,
            Nexus.Service.Platform.Displays.NoopDisplayOrientationProvider>();
#endif
        services.AddSingleton<Nexus.Service.Platform.Displays.DisplayBrightnessController>();
        services.AddSingleton<Nexus.Service.Platform.Displays.DisplayTopologyService>();
        return services;
    }

    public static IServiceCollection AddNexusActivity(this IServiceCollection services)
    {
        services.AddSingleton<Nexus.Service.Activity.Storage.IScreenTimeStore>(_ =>
        {
            try
            {
                return new Nexus.Service.Activity.Storage.SqliteScreenTimeStore();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[screentime-store] sqlite unavailable, using in-memory: {ex.Message}");
                return new Nexus.Service.Activity.Storage.InMemoryScreenTimeStore();
            }
        });

        // Windows/macOS push the accent from their native shell; the Linux
        // block below overrides this with the portal reader (last registration
        // wins for the resolved instance).
        services.AddSingleton<ISystemAccentProvider, NullSystemAccentProvider>();
#if WINDOWS
        services.AddSingleton<IScreenTimeProvider, WindowsScreenTimeProvider>();
        services.AddSingleton<IAppDetectionProvider, StubAppDetectionProvider>();
        // Enumeration (Get-StartApps) is per-user and empty from Session 0, so
        // route it through the user-session helper. Launch stays direct (explorer).
        services.AddSingleton<IShortcutsProvider, HelperShortcutsProxy>();
        services.AddSingleton<IMediaProvider, WindowsMediaProvider>();
        services.AddSingleton<IVolumeProvider, WindowsVolumeProvider>();
        services.AddSingleton<IAudioDeviceProvider, WindowsAudioDeviceProvider>();
        services.AddSingleton<IBeatsProvider, WasapiLoopbackBeatsProvider>();
#elif MACOS
        services.AddSingleton<IScreenTimeProvider, MacScreenTimeProvider>();
        services.AddSingleton<IAppDetectionProvider, MacAppDetectionProvider>();
        services.AddSingleton<IShortcutsProvider, MacShortcutsProvider>();
        services.AddSingleton<IMediaProvider, MacMediaProvider>();
        services.AddSingleton<IVolumeProvider, MacVolumeProvider>();
        services.AddSingleton<IAudioDeviceProvider, MacAudioDeviceProvider>();
        services.AddSingleton<IBeatsProvider, MacAudioBeatsProvider>();
#elif LINUX
        services.AddSingleton<Nexus.Service.Activity.LinuxScreenTimeProvider>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Activity.LinuxScreenTimeProvider>());
        services.AddSingleton<IScreenTimeProvider>(sp => sp.GetRequiredService<Nexus.Service.Activity.LinuxScreenTimeProvider>());
        services.AddSingleton<IAppDetectionProvider, StubAppDetectionProvider>();
        services.AddSingleton<IShortcutsProvider, LinuxShortcutsProvider>();
        services.AddSingleton<ISystemAccentProvider, Nexus.Service.Platform.Linux.LinuxSystemAccentProvider>();
        services.AddSingleton<IMediaProvider, LinuxMediaProvider>();
        services.AddSingleton<IVolumeProvider, LinuxVolumeProvider>();
        services.AddSingleton<IAudioDeviceProvider, LinuxAudioDeviceProvider>();
        services.AddSingleton<IBeatsProvider, BeatsProvider>();
#else
        services.AddSingleton<IScreenTimeProvider, StubScreenTimeProvider>();
        services.AddSingleton<IAppDetectionProvider, StubAppDetectionProvider>();
        services.AddSingleton<IShortcutsProvider, StubShortcutsProvider>();
        services.AddSingleton<IMediaProvider, StubMediaProvider>();
        services.AddSingleton<IVolumeProvider, StubVolumeProvider>();
        services.AddSingleton<IAudioDeviceProvider, StubAudioDeviceProvider>();
        services.AddSingleton<IBeatsProvider, StubBeatsProvider>();
#endif
        return services;
    }

    public static IServiceCollection AddNexusNetwork(this IServiceCollection services)
    {
#if WINDOWS
        services.AddSingleton<WindowsNetworkProvider>();
        services.AddHostedService(sp => sp.GetRequiredService<WindowsNetworkProvider>());
        services.AddSingleton<INetworkProvider>(sp => sp.GetRequiredService<WindowsNetworkProvider>());
#elif MACOS
        services.AddSingleton<MacNetworkProvider>();
        services.AddHostedService(sp => sp.GetRequiredService<MacNetworkProvider>());
        services.AddSingleton<INetworkProvider>(sp => sp.GetRequiredService<MacNetworkProvider>());
#elif LINUX
        services.AddSingleton<LinuxNetworkProvider>();
        services.AddHostedService(sp => sp.GetRequiredService<LinuxNetworkProvider>());
        services.AddSingleton<INetworkProvider>(sp => sp.GetRequiredService<LinuxNetworkProvider>());
#else
        services.AddSingleton<INetworkProvider, StubNetworkProvider>();
#endif
        return services;
    }

    public static IServiceCollection AddNexusLifecycle(this IServiceCollection services)
    {
#if WINDOWS
        services.AddSingleton<IStartupProvider, WindowsStartupProvider>();
#elif MACOS
        services.AddSingleton<IStartupProvider, MacStartupProvider>();
#elif LINUX
        services.AddSingleton<IStartupProvider, LinuxStartupProvider>();
#else
        services.AddSingleton<IStartupProvider, StubStartupProvider>();
#endif

        services.AddSingleton<IShutdownProvider, StubShutdownProvider>();
#if WINDOWS
        services.AddSingleton<Nexus.Service.Platform.Power.ISystemPowerProvider, Nexus.Service.Platform.Power.WindowsSystemPowerProvider>();
#elif MACOS
        services.AddSingleton<Nexus.Service.Platform.Power.ISystemPowerProvider, Nexus.Service.Platform.Power.MacSystemPowerProvider>();
#elif LINUX
        services.AddSingleton<Nexus.Service.Platform.Power.ISystemPowerProvider, Nexus.Service.Platform.Power.LinuxSystemPowerProvider>();
#else
        services.AddSingleton<Nexus.Service.Platform.Power.ISystemPowerProvider, Nexus.Service.Platform.Power.StubSystemPowerProvider>();
#endif
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

    public static IServiceCollection AddNexusBenchmarks(this IServiceCollection services)
    {
        services.AddSingleton<Nexus.Service.Benchmarks.IBenchmarkProvider, Nexus.Service.Benchmarks.Providers.DefaultBenchmarkProvider>();
        services.AddSingleton<Nexus.Service.Benchmarks.BenchmarkRunner>();
        services.AddSingleton<ProfileManager>();
        services.AddSingleton<Nexus.Service.Media.MediaLibrary>();
        services.AddSingleton<Nexus.Service.Gallery.GalleryLibrary>();
        services.AddSingleton<Nexus.Service.Gallery.IGalleryDialogPicker, Nexus.Service.Gallery.GalleryDialogPicker>();
        return services;
    }

    /// <summary>
    /// Widget runtime services. Serving routes are wired in
    /// <see cref="Nexus.Service.Routes.AppRoutes.MapAppEndpoints"/>
    /// in Program.cs.
    /// </summary>
    public static IServiceCollection AddNexusWidgets(this IServiceCollection services)
    {
        services.AddSingleton<Nexus.Service.Widgets.AppRegistry>();
        services.AddSingleton<Nexus.Service.Widgets.WidgetSettingsService>();
        services.AddSingleton<Nexus.Service.Widgets.AppProxyService>();
        services.AddSingleton<Nexus.Service.Widgets.AppInstaller>();
        services.AddSingleton<Nexus.Service.Store.CloudApiClient>();
        services.AddSingleton<Nexus.Service.Store.AccountLinkService>();
        services.AddSingleton<Nexus.Service.Store.CloudStoreInstaller>();
        services.AddSingleton<Nexus.Service.Widgets.AppCodeSessionService>();
        services.AddSingleton<Nexus.Service.Widgets.AppActionRegistry>(sp =>
        {
            var registry = new Nexus.Service.Widgets.AppActionRegistry();
            // First-party action modules. Each registers its own actions
            // by name; the manifest's capabilities.dispatch allowlist
            // gates per-widget access.
            Nexus.Service.Widgets.AppActions.DisplayActions.RegisterAll(registry);
            Nexus.Service.Widgets.AppActions.ScreentimeActions.RegisterAll(registry);
            Nexus.Service.Widgets.AppActions.MediaActions.RegisterAll(registry);
            Nexus.Service.Widgets.AppActions.CoolingActions.RegisterAll(registry);
            Nexus.Service.Widgets.AppActions.LightingActions.RegisterAll(registry);
            return registry;
        });
        services.AddSingleton<Nexus.Service.Widgets.AppDispatchRateLimiter>();

        // Generic external-tool manager (NEX-13): fetches + runs a device's sidecar
        // executable. Hosted so its StopAsync kills every tracked tool process on
        // service shutdown.
        services.AddSingleton<Nexus.Service.Common.ExternalTools.ExternalToolManager>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Common.ExternalTools.ExternalToolManager>());
        // Auto-launches each installed bundled driver app's binary when its device
        // is present (runs at boot, pre-login).
        services.AddHostedService<Nexus.Service.Common.ExternalTools.DriverAutoLaunchWorker>();
        return services;
    }

    public static IServiceCollection AddNexusPanel(this IServiceCollection services, int servicePort)
    {
        // QSeriesPortWatcher keeps `adb reverse tcp:{servicePort}` alive
        // while a HYTE Q60 / Q80 USB display is attached. Without it,
        // every time Y70's adb-server restarts the panel's multiplex
        // WebSocket on the Q-series silently freezes. Windows-only — the
        // Q-series host stack lives on the Y70 PC.
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<Nexus.Service.QSeries.QSeriesPortWatcher>(
                sp => new Nexus.Service.QSeries.QSeriesPortWatcher(
                    servicePort,
                    sp.GetRequiredService<Nexus.Service.Devices.Detection.HardwarePresence>(),
                    sp.GetRequiredService<Nexus.Service.Panel.PanelDeviceRegistry>()));
            services.AddHostedService(sp =>
                sp.GetRequiredService<Nexus.Service.QSeries.QSeriesPortWatcher>());
        }

        services.AddSingleton<Nexus.Service.Panel.PanelKioskLauncher>();
        services.AddSingleton<Nexus.Service.Panel.PanelOverlayHostLauncher>();
        // IOverlayHost picks the right impl per OS. Mac spawns the Swift
        // sidecar nexus-overlay-helper (transparent NSWindow + WKWebView
        // per NSScreen, plus fullscreen panel kiosks). Windows spawns
        // nexus-overlay.exe (WinForms + WebView2). Linux widgets are a
        // no-op until an X11/Wayland surface is added; monitor-panel
        // kiosks run through LinuxPanelKioskHost instead.
#if MACOS
        Nexus.Service.Platform.Mac.MacOverlayHostLauncher.Configure(servicePort);
        services.AddSingleton<Nexus.Service.Panel.IOverlayHost, Nexus.Service.Platform.Mac.MacOverlayHostLauncher>();
#elif WINDOWS
        services.AddSingleton<Nexus.Service.Panel.IOverlayHost>(sp =>
            sp.GetRequiredService<Nexus.Service.Panel.PanelOverlayHostLauncher>());
#else
        services.AddSingleton<Nexus.Service.Panel.IOverlayHost, Nexus.Service.Panel.NoopOverlayHost>();
#endif
#if LINUX
        Nexus.Service.Platform.Linux.LinuxPanelKioskHost.Configure(servicePort);
        services.AddSingleton<Nexus.Service.Platform.Linux.LinuxPanelKioskHost>();
#endif
        services.AddSingleton<Nexus.Service.Panel.PanelPhonePairingService>();
        services.AddSingleton<Nexus.Service.Panel.PanelDeviceRegistry>();

        // Cloud-relay transport: holds one outbound relay socket per paired
        // phone session when the user has opted in (RemoteControl + Relay). It
        // bridges relayed, end-to-end-encrypted frames into the same
        // MultiplexHub the LAN /ws path uses, so the killswitch already applies.
        // Event-driven off IConfigStore.OnChanged; no poll loop.
        // REST-over-relay tunnel dispatcher: runs a tunneled request through the
        // service's OWN endpoint pipeline, authorized as the relay session. The
        // pipeline is captured + primed in Program.cs after the app is built.
        services.AddSingleton<Nexus.Service.Relay.RelayHttpDispatcher>();
        services.AddSingleton<Nexus.Service.Relay.RelayConnectionService>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Relay.RelayConnectionService>());
        return services;
    }

    public static IServiceCollection AddNexusLinuxDBus(this IServiceCollection services)
    {
#if LINUX
        services.AddSingleton<Nexus.Service.Platform.Linux.DBus.DBusConnection>();
        services.AddHostedService<Nexus.Service.Platform.Linux.LinuxTrayService>();
#endif
        return services;
    }

    /// <summary>
    /// Windows user-session helper IPC. The named-pipe server, registry, and
    /// typed command client. Other platforms run their providers natively in
    /// the user-context daemon, so no helper subsystem is registered.
    /// </summary>
    public static IServiceCollection AddNexusHelper(this IServiceCollection services)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<Nexus.Service.Helper.HelperRegistry>();
            services.AddSingleton<Nexus.Service.Helper.HelperPipeServer>();
            services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Helper.HelperPipeServer>());
        }
#endif
        return services;
    }
}
