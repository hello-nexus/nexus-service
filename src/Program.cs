// Nexus local service - Minimal API host for Native AOT.
//
// Default bind: http://localhost:9400.
// Override with the first command-line arg:
//     nexus-service http://localhost:9400

using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nexus.Service.Activity;
using Nexus.Service.Auth;
using Nexus.Service.Cooling;
using Nexus.Service.DependencyInjection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Routes;
using Nexus.Service.Security;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

// Linux screen-mirror capture helper: the root daemon re-invokes itself as the
// session user (setpriv) for this, because the xdg-desktop-portal ScreenCast
// portal rejects a root caller (can't read its /proc). Must be the very first
// thing - its stdout (fd 1) carries the raw RGB frame stream, so nothing else
// (not even a boot-timer line) may write to stdout before it takes over.
#if LINUX
if (args.Length > 0
    && args[0] == Nexus.Service.Lighting.Capture.LinuxScreenCastHelper.Verb)
{
    return Nexus.Service.Lighting.Capture.LinuxScreenCastHelper.Run(args);
}
#endif

Nexus.Service.Lifecycle.BootTimer.Mark("process entry");

// Early-exit CLI flags (install/uninstall/tray/--open-app/protocol URLs,
// or no-args double-click on Windows). Each handler short-circuits the
// daemon startup. Runs before the single-instance mutex because
// --install-pawnio is briefly a second instance during the elevated install.
if (Nexus.Service.Lifecycle.CommandLineEntry.TryEarlyExit(args) is int earlyExit)
    return earlyExit;
Nexus.Service.Lifecycle.BootTimer.Mark("after CommandLineEntry.TryEarlyExit");

// Pull out the lifecycle flags that gate behaviour later (SCM service
// mode, --no-window startup suppression, --relaunch-elevated self-elevation
// follow-up). The cleaned args are forwarded to the host below.
var (cliArgs, serviceMode, suppressStartupWindow, isRelaunchElevated) =
    Nexus.Service.Lifecycle.CommandLineEntry.StripLifecycleFlags(args);
args = cliArgs;

// --emit-openapi <path>: write the generated OpenAPI document (the REST-route
// inventory in docs/openapi.json) from the real route registration, then exit
// before any hardware starts. Stripped from args so URL resolution ignores it;
// gated as a test host below so every boot side effect is skipped.
string? emitOpenApiPath = null;
{
    var kept = new List<string>(args.Length);
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--emit-openapi")
        {
            // A trailing flag with no path must fail loudly, not fall through
            // to booting the hardware daemon.
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("--emit-openapi requires an output path");
                return 2;
            }
            emitOpenApiPath = args[++i];
            continue;
        }
        kept.Add(args[i]);
    }
    args = kept.ToArray();
}

// Integration-test host marker. The WebApplicationFactory fixture
// (tests/.../Integration/NexusAppFactory) sets NEXUS_TEST_HOST=1 before the
// entry point runs so the in-process test host skips machine-mutating boot
// side effects - single-instance mutex, HTTPS cert provisioning, GPU/profile
// init, orphan-process cleanup, OS protocol-handler registration - and the
// platform GUI/service host, falling through to a plain app.Run() that the
// factory intercepts. Production (env unset) takes the original path unchanged.
var testHost = Environment.GetEnvironmentVariable("NEXUS_TEST_HOST") == "1"
    || emitOpenApiPath is not null;

// Hold LhmComputer's background Open until the boot-time PawnIO check has
// run, so a driver installed or repaired this boot is visible to SuperIO
// enumeration immediately. Armed only where WireAppWindowAndPawnIo will
// signal it; the test host and non-Windows platforms never wait.
if (OperatingSystem.IsWindows() && !testHost)
    Nexus.Service.Lifecycle.PawnIoBootGate.Arm();

// Root system daemon (full hardware access) adopts the active user's session
// env - D-Bus, runtime dir, config home, display - so the tray, MPRIS media,
// volume, and dashboard launcher keep working. No-op for a --user install.
// Done FIRST so HOME/XDG_* are correct before anything (e.g. the service log)
// resolves a path from them.
#if LINUX
Nexus.Service.Platform.Linux.LinuxSession.AdoptActiveSessionEnv();
#endif

// Capture stdout / stderr to a rotating nexus-service.log file before anything else
// writes to the console. Doesn't change Console behaviour - just tees output.
Nexus.Service.Platform.ServiceLog.Initialize();
Nexus.Service.Lifecycle.BootTimer.Mark("after ServiceLog.Initialize");

var url = ServiceLaunchIntent.ResolveServiceUrl(args);
var servicePort = ServiceLaunchIntent.ResolveServicePort(url);
Nexus.Service.Lifecycle.BootTimer.Mark("after URL resolve");

// Single-instance guard - if another nexus-service is already running,
// open or focus the dashboard window instead of spawning a second service.
// When relaunching elevated, retry for up to 10s while the parent shuts down.
// Skipped under SCM: the service controller already enforces single-instance.
System.Threading.Mutex? singleInstance = null;
if (!serviceMode && !testHost)
{
    bool isFirst;
    var deadline = DateTime.UtcNow.AddSeconds(isRelaunchElevated ? 10 : 0);
    while (true)
    {
        singleInstance = new System.Threading.Mutex(true, "Global\\NexusServiceMutex", out isFirst);
        if (isFirst) break;
        singleInstance.Dispose();
        singleInstance = null;
        if (!isRelaunchElevated || DateTime.UtcNow >= deadline)
        {
            Console.WriteLine("[nexus-service] already running, opening dashboard window");
            OpenExistingServiceWindow(servicePort);
            return 0;
        }
        System.Threading.Thread.Sleep(150);
    }
}
using var _singleInstance = singleInstance;
Nexus.Service.Lifecycle.BootTimer.Mark("after single-instance mutex");

// TEMPORARY (remove ~2026-07-20 with DataLayoutMigration): migrate the flat data
// layout to the grouped devices/ + media/ layout before any store resolves its
// directory (a store must not create the new target ahead of the move). One-shot
// and idempotent; skipped for the test/openapi hosts so doc generation never
// touches a dev's data.
if (!testHost)
{
    Nexus.Service.Lifecycle.DataLayoutMigration.Run();
    Nexus.Service.Lifecycle.BootTimer.Mark("after DataLayoutMigration");
}

// Cold-start self-elevation: when the user double-clicks the EXE while no
// service is running and we're not yet elevated, prompt for UAC and let the
// elevated child take over the mutex. The manifest is asInvoker, so this is
// the only path that triggers a UAC prompt; subsequent double-clicks while
// the service is running hit the second-instance handoff above and never
// prompt. --no-window means schtask logon launch - skip auto-elevate so we
// don't ambush the user with UAC at sign-in. --service skips because SCM
// already runs us as LocalSystem.
if (OperatingSystem.IsWindows()
    && !testHost
    && !isRelaunchElevated
    && !suppressStartupWindow
    && !serviceMode
    && !Nexus.Service.Platform.ProcessElevation.GetCurrent().IsElevated)
{
    var coldStartElevation = Nexus.Service.Lifecycle.ProcessRelauncher.TryRelaunchAsAdmin();
    if (coldStartElevation == Nexus.Service.Lifecycle.RelaunchResult.Started)
    {
        return 0;
    }
    // UAC denied or relaunch failed: fall through and start unelevated.
}

// Bind on all interfaces so phones on the same LAN can reach the panel
// phone pairing surface without internet.
var httpsPort = servicePort == 9400 ? 9443 : servicePort + 443;

// Q-series panel tunnel listener: a second loopback-only port that
// QSeriesPortWatcher points the panel's `adb reverse` at. The adb tunnel is
// the only intended client (see PanelTunnelMonitor for the caveats), giving
// the watcher's escalation reboot an attributable liveness signal - on the
// main port, panel traffic is indistinguishable from the desktop dashboard
// (both 127.0.0.1), so an open dashboard masked a stranded panel
// indefinitely. Windows-only,
// matching the Q-series host stack. A taken port deactivates the monitor
// (watcher falls back to the legacy record-based gate) instead of failing the
// Kestrel bind and taking the service down.
var panelTunnelPort = servicePort + 1;
var panelTunnelMonitor = new Nexus.Service.Panel.PanelTunnelMonitor(
    OperatingSystem.IsWindows() && !testHost && IsLoopbackPortFree(panelTunnelPort) ? panelTunnelPort : null);

static bool IsLoopbackPortFree(int port)
{
    try
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        probe.Start();
        probe.Stop();
        return true;
    }
    catch (System.Net.Sockets.SocketException)
    {
        Console.Error.WriteLine($"[nexus-service] panel tunnel port {port} unavailable; panel liveness falls back to record-based gate");
        return false;
    }
}
X509Certificate2? localHttpsCertificate = null;
if (!testHost)
{
    try
    {
        localHttpsCertificate = LocalHttpsCertificate.LoadOrCreate();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[nexus-service] local HTTPS disabled: {ex.Message}");
    }
}
Nexus.Service.Lifecycle.BootTimer.Mark("after LocalHttpsCertificate.LoadOrCreate");

// Set content root to the exe's directory so wwwroot/ is found
// regardless of which directory the user double-clicks from.
var exeDir = AppContext.BaseDirectory;
// Dev override: if `<CommonAppData>/Nexus/wwwroot-dev/index.html` exists,
// serve from there instead of the installed wwwroot. Lets us replace the
// SPA bundle on a running install without elevating into Program Files
// (which on the Q60 bench rig triggers a USB perturbation that degrades
// the device's WebView GPU state). The override is a sibling of the
// installer payload, not a merge - it fully shadows the bundled wwwroot
// when present, so the dev push must contain a full SPA build.
static string ResolveWebRoot(string exeDir)
{
    var defaultRoot = Path.Combine(exeDir, "wwwroot");
    if (!OperatingSystem.IsWindows()) return defaultRoot;
    try
    {
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrEmpty(commonAppData)) return defaultRoot;
        var dev = Path.Combine(commonAppData, "Nexus", "wwwroot-dev");
        if (File.Exists(Path.Combine(dev, "index.html")))
        {
            Console.Error.WriteLine($"[nexus-service] wwwroot dev override active: {dev}");
            return dev;
        }
    }
    catch { /* fall through to default */ }
    return defaultRoot;
}
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = exeDir,
    WebRootPath = ResolveWebRoot(exeDir),
});
Nexus.Service.Lifecycle.BootTimer.Mark("after WebApplication.CreateSlimBuilder");
// Logging policy: the service's own diagnostics go through Console/ServiceLog, so
// the only ILogger output is framework noise. Drop it to Warning - in particular
// Microsoft.AspNetCore.Hosting.Diagnostics' per-request "Request starting/finished"
// Information lines, which otherwise flood nexus-service.log on every internal API call.
// Keep Microsoft.Hosting.Lifetime at Information for the useful "Now listening" /
// "Application started/stopping" boot markers.
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
// Kestrel + form upload body limits. Default Kestrel cap is 30 MB which drops
// larger multipart uploads before /media/import sees them (the browser then
// reports "could not reach the service"). Use the largest route cap (panel
// background media allows bigger uploads than lighting) so the route-level
// check is the only place we reject oversized uploads.
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = System.Math.Max(Nexus.Service.Media.MediaImporter.MaxFileSize, Nexus.Service.Panel.PanelBgImporter.MaxFileSize);
    if (emitOpenApiPath is not null)
    {
        // Emit mode starts the host only to register endpoints; bind an
        // ephemeral loopback port so it never collides with a running service
        // and serves no traffic. Dynamic (:0) binding requires an explicit
        // address, not ListenLocalhost.
        k.Listen(System.Net.IPAddress.Loopback, 0);
        return;
    }
    k.ListenAnyIP(servicePort);
    if (localHttpsCertificate is not null)
    {
        k.ListenAnyIP(httpsPort, o => o.UseHttps(localHttpsCertificate));
    }
    if (panelTunnelMonitor.Port is int tunnelPort)
    {
        // IPv4 loopback only - the adb server's host-side connect is IPv4, and
        // ListenLocalhost's dual-family bind could survive on [::1] alone,
        // leaving the monitor "active" on a socket adb can't reach.
        k.Listen(System.Net.IPAddress.Loopback, tunnelPort, lo => lo.Use(next => ctx =>
        {
            ctx.Transport = new Nexus.Service.Panel.TunnelActivityDuplexPipe(ctx.Transport, panelTunnelMonitor);
            return next(ctx);
        }));
    }
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = System.Math.Max(Nexus.Service.Media.MediaImporter.MaxFileSize, Nexus.Service.Panel.PanelBgImporter.MaxFileSize);
    o.ValueLengthLimit = int.MaxValue;
});

// JSON - source-generated for AOT (no reflection). Enums serialize as
// integers by default; opt specific enums into string form with a typed
// JsonStringEnumConverter<TEnum> on the type, never the non-generic global
// converter (not AOT-safe).
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

// OpenAPI route inventory (docs/openapi.json, generated by --emit-openapi).
// AddEndpointsApiExplorer is required because CreateSlimBuilder omits the API
// Explorer services OpenAPI reads endpoints from (without it the document has
// zero paths). Title/version are pinned so the committed artifact is byte-stable
// across builds and machines.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "Nexus local service REST API";
    doc.Info.Version = "v1";
    // Servers is auto-filled from the bind address (an ephemeral port under
    // --emit-openapi); clear it so the committed artifact is byte-stable.
    doc.Servers?.Clear();
    return Task.CompletedTask;
}));

// CORS - loopback for the bundled SPA, plus the public web app at
// hellonexus.com (HTTPS only). The hosted SPA fetches /pair to obtain a token,
// then talks to the local service on http://localhost:9400 from the browser.
// Token-based auth still gates every state-changing endpoint, so widening the
// origin list does not weaken the CSRF posture - the attacker would still need
// the per-installation token, which only loopback callers can request.
var allowedOrigins = CorsConfig.BuildAllowedOrigins(servicePort, localHttpsCertificate is not null ? httpsPort : 0);
#if DEBUG
// Debug build: accept any http://localhost:* or http://127.0.0.1:* so the
// Vite dev server on a random port (5173-5180 etc.) can reach the service
// during frontend iteration. Loopback-only, no public surface exposed.
builder.Services.AddNexusCors(allowedOrigins, debugLoopbackWildcard: true);
#else
// Release/AOT build: strict exact-match allowlist (see CorsConfig) so
// production only accepts the bundled SPA on loopback plus hellonexus.com.
builder.Services.AddNexusCors(allowedOrigins, debugLoopbackWildcard: false);
#endif

builder.Services.AddHttpClient();
Nexus.Service.Lifecycle.BootTimer.Mark("after Kestrel + JSON + CORS + AddHttpClient");

// All DI registrations live in per-domain extension methods under
// src/DependencyInjection/. Order matters only where there are cross-domain
// dependencies (e.g. Lighting consumes the OpenRGB controller registered in
// AddNexusLighting before AddNexusDevices uses it as ILightingDeviceProvider).
builder.Services.AddNexusCore();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusCore");
builder.Services.AddNexusSensors();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusSensors");
builder.Services.AddNexusCooling();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusCooling");
builder.Services.AddNexusDiagnostics();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusDiagnostics");
builder.Services.AddNexusBenchmarks();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusBenchmarks");
builder.Services.AddNexusLighting();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusLighting");
builder.Services.AddNexusWebcam();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusWebcam");
builder.Services.AddNexusDevices();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusDevices");
builder.Services.AddNexusPeripherals();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusPeripherals");
builder.Services.AddNexusActivity();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusActivity");
builder.Services.AddNexusNetwork();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusNetwork");
builder.Services.AddNexusLifecycle();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusLifecycle");
builder.Services.AddNexusWeather();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusWeather");
builder.Services.AddNexusStocks();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusStocks");
builder.Services.AddNexusWidgets();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusWidgets");
builder.Services.AddSingleton(panelTunnelMonitor);
builder.Services.AddNexusPanel(servicePort);
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusPanel");
builder.Services.AddNexusLinuxDBus();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusLinuxDBus");
builder.Services.AddNexusHelper();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusHelper");
builder.Services.AddNexusUpdate();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusUpdate");
builder.Services.AddNexusCloud();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusCloud");

// mDNS / Bonjour advertiser for the iOS companion app's Wi-Fi discovery.
// Reads HttpsPort + SpkiFingerprint + MachineName off PanelPhonePairingService
// after the Pairing config block below has populated them.
builder.Services.AddHostedService<Nexus.Service.Discovery.MdnsAdvertiser>();
Nexus.Service.Lifecycle.BootTimer.Mark("after MdnsAdvertiser register");

// Wallpaper-change push for panels rendering the desktop-wallpaper background.
builder.Services.AddHostedService<Nexus.Service.Panel.DesktopWallpaperWatcher>();

// Emit mode starts the host far enough to register endpoints for the OpenAPI
// document (WebApplication defers that to StartAsync); drop every hosted service
// so no background device work runs during that start.
if (emitOpenApiPath is not null)
    builder.Services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();

// ── Build ──
var app = builder.Build();
Nexus.Service.Lifecycle.BootTimer.Mark("after builder.Build()");

// Connect the session D-Bus once here - single-threaded, BEFORE hosted services
// (curve engine etc.) start. A root daemon drops euid for this socket connect
// (LinuxSession.ConnectAsSessionUser); doing it now keeps that process-wide euid
// window from racing a concurrent root pwm write. Idempotent + best-effort.
#if LINUX
if (app.Services.GetService(typeof(Nexus.Service.Platform.Linux.DBus.DBusConnection))
        is Nexus.Service.Platform.Linux.DBus.DBusConnection dbus)
{
    try { dbus.StartAsync().GetAwaiter().GetResult(); }
    catch (Exception ex) { Console.Error.WriteLine($"[dbus] startup connect skipped: {ex.Message}"); }
}
#endif

if (!testHost)
{
    Nexus.Service.Lifecycle.AppBootstrap.ScheduleGpuWarmup(app);
    Nexus.Service.Lifecycle.BootTimer.Mark("after ScheduleGpuWarmup (deferred to ApplicationStarted)");
    Nexus.Service.Lifecycle.AppBootstrap.InitializeProfiles(app);
    Nexus.Service.Lifecycle.BootTimer.Mark("after InitializeProfiles");
    Nexus.Service.Lifecycle.AppBootstrap.WireBeatsAndPresence(app);
    Nexus.Service.Lifecycle.BootTimer.Mark("after WireBeatsAndPresence");
    app.Services.GetRequiredService<Nexus.Service.Telemetry.ITelemetry>()
        .Capture(Nexus.Service.Telemetry.TelemetryEvents.AppStarted);
}

// Middleware pipeline
//
// REST-over-relay capture middleware - MUST be first. The off-LAN panel tunnels
// its REST calls over the relay (rid_http); RelayHttpDispatcher re-enters this
// exact pipeline in-process to serve them. As the very first middleware, the
// `next` we close over is the complete downstream chain (security headers,
// static files, routing, CORS, auth, endpoints), so a tunneled request runs
// through identical handling to a LAN request. Captured once on the priming
// pass below; the closure is a no-op on every subsequent request.
{
    var relayHttpDispatcher = app.Services.GetRequiredService<Nexus.Service.Relay.RelayHttpDispatcher>();
    app.Use(async (ctx, next) =>
    {
        if (!relayHttpDispatcher.IsReady)
        {
            relayHttpDispatcher.SetPipeline(c => next(c));
        }
        await next(ctx);
    });
}

var wsOptions = new WebSocketOptions();
// Real PING/PONG keepalive. KeepAliveTimeout must be set: without it the
// runtime's keepalive is an unsolicited outbound PONG that clients never
// answer. With it, the server PINGs and the browser answers at the protocol
// layer, so a connected-but-idle panel produces steady inbound bytes on the
// tunnel listener (PanelTunnelMonitor's liveness signal) and a dead socket is
// aborted instead of lingering.
wsOptions.KeepAliveInterval = TimeSpan.FromSeconds(30);
wsOptions.KeepAliveTimeout = TimeSpan.FromSeconds(60);
#if !DEBUG
// Release/AOT: pin WS to the service's own origins.
foreach (var origin in allowedOrigins)
{
    wsOptions.AllowedOrigins.Add(origin);
}
// In Debug we leave AllowedOrigins empty so the WS handshake accepts any
// loopback origin (matches the loose HTTP CORS above). Token-based auth on
// the query string still gates authenticated endpoints.
#endif
app.UseWebSockets(wsOptions);

app.UseNexusSecurityHeaders();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();

// Private Network Access: Chrome gates a request from a public page
// (https://hellonexus.com) to a loopback address (http://localhost:9400) behind
// a preflight that must be answered with Access-Control-Allow-Private-Network.
// ASP.NET's CORS middleware doesn't emit it, so echo it for our allowlisted
// origins - this is what lets the hosted web app detect and drive a local Nexus
// from the desktop browser. Only set for an allowed Origin on a PNA preflight.
var pnaAllowedOrigins = new HashSet<string>(allowedOrigins, StringComparer.OrdinalIgnoreCase);
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Method == "OPTIONS"
        && ctx.Request.Headers["Access-Control-Request-Private-Network"].ToString() == "true"
        && pnaAllowedOrigins.Contains(ctx.Request.Headers["Origin"].ToString()))
    {
        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            return Task.CompletedTask;
        });
    }
    await next(ctx);
});

app.UseCors();

app.UseNexusPathAuth();
Nexus.Service.Lifecycle.BootTimer.Mark("after middleware wire");

// ── Map all routes ──
app.MapPingEndpoints();
app.MapDefaultsEndpoints();
app.MapAuthEndpoints();
app.MapSystemEndpoints();
app.MapServiceControlEndpoints();
app.MapTelemetryEndpoints();
app.MapOnboardingEndpoints();
app.MapCoolingEndpoints();
app.MapBenchmarkEndpoints();
app.MapLightingEndpoints();
app.MapGameSyncEndpoints();
app.MapWebcamEndpoints();
app.MapObsEndpoints();
app.MapSteamEndpoints();
app.MapDiscordEndpoints();
app.MapDevicesEndpoints();
app.MapPeripheralEndpoints();
app.MapKeebEndpoints();
app.MapStreamDeckEndpoints();
app.MapDeckImageEndpoints();
app.MapDisplayEndpoints();
app.MapActivityEndpoints();
app.MapLifecycleEndpoints();
app.MapDiagnosticsEndpoints();
app.MapDiagnosticsHealthEndpoints();
app.MapMediaLibraryEndpoints();
app.MapPanelBgEndpoints();
app.MapGalleryEndpoints();
app.MapTransferEndpoints();
app.MapProfileEndpoints();
app.MapPanelEndpoints();
app.MapStreamedPanelEndpoints();
app.MapOverlayEndpoints();
app.MapWeatherEndpoints();
app.MapStockEndpoints();
app.MapAppEndpoints();
app.MapConflictEndpoints();
app.MapTryxEndpoints();
app.MapSlv3Endpoints();
app.MapSlv3LcdEndpoints();
app.MapUpdateEndpoints();
app.MapCloudEndpoints();
app.MapWebSocketEndpoints();
app.MapRtcEndpoints();
Nexus.Service.Lifecycle.BootTimer.Mark("after route mapping");

#if DEBUG
// Live OpenAPI document at /openapi/v1.json for local inspection. DEBUG-only:
// a release/AOT build maps no endpoint, so production exposes nothing. The
// committed docs/openapi.json is produced by the --emit-openapi path, not this.
app.MapOpenApi();
#endif

// --emit-openapi: the routes are now registered, so the OpenAPI document is
// complete. Write it and exit before pairing/prime/hardware wiring runs.
if (emitOpenApiPath is not null)
{
    Nexus.Service.Lifecycle.OpenApiEmitter.Emit(app, emitOpenApiPath);
    return 0;
}

{
    var pairing = app.Services.GetRequiredService<Nexus.Service.Panel.PanelPhonePairingService>();
    pairing.ServicePort = servicePort;
    pairing.HttpsPort = localHttpsCertificate is not null ? httpsPort : 0;
    pairing.SpkiFingerprint = localHttpsCertificate is not null
        ? Nexus.Service.Security.LocalHttpsCertificate.ComputeSpkiBase64Url(localHttpsCertificate)
        : string.Empty;
}
Nexus.Service.Lifecycle.BootTimer.Mark("after pairing wire (resolves PanelPhonePairingService)");

// SPA fallback
app.MapFallbackToFile("index.html");
Nexus.Service.Lifecycle.BootTimer.Mark("after MapFallbackToFile");

if (!testHost)
{
    // Prime the REST-over-relay pipeline capture once the host has started. Driving
    // a synthetic in-process request through the chain head makes the first
    // (capture) middleware record the full downstream pipeline; this MUST run after
    // ApplicationStarted, because the endpoint-execution terminal WebApplication
    // appends is only present in the built pipeline once the host has started. No
    // network call, no boot delay (it's on the post-start callback, off the
    // critical path), and no race: the relay tunnel only dispatches once a phone
    // peers up, well after this. The prime path matches no route → 404, harmless.
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var relayHttpDispatcher = app.Services.GetRequiredService<Nexus.Service.Relay.RelayHttpDispatcher>();
        var primeCtx = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = app.Services };
        primeCtx.Request.Method = "GET";
        primeCtx.Request.Path = Nexus.Service.Relay.RelayHttpDispatcher.PrimePath;
        primeCtx.Response.Body = System.IO.Stream.Null;
        try { ((IApplicationBuilder)app).Build()(primeCtx).GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.Error.WriteLine($"[nexus-service] relay http pipeline prime skipped: {ex.Message}"); }
    });

    // Kill orphan processes from previous crashed sessions.
    Nexus.Service.Platform.FfmpegTracker.CleanupOrphans();
    Nexus.Service.Lifecycle.BootTimer.Mark("after FfmpegTracker.CleanupOrphans");
    Nexus.Service.Panel.PanelOverlayHostLauncher.CleanupOrphans();
    Nexus.Service.Lifecycle.BootTimer.Mark("after PanelOverlayHostLauncher.CleanupOrphans");
    Nexus.Service.Lighting.Rgb.OpenRgbProcessManager.CleanupOrphans();
    Nexus.Service.Lifecycle.BootTimer.Mark("after OpenRgbProcessManager.CleanupOrphans");

    // Lock the SDK-app roots so a non-admin user can't plant a widget the
    // service serves (prod / LocalSystem only; no-op for an interactive dev run).
    Nexus.Service.Widgets.AppInstallPaths.SecureUserRoots();

    // Register nexus:// protocol handler (idempotent - safe on every launch)
    Nexus.Service.Platform.ProtocolHandler.Register();
    Nexus.Service.Lifecycle.BootTimer.Mark("after ProtocolHandler.Register");
}

Console.WriteLine($"[nexus-service] listening on {url}");

app.Lifetime.ApplicationStarted.Register(() =>
    Nexus.Service.Lifecycle.BootTimer.Mark("ApplicationStarted (host start complete, Kestrel bound)"));

// GUI/tray/overlay wiring and the platform service host. The integration-test
// host skips all of it and falls through to the plain app.Run() below, which
// WebApplicationFactory intercepts at HostBuilt before Kestrel binds.
if (!testHost)
{
if (OperatingSystem.IsWindows() && !serviceMode)
    Nexus.Service.Platform.Windows.TrayBootstrap.ConfigureTray(app);
Nexus.Service.Lifecycle.BootTimer.Mark("after TrayBootstrap.ConfigureTray (if interactive)");

#if WINDOWS
if (serviceMode)
    Nexus.Service.Platform.Windows.TrayBootstrap.WireHelperPipe(app);
Nexus.Service.Lifecycle.BootTimer.Mark("after TrayBootstrap.WireHelperPipe (if service)");
#endif

Nexus.Service.Panel.OverlayHostBootstrap.Wire(app);
Nexus.Service.Lifecycle.BootTimer.Mark("after OverlayHostBootstrap.Wire");

if (OperatingSystem.IsWindows())
    Nexus.Service.Platform.Windows.TrayBootstrap.WireAppWindowAndPawnIo(app, serviceMode, suppressStartupWindow);
Nexus.Service.Lifecycle.BootTimer.Mark("after WireAppWindowAndPawnIo");

#if MACOS
return Nexus.Service.Platform.Mac.MacAppBootstrap.Run(app, servicePort);
#endif

#if WINDOWS
if (serviceMode)
{
    Nexus.Service.Lifecycle.BootTimer.Mark("calling WindowsServiceHost.Run -> app.StartAsync");
    return Nexus.Service.Lifecycle.WindowsServiceHost.Run(args, async (_, ct) =>
    {
        // Shutdown is process exit, not a graceful unwind. Start the host, wait
        // for the SCM stop signal, persist the few things the OS won't, then
        // return so WindowsServiceHost reports STOPPED and Environment.Exits.
        // The OS reclaims sockets/serial/HID/GPU/file handles; the
        // KILL_ON_JOB_CLOSE child job reaps OpenRGB/ffmpeg.
        await app.StartAsync(CancellationToken.None).ConfigureAwait(false);
        // Wake on either the SCM stop control (ct) or an in-process
        // StopApplication. /service/stop, the tray "Shut down", factory-reset,
        // and the GPU-change restart all stop via IHostApplicationLifetime, not
        // the SCM, so the loop must observe ApplicationStopping too.
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping))
        {
            try { await Task.Delay(System.Threading.Timeout.Infinite, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        FastServiceShutdown(app);
        return 0;
    });
}
#endif
}

Nexus.Service.Lifecycle.BootTimer.Mark("calling app.Run() (host start begins)");
app.Run();
return 0;

// ── Local functions ─────────────────────────────────────────────────────────

#if WINDOWS
// Do only what the OS won't do on process exit, concurrently under one hard
// cap: persist debounced settings + dirty profile, release fans (the hubs hold
// the last commanded PWM with no failsafe), reset any connected Stream Deck
// (it holds its last-pushed frame with no failsafe either), kill the external
// driver tools (plain Process.Start children, so no kill-job holds them), and
// reap the cross-session UI the kill-job can't hold (overlay host + tray
// helper). A wedged hub, deck, or disconnected helper can't push the exit past
// the cap; everything else - the sockets, serial, remaining HID, GPU, OpenRGB -
// dies with the process.
static void FastServiceShutdown(WebApplication app)
{
    var sp = app.Services;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var done = Task.WaitAll(new[]
    {
        Task.Run(() =>
        {
            try { sp.GetService<IConfigStore>()?.FlushNow(); } catch { }
            try { sp.GetService<ProfileManager>()?.SaveActiveProfile(); } catch { }
            try { sp.GetService<Nexus.Service.Cloud.CloudProfileSyncService>()?.FlushPendingSyncBlocking(TimeSpan.FromMilliseconds(1000)); } catch { }
        }),
        Task.Run(() => { try { sp.GetService<IFanControlProvider>()?.ReleaseAll(); } catch { } }),
        Task.Run(() => { try { sp.GetService<Nexus.Service.Peripherals.StreamDeck.StreamDeckConnectionWorker>()?.ResetConnectedSurfacesForShutdown(); } catch { } }),
        Task.Run(() => { try { sp.GetService<Nexus.Service.Common.ExternalTools.ExternalToolManager>()?.TerminateAll(); } catch { } }),
        Task.Run(() => FastWindowsUiTeardown(sp)),
    }, millisecondsTimeout: 1500);
    Console.Error.WriteLine($"[shutdown] fast teardown {(done ? "complete" : "TIMED OUT")} in {sw.ElapsedMilliseconds}ms");
}

// The overlay host and tray helper run in the user session (spawned cross-session
// via schtasks), so the KILL_ON_JOB_CLOSE job can't hold them. Reap the overlay
// directly and ask the helper to exit, briefly.
static void FastWindowsUiTeardown(IServiceProvider sp)
{
    // Stop() latches the no-respawn flag, but it reaps the overlay by a PID file
    // that the flaky cross-session schtasks spawn doesn't always write in time,
    // so also reap any nexus-overlay by image name.
    try { sp.GetService<Nexus.Service.Panel.PanelOverlayHostLauncher>()?.Stop(); } catch { }
    try
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("nexus-overlay"))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
        }
    }
    catch { }
    try
    {
        var registry = sp.GetService<Nexus.Service.Helper.HelperRegistry>();
        if (registry is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            Nexus.Service.Helper.Domains.LifecycleCommands
                .SendShutdownAsync(registry, cts.Token).GetAwaiter().GetResult();
        }
    }
    catch { }
}
#endif

static void OpenExistingServiceWindow(int servicePort)
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            Nexus.Service.Platform.Windows.TrayIcon.OpenLocalWindow(servicePort);
            return;
        }

        OpenInAppMode(ServiceLaunchIntent.LocalDashboardUrl(servicePort));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[nexus-service] second launch window handoff failed: {ex.Message}");
    }
}

static void OpenInAppMode(string url)
{
    // Find Chrome or Edge and launch with --app=URL for a chromeless window
    // (no address bar, no tabs) - similar to msedge.exe --app on Windows.
    // Fall back to the default browser if neither is installed.
    try
    {
        string? browser = null;
        string[] candidates = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? new[]
            {
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                "/Applications/Chromium.app/Contents/MacOS/Chromium",
                "/Applications/Arc.app/Contents/MacOS/Arc",
                "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
            }
            : Array.Empty<string>();

        foreach (var path in candidates)
        {
            if (System.IO.File.Exists(path))
            { browser = path; break; }
        }

        if (browser is not null)
        {
            var dataDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-app");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = browser,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add($"--app={url}");
            psi.ArgumentList.Add($"--user-data-dir={dataDir}");
            System.Diagnostics.Process.Start(psi);
            return;
        }

        // Fallback: open in default browser
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[mac-status-bar] open {url} failed: {ex.Message}");
    }
}

// Exposes the implicit top-level Program type to the test assembly so
// WebApplicationFactory<Program> can host the app in-process. No members - the
// entry point is the top-level statements above. See Integration/NexusAppFactory.
public partial class Program { }
