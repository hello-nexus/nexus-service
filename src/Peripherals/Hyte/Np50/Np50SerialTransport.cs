using System;
using System.IO.Ports;
using System.Threading;

namespace Qos.Service.Peripherals.Hyte.Np50;

/// <summary>
/// <see cref="INp50Transport"/> backed by <see cref="SerialPort"/>. NP50 / MiniHub
/// enumerate as USB CDC virtual COM ports, so the baud rate is nominal — the
/// USB stack frames the packets. We pick conservative defaults (115200 8N1)
/// that match common CDC firmware expectations.
///
/// Concurrency model: <b>writes and reads use independent locks</b> so a
/// long heartbeat read (the 240-byte port-info poll has a 400 ms timeout
/// and the worker chains three of them) does NOT block the 30 Hz lighting
/// writer. SerialPort's underlying read/write streams are independent —
/// concurrent Write-during-Read on a single port instance is fine as long
/// as multiple writers are serialised among themselves (which the write
/// lock guarantees).
///
/// HYTE's shipping nexus-control-service takes the same shape via a
/// background write queue (<c>SmartHubCommandBase._executionQueue</c>)
/// that never shares a lock with reads. Two locks gets us the same
/// property without a queue thread.
/// </summary>
public sealed class Np50SerialTransport : INp50Transport
{
    private readonly SerialPort _port;
    // Writes from many threads (lighting writer, heartbeat, route handlers
    // changing cooling mode / fan speed) get serialised here so the wire
    // never sees interleaved bytes from two commands. Held briefly: a
    // single Write call returns once the OS has accepted the bytes.
    private readonly object _writeLock = new();
    // Reads + DiscardInput take this lock. Held for the full per-poll
    // timeout (up to a few hundred ms), so it MUST NOT be the same lock
    // writes take. Only the heartbeat worker reads, so this lock sees
    // single-threaded use in practice.
    private readonly object _readLock = new();
    private bool _disposed;

    public Np50SerialTransport(string portName, string serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        Serial = serial ?? "";
        _port = new SerialPort(portName, baudRate: 115200, Parity.None, dataBits: 8, StopBits.One)
        {
            // Plenty of headroom for the 240-byte channel-info read.
            ReadBufferSize = 4096,
            WriteBufferSize = 4096,
            // Defaults; per-call timeouts are applied at Read time.
            ReadTimeout = 500,
            WriteTimeout = 500,
            // CDC virtual COMs ignore handshake bits but some Windows drivers
            // refuse to deliver data without DTR/RTS asserted.
            DtrEnable = true,
            RtsEnable = true,
        };
        _port.Open();
        // Don't leave stale bytes from a previous session in the input buffer.
        _port.DiscardInBuffer();
        _port.DiscardOutBuffer();
    }

    public bool IsOpen => !_disposed && _port.IsOpen;

    public string Serial { get; }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Np50SerialTransport));
        lock (_writeLock)
        {
            // SerialPort.Write expects a byte[] + offset + count. Allocate per-call
            // rather than reuse a buffer so callers don't need to synchronize.
            var copy = data.ToArray();
            _port.Write(copy, 0, copy.Length);
        }
    }

    public void DiscardInput()
    {
        if (_disposed) return;
        lock (_readLock)
        {
            try { if (_port.IsOpen) _port.DiscardInBuffer(); } catch { /* port may have closed mid-call */ }
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Np50SerialTransport));
        lock (_readLock)
        {
            // Loop until we have what the caller asked for, the timeout elapses,
            // or the port closes underneath us. SerialPort.Read returns "at least 1
            // byte" but typically fewer than requested if the device hasn't sent
            // them yet, so accumulate.
            var deadline = Environment.TickCount + Math.Max(1, timeoutMs);
            var rented = new byte[buffer.Length];
            var total = 0;
            while (total < buffer.Length)
            {
                var remaining = deadline - Environment.TickCount;
                if (remaining <= 0) break;
                _port.ReadTimeout = remaining;
                int n;
                try
                {
                    n = _port.Read(rented, total, buffer.Length - total);
                }
                catch (TimeoutException)
                {
                    break;
                }
                if (n <= 0) break;
                total += n;
            }
            new ReadOnlySpan<byte>(rented, 0, total).CopyTo(buffer);
            return total;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_port.IsOpen) _port.Close(); } catch { /* best effort */ }
        try { _port.Dispose(); } catch { /* best effort */ }
    }
}
