#if WINDOWS
using System;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using System.Text.Json;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Lighting.Capture;

/// <summary>
/// Service-side <see cref="IScreenFrameSource"/> that delegates capture to
/// the user-session helper. <see cref="Start"/> issues a
/// <c>screenMirror.start</c> RPC; the helper pushes canvas-sized RGB24
/// frames back via <c>screenMirror.frame</c> envelopes. We subscribe to
/// <see cref="HelperRegistry.InboundEnvelope"/> and cache the latest frame
/// so <see cref="ScreenMirrorEffect"/> can pull without blocking on the
/// pipe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperScreenFrameSource : IScreenFrameSource, IDisposable
{
    private readonly HelperRegistry _registry;
    private readonly object _lock = new();
    private byte[]? _latest;
    private int _latestW, _latestH;
    private int _expectedW, _expectedH;
    private bool _subscribed;
    private readonly Func<string, int, int, Task> _sendStart;
    private readonly Func<Task> _sendStop;
    private bool _active;
    private bool _connectedHooked;
    private string _activeMonitorId = "";
    private int _activeW, _activeH;
    // Bumped by every Start and Stop; a queued start from an older generation is stale.
    private int _generation;
    // start/stop sends run one at a time in call order, so a late start never lands after a Stop.
    private Task _sends = Task.CompletedTask;

    public HelperScreenFrameSource(HelperRegistry registry)
        : this(registry,
            (monitorId, w, h) => ScreenMirrorCommands.StartAsync(registry, monitorId, w, h),
            () => ScreenMirrorCommands.StopAsync(registry))
    {
    }

    internal HelperScreenFrameSource(HelperRegistry registry, Func<string, int, int, Task> sendStart, Func<Task> sendStop)
    {
        _registry = registry;
        _sendStart = sendStart;
        _sendStop = sendStop;
    }

    internal Task PendingSends
    {
        get { lock (_lock) return _sends; }
    }

    public void Start(string monitorId, int width, int height)
    {
        EnsureSubscribed();
        int generation;
        lock (_lock)
        {
            generation = ++_generation;
            _expectedW = width;
            _expectedH = height;
            // Drop any frame from a previous capture session - it's the wrong
            // size and would render garbage if blitted into the new canvas.
            _latest = null;
            _latestW = 0;
            _latestH = 0;
            _active = true;
            _activeMonitorId = monitorId;
            _activeW = width;
            _activeH = height;
            if (!_connectedHooked)
            {
                _connectedHooked = true;
                _registry.Connected += OnHelperConnected;
            }
        }
        // Fire-and-forget: the pipe write runs through the helper's
        // SemaphoreSlim and could stall briefly if other domains are mid-
        // write. We're called from the engine render thread (via
        // ScreenMirrorEffect.EnsureStarted), so blocking here would freeze
        // the lighting loop. Frames start flowing once the helper acks
        // internally; until then TryAcquireFrame returns null and the
        // effect renders dark grey, which is the same behavior as
        // a stalled DXGI/ffmpeg startup.
        Enqueue(() => SendStartIfCurrentAsync(generation, monitorId, width, height, resend: false));
    }

    public void Stop()
    {
        // Same fire-and-forget rationale as Start. Stop is invoked from
        // LightingEngine.SetEffect under its swap lock when the old effect
        // is disposed - blocking here can stall the next effect's
        // construction.
        lock (_lock)
        {
            _active = false;
            _generation++;
            if (_connectedHooked)
            {
                _connectedHooked = false;
                _registry.Connected -= OnHelperConnected;
            }
            _latest = null;
            _latestW = 0;
            _latestH = 0;
        }
        Enqueue(_sendStop);
    }

    public byte[]? TryAcquireFrame(out int width, out int height)
    {
        lock (_lock)
        {
            width = _latestW;
            height = _latestH;
            return _latest;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _active = false;
            if (_connectedHooked)
            {
                _connectedHooked = false;
                _registry.Connected -= OnHelperConnected;
            }
        }
        if (_subscribed)
        {
            _subscribed = false;
            _registry.InboundEnvelope -= OnEnvelope;
        }
    }

    // Start is dropped while no helper is connected (logon autostart), and a
    // restarted helper has no capture running, so re-issue the request on every
    // connect. Dispatched off the registry's thread: Connected is raised before
    // the helper read loop starts.
    private void OnHelperConnected(HelperConnection connection) => _ = Task.Run(ResendStartAsync);

    internal Task ResendStartAsync()
    {
        string monitorId;
        int w, h, generation;
        lock (_lock)
        {
            if (!_active) return Task.CompletedTask;
            generation = _generation;
            monitorId = _activeMonitorId;
            w = _activeW;
            h = _activeH;
        }
        return Enqueue(() => SendStartIfCurrentAsync(generation, monitorId, w, h, resend: true));
    }

    private Task Enqueue(Func<Task> send)
    {
        lock (_lock)
        {
            _sends = _sends.ContinueWith(_ => RunSendAsync(send), TaskScheduler.Default).Unwrap();
            return _sends;
        }
    }

    private static async Task RunSendAsync(Func<Task> send)
    {
        try { await send().ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine($"[screen-mirror] helper send failed: {ex.Message}"); }
    }

    private Task SendStartIfCurrentAsync(int generation, string monitorId, int width, int height, bool resend)
    {
        lock (_lock)
        {
            if (generation != _generation) return Task.CompletedTask;
        }
        if (resend) ServiceLog.Info("[screen-mirror] helper connected, re-sending screenMirror.start");
        return _sendStart(monitorId, width, height);
    }

    private void EnsureSubscribed()
    {
        if (_subscribed) return;
        _subscribed = true;
        _registry.InboundEnvelope += OnEnvelope;
    }

    private void OnEnvelope(HelperConnection _, HelperEnvelope env)
    {
        if (env.Type != "screenMirror.frame" || env.Payload is null) return;
        // Skip the JSON decode entirely when no capture session is active.
        // The subscription stays registered for the lifetime of the singleton
        // so any in-flight frames from a late-arriving helper or a prior
        // session would otherwise pay the ~58 KB base64 decode cost.
        if (_expectedW == 0) return;
        try
        {
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ScreenMirrorFramePayload);
            if (p is null) return;
            // Drop stale frames from a prior capture session - the canvas
            // resampling assumes the buffer size matches what we asked for.
            if (_expectedW != 0 && (p.Width != _expectedW || p.Height != _expectedH)) return;
            lock (_lock)
            {
                _latest = p.Bytes;
                _latestW = p.Width;
                _latestH = p.Height;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[screen-frame-source] decode failed: {ex.Message}"); }
    }
}
#endif
