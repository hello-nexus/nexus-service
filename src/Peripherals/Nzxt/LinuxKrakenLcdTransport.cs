using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Peripherals.LinuxUsb;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// usbfs bulk OUT transport for the Kraken LCD. Claims interface 0 only: the HID control
/// interface stays bound to usbhid so the hidraw channel keeps working.
/// </summary>
public sealed class LinuxKrakenLcdTransport : IKrakenLcdTransport
{
    internal const int BulkInterface = 0;
    internal const byte BulkOutEndpoint = 0x02;

    // Bounds a stalled write rather than blocking the caller indefinitely.
    private const uint WriteTimeoutMs = 5000;

    private readonly LinuxUsbfsInterface _usb;
    private readonly object _ioLock = new();
    private bool _disposed;

    public LinuxKrakenLcdTransport(string devNode)
    {
        _usb = new LinuxUsbfsInterface(devNode, BulkInterface);

        // Clears a halt left by a prior session; a stalled bulk OUT pipe rejects writes.
        var err = _usb.ClearHalt(BulkOutEndpoint);
        if (err != 0)
        {
            ServiceLog.Warn($"[nzxt-kraken] ClearHalt failed: errno {err}");
        }
    }

    public bool Write(ReadOnlySpan<byte> data)
    {
        if (_disposed || data.Length == 0)
        {
            return false;
        }
        var buffer = data.ToArray();
        lock (_ioLock)
        {
            if (_disposed)
            {
                return false;
            }
            if (_usb.WriteAll(BulkOutEndpoint, buffer, WriteTimeoutMs, out var errno))
            {
                return true;
            }
            ServiceLog.Warn($"[nzxt-kraken] LCD write of {buffer.Length} bytes failed on {_usb.DevNode}: errno {errno}");
            return false;
        }
    }

    public void Dispose()
    {
        lock (_ioLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _usb.Dispose();
        }
    }
}

public sealed class LinuxKrakenLcdTransportFactory : IKrakenLcdTransportFactory
{
    private readonly string _sysfsRoot;

    public LinuxKrakenLcdTransportFactory() : this(LinuxUsbSysfs.SysfsRoot)
    {
    }

    internal LinuxKrakenLcdTransportFactory(string sysfsRoot)
    {
        _sysfsRoot = sysfsRoot;
    }

    public IKrakenLcdTransport? Open(string? serial)
    {
        foreach (var devNode in Candidates(serial))
        {
            try
            {
                return new LinuxKrakenLcdTransport(devNode);
            }
            catch (IOException ex)
            {
                ServiceLog.Warn($"[nzxt-kraken] LCD bulk open failed: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>Device nodes of every Kraken, the one carrying <paramref name="serial"/> first.</summary>
    internal List<string> Candidates(string? serial)
    {
        var matching = new List<string>();
        var others = new List<string>();
        foreach (var node in LinuxUsbSysfs.Find(_sysfsRoot, KrakenProtocol.VendorId, KrakenProtocol.ProductIds))
        {
            if (!string.IsNullOrEmpty(serial) && string.Equals(node.Serial, serial, StringComparison.OrdinalIgnoreCase))
            {
                matching.Add(node.DevNode);
            }
            else
            {
                others.Add(node.DevNode);
            }
        }
        matching.AddRange(others);
        return matching;
    }
}
