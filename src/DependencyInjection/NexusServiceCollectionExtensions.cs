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
using Nexus.Service.Update;
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
        services.AddSingleton<Nexus.Service.Conflicts.IConflictDetector>(
            sp => sp.GetRequiredService<Nexus.Service.Conflicts.ConflictWatcher>());
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
#error No ISensorProvider for this target - wire one when adding a platform.
#endif
        services.AddSingleton<ProcessMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<ProcessMonitor>());
        services.AddSingleton<Nexus.Service.Activity.GpuProcessMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Activity.GpuProcessMonitor>());
        services.AddSingleton<SystemSpecsCollector>();
        // Pre-warms the specs cache in the background after host start so the
        // first Devices → System Specs request doesn't pay a cold PowerShell
        // spawn. Hard rule: this MUST stay off the startup critical path -
        // see SystemSpecsPrewarmService.ExecuteAsync.
        services.AddHostedService<SystemSpecsPrewarmService>();
        services.AddSingleton<OemInfo>();
        // Warms the SMBIOS manufacturer read off the critical path; see
        // OemInfoPrewarmService.ExecuteAsync.
        services.AddHostedService<OemInfoPrewarmService>();
        // Pushes a pairing-QR-refresh nudge to the dashboard when the host IP
        // changes (VPN/Wi-Fi↔wired/DHCP), so a displayed QR doesn't keep
        // embedding a stale LAN address until its TTL. Off the critical path -
        // it only subscribes to NetworkChange.NetworkAddressChanged.
        services.AddHostedService<Nexus.Service.Net.NetworkAddressChangeListener>();
        // One-time hardware/specs snapshot to nexus-service.log after discovery
        // settles, so a tester's log opens with the full detected picture.
        // Off the critical path; see StartupDiagnosticsDumpService.ExecuteAsync.
        services.AddHostedService<Nexus.Service.Diagnostics.StartupDiagnosticsDumpService>();
        // Anonymous fleet heartbeat - post-boot, off the critical path, gated by
        // the collect-anonymous-data setting (default on).
        services.AddHostedService<Nexus.Service.Telemetry.HeartbeatService>();
        // Product telemetry - anonymous events to PostHog, same opt-out + install
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
        // The runtime plugin-provider registry - the single seam the cooling /
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
                SmartHubCoolingProvider.IsSmartHubId, sp.GetRequiredService<SmartHubCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                QSeriesCoolerCoolingProvider.IsQSeriesId, sp.GetRequiredService<QSeriesCoolerCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.LianLiCoolingProvider.IsLianLiId, sp.GetRequiredService<Nexus.Service.Cooling.LianLiCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.LianLiTlCoolingProvider.IsLianLiTlId, sp.GetRequiredService<Nexus.Service.Cooling.LianLiTlCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.Galahad2CoolingProvider.IsGalahad2Id, sp.GetRequiredService<Nexus.Service.Cooling.Galahad2CoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.CorsairLinkCoolingProvider.IsCorsairId, sp.GetRequiredService<Nexus.Service.Cooling.CorsairLinkCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.Slv3CoolingProvider.IsSlv3Id, sp.GetRequiredService<Nexus.Service.Cooling.Slv3CoolingProvider>())));
        services.AddSingleton<ICoolingProvider>(sp => (ICoolingProvider)sp.GetRequiredService<IFanControlProvider>());
#elif MACOS
        services.AddSingleton<MacFanControlProvider>();
        services.AddSingleton<IFanControlProvider>(sp => new CompositeFanControlProvider(
            sp.GetRequiredService<MacFanControlProvider>(),
            sp.GetRequiredService<Np50CoolingProvider>(),
            sp.GetRequiredService<MiniHubCoolingProvider>(),
            sp.GetRequiredService<PluginProviderRegistry>(),
            new CompositeFanControlProvider.FanSource(
                SmartHubCoolingProvider.IsSmartHubId, sp.GetRequiredService<SmartHubCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                QSeriesCoolerCoolingProvider.IsQSeriesId, sp.GetRequiredService<QSeriesCoolerCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.LianLiCoolingProvider.IsLianLiId, sp.GetRequiredService<Nexus.Service.Cooling.LianLiCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.LianLiTlCoolingProvider.IsLianLiTlId, sp.GetRequiredService<Nexus.Service.Cooling.LianLiTlCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.Galahad2CoolingProvider.IsGalahad2Id, sp.GetRequiredService<Nexus.Service.Cooling.Galahad2CoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.CorsairLinkCoolingProvider.IsCorsairId, sp.GetRequiredService<Nexus.Service.Cooling.CorsairLinkCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.Slv3CoolingProvider.IsSlv3Id, sp.GetRequiredService<Nexus.Service.Cooling.Slv3CoolingProvider>())));
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
                QSeriesCoolerCoolingProvider.IsQSeriesId, sp.GetRequiredService<QSeriesCoolerCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                LinuxLiquidctlProvider.IsLiquidctlId, sp.GetRequiredService<LinuxLiquidctlProvider>()),
            new CompositeFanControlProvider.FanSource(
                LinuxNvidiaFanProvider.IsNvidiaId, sp.GetRequiredService<LinuxNvidiaFanProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.LianLiCoolingProvider.IsLianLiId, sp.GetRequiredService<Nexus.Service.Cooling.LianLiCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.LianLiTlCoolingProvider.IsLianLiTlId, sp.GetRequiredService<Nexus.Service.Cooling.LianLiTlCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.Galahad2CoolingProvider.IsGalahad2Id, sp.GetRequiredService<Nexus.Service.Cooling.Galahad2CoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.CorsairLinkCoolingProvider.IsCorsairId, sp.GetRequiredService<Nexus.Service.Cooling.CorsairLinkCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.Slv3CoolingProvider.IsSlv3Id, sp.GetRequiredService<Nexus.Service.Cooling.Slv3CoolingProvider>())));
        services.AddSingleton<ICoolingProvider>(sp => (ICoolingProvider)sp.GetRequiredService<IFanControlProvider>());
#else
        services.AddSingleton<IFanControlProvider>(sp => new CompositeFanControlProvider(
            sp.GetRequiredService<StubCoolingProvider>(),
            sp.GetRequiredService<Np50CoolingProvider>(),
            sp.GetRequiredService<MiniHubCoolingProvider>(),
            sp.GetRequiredService<PluginProviderRegistry>(),
            new CompositeFanControlProvider.FanSource(
                SmartHubCoolingProvider.IsSmartHubId, sp.GetRequiredService<SmartHubCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                QSeriesCoolerCoolingProvider.IsQSeriesId, sp.GetRequiredService<QSeriesCoolerCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.LianLiCoolingProvider.IsLianLiId, sp.GetRequiredService<Nexus.Service.Cooling.LianLiCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.LianLiTlCoolingProvider.IsLianLiTlId, sp.GetRequiredService<Nexus.Service.Cooling.LianLiTlCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.Galahad2CoolingProvider.IsGalahad2Id, sp.GetRequiredService<Nexus.Service.Cooling.Galahad2CoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.CorsairLinkCoolingProvider.IsCorsairId, sp.GetRequiredService<Nexus.Service.Cooling.CorsairLinkCoolingProvider>()),
            new CompositeFanControlProvider.FanSource(
                Nexus.Service.Cooling.Slv3CoolingProvider.IsSlv3Id, sp.GetRequiredService<Nexus.Service.Cooling.Slv3CoolingProvider>())));
        services.AddSingleton<ICoolingProvider>(sp => (ICoolingProvider)sp.GetRequiredService<IFanControlProvider>());
#endif
        services.AddSingleton<Np50CoolingProvider>();
        services.AddSingleton<MiniHubCoolingProvider>();
        services.AddSingleton<SmartHubCoolingProvider>();
        services.AddSingleton<QSeriesCoolerCoolingProvider>();
        services.AddSingleton<ICurveProvider>(sp => sp.GetRequiredService<StubCoolingProvider>());
        services.AddSingleton<CurveEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<CurveEngine>());
        services.AddSingleton<CalibrationRunner>();
        return services;
    }

    /// <summary>
    /// Diagnostics app: SMART/NVMe storage health, GPU health (NVML), cooling
    /// stall detection, memory (SMBIOS + Windows Memory Diagnostic), pnp
    /// problem scan, the Windows Event Log incident monitor, the health
    /// aggregator, and its background alert poller. Every Windows-only
    /// readout self-gates via OperatingSystem.IsWindows() inside its own
    /// module; SmartHealthMonitor is the one type whose constructor itself
    /// differs by platform (it holds the shared LhmComputer only on
    /// Windows). Depends on IFanControlProvider (AddNexusCooling) and
    /// ISensorProvider (AddNexusSensors) having already been registered.
    /// </summary>
    public static IServiceCollection AddNexusDiagnostics(this IServiceCollection services)
    {
        services.AddSingleton<Nexus.Service.Diagnostics.EventLog.EventLogMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Diagnostics.EventLog.EventLogMonitor>());

#if WINDOWS
        services.AddSingleton<Nexus.Service.Diagnostics.Storage.SmartHealthMonitor>(sp =>
            new Nexus.Service.Diagnostics.Storage.SmartHealthMonitor(sp.GetRequiredService<LhmComputer>()));
#else
        services.AddSingleton<Nexus.Service.Diagnostics.Storage.SmartHealthMonitor>();
#endif

        services.AddSingleton<Nexus.Service.Diagnostics.Gpu.GpuHealthMonitor>();
        services.AddSingleton<Nexus.Service.Diagnostics.Memory.MemoryDiagnosticOrchestrator>();
        services.AddSingleton<Nexus.Service.Diagnostics.SystemInfo.PnpProblemScanner>();

        services.AddSingleton<Nexus.Service.Diagnostics.Cooling.CoolingStallDetector>();
        services.AddSingleton<Nexus.Service.Diagnostics.Cooling.CoolingStallFeeder>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Diagnostics.Cooling.CoolingStallFeeder>());

        services.AddSingleton<Nexus.Service.Diagnostics.Temperature.ITemperatureHistoryStore>(_ =>
        {
            try
            {
                return new Nexus.Service.Diagnostics.Temperature.SqliteTemperatureHistoryStore();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[temperature-store] sqlite unavailable, using in-memory: {ex.Message}");
                return new Nexus.Service.Diagnostics.Temperature.InMemoryTemperatureHistoryStore();
            }
        });
        services.AddSingleton<Nexus.Service.Diagnostics.Temperature.TemperatureSampler>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Diagnostics.Temperature.TemperatureSampler>());

        services.AddSingleton<Nexus.Service.Diagnostics.DiagnosticsHealthModel>();
        services.AddSingleton<Nexus.Service.Diagnostics.DiagnosticsAlertService>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Diagnostics.DiagnosticsAlertService>());

        services.AddSingleton<Nexus.Service.Routes.SteamGameLibraryCache>();
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
        services.AddSingleton<Nexus.Service.Lighting.GameSyncGameScanner>();
        services.AddSingleton<IObsProvider, ObsProvider>();
        services.AddSingleton<ISteamProvider, SteamProvider>();
        services.AddSingleton<IDiscordProvider, DiscordProvider>();
        services.AddSingleton<Nexus.Service.Integrations.HomeAssistant.HomeAssistantClient>();
        services.AddSingleton<Nexus.Service.Integrations.HomeAssistant.HomeAssistantHub>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Integrations.HomeAssistant.HomeAssistantHub>());

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
        // serial - same race-and-hold pattern that lets NP50 and MiniHub
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
        services.AddSingleton<Nexus.Service.Lighting.Zones.IDeviceStructureSource>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.SmartHubLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.Zones.IComposableHubSource>(
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
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Keeb.KeebReactiveRenderer>();
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
            sp.GetRequiredService<Nexus.Service.Lighting.Engine.LightingEngine>(),
            sp.GetRequiredService<Nexus.Service.Devices.DeviceControlGate>(),
            sp.GetRequiredService<Nexus.Service.Peripherals.Keeb.IKeebProvider>(),
            sp.GetRequiredService<Nexus.Service.Lighting.KeebLightingDeviceProvider>()));
        services.AddHostedService<Nexus.Service.Peripherals.Hyte.Keeb.KeebInputWorker>();

        // Stream Deck: gen1-protocol button decks (Mini bench-verified
        // 2026-07-10). Peripheral, not lighting - no frame contributor, no
        // 30 Hz tick; see plans/streamdeck-support.md Phase 0/1. The
        // connection worker always starts with no simulated deck; the
        // localhost-only /streamdeck/dev/simulate route picks a model at
        // runtime via StreamDeckConnectionWorker.SetSimulatedModel, so a
        // running app constructs a SimulatedStreamDeckSurface only if that
        // route is called (a release web bundle exposes no UI to call it).
        services.AddSingleton<Nexus.Service.Peripherals.StreamDeck.StreamDeckImageCache>();
        // Lazy so resolving it does not construct StreamDeckConnectionWorker
        // right away - DeckActionExecutor needs it for deckBrightness/deckSleep,
        // but the worker also depends on IDeckActionExecutor, and a direct
        // constructor cycle would blow up at first resolution. The executor
        // instance calling .Value is already fully constructed by then, so
        // deferring the worker lookup to that point can never re-enter its
        // own construction, regardless of which caller (the worker's key
        // dispatch, or the test-press route) triggered it.
        services.AddSingleton(sp => new System.Lazy<Nexus.Service.Deck.IDeckSurfaceControl>(
            () => sp.GetRequiredService<Nexus.Service.Peripherals.StreamDeck.StreamDeckConnectionWorker>()));
        services.AddSingleton<Nexus.Service.Audio.AudioFilePlayer>();
        services.AddSingleton<Nexus.Service.Deck.DeckActionExecutor>();
        services.AddSingleton<Nexus.Service.Deck.IDeckActionExecutor>(sp =>
            sp.GetRequiredService<Nexus.Service.Deck.DeckActionExecutor>());
        services.AddSingleton<Nexus.Service.Peripherals.StreamDeck.StreamDeckConnectionWorker>(sp =>
            new Nexus.Service.Peripherals.StreamDeck.StreamDeckConnectionWorker(
                sp.GetRequiredService<Nexus.Service.Peripherals.Hid.IHidEnumerator>(),
                sp.GetRequiredService<Nexus.Service.Devices.Detection.HardwarePresence>(),
                sp.GetRequiredService<Nexus.Service.Devices.DeviceControlGate>(),
                sp.GetRequiredService<Nexus.Service.Persistence.IConfigStore>(),
                sp.GetRequiredService<Nexus.Service.Deck.IDeckActionExecutor>(),
                sp.GetRequiredService<Nexus.Service.Peripherals.StreamDeck.StreamDeckImageCache>(),
                sp.GetRequiredService<Nexus.Service.Sockets.MultiplexHub>(),
                sp.GetRequiredService<Nexus.Service.Sensors.ISensorProvider>(),
                weather: sp.GetRequiredService<Nexus.Service.Platform.Weather.IWeatherProvider>()));
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Peripherals.StreamDeck.StreamDeckConnectionWorker>());

        // Elgato Stream Deck profile import: read-only against the local
        // Elgato software's own store, never touching a physical deck.
        services.AddSingleton<Nexus.Service.Peripherals.StreamDeck.ElgatoImport.ElgatoProfileLocator>();
        services.AddSingleton<Nexus.Service.Peripherals.StreamDeck.ElgatoImport.ElgatoProfileTranslator>();

        // Lian Li Uni Hub SL-Infinity: HID connection worker + lighting + cooling.
        services.AddSingleton<Nexus.Service.Peripherals.LianLi.LianLiHub>();
        services.AddSingleton<Nexus.Service.Cooling.LianLiCoolingProvider>();
        services.AddSingleton<Nexus.Service.Lighting.LianLiLightingDeviceProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.LianLiLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.Zones.IDeviceStructureSource>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.LianLiLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.Zones.IComposableHubSource>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.LianLiLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.LianLiLightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.LianLiLightingFrameWriter>());
        services.AddHostedService<Nexus.Service.Peripherals.LianLi.LianLiConnectionWorker>();

        // Lian Li L-Wireless (SLV3) dongles: WinUSB TX/RX transport + discovery +
        // bind/unbind/identify connection worker. Windows-only for v1 (see
        // plans/lianli-wireless-support.md); other platforms get a stub discovery
        // that finds nothing, so the hub stays disconnected.
#if WINDOWS
        services.AddSingleton<Nexus.Service.Peripherals.LianLiWireless.ISlv3Discovery,
                              Nexus.Service.Peripherals.LianLiWireless.WindowsSlv3Discovery>();
#else
        services.AddSingleton<Nexus.Service.Peripherals.LianLiWireless.ISlv3Discovery,
                              Nexus.Service.Peripherals.LianLiWireless.StubSlv3Discovery>();
#endif
        services.AddSingleton<Nexus.Service.Peripherals.LianLiWireless.Slv3Hub>(sp =>
            new Nexus.Service.Peripherals.LianLiWireless.Slv3Hub(
                sp.GetRequiredService<Nexus.Service.Peripherals.LianLiWireless.ISlv3Discovery>(),
                port => new Nexus.Service.Peripherals.LianLiWireless.Slv3Transport(port.PortName, port.Role)));
        services.AddSingleton<Nexus.Service.Cooling.Slv3CoolingProvider>();
        services.AddHostedService<Nexus.Service.Peripherals.LianLiWireless.Slv3ConnectionWorker>();

        // SL-LCD Wireless fan screens: independent wired USB devices (not the
        // RF link). Windows-only for v1, same discovery-stub pattern as the
        // dongles above.
#if WINDOWS
        services.AddSingleton<Nexus.Service.Peripherals.LianLiWireless.ISlv3LcdDiscovery,
                              Nexus.Service.Peripherals.LianLiWireless.WindowsSlv3LcdDiscovery>();
#else
        services.AddSingleton<Nexus.Service.Peripherals.LianLiWireless.ISlv3LcdDiscovery,
                              Nexus.Service.Peripherals.LianLiWireless.StubSlv3LcdDiscovery>();
#endif
        services.AddSingleton<Nexus.Service.Peripherals.LianLiWireless.Slv3LcdHub>(sp =>
            new Nexus.Service.Peripherals.LianLiWireless.Slv3LcdHub(
                sp.GetRequiredService<Nexus.Service.Peripherals.LianLiWireless.ISlv3LcdDiscovery>(),
                port => new Nexus.Service.Peripherals.LianLiWireless.Slv3LcdTransport(port.PortName)));
        services.AddHostedService<Nexus.Service.Peripherals.LianLiWireless.Slv3LcdConnectionWorker>();
        services.AddSingleton<Nexus.Service.Peripherals.LianLiWireless.Slv3LcdMediaLibrary>();
        services.AddSingleton<Nexus.Service.Peripherals.LianLiWireless.Slv3LcdSensorReader>();
        services.AddHostedService<Nexus.Service.Peripherals.LianLiWireless.Slv3LcdStreamingWorker>();

        // SLV3 wireless RGB: one lighting device per bound fan chain (inner/outer
        // ring zones), streamed to the engine as a live single-frame RF_RgbSync
        // animation. No firmware ROM-effect catalog for v1 (see
        // plans/lianli-wireless-support.md section 2 phase note).
        services.AddSingleton<Nexus.Service.Lighting.Slv3LightingDeviceProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.Slv3LightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.Zones.IDeviceStructureSource>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.Slv3LightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.Slv3LightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.Slv3LightingFrameWriter>());

        // Lian Li Uni Fan TL: hub + cooling provider + connection worker.
        services.AddSingleton<Nexus.Service.Peripherals.LianLiTl.TlFanHub>();
        services.AddSingleton<Nexus.Service.Cooling.LianLiTlCoolingProvider>();
        services.AddHostedService<Nexus.Service.Peripherals.LianLiTl.TlFanConnectionWorker>();

        // Lian Li Galahad II Trinity AIO: hub + cooling provider + lighting + connection worker.
        services.AddSingleton<Nexus.Service.Peripherals.Galahad2.Galahad2Hub>();
        services.AddSingleton<Nexus.Service.Cooling.Galahad2CoolingProvider>();
        services.AddSingleton<Nexus.Service.Lighting.Galahad2LightingDeviceProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.Galahad2LightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.Zones.IDeviceStructureSource>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.Galahad2LightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.Galahad2LightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.Galahad2LightingFrameWriter>());
        services.AddHostedService<Nexus.Service.Peripherals.Galahad2.Galahad2ConnectionWorker>();

        // Corsair iCUE LINK System Hub: HID connection worker + lighting + cooling.
        // Auto-detects the daisy chain; no composition (each device is one fixed zone).
        services.AddSingleton<Nexus.Service.Peripherals.CorsairLink.CorsairLinkHub>();
        services.AddSingleton<Nexus.Service.Cooling.CorsairLinkCoolingProvider>();
        services.AddSingleton<Nexus.Service.Lighting.CorsairLinkLightingDeviceProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.CorsairLinkLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.Zones.IDeviceStructureSource>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.CorsairLinkLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.CorsairLinkLightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.CorsairLinkLightingFrameWriter>());
        services.AddHostedService<Nexus.Service.Peripherals.CorsairLink.CorsairLinkConnectionWorker>();
        services.AddSingleton<Nexus.Service.Peripherals.CorsairLink.CorsairLinkLcd>();
        services.AddSingleton<Nexus.Service.Peripherals.CorsairLink.CorsairLinkLcdMediaLibrary>();
        services.AddHostedService<Nexus.Service.Peripherals.CorsairLink.CorsairLinkLcdWorker>();

        // Lian Li Strimer Plus: HID connection worker + lighting.
        services.AddSingleton<Nexus.Service.Peripherals.Strimer.StrimerHub>();
        services.AddSingleton<Nexus.Service.Lighting.StrimerLightingDeviceProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.StrimerLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.Zones.IDeviceStructureSource>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.StrimerLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.StrimerLightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.StrimerLightingFrameWriter>());
        services.AddHostedService<Nexus.Service.Peripherals.Strimer.StrimerConnectionWorker>();

        // Smart (network) lights - Philips Hue, Nanoleaf, Govee today; WLED /
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
                sp.GetRequiredService<Nexus.Service.Lighting.LianLiLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.Slv3LightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.CorsairLinkLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.StrimerLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.Galahad2LightingDeviceProvider>(),
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
                sp.GetRequiredService<Nexus.Service.Lighting.LianLiLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.Slv3LightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.CorsairLinkLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.StrimerLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.Galahad2LightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.Smart.SmartLightProvider>(),
                sp.GetRequiredService<Nexus.Service.Persistence.IConfigStore>(),
                sp.GetRequiredService<Nexus.Service.Lighting.Engine.LightingEngine>()));
        }

        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.CnvsHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.QSeriesHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.Y70Handler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.KeebHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.FanHubHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.Aw5Handler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.Np50Handler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.SmartHubHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.LianLiHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.LianLiTlHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.LianLiWirelessHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.Galahad2Handler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.CorsairLinkHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.StrimerHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.TryxHandler>();
        // Registered as itself (not just IDeviceHandler) - StreamDeckRoutes.cs
        // injects the concrete type directly for GetWarning's detectedDevices.
        services.AddSingleton<Nexus.Service.Devices.Handlers.StreamDeckHandler>();
        services.AddSingleton<IDeviceHandler>(sp => sp.GetRequiredService<Nexus.Service.Devices.Handlers.StreamDeckHandler>());

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
        // Shared one-at-a-time flash gate: owns the mutex, IsFlashing flag, and Status
        // object that both FirmwareFlasher and ApkFlasher write to.
        services.AddSingleton<Nexus.Service.Devices.Firmware.FlashGate>();
        services.AddSingleton<Nexus.Service.Devices.Firmware.FirmwareFlasher>();
        services.AddSingleton<Nexus.Service.Devices.Firmware.ApkFlasher>();

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
        // by the NP50 binding - instead the hub factory below constructs
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

        // Tryx Panorama AIO: current firmware enumerates as a Windows usbprint
        // device (VID 0x391A, "RK PANO"); Linux/other platforms still see the
        // legacy CDC-ACM serial + ADB composite identity.
#if WINDOWS
        services.AddSingleton<Nexus.Service.Peripherals.Tryx.Panorama.ITryxPanoramaPanelDiscovery,
                              Nexus.Service.Peripherals.Tryx.Panorama.WindowsTryxRkDiscovery>();
#elif LINUX
        services.AddSingleton<Nexus.Service.Peripherals.Tryx.Panorama.ITryxPanoramaPanelDiscovery,
                              Nexus.Service.Peripherals.Tryx.Panorama.LinuxTryxPanoramaPortDiscovery>();
#else
        services.AddSingleton<Nexus.Service.Peripherals.Tryx.Panorama.ITryxPanoramaPanelDiscovery,
                              Nexus.Service.Peripherals.Tryx.Panorama.StubTryxPanoramaPortDiscovery>();
#endif
        services.AddSingleton<Nexus.Service.Peripherals.Tryx.Panorama.TryxPanoramaHub>(sp =>
            new Nexus.Service.Peripherals.Tryx.Panorama.TryxPanoramaHub(
                sp.GetRequiredService<Nexus.Service.Peripherals.Tryx.Panorama.ITryxPanoramaPanelDiscovery>(),
#if WINDOWS
                port => new Nexus.Service.Peripherals.Tryx.Panorama.WindowsTryxRkTransport(port.PortName, port.Serial),
#else
                port => new Nexus.Service.Peripherals.Tryx.Panorama.TryxPanoramaSerialTransport(port.PortName, port.Serial),
#endif
                sp.GetRequiredService<Nexus.Service.Sensors.ISensorProvider>(),
                sp.GetRequiredService<Nexus.Service.Fps.IFpsProvider>(),
                sp.GetRequiredService<Nexus.Service.Persistence.IConfigStore>()));
        services.AddSingleton<Nexus.Service.Peripherals.Tryx.Panorama.TryxPanoramaHeartbeatWorker>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Tryx.Panorama.TryxPanoramaHeartbeatWorker>());

#if WINDOWS
        services.AddSingleton<Nexus.Service.Devices.Detection.WindowsUsbEnumerator>();
        // Registered as the concrete type too: UsbDeviceChangeNotifier drives
        // Invalidate/SetTtl on the cache when PnP notifications are available.
        services.AddSingleton<Nexus.Service.Devices.Detection.CachingUsbEnumerator>(sp =>
            new Nexus.Service.Devices.Detection.CachingUsbEnumerator(
                sp.GetRequiredService<Nexus.Service.Devices.Detection.WindowsUsbEnumerator>()));
        services.AddSingleton<Nexus.Service.Devices.Detection.IUsbEnumerator>(sp =>
            sp.GetRequiredService<Nexus.Service.Devices.Detection.CachingUsbEnumerator>());
        services.AddHostedService(sp =>
            new Nexus.Service.Devices.Detection.UsbDeviceChangeNotifier(
                sp.GetRequiredService<Nexus.Service.Devices.Detection.CachingUsbEnumerator>()));
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
        services.AddSingleton<Nexus.Service.Devices.DeviceControlGate>();
        services.AddSingleton<DeviceManager>();
        services.AddSingleton<Nexus.Service.Devices.DeviceBroadcaster>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Devices.DeviceBroadcaster>());
        // Flips a never-manually-set third-party hub to Nexus Control ON the
        // first time it is connected while its competing brand app is not
        // running. Once adopted, the device stays on the Enabled list even if
        // the app later launches (DeviceControlGate stickiness carries it).
        services.AddHostedService<Nexus.Service.Devices.DeviceAdoptionService>();
        return services;
    }

    public static IServiceCollection AddNexusWeather(this IServiceCollection services)
    {
        services.AddSingleton<Nexus.Service.Platform.Weather.IWeatherProvider, Nexus.Service.Platform.Weather.OpenMeteoWeatherProvider>();
        return services;
    }

    public static IServiceCollection AddNexusStocks(this IServiceCollection services)
    {
        services.AddSingleton<Nexus.Service.Platform.Stocks.IStockQuoteProvider, Nexus.Service.Platform.Stocks.YahooStockQuoteProvider>();
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
        // Touch-mapping guard: repairs a touch digitizer mis-associated with
        // the wrong monitor. Detection needs the same user-session APIs as
        // topology; the registry write and devnode restart are session-
        // independent, so only the snapshot source is helper-backed.
        services.AddSingleton<Nexus.Service.Platform.Displays.ITouchMapSnapshotSource,
            Nexus.Service.Platform.Displays.HelperTouchMapSnapshotSource>();
        services.AddSingleton<Nexus.Service.Platform.Displays.IDigimonRegistryWriter,
            Nexus.Service.Platform.Displays.WindowsDigimonRegistryWriter>();
        services.AddSingleton<Nexus.Service.Platform.Displays.ITouchDigitizerDevnodeRestarter,
            Nexus.Service.Platform.Displays.WindowsTouchDigitizerDevnodeRestarter>();
        services.AddHostedService<Nexus.Service.Platform.Displays.TouchMappingGuardService>();
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
        // No touch-mapping mechanism outside Windows; the stub snapshot
        // source always reports "no helper", so TouchMappingGuard stays a
        // permanent no-op and never reaches the registry writer/restarter.
        services.AddSingleton<Nexus.Service.Platform.Displays.ITouchMapSnapshotSource,
            Nexus.Service.Platform.Displays.StubTouchMapSnapshotSource>();
        services.AddSingleton<Nexus.Service.Platform.Displays.IDigimonRegistryWriter,
            Nexus.Service.Platform.Displays.NullDigimonRegistryWriter>();
        services.AddSingleton<Nexus.Service.Platform.Displays.ITouchDigitizerDevnodeRestarter,
            Nexus.Service.Platform.Displays.NullTouchDigitizerDevnodeRestarter>();
#endif
        services.AddSingleton<Nexus.Service.Platform.Displays.TouchMappingGuard>();
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

        // Extracted body of SystemRoutes.cs so the Stream Deck executor can
        // drive the same OS-level actions headless (no loopback HTTP call).
        services.AddSingleton<Nexus.Service.Actions.SystemActions>();

        // Replay persisted lighting + cooling state to hardware on startup.
        // Lives in Lifecycle because it doesn't belong to a single domain.
        services.AddHostedService<AutoRestoreOnStart>();
        return services;
    }

    public static IServiceCollection AddNexusBenchmarks(this IServiceCollection services)
    {
        services.AddSingleton<Nexus.Service.Benchmarks.IBenchmarkProvider, Nexus.Service.Benchmarks.Providers.ExternalToolBenchmarkProvider>();
        services.AddSingleton<Nexus.Service.Benchmarks.BenchmarkRunner>();
        services.AddSingleton<ProfileManager>();
        services.AddSingleton<Nexus.Service.Media.MediaLibrary>();
        services.AddSingleton<Nexus.Service.Panel.PanelBgLibrary>();
        services.AddSingleton<Nexus.Service.Deck.DeckImageStore>();
        services.AddSingleton<Nexus.Service.Gallery.GalleryLibrary>();
        // Also consumed by /system/pick-path (SystemRoutes.cs), not just gallery.
        services.AddSingleton<Nexus.Service.Platform.IFileDialogPicker, Nexus.Service.Platform.FileDialogPicker>();
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
            Nexus.Service.Widgets.AppActions.AppInstallActions.RegisterAll(registry);
            Nexus.Service.Widgets.AppActions.SystemSpecsActions.RegisterAll(registry);
            return registry;
        });
        services.AddSingleton<Nexus.Service.Widgets.AppDispatchRateLimiter>();

        // Generic external-tool manager (NEX-13): fetches + installs a device's
        // sidecar payload, routed to a per-target strategy. Hosted so its StopAsync
        // tears down every tracked tool on service shutdown.
        services.AddSingleton<Nexus.Service.Common.ExternalTools.IAdbDeviceRegistry,
            Nexus.Service.Common.ExternalTools.AdbDeviceRegistry>();
        services.AddSingleton<Nexus.Service.Common.ExternalTools.IToolInstallStrategy,
            Nexus.Service.Common.ExternalTools.HostExeInstallStrategy>();
        services.AddSingleton<Nexus.Service.Common.ExternalTools.IToolInstallStrategy,
            Nexus.Service.Common.ExternalTools.AndroidAdbInstallStrategy>();
        services.AddSingleton<Nexus.Service.Common.ExternalTools.ExternalToolManager>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Common.ExternalTools.ExternalToolManager>());
        // Auto-launches each installed bundled driver app's binary when its device
        // is present (runs at boot, pre-login).
        services.AddSingleton<Nexus.Service.Common.ExternalTools.IDriverGateStopHook, Nexus.Service.Peripherals.Aw5.Aw5PanelBlanker>();
        // false: the AW5 is driven natively below, and its vendor binary would be a
        // second writer on the same HID. Flip to true to restore the vendor path,
        // which stands the native worker down.
        services.AddSingleton(new Nexus.Service.Common.ExternalTools.DriverExePolicy(enabled: false));
        services.AddHostedService<Nexus.Service.Common.ExternalTools.DriverAutoLaunchWorker>();

        // Drives the AW5 pump displays in place of the vendor driver .exe. Exactly one
        // of the two runs; DriverExePolicy picks which.
        services.AddSingleton<Nexus.Service.Peripherals.Aw5.Aw5Hub>();
        services.AddSingleton<Nexus.Service.Peripherals.Aw5.Aw5SensorReader>();
        services.AddHostedService<Nexus.Service.Peripherals.Aw5.Aw5PanelWorker>();
        return services;
    }

    public static IServiceCollection AddNexusPanel(this IServiceCollection services, int servicePort)
    {
        // QSeriesPortWatcher keeps `adb reverse tcp:{servicePort}` alive
        // while a HYTE Q60 / Q80 USB display is attached. Without it,
        // every time Y70's adb-server restarts the panel's multiplex
        // WebSocket on the Q-series silently freezes. Windows-only - the
        // Q-series host stack lives on the Y70 PC.
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<Nexus.Service.QSeries.QSeriesPortWatcher>(
                sp => new Nexus.Service.QSeries.QSeriesPortWatcher(
                    servicePort,
                    sp.GetRequiredService<Nexus.Service.Devices.Detection.HardwarePresence>(),
                    sp.GetRequiredService<Nexus.Service.Panel.PanelDeviceRegistry>(),
                    sp.GetRequiredService<Nexus.Service.Devices.DeviceControlGate>(),
                    sp.GetRequiredService<IConfigStore>(),
                    sp.GetRequiredService<Nexus.Service.Common.ExternalTools.IAdbDeviceRegistry>(),
                    sp.GetService<Nexus.Service.Panel.PanelTunnelMonitor>()));
            services.AddHostedService(sp =>
                sp.GetRequiredService<Nexus.Service.QSeries.QSeriesPortWatcher>());
        }

        services.AddSingleton<Nexus.Service.Panel.UsbPhoneWatcher>(
            sp => new Nexus.Service.Panel.UsbPhoneWatcher(
                servicePort,
                sp.GetRequiredService<Nexus.Service.Devices.Detection.HardwarePresence>()));
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Panel.UsbPhoneWatcher>());

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
        services.AddSingleton<Nexus.Service.Panel.PanelAutoPromotion>();

        // Corsair Xeneon Edge auto-orientation + native settings: reads the
        // panel's hardware orientation sensor over vendor HID and applies the
        // matching Windows display rotation. Cross-platform HID read like the
        // Keeb workers; the apply side degrades to a no-op off Windows via
        // NoopDisplayOrientationProvider. Registered as a plain singleton
        // (in addition to IHostedService below) so the /displays/{id}/xeneon-
        // settings routes can resolve it directly to reach
        // ReadSettingsAsync/SetControlAsync - it is the single owner of the
        // HID handle those calls must serialize through.
        services.AddSingleton<Nexus.Service.Peripherals.Corsair.XeneonEdge.XeneonEdgeOrientationWorker>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Corsair.XeneonEdge.XeneonEdgeOrientationWorker>());

        // Streamed panels: panels rendered off-screen by the overlay's stream
        // engine and piped as H.264 to USB display devices through swappable
        // IStreamedPanelTransport implementations. The D213 reference device
        // is DEV_TOOLS-only (final hardware undefined; a release must never
        // claim a consumer gadget whose adb serial happens to match) and
        // Windows-only (the render engine needs WGC/MF in the session-1
        // overlay). The coordinator idles when no discovery is registered.
        services.AddSingleton<Nexus.Service.Panel.Streams.StreamedPanelStore>();
#if DEV_TOOLS
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<Nexus.Service.Panel.Streams.IStreamedPanelDiscovery>(sp =>
                new Nexus.Service.Panel.Streams.D213PanelDiscovery(
                    sp.GetRequiredService<Nexus.Service.Panel.Streams.StreamedPanelStore>()));
        }
#endif
        services.AddSingleton<Nexus.Service.Panel.Streams.StreamedPanelCoordinator>(sp =>
        {
            Action? notifyOverlay = null;
#if WINDOWS
            // Same push nudge OverlayHostBootstrap uses: the overlay refetches
            // prefs AND stream assignments on it, so no new IPC message exists.
            var helperRegistry = sp.GetService<Nexus.Service.Helper.HelperRegistry>();
            if (helperRegistry is not null)
            {
                notifyOverlay = () =>
                {
                    try { _ = Nexus.Service.Helper.Domains.LifecycleCommands.NotifyOverlayPrefsChangedAsync(helperRegistry); }
                    catch { }
                };
            }
#endif
            return new Nexus.Service.Panel.Streams.StreamedPanelCoordinator(
                sp.GetServices<Nexus.Service.Panel.Streams.IStreamedPanelDiscovery>(),
                sp.GetRequiredService<Nexus.Service.Panel.Streams.StreamedPanelStore>(),
                sp.GetRequiredService<Nexus.Service.Panel.PanelDeviceRegistry>(),
                sp.GetRequiredService<Nexus.Service.Devices.DeviceControlGate>(),
                sp.GetRequiredService<Nexus.Service.Panel.IOverlayHost>(),
                notifyOverlay);
        });
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Panel.Streams.StreamedPanelCoordinator>());

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

        // WebRTC DataChannel direct P2P transport: STUN-only fallback-free path
        // that rides the SAME sealed framing as the relay, over data channels
        // instead of a relay WebSocket. Endpoint-driven singleton, not a hosted
        // service - see RtcSessionManager's class doc.
        services.AddSingleton<Nexus.Service.Rtc.RtcSessionManager>();
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
    /// OTA self-update engine. Cross-platform compile; install handoff is
    /// Windows-only and is gated internally by platform checks.
    /// </summary>
    public static IServiceCollection AddNexusUpdate(this IServiceCollection services)
    {
        services.AddSingleton<IUpdateSource>(sp =>
            new GitHubReleaseProvider(sp.GetRequiredService<IHttpClientFactory>()));
        services.AddSingleton<UpdateDownloader>();
        // UpdateService is both a singleton (for route access) and a hosted
        // service (for the background poll loop). The same instance is reused.
        services.AddSingleton<UpdateService>();
        services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
        return services;
    }

    /// <summary>
    /// Cloud accounts: token-holder client, profile sync, and device
    /// reporting. Zero cost when logged out - CloudProfileSyncService and
    /// CloudDeviceReporter both idle until CloudAccountService reports an
    /// active account.
    /// </summary>
    public static IServiceCollection AddNexusCloud(this IServiceCollection services)
    {
        services.AddSingleton<Nexus.Service.Cloud.ICloudApiClient, Nexus.Service.Cloud.CloudApiClient>();
        services.AddSingleton<Nexus.Service.Cloud.CloudAccountService>();
        services.AddSingleton<Nexus.Service.Cloud.CloudProfileSyncService>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Cloud.CloudProfileSyncService>());
        services.AddHostedService<Nexus.Service.Cloud.CloudDeviceReporter>();
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
