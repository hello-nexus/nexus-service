using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Platform.Linux.Render;

namespace Nexus.Service.Platform.Linux;

public sealed class LinuxStreamRenderHost : BackgroundService
{
    private const int ReconcileMs = 1000;
    private const int BrowserIdleCloseMs = 60_000;
    private const int SessionRetryMs = 5_000;
    private const int MaxRapidBrowserFailures = 5;
    private const int MissingBrowserRetryMs = 30_000;
    private const int BrowserBackoffStepMs = 2_000;
    private const int BrowserBackoffMaxMs = 60_000;
    private const int RapidExitMs = 60_000;
    private const int SessionStartTimeoutMs = 20_000;
    private const string Tag = "[panel-render]";

    private static int _servicePort = ServiceLaunchIntent.DefaultServicePort;
    public static void Configure(int servicePort) => _servicePort = servicePort;

    private readonly StreamedPanelCoordinator _coordinator;
    private readonly TokenService _tokens;
    private readonly Dictionary<string, Running> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _retryAfter = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loggedOnce = new(StringComparer.Ordinal);
    private HeadlessBrowser? _browser;
    private long _idleSince = -1;
    private int _rapidBrowserFailures;
    private long _browserStartedAt;
    private long _browserRetryAfter;

    private sealed record Running(PanelRenderSession Render, RawFrameIngestClient Ingest);

    public LinuxStreamRenderHost(StreamedPanelCoordinator coordinator, TokenService tokens)
    {
        _coordinator = coordinator;
        _tokens = tokens;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ServiceLog.Warn($"{Tag} reconcile failed: {ex.GetType().Name}: {ex.Message}");
                }
                await Task.Delay(ReconcileMs, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            StopAll();
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var wanted = new Dictionary<string, StreamAssignmentDto>(StringComparer.Ordinal);
        foreach (var a in _coordinator.GetAssignments().Assignments)
        {
            if (string.IsNullOrEmpty(a.SessionId) || string.IsNullOrEmpty(a.PanelDeviceId))
            {
                continue;
            }
            if (!string.Equals(a.Codec, "rawBgra", StringComparison.OrdinalIgnoreCase))
            {
                LogOnce($"codec:{a.PanelDeviceId}", $"{Tag} {a.PanelDeviceId} asks for {a.Codec}; only raw frames are rendered on Linux");
                continue;
            }
            wanted[a.SessionId] = a;
        }

        foreach (var (id, running) in new List<KeyValuePair<string, Running>>(_sessions))
        {
            var gone = !wanted.ContainsKey(id);
            if (gone || running.Render.Faulted || running.Ingest.IsFaulted)
            {
                ServiceLog.Info($"{Tag} session {id} {(gone ? "ended" : "faulted; restarting")} after {running.Render.FramesDelivered} frame(s)");
                StopSession(id);
                if (!gone)
                {
                    _retryAfter[id] = Environment.TickCount64 + SessionRetryMs;
                }
            }
        }

        if (wanted.Count == 0)
        {
            if (_browser is not null)
            {
                if (_idleSince < 0)
                {
                    _idleSince = Environment.TickCount64;
                }
                else if (Environment.TickCount64 - _idleSince > BrowserIdleCloseMs)
                {
                    ServiceLog.Info($"{Tag} no panels to render; closing the headless browser");
                    CloseBrowser();
                }
            }
            return;
        }
        _idleSince = -1;

        if (_browser is { HasExited: true })
        {
            ServiceLog.Warn($"{Tag} headless browser exited; restarting");
            var lived = Environment.TickCount64 - _browserStartedAt;
            StopAll();
            _rapidBrowserFailures = lived < RapidExitMs ? _rapidBrowserFailures + 1 : 1;
            _browserRetryAfter = Environment.TickCount64 + BackoffMs();
        }
        if (_browser is null && !await EnsureBrowserAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        foreach (var (id, a) in wanted)
        {
            if (_sessions.ContainsKey(id)
                || (_retryAfter.TryGetValue(id, out var after) && Environment.TickCount64 < after))
            {
                continue;
            }
            await StartSessionAsync(a, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> EnsureBrowserAsync(CancellationToken ct)
    {
        if (Environment.TickCount64 < _browserRetryAfter)
        {
            return false;
        }
        if (_rapidBrowserFailures > MaxRapidBrowserFailures)
        {
            LogOnce("capped", $"{Tag} headless browser keeps failing; panel rendering stopped until Nexus restarts");
            return false;
        }
        if (LinuxSession.IsRootDaemon && LinuxSession.SessionUid is null)
        {
            LogOnce("no-session", $"{Tag} panels are waiting for a desktop login to render");
            return false;
        }
        if (RunsAsRootWithoutUserSwitch())
        {
            LogOnce("root", $"{Tag} running as root with no desktop user to switch to; refusing to start a browser as root");
            return false;
        }
        var profileDir = ProfileDir();
        if (profileDir is null)
        {
            LogOnce("no-home", $"{Tag} no home directory for the browser profile; panels cannot render");
            return false;
        }
        var browser = LinuxBrowsers.FindChromium();
        if (browser is null)
        {
            LogOnce("no-browser", $"{Tag} no Chromium-family browser found (Brave, Chrome, Chromium, Edge, Vivaldi); panels cannot render");
            _browserRetryAfter = Environment.TickCount64 + MissingBrowserRetryMs;
            return false;
        }
        if (LinuxBrowsers.FlatpakAppId(browser) is not null)
        {
            LogOnce("flatpak", $"{Tag} {browser} is a Flatpak, which cannot hand the browser its control pipe; install a native package");
            _browserRetryAfter = Environment.TickCount64 + MissingBrowserRetryMs;
            return false;
        }
        try
        {
            _browser = await HeadlessBrowser.LaunchAsync(browser, profileDir, LinuxSession.WrapSpawnAsSessionUser,
                line => ServiceLog.Info($"{Tag} {line}"), ct).ConfigureAwait(false);
            _browserStartedAt = Environment.TickCount64;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _rapidBrowserFailures++;
            _browserRetryAfter = Environment.TickCount64 + BackoffMs();
            ServiceLog.Warn($"{Tag} could not start {browser} headless: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private async Task StartSessionAsync(StreamAssignmentDto a, CancellationToken ct)
    {
        var origin = $"http://localhost:{_servicePort}";
        var url = $"{origin}/panel/{Uri.EscapeDataString(a.PanelDeviceId)}?token={Uri.EscapeDataString(_tokens.Token)}"
            + $"&streamFps={Math.Clamp(a.Fps, 1, PanelRenderSession.MaxFps)}";
        var render = new PanelRenderSession(_browser!.Cdp, a.SessionId, url, a.CssWidth, a.CssHeight, a.Dpr, a.Fps);
        var ingest = new RawFrameIngestClient(origin, a.SessionId, _tokens.Token);
        render.FrameSink = ingest.SendFrame;
        try
        {
            using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startTimeout.CancelAfter(SessionStartTimeoutMs);
            await render.StartAsync(startTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ServiceLog.Warn($"{Tag} session {a.SessionId} for {a.PanelDeviceId} failed to start: {ex.GetType().Name}: {ex.Message}");
            render.Dispose();
            ingest.Dispose();
            _retryAfter[a.SessionId] = Environment.TickCount64 + SessionRetryMs;
            return;
        }
        _sessions[a.SessionId] = new Running(render, ingest);
        _retryAfter.Remove(a.SessionId);
        ServiceLog.Info($"{Tag} rendering {a.PanelDeviceId} session={a.SessionId} "
            + $"{render.PixelWidth}x{render.PixelHeight}@{render.Fps}");
    }

    private void StopSession(string id)
    {
        if (_sessions.Remove(id, out var running))
        {
            running.Render.Dispose();
            running.Ingest.Dispose();
        }
    }

    private void StopAll()
    {
        foreach (var id in new List<string>(_sessions.Keys))
        {
            StopSession(id);
        }
        CloseBrowser();
    }

    private void CloseBrowser()
    {
        _browser?.Dispose();
        _browser = null;
        _idleSince = -1;
    }

    private void LogOnce(string key, string message)
    {
        if (_loggedOnce.Add(key))
        {
            ServiceLog.Info(message);
        }
    }

    private int BackoffMs() => Math.Min(BrowserBackoffMaxMs, BrowserBackoffStepMs * _rapidBrowserFailures);

    private static bool RunsAsRootWithoutUserSwitch()
    {
        try
        {
            return string.Equals(Environment.UserName, "root", StringComparison.Ordinal)
                && LinuxSession.SessionUid is null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // Not hidden: a snap-confined browser may only use non-hidden paths under HOME.
    // No fallback when HOME is unresolvable, since a shared temp path can be pre-created by any local user.
    internal static string? ProfileDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, "nexus-panel-render");
    }
}
