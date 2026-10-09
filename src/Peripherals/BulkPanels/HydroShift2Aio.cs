using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The HydroShift II's pump, fan headers and ring, driven over the LCD's pipe between
/// frames. Every write here goes through <see cref="BulkPanelHub.Exchange{T}"/>, so this
/// loop is the only AIO writer and never interleaves with a frame.
/// </summary>
public sealed class HydroShift2Aio : BackgroundService
{
    private const int TickMs = 250;
    private const int ParamsPollMs = 1000;

    /// <summary>L-Connect resends pump/fan on this cadence; a single low pump target drifted back up within seconds (measured).</summary>
    private const int PumpFanResendMs = 3600;

    /// <summary>The firmware acks nothing else while it applies a ring upload, so each upload hitches the glass (measured).</summary>
    private const int RingMinIntervalMs = 2000;

    /// <summary>
    /// The firmware leaves ring uploads unanswered while bound to a dongle and for ~13 s after
    /// it is released (measured), and each unanswered upload stalls the glass for seconds.
    /// </summary>
    private const int RingAfterWirelessHoldMs = 20_000;

    /// <summary>First retry after an unanswered ring upload, past the few-second stall it causes (measured); doubles up to the cap.</summary>
    private const int RingRetryMs = 5000;
    private const int RingRetryMaxMs = 40_000;

    private readonly BulkPanelHub _hub;
    private readonly HydroShift2LcdDriver _driver;
    private readonly Func<string?, bool> _wirelessOwns;
    private readonly Func<string, bool> _nexusWidgets;

    private readonly object _lock = new();
    private HydroShift2Params? _params;
    private readonly bool[] _fanSeen = new bool[HydroShift2Protocol.FanSlots];
    private int? _pumpDuty;
    private readonly int?[] _fanDuty = new int?[HydroShift2Protocol.FanSlots];
    private byte[]? _sentFans;
    private bool _pumpFanDirty;
    private byte[]? _ring;
    private int _ringFrames;
    private byte _ringInterval;
    private bool _ringDirty;

    private long _lastParamsAt;
    private long _lastPumpFanAt;
    private long _lastRingAt;
    private long _ringHoldUntil;
    private int _ringRetryMs;
    private bool _wirelessHeld;
    private bool _wasAvailable;
    private bool _driving;
    private volatile bool _overlayClear;

    /// <summary>Whether the glass showed the AIO's own screen at the last check; null until a reading after connect names the unit.</summary>
    private bool? _ownScreen;

    /// <param name="wirelessOwns">True while a wireless dongle Nexus drives has this unit (by radio MAC; null before the first reading) bound; that link then owns pump, fans and ring.</param>
    /// <param name="nexusWidgets">False for a unit (by radio MAC) whose glass the user gave to its own wireless screen; null keeps Nexus widgets on every unit.</param>
    public HydroShift2Aio(BulkPanelHub hub, HydroShift2LcdDriver driver, Func<string?, bool> wirelessOwns, Func<string, bool>? nexusWidgets = null)
    {
        _hub = hub;
        _driver = driver;
        _wirelessOwns = wirelessOwns;
        _nexusWidgets = nexusWidgets ?? (_ => true);
    }

    /// <summary>The wireless link owns this unit and the user chose its own screen over Nexus widgets, so the glass is not streamed.</summary>
    public bool ShowsOwnScreen => Params?.Mac is { } mac && _wirelessOwns(mac) && !_nexusWidgets(mac);

    /// <summary>
    /// Raised from the loop with the unit's MAC and <see cref="ShowsOwnScreen"/> after a connect
    /// and on every change, so the panel stream stops or starts at once. The AIO's own screen
    /// then needs a switch to RF control sent after the stream stopped: the connect handshake
    /// and the frames wiped what an earlier switch drew.
    /// </summary>
    public event Action<string, bool>? ScreenOwnerChanged;

    /// <summary>Wipes what the firmware drew over the glass once the unit is read, unless it shows its own screen by then.</summary>
    public void ClearScreenOverlay() => _overlayClear = true;

    /// <summary>Connected over USB and not handed to the wireless link.</summary>
    public bool IsAvailable => _hub.IsConnected && !_wirelessOwns(Params?.Mac);

    public HydroShift2Params? Params { get { lock (_lock) { return _params; } } }

    /// <summary>The connected head is the round LCD-C.</summary>
    public bool Round => _driver.Round;

    /// <summary>Raised from the loop when <see cref="IsAvailable"/> flips.</summary>
    public event Action? AvailabilityChanged;

    /// <summary>A fan slot counts once it has reported a speed this connection, so a fan stopped at 0% keeps its channel.</summary>
    public bool FanPresent(int slot) { lock (_lock) { return _fanSeen[slot]; } }

    public int? PumpDuty { get { lock (_lock) { return _pumpDuty; } } }

    public int? FanDuty(int slot) { lock (_lock) { return _fanDuty[slot]; } }

    /// <summary>The duty percent last written to a fan slot, driven or default, or null before any write.</summary>
    public int? SentFanDuty(int slot) { lock (_lock) { return _sentFans is { } fans ? fans[slot] * 100 / 255 : null; } }

    /// <summary>Drives the pump at a duty percent, or lets go of it with null.</summary>
    public void SetPumpDuty(int? dutyPercent)
    {
        lock (_lock)
        {
            if (_pumpDuty == dutyPercent) return;
            _pumpDuty = dutyPercent;
            _pumpFanDirty = true;
        }
    }

    public void SetFanDuty(int slot, int? dutyPercent)
    {
        lock (_lock)
        {
            if (_fanDuty[slot] == dutyPercent) return;
            _fanDuty[slot] = dutyPercent;
            _pumpFanDirty = true;
        }
    }

    /// <summary>Sets the ring to one still frame of packed RGB; unchanged colours are not re-sent.</summary>
    public void SetRing(ReadOnlySpan<byte> rgb) => SetRingAnimation(rgb, 1, 1);

    /// <summary>Sets the ring to an animation the firmware loops (packed RGB frames, interval in 0.625 ms ticks); an unchanged one is not re-sent.</summary>
    public void SetRingAnimation(ReadOnlySpan<byte> frames, int frameCount, byte intervalTicks)
    {
        lock (_lock)
        {
            if (_ring is not null && _ringFrames == frameCount && _ringInterval == intervalTicks && frames.SequenceEqual(_ring)) return;
            _ring = frames.ToArray();
            _ringFrames = frameCount;
            _ringInterval = intervalTicks;
            _ringDirty = true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick(Environment.TickCount64);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[{HydroShift2LcdDriver.Id}] aio tick failed: {ex.Message}");
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal void Tick(long now)
    {
        if (!_hub.IsConnected)
        {
            // Cleared even while the dongle owns the unit, so a reconnect starts without a hold.
            _ringHoldUntil = 0;
            _ringRetryMs = 0;
            _wirelessHeld = false;
            _overlayClear = false;
            _ownScreen = null;
            // A reconnect reads the unit again, even while the dongle owns it and OnDisconnected is skipped.
            lock (_lock)
            {
                _params = null;
            }
            _lastParamsAt = 0;
            if (_wasAvailable)
            {
                OnDisconnected();
                RaiseAvailabilityChanged();
            }
            return;
        }

        if (now - _lastParamsAt >= ParamsPollMs)
        {
            _lastParamsAt = now;
            var reading = _hub.Exchange(pipe => _driver.ReadParams(pipe), (HydroShift2Params?)null);
            if (reading is not null)
            {
                lock (_lock)
                {
                    _params = reading;
                    for (int i = 0; i < _fanSeen.Length; i++)
                    {
                        _fanSeen[i] |= reading.FanRpm[i] > 0;
                    }
                }
            }
        }

        if (Params is { } current)
        {
            var own = ShowsOwnScreen;
            if (own != _ownScreen)
            {
                // Nexus widgets taking the glass back wipe the AIO's screen; after a connect the handshake already did.
                if (!own && _ownScreen is not null)
                {
                    _overlayClear = true;
                }
                _ownScreen = own;
                RaiseScreenOwnerChanged(current.Mac, own);
            }
            if (_overlayClear)
            {
                _overlayClear = false;
                if (!own)
                {
                    _hub.Exchange(pipe => _driver.ClearOverlay(pipe), false);
                }
            }
        }

        var available = IsAvailable;
        if (available != _wasAvailable)
        {
            _wasAvailable = available;
            if (available)
            {
                // Back from the wireless link, which may have repainted the ring.
                lock (_lock)
                {
                    _ringDirty = _ring is not null;
                }
                if (_wirelessHeld)
                {
                    _wirelessHeld = false;
                    _ringHoldUntil = now + RingAfterWirelessHoldMs;
                }
            }
            RaiseAvailabilityChanged();
        }
        if (!available)
        {
            _wirelessHeld = true;
            return;
        }

        SendPumpFanIfDue(now);
        SendRingIfDue(now);
    }

    private void SendPumpFanIfDue(long now)
    {
        int pumpRpm;
        bool driven;
        var fans = new byte[HydroShift2Protocol.FanSlots];
        lock (_lock)
        {
            driven = _pumpDuty is not null || Array.Exists(_fanDuty, d => d is not null);
            if (!driven)
            {
                if (!_driving) return;
                _driving = false;
                _pumpFanDirty = false;
                pumpRpm = HydroShift2Protocol.DefaultPumpRpm;
                Array.Fill(fans, HydroShift2Protocol.DefaultFanByte);
            }
            else
            {
                if (!_pumpFanDirty && now - _lastPumpFanAt < PumpFanResendMs) return;
                _driving = true;
                _pumpFanDirty = false;
                pumpRpm = _pumpDuty is { } pump ? HydroShift2Protocol.PumpRpmForDuty(pump, _driver.Round) : HydroShift2Protocol.DefaultPumpRpm;
                for (int i = 0; i < fans.Length; i++)
                {
                    fans[i] = _fanDuty[i] is { } duty ? (byte)(Math.Clamp(duty, 0, 100) * 255 / 100) : HydroShift2Protocol.DefaultFanByte;
                }
            }
            _sentFans = fans;
        }
        _lastPumpFanAt = now;
        if (!_hub.Exchange(pipe => _driver.SyncPumpFan(pipe, pumpRpm, fans, driven), false))
        {
            ServiceLog.Warn($"[{HydroShift2LcdDriver.Id}] pump/fan write unanswered");
        }
    }

    private void SendRingIfDue(long now)
    {
        byte[] ring;
        int frames;
        byte interval;
        lock (_lock)
        {
            if (!_ringDirty || _ring is null || now - _lastRingAt < RingMinIntervalMs || now < _ringHoldUntil)
            {
                return;
            }
            ring = _ring;
            frames = _ringFrames;
            interval = _ringInterval;
            _ringDirty = false;
        }
        _lastRingAt = now;
        if (_hub.Exchange(pipe => _driver.PushRing(pipe, ring, frames, interval), false))
        {
            _ringRetryMs = 0;
            return;
        }
        // Retrying while the firmware still ignores uploads keeps the glass stalled, so back off.
        _ringRetryMs = _ringRetryMs == 0 ? RingRetryMs : Math.Min(_ringRetryMs * 2, RingRetryMaxMs);
        _ringHoldUntil = now + _ringRetryMs;
        ServiceLog.Warn($"[{HydroShift2LcdDriver.Id}] ring write unanswered; retrying in {_ringRetryMs / 1000} s");
        lock (_lock)
        {
            // A ring effect is set once, so an unanswered upload would never be tried again.
            _ringDirty |= ReferenceEquals(_ring, ring);
        }
    }

    private void RaiseAvailabilityChanged()
    {
        try
        {
            AvailabilityChanged?.Invoke();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[{HydroShift2LcdDriver.Id}] availability subscriber failed: {ex.Message}");
        }
    }

    private void RaiseScreenOwnerChanged(string mac, bool ownScreen)
    {
        try
        {
            ScreenOwnerChanged?.Invoke(mac, ownScreen);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[{HydroShift2LcdDriver.Id}] screen owner subscriber failed: {ex.Message}");
        }
    }

    /// <summary>Forgets readings and forces the next connection to resend every driven target.</summary>
    private void OnDisconnected()
    {
        _wasAvailable = false;
        lock (_lock)
        {
            _params = null;
            _sentFans = null;
            Array.Clear(_fanSeen);
            _pumpFanDirty = true;
            _ringDirty = _ring is not null;
        }
        _lastParamsAt = 0;
        _lastPumpFanAt = 0;
        _lastRingAt = 0;
    }
}
