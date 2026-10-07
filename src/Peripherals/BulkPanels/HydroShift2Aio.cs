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

    private readonly BulkPanelHub _hub;
    private readonly HydroShift2LcdDriver _driver;
    private readonly Func<bool> _wirelessOwns;

    private readonly object _lock = new();
    private HydroShift2Params? _params;
    private readonly bool[] _fanSeen = new bool[HydroShift2Protocol.FanSlots];
    private int? _pumpDuty;
    private readonly int?[] _fanDuty = new int?[HydroShift2Protocol.FanSlots];
    private byte[]? _sentFans;
    private bool _pumpFanDirty;
    private byte[]? _ring;
    private bool _ringDirty;

    private long _lastParamsAt;
    private long _lastPumpFanAt;
    private long _lastRingAt;
    private bool _wasAvailable;
    private bool _driving;

    /// <param name="wirelessOwns">True while a wireless dongle Nexus drives has any HydroShift II bound; that link then owns pump, fans and ring, as in L-Connect.</param>
    public HydroShift2Aio(BulkPanelHub hub, HydroShift2LcdDriver driver, Func<bool> wirelessOwns)
    {
        _hub = hub;
        _driver = driver;
        _wirelessOwns = wirelessOwns;
    }

    /// <summary>Connected over USB and not handed to the wireless link.</summary>
    public bool IsAvailable => _hub.IsConnected && !_wirelessOwns();

    public HydroShift2Params? Params { get { lock (_lock) { return _params; } } }

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

    /// <summary>Sets the ring from packed RGB; unchanged colours are not re-sent.</summary>
    public void SetRing(ReadOnlySpan<byte> rgb)
    {
        lock (_lock)
        {
            if (_ring is not null && rgb.SequenceEqual(_ring)) return;
            _ring = rgb.ToArray();
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

        var available = IsAvailable;
        if (available != _wasAvailable)
        {
            _wasAvailable = available;
            RaiseAvailabilityChanged();
        }
        if (!available)
        {
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
                pumpRpm = _pumpDuty is { } pump ? HydroShift2Protocol.PumpRpmForDuty(pump) : HydroShift2Protocol.DefaultPumpRpm;
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
        lock (_lock)
        {
            if (!_ringDirty || _ring is null || now - _lastRingAt < RingMinIntervalMs)
            {
                return;
            }
            ring = _ring;
            _ringDirty = false;
        }
        _lastRingAt = now;
        if (!_hub.Exchange(pipe => _driver.PushRing(pipe, ring), false))
        {
            ServiceLog.Warn($"[{HydroShift2LcdDriver.Id}] ring write unanswered");
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
