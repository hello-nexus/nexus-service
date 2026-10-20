using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// Opens the Glacial LCD's HID channel while the panel is present and Nexus Control is on.
/// On connect it does what MB Manager does at start (hello, one state query) and seeds the
/// saved brightness, display and sleep state from the reply; it writes to the panel only
/// after the user has changed something in Nexus, then applies each change at once and keeps
/// a hardware monitor's widgets fresh. HID only: the bulk interface is not used.
/// </summary>
public sealed class GlacialLcdWorker : BackgroundService
{
    public const string HandlerId = "asus-glacial-lcd";

    private const int MissesToDetach = 2;
    private const int PresencePollMs = 2000;
    private const int LoopMs = 250;
    private const int MaxBackoffMs = 5 * 60 * 1000;
    private const int StableSessionMs = 30_000;
    private const int LogEveryMs = 60_000;
    private const int BackoffSliceMs = 500;

    private readonly GlacialLcdLink _link;
    private readonly IHidEnumerator _hid;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;
    private readonly IConfigStore _store;
    private readonly ISensorProvider _sensors;
    private readonly int _connectPollMs;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly GlacialLcdConfirmed _confirmed = new();
    private volatile bool _connected;
    private long _lastLogMs = long.MinValue;

    public GlacialLcdWorker(
        IHidEnumerator hid, DeviceControlGate gate, HardwarePresence presence, IConfigStore store, ISensorProvider sensors,
        GlacialLcdLink? link = null, int connectPollMs = 5000)
    {
        _link = link ?? new GlacialLcdLink();
        _link.Changed += RequestApply;
        _hid = hid;
        _gate = gate;
        _presence = presence;
        _store = store;
        _sensors = sensors;
        _connectPollMs = connectPollMs;
    }

    public bool IsConnected => _connected;

    /// <summary>Tells the worker the saved state changed; it applies the difference on its next pass.</summary>
    public void RequestApply()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled(HandlerId) || !_presence.UsbPresent(0x0B05, GlacialLcdProtocol.ProductId))
                {
                    failures = 0;
                    await BackoffAsync(_connectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                long startedMs = Environment.TickCount64;
                bool stable = await TrySession(stoppingToken).ConfigureAwait(false);
                if (stable || Environment.TickCount64 - startedMs >= StableSessionMs)
                {
                    failures = 0;
                }
                else
                {
                    failures++;
                    await BackoffAsync(Backoff(failures), stoppingToken).ConfigureAwait(false);
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
                await BackoffAsync(Backoff(failures), stoppingToken).ConfigureAwait(false);
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

    /// <summary>One open-to-close session. True when it ended because the panel or the gate went away, false when the link failed.</summary>
    private async Task<bool> TrySession(CancellationToken stoppingToken)
    {
        using var hid = OpenHid();
        if (hid is null)
        {
            Log("no HID channel to open", error: false);
            return false;
        }
        var screen = new GlacialLcdScreen(
            hid,
            ReadSettings,
            (slot, index) => GlacialLcdSensors.Read(_sensors, slot, index),
            _confirmed,
            Seed,
            message => Log(message, error: false),
            ChooseDefaultMode,
            ModeLanded,
            () => _link.Streaming,
            () => _link.ShowPending = true,
            () => _link.BrightnessOverride);
        bool opened = false;
        // An upload holds the panel for its whole length; wait for it without blocking shutdown.
        while (!stoppingToken.IsCancellationRequested)
        {
            if (Monitor.TryEnter(_link.Sync, LoopMs))
            {
                try
                {
                    opened = screen.Open();
                }
                finally
                {
                    Monitor.Exit(_link.Sync);
                }
                break;
            }
        }
        if (!opened)
        {
            Log("the panel did not take the opening reports", error: false);
            return false;
        }
        ServiceLog.Info($"[{HandlerId}] connected");
        _connected = true;
        try
        {
            return await RunSession(screen, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            _connected = false;
            ServiceLog.Info($"[{HandlerId}] disconnected");
        }
    }

    private GlacialLcdSettings ReadSettings()
    {
        GlacialLcdSettings copy = new();
        _store.Read(s => copy = s.Devices.GlacialLcd.Copy());
        return copy;
    }

    /// <summary>Takes the panel's own brightness, display and sleep state as the saved one, for each field the user has not set.</summary>
    private void Seed(int brightness, bool displayOn, bool keepOn) => _store.Update(s =>
    {
        var g = s.Devices.GlacialLcd;
        // A lock dim in force is not the panel's own level, so it is not taken as the saved one.
        if (!g.BrightnessEdited && _link.BrightnessOverride is null) { g.Brightness = brightness; }
        if (!g.DisplayEdited) { g.DisplayOn = displayOn; }
        if (!g.SleepEdited) { g.StayOnWhileAsleep = keepOn; }
    });

    /// <summary>Records the first-run animation the screen just selected, so the saved state matches the panel.</summary>
    private void ChooseDefaultMode() => _store.Update(s =>
    {
        var g = s.Devices.GlacialLcd;
        if (!g.ModeChosen)
        {
            g.ModeChosen = true;
            g.Mode = GlacialLcdSettings.ModeAnimated;
            g.AnimatedAsset = GlacialLcdSettings.FirstRunAnimation;
            g.ModePending = false;
        }
    });

    /// <summary>Clears the pending flag once the mode that reached the panel is still the saved one.</summary>
    private void ModeLanded(string key) => _store.Update(s =>
    {
        var g = s.Devices.GlacialLcd;
        if (GlacialLcdScreen.ModeKeyOf(g.Copy()) == key)
        {
            g.ModePending = false;
        }
    });

    /// <summary>A backoff wait that ends early when the gate flips, the panel appears or goes, or a change is requested.</summary>
    private async Task BackoffAsync(int delayMs, CancellationToken ct)
    {
        bool enabled = _gate.IsEnabled(HandlerId);
        bool present = _presence.UsbPresent(0x0B05, GlacialLcdProtocol.ProductId);
        long until = Environment.TickCount64 + delayMs;
        while (Environment.TickCount64 < until)
        {
            int slice = (int)Math.Min(BackoffSliceMs, until - Environment.TickCount64);
            if (await _wake.WaitAsync(Math.Max(slice, 1), ct).ConfigureAwait(false)
                || _gate.IsEnabled(HandlerId) != enabled
                || _presence.UsbPresent(0x0B05, GlacialLcdProtocol.ProductId) != present)
            {
                return;
            }
        }
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

    private async Task<bool> RunSession(GlacialLcdScreen screen, CancellationToken stoppingToken)
    {
        int misses = 0;
        long nextPresenceMs = Environment.TickCount64 + PresencePollMs;
        int lastSavedBrightness = ReadSettings().Brightness;
        while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled(HandlerId))
        {
            await _wake.WaitAsync(LoopMs, stoppingToken).ConfigureAwait(false);
            var saved = ReadSettings();
            if (saved.Brightness != lastSavedBrightness)
            {
                // The user moved the slider, which ends any lock dim.
                lastSavedBrightness = saved.Brightness;
                _link.ClearBrightnessOverride();
            }
            PublishToUpload(screen, saved);
            // One owner of the panel at a time: an upload in progress holds this for its whole length.
            if (Monitor.TryEnter(_link.Sync))
            {
                try
                {
                    if (!screen.Reconcile() || !screen.Tick())
                    {
                        Log("a report was refused; reopening", error: false);
                        return false;
                    }
                }
                finally
                {
                    Monitor.Exit(_link.Sync);
                }
                var after = screen.PendingFlagEdits();
                if (!after.Brightness && !after.Display && !after.Sleep)
                {
                    _link.NativePending = false;
                    _link.ClearCancel();
                }
            }
            if (Environment.TickCount64 >= nextPresenceMs)
            {
                nextPresenceMs = Environment.TickCount64 + PresencePollMs;
                misses = _presence.UsbPresent(0x0B05, GlacialLcdProtocol.ProductId) ? 0 : misses + 1;
                if (misses >= MissesToDetach)
                {
                    return true;
                }
            }
        }
        return true;
    }

    /// <summary>Tells the upload whether the display is dark, whether a native edit is waiting, and cancels a running upload for the edits that must not wait.</summary>
    private void PublishToUpload(GlacialLcdScreen screen, GlacialLcdSettings saved)
    {
        int level = _link.BrightnessOverride ?? saved.Brightness;
        _link.Dark = !saved.DisplayOn || level == 0;
        if (!_link.Streaming)
        {
            _link.NativePending = false;
            return;
        }
        var pending = screen.PendingFlagEdits();
        _link.NativePending = pending.Brightness || pending.Display || pending.Sleep;
        // A dark display or a sleep edit must not sit behind the transfer; other edits wait their turn.
        if (pending.Sleep || (pending.Display && !saved.DisplayOn) || (pending.Brightness && level == 0))
        {
            _link.RequestCancel();
        }
    }

    private IHidDevice? OpenHid()
    {
        HidDeviceInfo? best = null;
        foreach (var info in _hid.Find(0x0B05, GlacialLcdProtocol.ProductId))
        {
            if (best == null || info.OutputReportByteLength > best.OutputReportByteLength)
            {
                best = info;
            }
        }
        // Overlapped, so the state-reply reads honor their timeout on Windows.
        return best == null ? null : _hid.Open(best.Path, forInput: true);
    }
}
