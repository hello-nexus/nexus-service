using System;
using System.Threading;
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

    private readonly object _writeLock = new();
    private readonly MultiplexHub? _ws;
    private IHidDevice? _device;
    private volatile NzxtFanReading[] _fans = Array.Empty<NzxtFanReading>();
    private volatile bool _isConnected;
    private int _generation;
    private int _missedPolls;

    public NzxtFanControllerHub(MultiplexHub? ws = null)
    {
        _ws = ws;
    }

    internal int StatusTimeoutMs { get; set; } = 1500;

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
    }

    /// <summary>Starts the status stream and waits for the first report. Worker thread only.</summary>
    public bool Connect()
    {
        IHidDevice? device;
        lock (_writeLock)
        {
            device = _device;
            if (device == null
                || !device.Write(NzxtFanControllerProtocol.EncodeSetStatusInterval())
                || !device.Write(NzxtFanControllerProtocol.EncodeStartReporting()))
            {
                return false;
            }
        }
        if (!ReadStatus(device))
        {
            return false;
        }
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _missedPolls, 0);
        _isConnected = true;
        if (_ws != null)
        {
            PanelTopics.BroadcastCooling(_ws);
        }
        return true;
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
                return false;
            }
            if (n > 0 && NzxtFanControllerProtocol.TryDecodeStatus(buf.AsSpan(0, n), out var fans))
            {
                _fans = fans;
                return true;
            }
        }
        return false;
    }
}
