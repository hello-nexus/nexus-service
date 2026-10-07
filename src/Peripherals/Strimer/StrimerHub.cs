using System;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.Strimer;

public sealed class StrimerHub : IDisposable
{
    private readonly object _lock = new();
    private readonly byte[] _colorReport = new byte[StrimerProtocol.OutputReportSize];
    private readonly byte[] _cmdReport   = new byte[StrimerProtocol.OutputReportSize];
    private IHidDevice? _device;
    private bool _disposed;
    private int _consecutiveWriteFailures;

    public StrimerState State { get; } = new();

    public bool IsConnected => State.IsConnected;

    // Incremented each time a Write call returns false; reset to 0 on success.
    // The connection worker detaches once this exceeds its threshold.
    public int ConsecutiveWriteFailures => _consecutiveWriteFailures;

    public void Attach(IHidDevice device)
    {
        lock (_lock)
        {
            _device = device;
            State.IsConnected = true;
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            _device?.Dispose();
            _device = null;
            State.IsConnected = false;
            _consecutiveWriteFailures = 0;
        }
    }

    public bool SendColorData(int zone, ReadOnlySpan<byte> leds)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            StrimerProtocol.WriteColorData(_colorReport, zone, leds);
            var ok = _device.Write(_colorReport);
            TrackWrite(ok);
            return ok;
        }
    }

    public bool SendEffectCommit(int zone, byte mode, byte speed, byte dir, byte brightness)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            return WriteCommand(StrimerProtocol.BuildEffectCommit(zone, mode, speed, dir, brightness));
        }
    }

    /// <summary>
    /// Latches the frame. The zone mask has to match the attached GPU harness, so the caller
    /// passes the count it actually drove rather than this assuming all twelve zones exist.
    /// </summary>
    public bool SendApplyLatch(int gpuZoneCount = StrimerProtocol.GpuZoneCount)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            return WriteCommand(StrimerProtocol.BuildApplyLatch(gpuZoneCount));
        }
    }

    /// <summary>Hands the cable to the motherboard ARGB input (on) or back to the host (off).</summary>
    public bool SendArgbSync(bool on)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            return WriteCommand(StrimerProtocol.BuildArgbSync(on)) && WriteCommand(StrimerProtocol.BuildEffectEnable());
        }
    }

    // Caller holds _lock.
    private bool WriteCommand(ReadOnlySpan<byte> command)
    {
        Array.Clear(_cmdReport);
        command.CopyTo(_cmdReport);
        var ok = _device!.Write(_cmdReport);
        TrackWrite(ok);
        return ok;
    }

    // Caller holds _lock.
    private void TrackWrite(bool ok)
    {
        if (ok)
        {
            _consecutiveWriteFailures = 0;
        }
        else
        {
            _consecutiveWriteFailures++;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _device?.Dispose();
            _device = null;
        }
    }
}
