using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

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
    private const int MaxDrainedReports = 16;
    private const long ParseLogIntervalMs = 60_000;

    /// <summary>Status older than this many keepalive cycles no longer counts as a live reading.</summary>
    private const int StatusStaleCycles = 3;

    private readonly object _stateLock = new();
    private IHidDevice? _device;
    private volatile bool _connected;
    private volatile RyujinSnapshot _snapshot = RyujinSnapshot.Empty;
    private long _statusAtMs;
    private int? _wantPump;
    private int? _wantFan;
    private string? _wantReason;
    private bool _dutySeen;
    private bool _statusSeen;
    private bool _fallback;
    private int _pollsWithoutDuty;
    private long _lastParseLogMs = long.MinValue;

    public const string KeepaliveReason = "keepalive";

    internal Action<string> Info { get; set; } = ServiceLog.Info;
    internal Action<string> Warn { get; set; } = ServiceLog.Warn;

    /// <summary>Released when a duty is waiting to be written, so the worker sends it at once.</summary>
    public SemaphoreSlim Wake { get; } = new(0);

    internal Func<long> NowMs { get; set; } = () => Environment.TickCount64;

    public bool IsConnected => _connected;

    /// <summary>USB serial of the attached cooler, or null.</summary>
    public string? Serial { get { lock (_stateLock) { return _device?.Serial; } } }

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
            ResetSession();
            _connected = true;
        }
    }

    private void ResetSession()
    {
        _wantPump = null;
        _wantFan = null;
        _wantReason = null;
        _dutySeen = false;
        _statusSeen = false;
        _fallback = false;
        _pollsWithoutDuty = 0;
    }

    public void Detach()
    {
        lock (_stateLock)
        {
            _connected = false;
            _snapshot = RyujinSnapshot.Empty;
            ResetSession();
            _device?.Dispose();
            _device = null;
        }
    }

    /// <summary>
    /// Records the duty wanted for the channels given; a channel left null keeps the duty the
    /// cooler last reported or was last sent. False while disconnected.
    /// </summary>
    public bool Request(int? pumpPercent, int? fanPercent, string reason)
    {
        lock (_stateLock)
        {
            if (_device is null)
            {
                return false;
            }
            if (pumpPercent is { } p)
            {
                _wantPump = RyujinCoolingProtocol.ClampPump(p);
            }
            if (fanPercent is { } f)
            {
                _wantFan = RyujinCoolingProtocol.ClampFan(f);
            }
            if (_wantReason is null || reason != KeepaliveReason)
            {
                _wantReason = reason;
            }
        }
        Wake.Release();
        return true;
    }

    /// <summary>
    /// Writes the wanted duty if one is waiting and both channels are known. A channel nobody is
    /// driving and the cooler has not reported waits for its first duty reply; the safe duty is
    /// never written for it. Worker thread only.
    /// </summary>
    public bool Flush()
    {
        IHidDevice? device;
        int pump;
        int fan;
        int? oldPump;
        int? oldFan;
        string reason;
        lock (_stateLock)
        {
            if (_device is null)
            {
                return false;
            }
            if (_wantPump is null && _wantFan is null)
            {
                return true;
            }
            var snap = _snapshot;
            int? knownPump = snap.PumpDuty >= 0 ? snap.PumpDuty : _fallback ? RyujinCoolingProtocol.FallbackDuty : null;
            int? knownFan = snap.FanDuty >= 0 ? snap.FanDuty : _fallback ? RyujinCoolingProtocol.FallbackDuty : null;
            if ((_wantPump ?? knownPump) is not { } p || (_wantFan ?? knownFan) is not { } f)
            {
                return true;
            }
            pump = p;
            fan = f;
            oldPump = snap.PumpDuty >= 0 ? snap.PumpDuty : null;
            oldFan = snap.FanDuty >= 0 ? snap.FanDuty : null;
            reason = _wantReason ?? KeepaliveReason;
            device = _device;
            _wantPump = null;
            _wantFan = null;
            _wantReason = null;
        }
        if (!WriteGated(device, RyujinCoolingProtocol.EncodeSetDuty(pump, fan)))
        {
            lock (_stateLock)
            {
                _wantPump ??= pump;
                _wantFan ??= fan;
                _wantReason ??= reason;
            }
            return false;
        }
        lock (_stateLock)
        {
            _snapshot = _snapshot with { PumpDuty = pump, FanDuty = fan };
        }
        if (oldPump != pump || oldFan != fan)
        {
            Info($"[asus-ryujin-cooling] duty pump {Show(oldPump)} -> {pump}, fan {Show(oldFan)} -> {fan} ({reason})");
        }
        return true;
    }

    /// <summary>
    /// One status request and one duty request, each with its reply read outside any lock.
    /// False only when a write fails or the device reads as gone; a silent device is not a failure.
    /// Worker thread only.
    /// </summary>
    public bool Poll(Func<IReadOnlyList<byte[]>>? sensorFrames = null)
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
        if (!WriteGated(device, RyujinCoolingProtocol.EncodeStatusRequest())
            || !ReadReplies(device, status: true)
            || !SendAll(device, sensorFrames?.Invoke())
            || !WriteGated(device, RyujinCoolingProtocol.EncodeDutyRequest())
            || !ReadReplies(device, status: false))
        {
            return false;
        }
        lock (_stateLock)
        {
            if (!_dutySeen && !_fallback && ++_pollsWithoutDuty >= RyujinCoolingProtocol.DutyReplyGraceCycles)
            {
                _fallback = true;
                Warn($"[asus-ryujin-cooling] no duty reply parsed in {_pollsWithoutDuty} polls; an undriven channel will ride at {RyujinCoolingProtocol.FallbackDuty}%");
            }
        }
        return true;
    }

    /// <summary>
    /// Writes display reports in order, pausing where Armoury Crate does, then drops anything the
    /// device answered so a stale reply cannot be mistaken for the next poll's. Worker thread only.
    /// </summary>
    public bool Send(IReadOnlyList<byte[]> frames)
    {
        IHidDevice? device;
        lock (_stateLock)
        {
            device = _device;
        }
        if (device is null || !SendAll(device, frames))
        {
            return false;
        }
        Span<byte> sink = stackalloc byte[RyujinCoolingProtocol.ReportLength];
        for (int i = 0; i < MaxDrainedReports && device.Read(sink, 0) > 0; i++)
        {
        }
        return true;
    }

    private static bool SendAll(IHidDevice device, IReadOnlyList<byte[]>? frames)
    {
        if (frames is null)
        {
            return true;
        }
        foreach (var frame in frames)
        {
            if (!WriteGated(device, frame))
            {
                return false;
            }
            int gap = RyujinMonitorProtocol.GapAfterMs(frame[1]);
            if (gap > 0)
            {
                Thread.Sleep(gap);
            }
        }
        return true;
    }

    private static string Show(int? duty) => duty?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";

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
                bool first;
                lock (_stateLock)
                {
                    _snapshot = _snapshot with { LiquidTempC = temp, PumpRpm = pump, FanRpm = fan, StatusFresh = true };
                    _statusAtMs = NowMs();
                    first = !_statusSeen;
                    _statusSeen = true;
                }
                if (first)
                {
                    Info($"[asus-ryujin-cooling] first status reply: liquid {temp:0.0} C, pump {pump} rpm, fan {fan} rpm, raw {Hex(report)}");
                }
                return true;
            }
            if (!status && RyujinCoolingProtocol.TryParseDuty(report, out var pumpDuty, out var fanDuty))
            {
                bool first;
                lock (_stateLock)
                {
                    // A duty waiting to be written is newer than what the cooler still reports.
                    if (_wantPump is null && _wantFan is null)
                    {
                        _snapshot = _snapshot with { PumpDuty = pumpDuty, FanDuty = fanDuty };
                    }
                    else
                    {
                        _snapshot = _snapshot with
                        {
                            PumpDuty = _snapshot.PumpDuty >= 0 ? _snapshot.PumpDuty : pumpDuty,
                            FanDuty = _snapshot.FanDuty >= 0 ? _snapshot.FanDuty : fanDuty,
                        };
                    }
                    first = !_dutySeen;
                    _dutySeen = true;
                }
                if (first)
                {
                    Info($"[asus-ryujin-cooling] first duty reply: pump {pumpDuty}%, fan {fanDuty}%, raw {Hex(report)}");
                }
                return true;
            }
            LogUnparsed(report, status);
        }
        return true;
    }

    private void LogUnparsed(ReadOnlySpan<byte> report, bool status)
    {
        long now = NowMs();
        lock (_stateLock)
        {
            if (_lastParseLogMs != long.MinValue && now - _lastParseLogMs < ParseLogIntervalMs)
            {
                return;
            }
            _lastParseLogMs = now;
        }
        Warn($"[asus-ryujin-cooling] unparsed {(status ? "status" : "duty")} reply, raw {Hex(report)}");
    }

    private static string Hex(ReadOnlySpan<byte> report) => Convert.ToHexString(report[..Math.Min(report.Length, 24)]);

    private static bool WriteGated(IHidDevice device, byte[] report)
    {
        lock (RyujinProtocol.HidWriteGate)
        {
            return device.Write(report);
        }
    }

    public void Dispose() => Detach();
}
