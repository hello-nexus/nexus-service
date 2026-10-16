using System;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// The HydroShift LCD 360R's three ARGB fans, driven per LED over the panel's own HID handle.
/// Every write goes through the hub's exchange, so it is serialized with frame chunks and the
/// session keepalive and cannot run before the attach (settle included) has finished.
/// Only the 360R carries lit fans: the 360S ships plain fans and the 360TL lights through its hub.
/// </summary>
public sealed class HydroShiftFanLeds
{
    public const int ProductId360R = 0x7399;

    /// <summary>Drains acks queued behind a write; short, because the hub lock is held and frames wait on it.</summary>
    private const int AckTimeoutMs = 2;

    private const int MaxAckReads = 4;

    /// <summary>First wait after a failed write; doubles per failure up to <see cref="MaxBackoffMs"/>.</summary>
    internal const long InitialBackoffMs = 1000;

    internal const long MaxBackoffMs = 60_000;

    private const long FailureLogIntervalMs = 30_000;

    private readonly JpegPanelHub _hub;
    private readonly Func<long> _clock;

    // Touched under the hub lock (Write) and on the poller (Poll); the device reference is
    // what ties priming to one attach, so a re-attach primes again.
    private IHidDevice? _primedFor;
    private bool _connected;
    private long _backoffMs;
    private long _retryAtMs;
    private long _failures;
    private long _failuresLogged;
    private long _lastFailureLogMs = long.MinValue / 2;
    private bool _failureLogged;

    public HydroShiftFanLeds(JpegPanelHub hub, Func<long>? clock = null)
    {
        _hub = hub;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public string DeviceId => _hub.Model.HandlerId;

    public bool IsConnected => _hub.IsConnected && _hub.AttachedProductId == ProductId360R;

    /// <summary>Raised from <see cref="Poll"/> when the 360R attaches or detaches.</summary>
    public event Action? ConnectionChanged;

    /// <summary>Edge-detects the connection for the lighting provider's device list.</summary>
    public void Poll()
    {
        var now = IsConnected;
        if (now == _connected)
        {
            return;
        }
        _connected = now;
        if (!now)
        {
            _hub.Exchange((_, _) => { _primedFor = null; return true; }, false);
            _backoffMs = 0;
            _retryAtMs = 0;
            _failures = 0;
            _failuresLogged = 0;
            _failureLogged = false;
        }
        ConnectionChanged?.Invoke();
    }

    /// <summary>
    /// Sends one color per LED (RGB triples, fan order). False when no 360R is attached, a write fails,
    /// or a recent failure is still backing off; a failure drops the prime and waits longer each time
    /// before the LED path is brought up again, so a firmware that refuses it cannot hold the hub lock
    /// on every tick. The hid layer has no per-call write timeout, so a single stalled write still
    /// holds the lock for the transport's own timeout.
    /// </summary>
    public bool Write(ReadOnlySpan<byte> rgb)
    {
        var now = _clock();
        if (now < _retryAtMs)
        {
            return false;
        }
        // Spans cannot cross the exchange lambda, so the colors travel as a copy.
        var colors = rgb.ToArray();
        bool attached = false;
        bool ok = _hub.Exchange((device, report) =>
        {
            if (device.ProductId != ProductId360R)
            {
                return false;
            }
            attached = true;
            if (!ReferenceEquals(_primedFor, device))
            {
                for (int i = 0; i < LianLiAioProtocol.PrimePacketCount; i++)
                {
                    LianLiAioProtocol.FillFanLedPrime(report, i);
                    if (!device.Write(report))
                    {
                        return false;
                    }
                    Drain(device);
                }
                _primedFor = device;
                if (!_failureLogged)
                {
                    ServiceLog.Info($"[{DeviceId}] fan LED path primed; the pump ring prime (static, black) was sent with it");
                }
            }
            LianLiAioProtocol.FillPerLed(report, colors);
            if (!device.Write(report))
            {
                _primedFor = null;
                return false;
            }
            Drain(device);
            return true;
        }, false);
        if (ok)
        {
            if (_backoffMs > 0)
            {
                ServiceLog.Info($"[{DeviceId}] fan LED writes recovered after {_failures} failure(s)");
            }
            _backoffMs = 0;
            _retryAtMs = 0;
            _failures = 0;
            _failuresLogged = 0;
            _failureLogged = false;
        }
        else if (attached)
        {
            NoteFailure(now);
        }
        return ok;
    }

    private void NoteFailure(long now)
    {
        _failures++;
        _backoffMs = _backoffMs == 0 ? InitialBackoffMs : Math.Min(_backoffMs * 2, MaxBackoffMs);
        _retryAtMs = now + _backoffMs;
        if (!_failureLogged)
        {
            _failureLogged = true;
            _lastFailureLogMs = now;
            _failuresLogged = _failures;
            ServiceLog.Warn($"[{DeviceId}] fan LED write failed; retrying in {_backoffMs / 1000} s");
        }
        else if (now - _lastFailureLogMs >= FailureLogIntervalMs)
        {
            ServiceLog.Warn($"[{DeviceId}] fan LED writes still failing: {_failures - _failuresLogged} more failure(s), next retry in {_backoffMs / 1000} s");
            _lastFailureLogMs = now;
            _failuresLogged = _failures;
        }
    }

    // The frame path drains one ack per frame; a control write drains its own so the IN
    // endpoint cannot back up until the panel stops taking output reports.
    private static void Drain(IHidDevice device)
    {
        Span<byte> ack = stackalloc byte[64];
        for (int i = 0; i < MaxAckReads; i++)
        {
            if (device.Read(ack, AckTimeoutMs) <= 0)
            {
                return;
            }
        }
    }
}
