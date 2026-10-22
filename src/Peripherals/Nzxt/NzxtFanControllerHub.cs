using System;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// Fan channels of the NZXT RGB & Fan Controller. Reads status reports on a handle the
/// worker owns and writes fixed duties from any thread. Never sends a lighting command.
/// </summary>
public sealed class NzxtFanControllerHub
{
    public const string DeviceId = "nzxt-fan-controller";
    public const string ProductName = "NZXT RGB & Fan Controller";

    private const int StatusTimeoutMs = 1500;

    private readonly object _writeLock = new();
    private IHidDevice? _device;
    private volatile NzxtFanReading[] _fans = Array.Empty<NzxtFanReading>();
    private volatile bool _isConnected;

    /// <summary>Latest status per channel; empty while disconnected. The array is replaced whole, never mutated.</summary>
    public NzxtFanReading[] Fans => _fans;

    public bool IsConnected => _isConnected;

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
        _isConnected = true;
        return true;
    }

    /// <summary>Waits for the next status report. False means none arrived or the device is gone. Worker thread only.</summary>
    public bool Poll()
    {
        IHidDevice? device;
        lock (_writeLock)
        {
            device = _device;
        }
        return device != null && ReadStatus(device);
    }

    public void Detach()
    {
        lock (_writeLock)
        {
            _isConnected = false;
            _fans = Array.Empty<NzxtFanReading>();
            _device?.Dispose();
            _device = null;
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
