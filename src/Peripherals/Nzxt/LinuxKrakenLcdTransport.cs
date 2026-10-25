using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Peripherals.LinuxUsb;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// usbfs bulk OUT transport for the Kraken LCD. Claims only the interface that owns the bulk
/// OUT endpoint: the HID control interface stays bound to usbhid so the hidraw channel keeps working.
/// </summary>
public sealed class LinuxKrakenLcdTransport : IKrakenLcdTransport
{
    internal const byte BulkOutEndpoint = 0x02;

    // Bounds a stalled write rather than blocking the caller indefinitely.
    private const uint WriteTimeoutMs = 5000;

    private readonly LinuxUsbfsInterface _usb;
    private readonly object _ioLock = new();
    private bool _disposed;
    private bool _writeFailing;

    public LinuxKrakenLcdTransport(string devNode, int interfaceNumber)
    {
        _usb = new LinuxUsbfsInterface(devNode, interfaceNumber);

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
        lock (_ioLock)
        {
            if (_disposed)
            {
                return false;
            }
            if (_usb.WriteAll(BulkOutEndpoint, data, WriteTimeoutMs, out var errno))
            {
                _writeFailing = false;
                return true;
            }
            if (!_writeFailing)
            {
                ServiceLog.Warn($"[nzxt-kraken] LCD write of {data.Length} bytes failed on {_usb.DevNode}: errno {errno}");
            }
            _writeFailing = true;
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
        var candidates = Candidates(serial);
        foreach (var target in candidates)
        {
            try
            {
                return new LinuxKrakenLcdTransport(target.DevNode, target.Interface);
            }
            catch (IOException ex)
            {
                ServiceLog.Warn($"[nzxt-kraken] LCD bulk open failed: {ex.Message}");
            }
        }
        if (candidates.Count == 0 && !_loggedNoBulk)
        {
            _loggedNoBulk = true;
            ServiceLog.Info("[nzxt-kraken] no claimable LCD bulk interface found");
        }
        return null;
    }

    private bool _loggedNoBulk;

    /// <summary>
    /// Claimable bulk targets. Only the Kraken carrying <paramref name="serial"/> is considered
    /// when one matches; otherwise every Kraken is, as on Windows.
    /// </summary>
    internal List<(string DevNode, int Interface)> Candidates(string? serial)
    {
        var all = LinuxUsbSysfs.Find(_sysfsRoot, KrakenProtocol.VendorId, KrakenProtocol.ProductIds);
        var matching = new List<LinuxUsbDeviceNode>();
        if (!string.IsNullOrEmpty(serial))
        {
            foreach (var node in all)
            {
                if (string.Equals(node.Serial, serial, StringComparison.OrdinalIgnoreCase))
                {
                    matching.Add(node);
                }
            }
        }
        var result = new List<(string, int)>();
        foreach (var node in matching.Count > 0 ? matching : all)
        {
            var iface = ResolveBulkInterface(_sysfsRoot, node.SysName);
            if (iface is { } number)
            {
                result.Add((node.DevNode, number));
            }
        }
        return result;
    }

    /// <summary>
    /// The interface owning the bulk OUT endpoint, or null when none does or it is a HID
    /// interface: claiming that one would unbind usbhid and kill the control channel.
    /// </summary>
    internal static int? ResolveBulkInterface(string root, string sysName)
    {
        var number = LinuxUsbSysfs.FindInterfaceWithEndpoint(root, sysName, LinuxKrakenLcdTransport.BulkOutEndpoint);
        if (number is null)
        {
            return null;
        }
        var deviceDir = Path.Combine(root, sysName);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(deviceDir, sysName + ":*"))
            {
                var numberFile = Path.Combine(dir, "bInterfaceNumber");
                if (!File.Exists(numberFile)
                    || !int.TryParse(File.ReadAllText(numberFile).Trim(), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var n) || n != number)
                {
                    continue;
                }
                var classFile = Path.Combine(dir, "bInterfaceClass");
                var isHid = File.Exists(classFile) && File.ReadAllText(classFile).Trim() == "03";
                if (isHid || Directory.EnumerateDirectories(dir, "0003:*").Any())
                {
                    return null;
                }
                return number;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }
}
