// Nexus local service — Minimal API host for Native AOT.
//
// Default bind: http://localhost:9400.
// Override with the first command-line arg:
//     nexus-service http://localhost:9400

using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
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
// thing — its stdout (fd 1) carries the raw RGB frame stream, so nothing else
// (not even a boot-timer line) may write to stdout before it takes over.
#if LINUX
if (args.Length > 0
    && args[0] == Nexus.Service.Lighting.Capture.LinuxScreenCastHelper.Verb)
    return Nexus.Service.Lighting.Capture.LinuxScreenCastHelper.Run(args);
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

// Integration-test host marker. The WebApplicationFactory fixture
// (tests/.../Integration/NexusAppFactory) sets NEXUS_TEST_HOST=1 before the
// entry point runs so the in-process test host skips machine-mutating boot
// side effects — single-instance mutex, HTTPS cert provisioning, GPU/profile
// init, orphan-process cleanup, OS protocol-handler registration — and the
// platform GUI/service host, falling through to a plain app.Run() that the
// factory intercepts. Production (env unset) takes the original path unchanged.
var testHost = Environment.GetEnvironmentVariable("NEXUS_TEST_HOST") == "1";

// Root system daemon (full hardware access) adopts the active user's session
// env — D-Bus, runtime dir, config home, display — so the tray, MPRIS media,
// volume, and dashboard launcher keep working. No-op for a --user install.
// Done FIRST so HOME/XDG_* are correct before anything (e.g. the service log)
// resolves a path from them.
#if LINUX
Nexus.Service.Platform.Linux.LinuxSession.AdoptActiveSessionEnv();
#endif

// Capture stdout / stderr to a rotating service.log file before anything else
// writes to the console. Doesn't change Console behaviour - just tees output.
Nexus.Service.Platform.ServiceLog.Initialize();
Nexus.Service.Lifecycle.BootTimer.Mark("after ServiceLog.Initialize");

var url = ServiceLaunchIntent.ResolveServiceUrl(args);
var servicePort = ServiceLaunchIntent.ResolveServicePort(url);
Nexus.Service.Lifecycle.BootTimer.Mark("after URL resolve");

// Single-instance guard — if another nexus-service is already running,
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
// installer payload, not a merge — it fully shadows the bundled wwwroot
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
// the only ILogger output is framework noise. Drop it to Warning — in particular
// Microsoft.AspNetCore.Hosting.Diagnostics' per-request "Request starting/finished"
// Information lines, which otherwise flood service.log on every internal API call.
// Keep Microsoft.Hosting.Lifetime at Information for the useful "Now listening" /
// "Application started/stopping" boot markers.
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
// Kestrel + form upload body limits. Default Kestrel cap is 30 MB which drops
// larger multipart uploads before /media/import sees them (the browser then
// reports "could not reach the service"). Match MediaImporter.MaxFileSize so
// the route-level check is the only place we reject oversized uploads.
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = Nexus.Service.Media.MediaImporter.MaxFileSize;
    k.ListenAnyIP(servicePort);
    if (localHttpsCertificate is not null)
    {
        k.ListenAnyIP(httpsPort, o => o.UseHttps(localHttpsCertificate));
    }
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = Nexus.Service.Media.MediaImporter.MaxFileSize;
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
builder.Services.AddNexusWidgets();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusWidgets");
builder.Services.AddNexusPanel(servicePort);
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusPanel");
builder.Services.AddNexusLinuxDBus();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusLinuxDBus");
builder.Services.AddNexusHelper();
Nexus.Service.Lifecycle.BootTimer.Mark("DI: AddNexusHelper");

// mDNS / Bonjour advertiser for the iOS companion app's Wi-Fi discovery.
// Reads HttpsPort + SpkiFingerprint + MachineName off PanelPhonePairingService
// after the Pairing config block below has populated them.
builder.Services.AddHostedService<Nexus.Service.Discovery.MdnsAdvertiser>();
Nexus.Service.Lifecycle.BootTimer.Mark("after MdnsAdvertiser register");

// ── Build ──
var app = builder.Build();
Nexus.Service.Lifecycle.BootTimer.Mark("after builder.Build()");

// Connect the session D-Bus once here — single-threaded, BEFORE hosted services
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
    Nexus.Service.Lifecycle.AppBootstrap.EagerInitGpu(app);
    Nexus.Service.Lifecycle.BootTimer.Mark("after EagerInitGpu");
    Nexus.Service.Lifecycle.AppBootstrap.InitializeProfiles(app);
    Nexus.Service.Lifecycle.BootTimer.Mark("after InitializeProfiles");
    Nexus.Service.Lifecycle.AppBootstrap.WireBeatsAndPresence(app);
    Nexus.Service.Lifecycle.BootTimer.Mark("after WireBeatsAndPresence");
    app.Services.GetRequiredService<Nexus.Service.Telemetry.ITelemetry>()
        .Capture(Nexus.Service.Telemetry.TelemetryEvents.AppStarted);
}

// Middleware pipeline
//
// REST-over-relay capture middleware — MUST be first. The off-LAN panel tunnels
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
// origins — this is what lets the hosted web app detect and drive a local Nexus
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
app.MapCoolingEndpoints();
app.MapBenchmarkEndpoints();
app.MapLightingEndpoints();
app.MapWebcamEndpoints();
app.MapObsEndpoints();
app.MapSteamEndpoints();
app.MapDiscordEndpoints();
app.MapDevicesEndpoints();
app.MapPeripheralEndpoints();
app.MapKeebEndpoints();
app.MapDisplayEndpoints();
app.MapActivityEndpoints();
app.MapLifecycleEndpoints();
app.MapDiagnosticsEndpoints();
app.MapMediaLibraryEndpoints();
app.MapGalleryEndpoints();
app.MapTransferEndpoints();
app.MapProfileEndpoints();
app.MapPanelEndpoints();
app.MapOverlayEndpoints();
app.MapAppEndpoints();
app.MapStoreEndpoints();
app.MapConflictEndpoints();
app.MapWebSocketEndpoints();
Nexus.Service.Lifecycle.BootTimer.Mark("after route mapping");

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

    // Register nexus:// protocol handler (idempotent — safe on every launch)
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
    Nexus.Service.Lifecycle.BootTimer.Mark("calling WindowsServiceHost.Run -> app.RunAsync");
    return Nexus.Service.Lifecycle.WindowsServiceHost.Run(args, async (_, ct) =>
    {
        await app.RunAsync(ct).ConfigureAwait(false);
        return 0;
    });
}
#endif
}

Nexus.Service.Lifecycle.BootTimer.Mark("calling app.Run() (host start begins)");
app.Run();
return 0;

// ── Local functions ─────────────────────────────────────────────────────────

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
    // (no address bar, no tabs) — similar to msedge.exe --app on Windows.
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
// WebApplicationFactory<Program> can host the app in-process. No members — the
// entry point is the top-level statements above. See Integration/NexusAppFactory.
public partial class Program { }
