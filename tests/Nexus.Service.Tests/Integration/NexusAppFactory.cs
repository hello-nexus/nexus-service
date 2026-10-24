using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// In-process integration host. Boots the real <c>Program.cs</c> request
/// pipeline - middleware order, CORS, SecurityHeaders, PathAuth, every mapped
/// route, the source-generated JSON, and the WebSocket hubs - against an
/// in-memory TestServer. No Kestrel, no hardware, and no machine-mutating boot
/// side effects (single-instance mutex, HTTPS cert provisioning, GPU/profile
/// init, orphan-process cleanup, OS protocol-handler registration, GUI/service
/// host) - all gated by <c>NEXUS_TEST_HOST</c> in Program.cs.
///
/// Background <see cref="IHostedService"/>s (mDNS advertiser, curve engine,
/// device/serial/USB watchers, monitoring broadcaster) are stripped so request
/// handling is deterministic; a test that needs one re-adds it through
/// <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>.
/// </summary>
public class NexusAppFactory : WebApplicationFactory<Program>
{
    private readonly string _configDir;

    /// <summary>Per-factory settings.json path (temp). Survives for the host's lifetime.</summary>
    public string SettingsPath { get; }

    /// <summary>False for a host that must start with the features onboarding step undecided, which reads Lighting and Cooling as off.</summary>
    protected virtual bool FeaturesDecided => true;

    public NexusAppFactory()
    {
        // Must be set before the base class lazily starts the host (on first
        // CreateClient / Services access) so Program.cs's pre-build gates -
        // single-instance mutex and cert provisioning - observe it.
        Environment.SetEnvironmentVariable("NEXUS_TEST_HOST", "1");

        _configDir = Path.Combine(Path.GetTempPath(), "nexus-itest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
        SettingsPath = Path.Combine(_configDir, "settings.json");
    }

    /// <summary>
    /// Drops every persisted setting back to first-run defaults, so ONE host can
    /// serve a whole test class rather than the class booting a fresh one per
    /// test method. Call it from the constructor, which xUnit runs per test.
    ///
    /// Only the settings FILE is reset. Anything a singleton already cached in
    /// memory survives - including <see cref="Nexus.Service.Auth.TokenService"/>'s
    /// token, which is what keeps a shared host's bearer valid across the reset.
    /// A class whose tests depend on a singleton's in-memory state being fresh
    /// must keep booting its own host, or reset that singleton itself - the
    /// deck route classes do the latter for their worker and image cache.
    /// </summary>
    public void ResetSettings()
    {
        var store = Services.GetRequiredService<IConfigStore>();
        // Flush first: Update() queues an async write, and one landing after the
        // delete would restore the previous test's document.
        store.FlushNow();
        try { File.Delete(SettingsPath); } catch { /* already gone */ }
        try { File.Delete(Nexus.Service.Persistence.AtomicJsonFile.BackupPathFor(SettingsPath)); } catch { /* already gone */ }
        store.Reload();
        if (FeaturesDecided)
        {
            store.Update(s => s.FeaturesOnboardingCompleted = true);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            // A CreateClient() request models the LOCAL desktop client, which in
            // production always reaches the service over loopback. TestServer's
            // HttpClient path leaves Connection.RemoteIpAddress null, so default
            // it to loopback - otherwise the loopback-gated auth (desktop token,
            // dashboard shell) would 401/404 every CreateClient test. Tests that
            // exercise a LAN caller set an explicit non-loopback RemoteIpAddress
            // via Server.SendAsync (which runs before the pipeline), so the ??=
            // below leaves those untouched.
            services.AddSingleton<IStartupFilter, LoopbackRemoteDefaultStartupFilter>();

            // The request pipeline under test does not depend on any hosted
            // service being started, and they touch hardware/network and add
            // timing nondeterminism - so strip them all.
            services.RemoveAll<IHostedService>();

            // The IP location lookup would otherwise reach the network from a route.
            services.RemoveAll<Nexus.Service.Platform.Weather.IWeatherProvider>();
            services.AddSingleton<FakeWeatherProvider>();
            services.AddSingleton<Nexus.Service.Platform.Weather.IWeatherProvider>(sp => sp.GetRequiredService<FakeWeatherProvider>());

            // Isolate config (the auth token + all settings) to a per-factory
            // temp file so integration tests never read or mutate the
            // developer's real settings.json.
            services.RemoveAll<IConfigStore>();
            var configStore = new JsonConfigStore(SettingsPath);
            if (FeaturesDecided)
            {
                configStore.Update(s => s.FeaturesOnboardingCompleted = true);
            }
            services.AddSingleton<IConfigStore>(configStore);

            // IMcpAuditSink resolves McpServerHost -> McpToolRegistry ->
            // IAiEventLog even with hosted services stripped, since /ai/*
            // route handlers take McpServerHost as a DI param. Isolate its
            // data directory to the same per-factory temp dir so an AI-route
            // test never touches the developer's real machine data directory.
            services.RemoveAll<Nexus.Service.Mcp.History.IAiEventLog>();
            services.AddSingleton<Nexus.Service.Mcp.History.IAiEventLog>(
                new Nexus.Service.Mcp.History.Binary.BinaryAiEventLog(Path.Combine(_configDir, "ai-history")));

            // GetGameSessionsTool pulls the fps session store through the same
            // chain; its default location is the real machine database dir,
            // whose games.dict a running service holds open.
            services.RemoveAll<Nexus.Service.Games.BinaryFpsSessionStore>();
            services.AddSingleton(_ => new Nexus.Service.Games.BinaryFpsSessionStore(Path.Combine(_configDir, "fps")));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_configDir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// Front-of-pipeline middleware that defaults a null RemoteIpAddress to
    /// loopback (see the note at the registration site). Explicit non-loopback
    /// addresses set via <c>Server.SendAsync</c> are preserved.
    ///
    /// Consequence for test authors: a <c>CreateClient()</c> request is ALWAYS
    /// treated as loopback, so it cannot exercise a LAN-caller gate. A test that
    /// must act as a LAN caller MUST set a non-loopback RemoteIpAddress via
    /// <c>Server.SendAsync</c>, or it will false-pass the loopback-gated auth.
    /// </summary>
    private sealed class LoopbackRemoteDefaultStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (ctx, n) =>
                {
                    ctx.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                    await n(ctx);
                });
                next(app);
            };
    }
}
