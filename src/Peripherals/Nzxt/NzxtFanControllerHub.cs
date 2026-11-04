using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Nexus.Service.Platform;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Sockets;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// Fan channels of the NZXT RGB & Fan Controller. Reads status reports on a handle the
/// worker owns and writes fixed duties from any thread. Never sends a lighting command.
/// </summary>
public sealed class NzxtFanControllerHub
{
    public const string DeviceId = "nzxt-fan-controller";
    public const string ProductName = "NZXT RGB & Fan Controller";

    // Consecutive missed status windows before the device is treated as gone. Another app
    // re-initializing the controller can silence the stream for several windows.
    public const int MaxMissedPolls = 8;

    // Distinct report ids tracked per diagnostic window; anything past it lands in the last bucket.
    private const int MaxTrackedReportKinds = 12;
    private const long LogCeilingMs = 3_600_000;

    // A connection must stay up this long before the connect and wait log counters start over,
    // so a connect/drop flap cannot log every cycle.
    private const long StableConnectionMs = 600_000;

    private readonly object _writeLock = new();
    private readonly MultiplexHub? _ws;
    private IHidDevice? _device;
    private volatile NzxtFanReading[] _fans = Array.Empty<NzxtFanReading>();
    private volatile bool _isConnected;
    private int _generation;
    private int _missedPolls;

    // Worker-thread diagnostics: report counts since the last status report, by first two bytes.
    private readonly Dictionary<int, int> _reportCounts = new();
    private int _emptyReads;
    private int _readErrors;
    private long _openedAtMs;
    private long _lastStatusMs;
    private int _initResends;
    private readonly LogBackoff _giveUpLog = new();
    private readonly LogBackoff _waitLog = new();
    private readonly LogBackoff _readErrorLog = new();
    private readonly LogBackoff _cycleLog = new();
    private long _connectedAtMs;
    private long _lastUptimeMs;

    public NzxtFanControllerHub(MultiplexHub? ws = null)
    {
        _ws = ws;
    }

    internal int StatusTimeoutMs { get; set; } = 1500;

    /// <summary>True when the last failure was a read error, not silence. Worker thread only.</summary>
    public bool LastReadFailed => _readErrors > 0;

    /// <summary>True once <see cref="MaxMissedPolls"/> status windows in a row passed with no report.</summary>
    public bool Lost => Volatile.Read(ref _missedPolls) >= MaxMissedPolls;

    /// <summary>Latest status per channel; empty while disconnected. The array is replaced whole, never mutated.</summary>
    public NzxtFanReading[] Fans => _fans;

    public bool IsConnected => _isConnected;

    /// <summary>Whether the connect that just succeeded should log its connected and disconnected lines. Worker thread only.</summary>
    public bool LogConnectionCycle { get; private set; } = true;

    /// <summary>Changes on every successful connect, so a holder of per-connection state can tell a reconnect happened.</summary>
    public int Generation => Volatile.Read(ref _generation);

    public void Attach(IHidDevice device)
    {
        lock (_writeLock)
        {
            _device?.Dispose();
            _device = device;
        }
        _openedAtMs = Environment.TickCount64;
        ResetDiagnostics();
        _initResends = 0;
    }

    /// <summary>
    /// Starts the status stream and waits for the first report on the same handle, re-sending the
    /// init each silent window. A handle that reads fine but stays silent is waited on until the
    /// token cancels or <paramref name="isEnabled"/> turns false; a read error or failed write ends it.
    /// Worker thread only.
    /// </summary>
    public bool Connect(CancellationToken ct = default, Func<bool>? isEnabled = null)
    {
        for (int window = 0; ; window++)
        {
            if (ct.IsCancellationRequested || (isEnabled != null && !isEnabled()))
            {
                return false;
            }
            IHidDevice? device;
            lock (_writeLock)
            {
                device = _device;
                if (device == null
                    || !device.Write(NzxtFanControllerProtocol.EncodeSetStatusInterval())
                    || !device.Write(NzxtFanControllerProtocol.EncodeStartReporting()))
                {
                    LogGiveUp("init write failed", window);
                    return false;
                }
            }
            if (window > 0)
            {
                _initResends++;
            }
            if (ReadStatus(device))
            {
                Interlocked.Increment(ref _generation);
                Volatile.Write(ref _missedPolls, 0);
                _isConnected = true;
                if (_lastUptimeMs >= StableConnectionMs || _connectedAtMs == 0)
                {
                    _giveUpLog.Reset();
                    _waitLog.Reset();
                    _readErrorLog.Reset();
                    _cycleLog.Reset();
                }
                _connectedAtMs = Environment.TickCount64;
                LogConnectionCycle = _cycleLog.ShouldLog();
                if (LogConnectionCycle)
                {
                    ServiceLog.Info($"[nzxt-fan-controller] first status {Environment.TickCount64 - _openedAtMs} ms after open, {_initResends} init resend(s)");
                }
                if (_ws != null)
                {
                    PanelTopics.BroadcastCooling(_ws);
                }
                return true;
            }
            if (_readErrors > 0)
            {
                LogGiveUp("read error", window + 1);
                return false;
            }
            if (_waitLog.ShouldLog())
            {
                ServiceLog.Info($"[nzxt-fan-controller] still waiting for the first status after {window + 1} window(s), {Environment.TickCount64 - _openedAtMs} ms since open; {DescribeSilence()}");
            }
        }
    }

    /// <summary>One-line account of what the handle delivered since the last status report, for a detach or give-up log.</summary>
    public string DescribeSilence()
    {
        var sb = new StringBuilder();
        foreach (var (key, count) in _reportCounts)
        {
            sb.Append(key < 0 ? "other" : $"{key >> 8:X2} {key & 0xFF:X2}").Append(" x").Append(count).Append(", ");
        }
        if (sb.Length == 0)
        {
            sb.Append("no reports, ");
        }
        long sinceStatus = _lastStatusMs == 0 ? -1 : Environment.TickCount64 - _lastStatusMs;
        sb.Append($"empty reads {_emptyReads}, read errors {_readErrors}, ");
        sb.Append(sinceStatus < 0 ? "no 67 02 yet" : $"{sinceStatus} ms since last 67 02");
        return sb.ToString();
    }

    private void LogGiveUp(string cause, int windows)
    {
        if (_giveUpLog.ShouldLog())
        {
            ServiceLog.Warn($"[nzxt-fan-controller] connect gave up ({cause}) after {windows} window(s), {_initResends} init resend(s), attempt {_giveUpLog.Count}; {DescribeSilence()}");
        }
    }

    private void ResetDiagnostics()
    {
        if (_reportCounts.Count > 0)
        {
            _reportCounts.Clear();
        }
        _emptyReads = 0;
        _readErrors = 0;
    }

    /// <summary>
    /// Waits for the next status report. False means none arrived or the device is gone; a miss
    /// re-sends the init and start-reporting commands, guarding against the stream having stopped.
    /// Worker thread only.
    /// </summary>
    public bool Poll()
    {
        IHidDevice? device;
        lock (_writeLock)
        {
            device = _device;
        }
        if (device != null && ReadStatus(device))
        {
            Volatile.Write(ref _missedPolls, 0);
            return true;
        }
        Interlocked.Increment(ref _missedPolls);
        if (_readErrors > 0)
        {
            // A failing handle will not recover by re-sending; let the worker detach and reopen.
            Volatile.Write(ref _missedPolls, MaxMissedPolls);
            return false;
        }
        lock (_writeLock)
        {
            if (_device == device)
            {
                if (device != null
                    && device.Write(NzxtFanControllerProtocol.EncodeSetStatusInterval()))
                {
                    device.Write(NzxtFanControllerProtocol.EncodeStartReporting());
                }
            }
        }
        return false;
    }

    public void Detach()
    {
        bool wasConnected;
        lock (_writeLock)
        {
            wasConnected = _isConnected;
            if (wasConnected)
            {
                _lastUptimeMs = Environment.TickCount64 - _connectedAtMs;
            }
            _isConnected = false;
            _fans = Array.Empty<NzxtFanReading>();
            _device?.Dispose();
            _device = null;
        }
        if (wasConnected && _ws != null)
        {
            PanelTopics.BroadcastCooling(_ws);
        }
    }

    public bool SetFanDuty(int channel, int dutyPercent)
    {
        if ((uint)channel >= NzxtFanControllerProtocol.FanChannelCount)
        {
            return false;
        }
        lock (_writeLock)
        {
            return _device != null
                && _device.Write(NzxtFanControllerProtocol.EncodeFixedDuty(channel, dutyPercent));
        }
    }

    private bool ReadStatus(IHidDevice device)
    {
        var buf = new byte[NzxtFanControllerProtocol.ReportLength];
        var deadline = Environment.TickCount64 + StatusTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            int n = device.Read(buf, StatusTimeoutMs);
            if (n < 0)
            {
                _readErrors++;
                if (_readErrorLog.ShouldLog())
                {
                    ServiceLog.Warn($"[nzxt-fan-controller] hid read returned {n}");
                }
                return false;
            }
            if (n == 0)
            {
                _emptyReads++;
                continue;
            }
            if (NzxtFanControllerProtocol.TryDecodeStatus(buf.AsSpan(0, n), out var fans))
            {
                _fans = fans;
                _lastStatusMs = Environment.TickCount64;
                ResetDiagnostics();
                return true;
            }
            CountReport(buf, n);
        }
        return false;
    }

    private void CountReport(byte[] buf, int n)
    {
        int key = n >= 2 ? (buf[0] << 8) | buf[1] : buf[0] << 8;
        if (!_reportCounts.ContainsKey(key) && _reportCounts.Count >= MaxTrackedReportKinds)
        {
            key = -1;
        }
        _reportCounts[key] = _reportCounts.GetValueOrDefault(key) + 1;
    }

    /// <summary>Logs on occurrence 1, 2, 4, 8, ... and at least once per ceiling interval.</summary>
    private sealed class LogBackoff
    {
        private int _next = 1;
        private long _lastMs;

        public int Count { get; private set; }

        public bool ShouldLog()
        {
            Count++;
            long now = Environment.TickCount64;
            if (Count >= _next || now - _lastMs >= LogCeilingMs)
            {
                if (Count >= _next)
                {
                    _next = Count > int.MaxValue / 2 ? int.MaxValue : Count * 2;
                }
                _lastMs = now;
                return true;
            }
            return false;
        }

        public void Reset()
        {
            Count = 0;
            _next = 1;
            _lastMs = 0;
        }
    }
}
