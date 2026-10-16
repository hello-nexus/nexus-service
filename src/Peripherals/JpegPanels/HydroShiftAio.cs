using System;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// The HydroShift LCD 360R's pump and fans: speed writes and the RPM readout, over the panel's own
/// HID handle. Writes ride the hub's exchange, so they are serialized with frame chunks and cannot
/// run before the attach (settle included) has finished. The RPM comes from the session keepalive's
/// status reply; there is no second poll. Only the 360R takes part: the 360S and 360TL are untouched.
/// </summary>
public sealed class HydroShiftAio
{
    public const int ProductId = HydroShiftFanLeds.ProductId360R;

    /// <summary>Drains acks queued behind a write; short, because the hub lock is held and frames wait on it.</summary>
    private const int AckTimeoutMs = 2;

    private const int MaxAckReads = 4;

    private const long StaleMs = 5000;

    private const long FailureLogIntervalMs = 30_000;

    private readonly JpegPanelHub _hub;
    private readonly Func<long> _clock;

    // Shared between the keepalive (hub lock held) and the cooling provider.
    private readonly object _lock = new();
    private LianLiAioStatus? _status;
    private long _statusAt;
    private int? _wantPump;
    private int? _wantFan;

    // Touched only under the hub lock. A sent value is what Nexus last put on the device; null
    // means the device runs its own default.
    private IHidDevice? _sentFor;
    private int? _sentPump;
    private int? _sentFan;
    private long _backoffMs;
    private long _retryAtMs;
    private long _failures;
    private long _failuresLogged;
    private long _lastFailureLogMs = long.MinValue / 2;
    private bool _failureLogged;

    public HydroShiftAio(JpegPanelHub hub, Func<long>? clock = null)
    {
        _hub = hub;
        _clock = clock ?? (() => Environment.TickCount64);
        _hub.Detaching = HandBackOnDetach;
        if (_hub.Model.Handshake is LianLiAioHandshake handshake)
        {
            handshake.SetKeepaliveObserver(ProductId, OnKeepalive);
        }
    }

    public string DeviceId => _hub.Model.HandlerId;

    public bool IsConnected => _hub.IsConnected && _hub.AttachedProductId == ProductId;

    /// <summary>The last status reply, or null when none arrived in the last few seconds.</summary>
    public LianLiAioStatus? Status
    {
        get
        {
            lock (_lock)
            {
                return _status is not null && _clock() - _statusAt < StaleMs ? _status : null;
            }
        }
    }

    public int? PumpDuty { get { lock (_lock) { return _wantPump; } } }

    public int? FanDuty { get { lock (_lock) { return _wantFan; } } }

    /// <summary>Drives the pump at a duty percent, or hands it back to the motherboard header with null.</summary>
    public void SetPumpDuty(int? dutyPercent)
    {
        lock (_lock)
        {
            _wantPump = dutyPercent is { } d ? Math.Clamp(d, LianLiAioProtocol.HydroShiftPumpDutyFloor, 100) : null;
        }
        Push();
    }

    /// <summary>Drives all three fans at a duty percent, or parks them at the release duty with null.</summary>
    public void SetFanDuty(int? dutyPercent)
    {
        lock (_lock)
        {
            _wantFan = dutyPercent is { } d ? Math.Clamp(d, LianLiAioProtocol.HydroShiftFanDutyFloor, 100) : null;
        }
        Push();
    }

    private void Push() => _hub.Exchange((device, report) =>
    {
        Apply(device, report);
        return true;
    }, false);

    private void OnKeepalive(IHidDevice device, byte[] report, LianLiAioStatus? status)
    {
        if (status is not null)
        {
            lock (_lock)
            {
                _status = status;
                _statusAt = _clock();
            }
        }
        Apply(device, report, resend: true);
    }

    /// <summary>Under the hub lock: writes whatever differs from what the device holds, plus every wanted value when <paramref name="resend"/>, unless a failure is backing off.</summary>
    private void Apply(IHidDevice device, byte[] report, bool resend = false)
    {
        if (device.ProductId != ProductId)
        {
            return;
        }
        if (!ReferenceEquals(_sentFor, device))
        {
            ForgetSent(device);
        }
        int? wantPump;
        int? wantFan;
        lock (_lock)
        {
            wantPump = _wantPump;
            wantFan = _wantFan;
        }
        bool pumpDue = wantPump != _sentPump || (resend && wantPump is not null);
        bool fanDue = wantFan != _sentFan || (resend && wantFan is not null);
        if (!pumpDue && !fanDue)
        {
            return;
        }
        var now = _clock();
        if (now < _retryAtMs)
        {
            return;
        }
        bool ok = true;
        if (pumpDue)
        {
            ok = WritePump(device, report, wantPump);
            if (ok)
            {
                if ((wantPump is null) != (_sentPump is null))
                {
                    ServiceLog.Info(wantPump is not null
                        ? $"[{DeviceId}] pump driven"
                        : $"[{DeviceId}] pump handed back to motherboard sync");
                }
                _sentPump = wantPump;
            }
        }
        if (ok && fanDue)
        {
            ok = WriteFan(device, report, wantFan);
            if (ok)
            {
                _sentFan = wantFan;
            }
        }
        if (ok)
        {
            if (_backoffMs > 0)
            {
                ServiceLog.Info($"[{DeviceId}] cooling writes recovered after {_failures} failure(s)");
            }
            _backoffMs = 0;
            _retryAtMs = 0;
            _failures = 0;
            _failuresLogged = 0;
            _failureLogged = false;
        }
        else
        {
            NoteFailure(now);
        }
    }

    private void ForgetSent(IHidDevice device)
    {
        _sentFor = device;
        _sentPump = null;
        _sentFan = null;
        _backoffMs = 0;
        _retryAtMs = 0;
        _failures = 0;
        _failuresLogged = 0;
        _failureLogged = false;
    }

    private static bool WritePump(IHidDevice device, byte[] report, int? duty)
    {
        if (duty is { } d)
        {
            LianLiAioProtocol.FillHydroShiftPumpPwm(report, 0, d);
        }
        else
        {
            // Full duty rides along in case the firmware honors the PWM byte in motherboard sync.
            LianLiAioProtocol.FillHydroShiftPumpPwm(report, LianLiAioProtocol.PumpSourceMotherboard, 100);
        }
        return WriteAndDrain(device, report);
    }

    private static bool WriteFan(IHidDevice device, byte[] report, int? duty)
    {
        LianLiAioProtocol.FillHydroShiftFanPwm(report, duty ?? LianLiAioProtocol.HydroShiftFanReleaseDuty);
        return WriteAndDrain(device, report);
    }

    private static bool WriteAndDrain(IHidDevice device, byte[] report)
    {
        if (!device.Write(report))
        {
            return false;
        }
        Span<byte> ack = stackalloc byte[64];
        for (int i = 0; i < MaxAckReads; i++)
        {
            if (device.Read(ack, AckTimeoutMs) <= 0)
            {
                break;
            }
        }
        return true;
    }

    /// <summary>Gate off, shutdown or unplug: a driven pump goes back to the motherboard header and driven fans to the release duty before the handle closes.</summary>
    private void HandBackOnDetach(IHidDevice device, byte[] report)
    {
        if (device.ProductId != ProductId || !ReferenceEquals(_sentFor, device))
        {
            return;
        }
        if (_sentPump is not null && WritePump(device, report, null))
        {
            ServiceLog.Info($"[{DeviceId}] pump handed back to motherboard sync on detach");
        }
        if (_sentFan is not null && WriteFan(device, report, null))
        {
            ServiceLog.Info($"[{DeviceId}] fans parked on detach");
        }
        _sentFor = null;
        _sentPump = null;
        _sentFan = null;
    }

    private void NoteFailure(long now)
    {
        _failures++;
        _backoffMs = _backoffMs == 0
            ? HydroShiftFanLeds.InitialBackoffMs
            : Math.Min(_backoffMs * 2, HydroShiftFanLeds.MaxBackoffMs);
        _retryAtMs = now + _backoffMs;
        if (!_failureLogged)
        {
            _failureLogged = true;
            _lastFailureLogMs = now;
            _failuresLogged = _failures;
            ServiceLog.Warn($"[{DeviceId}] cooling write failed; retrying in {_backoffMs / 1000} s");
        }
        else if (now - _lastFailureLogMs >= FailureLogIntervalMs)
        {
            ServiceLog.Warn($"[{DeviceId}] cooling writes still failing: {_failures - _failuresLogged} more failure(s), next retry in {_backoffMs / 1000} s");
            _lastFailureLogMs = now;
            _failuresLogged = _failures;
        }
    }
}
