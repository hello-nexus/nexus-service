using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// The Galahad II LCD's pump and coolant probe, read and driven over the panel's HID handle
/// between frames, so this rides the panel's Nexus Control gate and L-Connect conflict.
/// </summary>
public sealed class Galahad2LcdAio : BackgroundService
{
    /// <summary>L-Connect resends the pump target on this cadence (reference driver), so the poll carries it.</summary>
    private const int TickMs = 1000;

    private const int ReplyTimeoutMs = 100;

    /// <summary>The reference driver's ack wait; the frame path's drain matches it.</summary>
    private const int AckTimeoutMs = 20;

    /// <summary>Frame acks and pump-write replies queue ahead of the status reply on the same IN pipe.</summary>
    private const int MaxReplyReads = 6;

    private const int StaleMs = 5000;

    /// <summary>Each unanswered poll holds the frame path for a read timeout, so a silent unit is asked less often.</summary>
    private const int SilentRetryMs = 10_000;

    /// <summary>Consecutive unanswered polls tolerated from a unit that has answered before backing off.</summary>
    private const int MaxMisses = 3;

    private readonly JpegPanelHub _hub;

    // Shared with the hub's detach hook, which runs on the connection worker.
    private readonly object _lock = new();
    private LianLiAioStatus? _status;
    private long _statusAt;
    private int? _pumpDuty;
    private int? _foundPwm;
    private bool _driving;

    private bool _connected;
    private string? _transition;
    private long _quietUntil;
    private int _misses;
    private bool _answered;
    private bool _silenceLogged;

    public Galahad2LcdAio(JpegPanelHub hub)
    {
        _hub = hub;
        _hub.Detaching = HandBackOnDetach;
    }

    public string DeviceId => _hub.Model.HandlerId;

    public string DeviceName => _hub.Model.Name;

    /// <summary>The last status reply, or null when none arrived in the last few seconds.</summary>
    public LianLiAioStatus? Status
    {
        get
        {
            lock (_lock)
            {
                return _status is not null && Environment.TickCount64 - _statusAt < StaleMs ? _status : null;
            }
        }
    }

    public int? PumpDuty { get { lock (_lock) { return _pumpDuty; } } }

    public bool IsConnected => _hub.IsConnected;

    /// <summary>Raised from the tick when the panel's handle attaches or detaches.</summary>
    public event Action? ConnectionChanged;

    /// <summary>Lights the pump-head ring one colour; false when nothing is attached or the write fails.</summary>
    public bool SetRing(byte r, byte g, byte b) => _hub.Exchange((device, report) =>
    {
        LianLiAioProtocol.FillSetPumpLight(report, r, g, b);
        if (!device.Write(report))
        {
            return false;
        }
        // The frame path drains one ack per frame, so a control write drains its own or the
        // IN endpoint backs up until the panel stops taking output reports.
        Span<byte> ack = stackalloc byte[64];
        device.Read(ack, AckTimeoutMs);
        return true;
    }, false);

    /// <summary>Drives the pump at a duty percent from the next tick, or hands it back with null.</summary>
    public void SetPumpDuty(int? dutyPercent)
    {
        lock (_lock)
        {
            _pumpDuty = dutyPercent is { } d ? Math.Clamp(d, LianLiAioProtocol.GalahadPumpDutyFloor, 100) : null;
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
                ServiceLog.Warn($"[{DeviceId}] aio tick failed: {ex.Message}");
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
            if (_connected)
            {
                _connected = false;
                _answered = false;
                _silenceLogged = false;
                _misses = 0;
                _quietUntil = 0;
                lock (_lock)
                {
                    _status = null;
                    _foundPwm = null;
                    _driving = false;
                }
                ConnectionChanged?.Invoke();
            }
            return;
        }
        if (!_connected)
        {
            _connected = true;
            ConnectionChanged?.Invoke();
        }
        if (now < _quietUntil)
        {
            return;
        }

        var status = _hub.Exchange(Exchange, (LianLiAioStatus?)null);
        if (_transition is { } transition)
        {
            _transition = null;
            ServiceLog.Info($"[{DeviceId}] {transition}");
        }
        if (status is null)
        {
            if (!_answered || ++_misses >= MaxMisses)
            {
                _quietUntil = now + SilentRetryMs;
            }
            if (!_silenceLogged)
            {
                _silenceLogged = true;
                ServiceLog.Warn($"[{DeviceId}] no status reply; pump and coolant readings unavailable");
            }
            return;
        }
        _misses = 0;
        _silenceLogged = false;
        int? foundNow = null;
        lock (_lock)
        {
            _status = status;
            _statusAt = now;
            if (_foundPwm is null)
            {
                _foundPwm = foundNow = LianLiAioProtocol.GalahadPwmForRpm(status.PumpRpm);
            }
        }
        if (!_answered)
        {
            _answered = true;
            ServiceLog.Info($"[{DeviceId}] status: pump {status.PumpRpm} rpm"
                + (foundNow is { } f ? $" (~{f}% PWM)" : "") + $", fan {status.FanRpm} rpm, coolant "
                + (status.CoolantC is { } c ? $"{c:F1} C" : "not reported"));
        }
    }

    /// <summary>Gate off, shutdown or unplug: a driven pump gets its own speed back before the handle closes.</summary>
    private void HandBackOnDetach(IHidDevice device, byte[] report)
    {
        int pwm;
        lock (_lock)
        {
            if (!_driving || _foundPwm is not { } found)
            {
                return;
            }
            pwm = found;
        }
        LianLiAioProtocol.FillSetPumpPwm(report, pwm);
        if (!device.Write(report))
        {
            return;
        }
        lock (_lock)
        {
            _driving = false;
        }
        ServiceLog.Info($"[{DeviceId}] pump handed back at {pwm}% PWM on detach");
    }

    /// <summary>
    /// Under the hub lock, so it is serialized with <see cref="HandBackOnDetach"/>: writes the
    /// pump target or hand-back, then asks for status and reads until its reply. The driven
    /// state changes only once its write lands, so a failed hand-back is retried next tick.
    /// </summary>
    private LianLiAioStatus? Exchange(IHidDevice device, byte[] report)
    {
        int? pwm = null;
        bool handBack = false;
        lock (_lock)
        {
            // Nothing is written until the unit's own speed is read, so it can be handed back.
            if (_foundPwm is { } found)
            {
                if (_pumpDuty is { } duty)
                {
                    pwm = duty;
                }
                else if (_driving)
                {
                    pwm = found;
                    handBack = true;
                }
            }
        }
        if (pwm is { } target)
        {
            LianLiAioProtocol.FillSetPumpPwm(report, target);
            if (!device.Write(report))
            {
                return null;
            }
            lock (_lock)
            {
                if (_driving == handBack)
                {
                    _transition = handBack ? $"pump handed back at {target}% PWM" : $"pump driven at {target}%";
                }
                _driving = !handBack;
            }
        }
        LianLiAioProtocol.FillACommand(report, LianLiAioProtocol.CmdStatus, ReadOnlySpan<byte>.Empty);
        if (!device.Write(report))
        {
            return null;
        }
        Span<byte> reply = stackalloc byte[64];
        for (int i = 0; i < MaxReplyReads; i++)
        {
            reply.Clear();
            int read = device.Read(reply, ReplyTimeoutMs);
            if (read <= 0)
            {
                return null;
            }
            if (LianLiAioProtocol.ParseStatus(reply[..read]) is { } status)
            {
                return status;
            }
        }
        return null;
    }
}
