using System;
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
/// whether or not the bulk side ever opens; writes take <see cref="RyujinProtocol.HidWriteGate"/>
/// so a duty report never lands between the LCD's upload and its commit.
/// </summary>
public sealed class RyujinCoolingHub : IDisposable
{
    private const int ReplyTimeoutMs = 200;
    private const int MaxRepliesPerRequest = 4;

    private readonly object _lock = new();
    private IHidDevice? _device;
    private volatile bool _connected;
    private volatile RyujinSnapshot _snapshot = RyujinSnapshot.Empty;

    public bool IsConnected => _connected;

    public RyujinSnapshot Snapshot => _snapshot;

    public void Attach(IHidDevice device)
    {
        lock (_lock)
        {
            _device?.Dispose();
            _device = device;
            _snapshot = RyujinSnapshot.Empty;
            _connected = true;
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            _connected = false;
            _snapshot = RyujinSnapshot.Empty;
            _device?.Dispose();
            _device = null;
        }
    }

    /// <summary>Writes both duties in one report; the cooler takes no single-channel form.</summary>
    public bool SetDuty(int pumpPercent, int fanPercent)
    {
        var report = RyujinCoolingProtocol.EncodeSetDuty(pumpPercent, fanPercent);
        lock (_lock)
        {
            if (_device is null || !WriteGated(_device, report))
            {
                return false;
            }
            var prev = _snapshot;
            _snapshot = prev with
            {
                PumpDuty = RyujinCoolingProtocol.ClampPump(pumpPercent),
                FanDuty = RyujinCoolingProtocol.ClampFan(fanPercent),
            };
            return true;
        }
    }

    /// <summary>
    /// One status request and one duty request, each with its reply read under the same lock.
    /// False only when a write fails or the device reads as gone; a silent device is not a failure.
    /// </summary>
    public bool Poll()
    {
        lock (_lock)
        {
            if (_device is null)
            {
                return false;
            }
            if (!WriteGated(_device, RyujinCoolingProtocol.EncodeStatusRequest()))
            {
                return false;
            }
            if (!ReadReplies(_device, status: true))
            {
                return false;
            }
            if (!WriteGated(_device, RyujinCoolingProtocol.EncodeDutyRequest()))
            {
                return false;
            }
            return ReadReplies(_device, status: false);
        }
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
            var prev = _snapshot;
            if (status && RyujinCoolingProtocol.TryParseStatus(report, out var temp, out var pump, out var fan))
            {
                _snapshot = prev with { LiquidTempC = temp, PumpRpm = pump, FanRpm = fan, StatusFresh = true };
                return true;
            }
            if (!status && RyujinCoolingProtocol.TryParseDuty(report, out var pumpDuty, out var fanDuty))
            {
                _snapshot = prev with { PumpDuty = pumpDuty, FanDuty = fanDuty };
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
