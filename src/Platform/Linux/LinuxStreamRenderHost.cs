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
    private const int GpuDemoteAfterFailures = 2;
    private const int MaxSessionRetryMs = 60_000;
    private const int HealthyAfterMs = 30_000;
    private const int GpuTierBudgetMs = 10_000;
    private const int GpuReprobeMs = 10 * 60_000;
    private const string Tag = "[panel-render]";

    private static int _servicePort = ServiceLaunchIntent.DefaultServicePort;
    public static void Configure(int servicePort) => _servicePort = servicePort;

    // Set only when a probe proved a GPU tier; reused by every browser relaunch until it is demoted.
    private static GpuTier? s_tier;
    // Software is never cached as an answer; it only pauses probing, so a transient failure heals.
    private static long s_noProbeUntil;

    private readonly StreamedPanelCoordinator _coordinator;
    private readonly TokenService _tokens;
    private readonly Dictionary<string, Running> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _retryAfter = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _faultStreak = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loggedOnce = new(StringComparer.Ordinal);
    private HeadlessBrowser? _browser;
    private long _idleSince = -1;
    private int _rapidBrowserFailures;
    private long _browserStartedAt;
    private long _browserRetryAfter;

    private sealed record Running(PanelRenderSession Render, RawFrameIngestClient Ingest, long StartedAt);

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
            if (running.Render.FramesDelivered > 0)
            {
                _rapidBrowserFailures = 0;
                // One frame then a crash must keep backing off, so the streak resets only after a healthy stretch.
                if (Environment.TickCount64 - running.StartedAt > HealthyAfterMs)
                {
                    _faultStreak.Remove(id);
                }
            }
            var gone = !wanted.ContainsKey(id);
            if (gone || running.Render.Faulted || running.Ingest.IsFaulted)
            {
                ServiceLog.Info($"{Tag} session {id} {(gone ? "ended" : "faulted; restarting")} after {running.Render.FramesDelivered} frame(s)");
                StopSession(id);
                if (!gone)
                {
                    ScheduleRetry(id);
                }
            }
        }

        foreach (var id in new List<string>(_retryAfter.Keys))
        {
            if (!wanted.ContainsKey(id))
            {
                _retryAfter.Remove(id);
            }
        }
        foreach (var id in new List<string>(_faultStreak.Keys))
        {
            if (!wanted.ContainsKey(id) && !_sessions.ContainsKey(id))
            {
                _faultStreak.Remove(id);
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

        // A GPU process that died under a live browser: the next launch uses the CPU path.
        if (_browser is { GpuProcessFailed: true, Tier: not GpuTier.Software } && s_tier is { } live)
        {
            ServiceLog.Warn($"{Tag} browser reported a GPU process failure on {live}; the next launch uses software rendering");
            DemoteGpu();
        }
        if (_browser is { HasExited: true })
        {
            ServiceLog.Warn($"{Tag} headless browser exited; restarting");
            var lived = Environment.TickCount64 - _browserStartedAt;
            StopAll();
            _rapidBrowserFailures = lived < RapidExitMs ? _rapidBrowserFailures + 1 : 1;
            _browserRetryAfter = Environment.TickCount64 + BackoffMs();
            DemoteGpuIfUnstable();
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
        var profileDir = ProfileDir(LinuxSession.SessionUid, LinuxSession.SessionHome,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
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
            _browser = s_tier is { } tier
                ? await HeadlessBrowser.LaunchAsync(browser, profileDir, LinuxSession.WrapSpawnAsSessionUser,
                    line => ServiceLog.Info($"{Tag} {line}"), ct, tier).ConfigureAwait(false)
                : !ProbeDue(Environment.TickCount64, s_noProbeUntil)
                    ? await HeadlessBrowser.LaunchAsync(browser, profileDir, LinuxSession.WrapSpawnAsSessionUser,
                        line => ServiceLog.Info($"{Tag} {line}"), ct, GpuTier.Software).ConfigureAwait(false)
                    : await LaunchProbingGpuAsync(browser, profileDir, ct).ConfigureAwait(false);
            _browserStartedAt = Environment.TickCount64;
            ServiceLog.Info($"{Tag} headless browser rendering tier: {_browser.Tier}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _rapidBrowserFailures++;
            _browserRetryAfter = Environment.TickCount64 + BackoffMs();
            DemoteGpuIfUnstable();
            ServiceLog.Warn($"{Tag} could not start {browser} headless: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // Try Vulkan, then EGL, each only trusted once the page's WebGL renderer is a real GPU, a captured
    // frame has the right pixels and stderr shows no GPU process failure; else the CPU path.
    private static async Task<HeadlessBrowser> LaunchProbingGpuAsync(string browser, string profileDir, CancellationToken ct)
    {
        HeadlessBrowser? kept = null;
        var tier = await GpuTiers.SelectAsync(async candidateTier =>
        {
            // One budget covers launch, renderer probe and capture check, so a wedged driver costs one tier's budget.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(GpuTierBudgetMs);
            HeadlessBrowser? candidate = null;
            try
            {
                candidate = await HeadlessBrowser.LaunchAsync(browser, profileDir, LinuxSession.WrapSpawnAsSessionUser,
                    line => ServiceLog.Info($"{Tag} {line}"), budget.Token, candidateTier).ConfigureAwait(false);
                // A wedged driver must not hold the loop: past the budget the tree is killed at once and reaped later.
                var live = candidate;
                using var killOnBudget = budget.Token.Register(live.Kill);
                var renderer = await candidate.ProbeRendererAsync(budget.Token).ConfigureAwait(false);
                var capturedOk = !GpuTiers.IsSoftwareRenderer(renderer)
                    && await CapturesCorrectlyAsync(candidate, budget.Token).ConfigureAwait(false);
                // Chromium prints the GPU-process markers even when it recovers on another backend in the same
                // browser, so a tier that logged one is rejected here rather than demoted after acceptance.
                // Unhooked before acceptance so the budget can never kill a browser that was kept; a budget
                // that already fired rejects the tier.
                killOnBudget.Dispose();
                if (!capturedOk || candidate.GpuProcessFailed || budget.IsCancellationRequested)
                {
                    await candidate.AbandonAsync().ConfigureAwait(false);
                    return false;
                }
                candidate.ClearGpuFailure();
                ServiceLog.Info($"{Tag} {candidateTier} renderer: {renderer}");
                kept = candidate;
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                ServiceLog.Info($"{Tag} gpu probe: {candidateTier} timed out");
                await AbandonAsync(candidate).ConfigureAwait(false);
                return false;
            }
            catch
            {
                await AbandonAsync(candidate).ConfigureAwait(false);
                throw;
            }
        }, line => ServiceLog.Info($"{Tag} gpu probe: {line}")).ConfigureAwait(false);

        if (tier == GpuTier.Software)
        {
            s_noProbeUntil = Environment.TickCount64 + GpuReprobeMs;
        }
        else
        {
            s_tier = tier;
        }
        return kept ?? await HeadlessBrowser.LaunchAsync(browser, profileDir, LinuxSession.WrapSpawnAsSessionUser,
            line => ServiceLog.Info($"{Tag} {line}"), ct, GpuTier.Software).ConfigureAwait(false);
    }

    private static Task AbandonAsync(HeadlessBrowser? browser) => browser?.AbandonAsync() ?? Task.CompletedTask;

    // Doubles from the base retry up to a ceiling; a session that stays healthy for a while starts over.
    internal static int SessionRetryDelayMs(int faultStreak) =>
        (int)Math.Min(MaxSessionRetryMs, (long)SessionRetryMs << Math.Clamp(faultStreak - 1, 0, 8));

    internal static bool ProbeDue(long now, long noProbeUntil) => now >= noProbeUntil;

    // A flat-color page must come back green; a GPU path that captures black or garbage fails here.
    internal static async Task<bool> CapturesCorrectlyAsync(HeadlessBrowser browser, CancellationToken ct)
    {
        const int size = 64;
        var frame = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var check = new PanelRenderSession(browser.Cdp, "gpu-check",
                "data:text/html,<html><body style=\"margin:0;background:%2300ff00\"></body></html>", size, size, 1.0, 10);
            check.FrameSink = f => frame.TrySetResult(f);
            await check.StartAsync(ct).ConfigureAwait(false);
            var bgra = await frame.Task.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            return IsGreen(bgra, size);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceLog.Info($"{Tag} gpu probe: capture check failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    internal static bool IsGreen(byte[] bgra, int size)
    {
        var i = (size / 2 * size + size / 2) * 4;
        return bgra.Length >= i + 4 && bgra[i] < 60 && bgra[i + 1] > 200 && bgra[i + 2] < 60;
    }

    // A GPU tier that keeps killing the browser is dropped for the CPU path instead of exhausting the retry cap.
    private void DemoteGpuIfUnstable()
    {
        if (s_tier is { } tier && _rapidBrowserFailures >= GpuDemoteAfterFailures)
        {
            ServiceLog.Warn($"{Tag} browser unstable on {tier}; falling back to software rendering");
            DemoteGpu();
        }
    }

    private static void DemoteGpu()
    {
        s_tier = null;
        s_noProbeUntil = Environment.TickCount64 + GpuReprobeMs;
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
            ScheduleRetry(a.SessionId);
            return;
        }
        _sessions[a.SessionId] = new Running(render, ingest, Environment.TickCount64);
        _retryAfter.Remove(a.SessionId);
        ServiceLog.Info($"{Tag} rendering {a.PanelDeviceId} session={a.SessionId} "
            + $"{render.PixelWidth}x{render.PixelHeight}@{render.Fps}");
    }

    private void ScheduleRetry(string id)
    {
        var streak = _faultStreak.GetValueOrDefault(id) + 1;
        _faultStreak[id] = streak;
        _retryAfter[id] = Environment.TickCount64 + SessionRetryDelayMs(streak);
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
    // No fallback when no home is known, since a shared temp path can be pre-created by any local user.
    // A root daemon that adopted a session runs the browser as that user, so only the adopted
    // home is usable; the process HOME may still be root's.
    internal static string? ProfileDir(uint? sessionUid, string? sessionHome, string? userProfile)
    {
        var home = sessionUid is null ? userProfile : sessionHome;
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, "nexus-panel-render");
    }
}
