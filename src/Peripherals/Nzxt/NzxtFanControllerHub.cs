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

    // Status windows Connect keeps asking on one handle before giving up. Reopening resets nothing
    // on the device, and customers saw the first report take 22 s or more, so this is sized to
    // cover the common slow start while still bounding a handle that never answers.
    public const int MaxConnectWindows = 10;

    // Distinct report ids tracked per diagnostic window; anything past it lands in the last bucket.
    private const int MaxTrackedReportKinds = 12;
    private const long ReadErrorLogIntervalMs = 30_000;

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
    private long _lastReadErrorLogMs;
    private int _initResends;
    private int _connectGiveUps;

    public NzxtFanControllerHub(MultiplexHub? ws = null)
    {
        _ws = ws;
    }

    internal int StatusTimeoutMs { get; set; } = 1500;

    internal int ConnectWindows { get; set; } = MaxConnectWindows;

    /// <summary>True when the last failure was a read error, not silence. Worker thread only.</summary>
    public bool LastReadFailed => _readErrors > 0;

    /// <summary>True once <see cref="MaxMissedPolls"/> status windows in a row passed with no report.</summary>
    public bool Lost => Volatile.Read(ref _missedPolls) >= MaxMissedPolls;

    /// <summary>Latest status per channel; empty while disconnected. The array is replaced whole, never mutated.</summary>
    public NzxtFanReading[] Fans => _fans;

    public bool IsConnected => _isConnected;

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
    /// init each silent window for up to <see cref="ConnectWindows"/> windows. Worker thread only.
    /// </summary>
    public bool Connect()
    {
        for (int window = 0; window < ConnectWindows; window++)
        {
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
                ServiceLog.Info($"[nzxt-fan-controller] first status {Environment.TickCount64 - _openedAtMs} ms after open, {_initResends} init resend(s)");
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
        }
        LogGiveUp("no status report", ConnectWindows);
        return false;
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
        _connectGiveUps++;
        // First few attempts in full, then every tenth, so a handle that never answers does not flood the log.
        if (_connectGiveUps <= 3 || _connectGiveUps % 10 == 0)
        {
            ServiceLog.Warn($"[nzxt-fan-controller] connect gave up ({cause}) after {windows} window(s), {_initResends} init resend(s), attempt {_connectGiveUps}; {DescribeSilence()}");
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
                long now = Environment.TickCount64;
                if (_lastReadErrorLogMs == 0 || now - _lastReadErrorLogMs >= ReadErrorLogIntervalMs)
                {
                    _lastReadErrorLogMs = now;
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
}
