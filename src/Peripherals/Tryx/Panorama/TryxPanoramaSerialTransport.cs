using System;
using System.IO.Ports;
using System.Threading;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>
/// <see cref="ITryxPanoramaTransport"/> backed by <see cref="SerialPort"/>.
/// Panorama enumerates as USB CDC (serial) + ADB composite. Serial is write-only;
/// the protocol sends frames and does not poll for responses. 115200 8N1, DTR/RTS
/// asserted per CDC firmware expectations.
/// Only writes are serialised; no read lock needed.
/// </summary>
public sealed class TryxPanoramaSerialTransport : ITryxPanoramaTransport
{
    private readonly SerialPort _port;
    private readonly object _writeLock = new();
    private bool _disposed;

    public TryxPanoramaSerialTransport(string portName, string serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        Serial = serial ?? "";
        PortName = portName;
        _port = new SerialPort(portName, baudRate: 115200, Parity.None, dataBits: 8, StopBits.One)
        {
            ReadBufferSize = 4096,
            WriteBufferSize = 4096,
            ReadTimeout = 500,
            WriteTimeout = 500,
            DtrEnable = true,
            RtsEnable = true,
        };
        Nexus.Service.Peripherals.IoSafety.SerialPortSafety.OpenAndClear(_port);
    }

    public bool IsOpen => !_disposed && _port.IsOpen;
    public string Serial { get; }
    public string PortName { get; }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TryxPanoramaSerialTransport));
        }
        lock (_writeLock)
        {
            var copy = data.ToArray();
            _port.Write(copy, 0, copy.Length);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Nexus.Service.Peripherals.IoSafety.SerialPortSafety.Close(_port);
    }
}
