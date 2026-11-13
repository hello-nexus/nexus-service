using System;
using System.Threading;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.Ryujin;

/// <summary>What the cooler last reported; duties are -1 until a duty reply has parsed.</summary>
public sealed record RyujinSnapshot(double LiquidTempC, int PumpRpm, int FanRpm, int PumpDuty, int FanDuty, bool StatusFresh)
{
    public static readonly RyujinSnapshot Empty = new(0, 0, 0, -1, -1, false);
}

/// <summary>
/// Owns the cooler's own HID handle. It is separate from the LCD's handle so pump and fan work
/// whether or not the bulk side ever opens. Callers only record the duty they want; the worker
/// thread is the one writer and the one reader, so no caller waits on the LCD's write gate or on
/// a device read. Writes take <see cref="RyujinProtocol.HidWriteGate"/> so a duty report never
/// lands between the LCD's upload and its commit.
/// </summary>
public sealed class RyujinCoolingHub : IDisposable
{
    private const int ReplyTimeoutMs = 200;
    private const int MaxRepliesPerRequest = 4;

    /// <summary>Status older than this many keepalive cycles no longer counts as a live reading.</summary>
    private const int StatusStaleCycles = 3;

    private readonly object _stateLock = new();
    private IHidDevice? _device;
    private volatile bool _connected;
    private volatile RyujinSnapshot _snapshot = RyujinSnapshot.Empty;
    private long _statusAtMs;
    private (int Pump, int Fan)? _pending;

    /// <summary>Released when a duty is waiting to be written, so the worker sends it at once.</summary>
    public SemaphoreSlim Wake { get; } = new(0);

    internal Func<long> NowMs { get; set; } = () => Environment.TickCount64;

    public bool IsConnected => _connected;

    public RyujinSnapshot Snapshot => _snapshot;

    /// <summary>True while the last status reply is recent enough to trust the temperature and speeds.</summary>
    public bool StatusFresh
    {
        get
        {
            var snap = _snapshot;
            long at;
            lock (_stateLock)
            {
                at = _statusAtMs;
            }
            return snap.StatusFresh && NowMs() - at < (long)StatusStaleCycles * RyujinCoolingProtocol.KeepaliveMs;
        }
    }

    public void Attach(IHidDevice device)
    {
        lock (_stateLock)
        {
            _device?.Dispose();
            _device = device;
            _snapshot = RyujinSnapshot.Empty;
            _pending = null;
            _connected = true;
        }
    }

    public void Detach()
    {
        lock (_stateLock)
        {
            _connected = false;
            _snapshot = RyujinSnapshot.Empty;
            _pending = null;
            _device?.Dispose();
            _device = null;
        }
    }

    /// <summary>Records both duties for the worker to write; the cooler takes no single-channel form. False while disconnected.</summary>
    public bool SetDuty(int pumpPercent, int fanPercent)
    {
        int pump = RyujinCoolingProtocol.ClampPump(pumpPercent);
        int fan = RyujinCoolingProtocol.ClampFan(fanPercent);
        lock (_stateLock)
        {
            if (_device is null)
            {
                return false;
            }
            _pending = (pump, fan);
            _snapshot = _snapshot with { PumpDuty = pump, FanDuty = fan };
        }
        Wake.Release();
        return true;
    }

    /// <summary>Writes the recorded duty if one is waiting. Worker thread only.</summary>
    public bool Flush()
    {
        IHidDevice? device;
        (int Pump, int Fan) duty;
        lock (_stateLock)
        {
            if (_device is null)
            {
                return false;
            }
            if (_pending is not { } pending)
            {
                return true;
            }
            device = _device;
            duty = pending;
            _pending = null;
        }
        if (WriteGated(device, RyujinCoolingProtocol.EncodeSetDuty(duty.Pump, duty.Fan)))
        {
            return true;
        }
        lock (_stateLock)
        {
            _pending ??= duty;
        }
        return false;
    }

    /// <summary>
    /// One status request and one duty request, each with its reply read outside any lock.
    /// False only when a write fails or the device reads as gone; a silent device is not a failure.
    /// Worker thread only.
    /// </summary>
    public bool Poll()
    {
        IHidDevice? device;
        lock (_stateLock)
        {
            device = _device;
        }
        if (device is null)
        {
            return false;
        }
        return WriteGated(device, RyujinCoolingProtocol.EncodeStatusRequest())
            && ReadReplies(device, status: true)
            && WriteGated(device, RyujinCoolingProtocol.EncodeDutyRequest())
            && ReadReplies(device, status: false);
    }

    private bool ReadReplies(IHidDevice device, bool status)
    {
        Span<byte> buffer = stackalloc byte[RyujinCoolingProtocol.ReportLength];
        for (int i = 0; i < MaxRepliesPerRequest; i++)
        {
            int n = device.Read(buffer, ReplyTimeoutMs);
            if (n < 0)
            {
                return false;
            }
            if (n == 0)
            {
                return true;
            }
            var report = buffer[..n];
            if (status && RyujinCoolingProtocol.TryParseStatus(report, out var temp, out var pump, out var fan))
            {
                lock (_stateLock)
                {
                    _snapshot = _snapshot with { LiquidTempC = temp, PumpRpm = pump, FanRpm = fan, StatusFresh = true };
                    _statusAtMs = NowMs();
                }
                return true;
            }
            if (!status && RyujinCoolingProtocol.TryParseDuty(report, out var pumpDuty, out var fanDuty))
            {
                lock (_stateLock)
                {
                    // A duty waiting to be written is newer than what the cooler still reports.
                    if (_pending is null)
                    {
                        _snapshot = _snapshot with { PumpDuty = pumpDuty, FanDuty = fanDuty };
                    }
                }
                return true;
            }
        }
        return true;
    }

    private static bool WriteGated(IHidDevice device, byte[] report)
    {
        lock (RyujinProtocol.HidWriteGate)
        {
            return device.Write(report);
        }
    }

    public void Dispose() => Detach();
}
