using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Peripherals.DeepCool;

/// <summary>The media job in hand: <see cref="MystiqueWorker.ProgressConverting"/> or <see cref="MystiqueWorker.ProgressSending"/> and its percent, or an empty phase.</summary>
public sealed record MystiqueProgress(string Phase, int Percent);

/// <summary>
/// Keeps a Mystique screen on live data while it is present and Nexus Control is on: opens its
/// bulk pipes, writes the settings groups the user has changed, then pushes Nexus's sensors
/// every tick. Without a host the screen falls back to its own preset animation.
/// </summary>
public sealed class MystiqueWorker : BackgroundService
{
    public const string HandlerId = "deepcool-mystique";

    private const int MissesToDetach = 2;
    private const int PresencePollMs = 2000;
    private const int LogEveryMs = 60_000;
    private const int MaxBackoffMs = 5 * 60 * 1000;
    private const int StableSessionMs = 30_000;
    private const int FormatRetryWaitMs = 15_000;

    private readonly IBulkUsbPipeFactory _pipes;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;
    private readonly IConfigStore _store;
    private readonly ISensorProvider _sensors;
    private readonly Nexus.Service.Cooling.IFanControlProvider? _fans;
    private readonly int _connectPollMs;
    private readonly int _tickMs;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile bool _connected;
    private long _lastLogMs = long.MinValue;

    public MystiqueWorker(
        IBulkUsbPipeFactory pipes, DeviceControlGate gate, HardwarePresence presence, IConfigStore store, ISensorProvider sensors,
        int connectPollMs = 5000, int tickMs = 1000, Nexus.Service.Cooling.IFanControlProvider? fans = null)
    {
        _fans = fans;
        _pipes = pipes;
        _gate = gate;
        _gate.Changed += OnGateChanged;
        _presence = presence;
        _store = store;
        _sensors = sensors;
        _connectPollMs = connectPollMs;
        _tickMs = tickMs;
    }

    public bool IsConnected => _connected;

    private sealed class PendingUpload(Func<MystiqueSession, bool> run)
    {
        public Func<MystiqueSession, bool> Run { get; } = run;
        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Waiting, Streaming or Withdrawn; only one side wins the move out of Waiting.
        public int State;
    }

    private const int UploadWaiting = 0;
    private const int UploadStreaming = 1;
    private const int UploadWithdrawn = -1;

    private PendingUpload? _pendingUpload;

    // Counts opened sessions, so a format retry waits for a fresh one rather than the one that failed.
    private int _sessions;

    /// <summary>
    /// Held by the media route from the slot choice to the saved entry, so two pictures cannot both take
    /// the last slot.
    /// </summary>
    public SemaphoreSlim UploadGate { get; } = new(1, 1);

    /// <summary>
    /// Hands a picture or GIF to the worker, which streams it between ticks so nothing else touches the
    /// pipes. False when no screen is connected, another upload is waiting, the stream fails, or the
    /// caller gives up before the stream starts; once it has started, the real result is awaited.
    /// </summary>
    /// <param name="source">Runs on the worker once the stream is finished, before anything else is sent, and names the source it leaves the screen on (null for none).</param>
    public async Task<bool> UploadAsync(MystiqueMediaPackage package, Func<MystiqueSourceAfter?> source, CancellationToken ct) =>
        await UploadAsync(new[] { package }, source, ct).ConfigureAwait(false) == 1;

    /// <summary>
    /// Streams the packages in one turn of the worker, the source command once after the last; returns how many
    /// were streamed cleanly, as <see cref="MystiqueSession.Upload(IReadOnlyList{MystiqueMediaPackage}, Func{MystiqueSourceAfter?}?, Action{long}?)"/>,
    /// 0 when a started turn ended without a count, or -1 when the turn was never taken.
    /// </summary>
    public async Task<int> UploadAsync(IReadOnlyList<MystiqueMediaPackage> packages, Func<MystiqueSourceAfter?> source, CancellationToken ct)
    {
        long total = 0;
        foreach (var package in packages)
        {
            foreach (var frame in package.Frames)
            {
                total += frame.Length;
            }
        }
        int streamed = -1;
        await RunMediaAsync(s =>
        {
            streamed = 0;
            streamed = s.Upload(packages, source, sent => ReportProgress(ProgressSending, sent, total));
            return streamed == packages.Count;
        }, ct).ConfigureAwait(false);
        return streamed;
    }


    public const string ProgressConverting = "converting";
    public const string ProgressSending = "sending";

    private static readonly MystiqueProgress Idle = new("", 0);

    // The page reads it while its upload request waits.
    private volatile MystiqueProgress _progress = Idle;

    public MystiqueProgress Progress => _progress;

    public void ReportProgress(string phase, long done, long total)
    {
        int percent = total > 0 ? (int)Math.Clamp(done * 100 / total, 0, 100) : 0;
        var current = _progress;
        if (current.Phase != phase || current.Percent != percent)
        {
            _progress = new MystiqueProgress(phase, percent);
        }
    }

    public void ClearProgress() => _progress = Idle;

    /// <summary>
    /// Erases every picture and GIF on the screen, between ticks like an upload. A refused or unanswered
    /// format reopens the session, and the format goes once more on the next one: the recorded screen
    /// answered it within a millisecond, so a missed reply is a lost exchange, and erasing twice is harmless.
    /// </summary>
    public async Task<bool> FormatAsync(CancellationToken ct)
    {
        int session = Volatile.Read(ref _sessions);
        if (await RunMediaAsync(s => s.Format(), ct).ConfigureAwait(false))
        {
            return true;
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(FormatRetryWaitMs);
        try
        {
            while (!_connected || Volatile.Read(ref _sessions) == session)
            {
                // Cuts short a reconnect backoff, which can outlast the wait.
                RequestApply();
                await Task.Delay(Math.Min(_tickMs, 100), wait.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        return await RunMediaAsync(s => s.Format(), ct).ConfigureAwait(false);
    }

    private async Task<bool> RunMediaAsync(Func<MystiqueSession, bool> run, CancellationToken ct)
    {
        if (!_connected)
        {
            return false;
        }
        var pending = new PendingUpload(run);
        if (Interlocked.CompareExchange(ref _pendingUpload, pending, null) is not null)
        {
            return false;
        }
        RequestApply();
        try
        {
            using var reg = ct.Register(() =>
            {
                if (Interlocked.CompareExchange(ref pending.State, UploadWithdrawn, UploadWaiting) == UploadWaiting)
                {
                    pending.Done.TrySetResult(false);
                }
            });
            return await pending.Done.Task.ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref _pendingUpload, null, pending);
        }
    }

    /// <summary>Tells the worker the saved settings changed; it sends the difference on its next pass.</summary>
    public void RequestApply()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void OnGateChanged(string handlerId, bool enabled)
    {
        if (handlerId == HandlerId)
        {
            RequestApply();
        }
    }

    public override void Dispose()
    {
        _gate.Changed -= OnGateChanged;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled(HandlerId) || !_presence.UsbPresent(MystiqueProtocol.VendorId, MystiqueProtocol.ProductId))
                {
                    failures = 0;
                    await WaitAsync(_connectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                long startedMs = Environment.TickCount64;
                if (await RunSession(stoppingToken).ConfigureAwait(false) || Environment.TickCount64 - startedMs >= StableSessionMs)
                {
                    failures = 0;
                }
                else
                {
                    failures++;
                    await WaitAsync(Backoff(failures), stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                Log($"worker error: {ex.Message}", error: true);
                await WaitAsync(Backoff(failures), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Exponential from the connect poll, capped at a few minutes.</summary>
    private int Backoff(int failures)
    {
        long delay = _connectPollMs;
        for (int i = 1; i < failures && delay < MaxBackoffMs; i++)
        {
            delay *= 2;
        }
        return (int)Math.Min(delay, Math.Max(MaxBackoffMs, _connectPollMs));
    }

    /// <summary>A wait that ends early when the gate flips or a change is requested.</summary>
    private async Task WaitAsync(int delayMs, CancellationToken ct)
    {
        await _wake.WaitAsync(delayMs, ct).ConfigureAwait(false);
    }

    /// <summary>One open-to-close session. True when it ended because the screen or the gate went away, false when the link failed.</summary>
    private async Task<bool> RunSession(CancellationToken stoppingToken)
    {
        using var pipe = _pipes.Open(
            MystiqueProtocol.VendorId, MystiqueProtocol.ProductId, MystiqueProtocol.LiveWritePipe, MystiqueProtocol.LiveReadPipe);
        if (pipe is null)
        {
            Log("the screen's pipes could not be opened", error: false);
            return false;
        }
        var session = new MystiqueSession(pipe);
        if (!session.Open(ReadSettings(), DateTime.Now))
        {
            Log("the screen did not answer the opening exchange", error: false);
            return false;
        }
        ServiceLog.Info($"[{HandlerId}] connected {session.Info}");
        Interlocked.Increment(ref _sessions);
        _connected = true;
        try
        {
            int misses = 0;
            long nextPresenceMs = Environment.TickCount64 + PresencePollMs;
            while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled(HandlerId))
            {
                await _wake.WaitAsync(_tickMs, stoppingToken).ConfigureAwait(false);
                if (!_gate.IsEnabled(HandlerId))
                {
                    break;
                }
                var upload = Volatile.Read(ref _pendingUpload);
                if (upload is not null && Interlocked.CompareExchange(ref upload.State, UploadStreaming, UploadWaiting) == UploadWaiting)
                {
                    bool sent = upload.Run(session);
                    upload.Done.TrySetResult(sent);
                    if (!sent)
                    {
                        Log("an upload or format was refused; reopening", error: false);
                        return false;
                    }
                    continue;
                }
                var settings = ReadSettings();
                if (!session.Apply(settings) || !session.Tick(MystiqueSensors.Read(_sensors, settings, MystiqueSensors.FanNames(_store, _fans)), DateTime.Now))
                {
                    Log("a write was refused; reopening", error: false);
                    return false;
                }
                if (Environment.TickCount64 >= nextPresenceMs)
                {
                    nextPresenceMs = Environment.TickCount64 + PresencePollMs;
                    misses = _presence.UsbPresent(MystiqueProtocol.VendorId, MystiqueProtocol.ProductId) ? 0 : misses + 1;
                    if (misses >= MissesToDetach)
                    {
                        return true;
                    }
                }
            }
            return true;
        }
        finally
        {
            _connected = false;
            // Also settles a stream an exception cut short, which never reported its own result.
            if (Volatile.Read(ref _pendingUpload) is { } left)
            {
                Interlocked.CompareExchange(ref left.State, UploadWithdrawn, UploadWaiting);
                left.Done.TrySetResult(false);
            }
            ServiceLog.Info($"[{HandlerId}] disconnected");
        }
    }

    private DeepCoolMystiqueSettings ReadSettings()
    {
        DeepCoolMystiqueSettings copy = new();
        _store.Read(s => copy = s.Devices.DeepCoolMystique.Copy());
        return copy;
    }

    private void Log(string message, bool error)
    {
        long now = Environment.TickCount64;
        if (_lastLogMs != long.MinValue && now - _lastLogMs < LogEveryMs)
        {
            return;
        }
        _lastLogMs = now;
        if (error)
        {
            ServiceLog.Error($"[{HandlerId}] {message}");
        }
        else
        {
            ServiceLog.Warn($"[{HandlerId}] {message}");
        }
    }
}
