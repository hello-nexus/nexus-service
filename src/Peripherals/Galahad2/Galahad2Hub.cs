using System;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.LianLiCp;

namespace Nexus.Service.Peripherals.Galahad2;

public sealed class Galahad2Hub : IDisposable, IGalahad2PumpTransport
{
    // Longer timeout for the connect-time handshake.
    private const int ConnectReadTimeoutMs = 1000;
    // Short poll timeout keeps _lock uncontested during steady-state RPM reads.
    private const int PollReadTimeoutMs = 200;
    // Plausibility ceiling for both channels.
    private const int RpmMax = 6000;

    private readonly object _lock = new();
    private IHidDevice? _device;
    private bool _disposed;

    // Both volatile so readers outside _lock see the latest value.
    private volatile bool _isConnected;
    private volatile Galahad2Snapshot _snapshot = Galahad2Snapshot.Empty;
    private volatile int _productId;

    public bool IsConnected => _isConnected;

    // Callers must read this reference once and use it for all field accesses.
    public Galahad2Snapshot Snapshot => _snapshot;

    public int ConnectedProductId => _productId;

    public void Attach(IHidDevice device, int productId = 0)
    {
        lock (_lock)
        {
            _device?.Dispose();
            _device = device;
            _productId = productId;
        }
    }

    // Sends the handshake, verifies a reply, and marks the hub connected.
    // All I/O is serialized inside _lock. Called from the worker thread only.
    public bool Connect()
    {
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }
            if (!_device.Write(Galahad2Protocol.EncodeHandshakeRequest()))
            {
                return false;
            }
            Span<byte> buf = stackalloc byte[CommandPacket.Length];
            int n = _device.Read(buf, ConnectReadTimeoutMs);
            if (n <= 0)
            {
                return false;
            }
            // Pump duty initialised to the floor; FanDuty starts at 0 (unknown before first set).
            _snapshot = new Galahad2Snapshot(0, 0, 0, Galahad2Protocol.PumpDutyFloor);
            _isConnected = true;
            return true;
        }
    }

    // Sends a set-fan command under _lock. Safe to call concurrently with PollRpm.
    public bool SetFan(int duty)
    {
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }
            bool ok = _device.Write(Galahad2Protocol.EncodeSetFan(duty));
            if (ok)
            {
                var prev = _snapshot;
                _snapshot = new Galahad2Snapshot(prev.FanRpm, prev.PumpRpm, duty, prev.PumpDuty);
            }
            return ok;
        }
    }

    // Sends a set-pump command under _lock. The encoder floors duty at PumpDutyFloor.
    public bool SetPump(int duty)
    {
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }
            bool ok = _device.Write(Galahad2Protocol.EncodeSetPump(duty));
            if (ok)
            {
                var prev = _snapshot;
                _snapshot = new Galahad2Snapshot(prev.FanRpm, prev.PumpRpm, prev.FanDuty, duty);
            }
            return ok;
        }
    }

    // Encodes before acquiring _lock so the payload allocation stays off the critical path.
    public bool SendLighting(byte ring, byte mode, byte brightness, byte speed, byte direction, ReadOnlySpan<byte> colors)
    {
        var packet = Galahad2Protocol.EncodeLighting(ring, mode, brightness, speed, direction, colors);
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }
            return _device.Write(packet);
        }
    }

    // Polls RPM: Write + Read both inside _lock so no I/O interleaves on the HID pipe.
    // Returns false only when the device appears disconnected (Read returns negative).
    public bool PollRpm()
    {
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }
            if (!_device.Write(Galahad2Protocol.EncodeHandshakeRequest()))
            {
                return false;
            }
            Span<byte> buf = stackalloc byte[CommandPacket.Length];
            int n = _device.Read(buf, PollReadTimeoutMs);
            if (n < 0)
            {
                return false;
            }
            if (n == 0)
            {
                // Timeout without data is not a disconnect.
                return true;
            }
            var reading = Galahad2Protocol.DecodeHandshake(buf);
            if (reading.HasValue)
            {
                var prev = _snapshot;
                int fanRpm = reading.Value.FanRpm >= 0 && reading.Value.FanRpm <= RpmMax
                    ? reading.Value.FanRpm
                    : prev.FanRpm;
                int pumpRpm = reading.Value.PumpRpm >= 0 && reading.Value.PumpRpm <= RpmMax
                    ? reading.Value.PumpRpm
                    : prev.PumpRpm;
                _snapshot = new Galahad2Snapshot(fanRpm, pumpRpm, prev.FanDuty, prev.PumpDuty);
            }
            return true;
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            _isConnected = false;
            _snapshot = Galahad2Snapshot.Empty;
            _productId = 0;
            _device?.Dispose();
            _device = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _isConnected = false;
            _snapshot = Galahad2Snapshot.Empty;
            _device?.Dispose();
            _device = null;
        }
    }
}
