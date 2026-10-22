using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Peripherals.LinuxUsb;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

public sealed class LinuxBulkUsbPipe : IBulkUsbPipe
{
    private const uint TransferTimeoutMs = 5000;

    private const int ENODEV = 19;
    private const int ESHUTDOWN = 108;

    private readonly LinuxUsbfsInterface _usb;
    private readonly byte _writePipeId;
    private readonly byte _readPipeId;
    private readonly object _ioLock = new();
    private readonly object _readLock = new();
    private volatile bool _disposed;
    private volatile bool _gone;
    private bool _loggedWriteFailure;

    public LinuxBulkUsbPipe(string devNode, int interfaceNumber, byte writePipeId, byte readPipeId, int productId = 0)
    {
        _writePipeId = writePipeId;
        _readPipeId = readPipeId;
        ProductId = productId;
        _usb = new LinuxUsbfsInterface(devNode, interfaceNumber);

        _usb.ClearHalt(writePipeId);
        if (readPipeId != 0)
        {
            _usb.ClearHalt(readPipeId);
        }
    }

    public int ProductId { get; }

    public bool IsGone => _gone;

    public string DevNode => _usb.DevNode;

    public bool Write(ReadOnlySpan<byte> data) => Write(_writePipeId, data);

    public bool Write(byte pipeId, ReadOnlySpan<byte> data)
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
            if (_usb.WriteAll(pipeId, data, TransferTimeoutMs, out var errno))
            {
                _loggedWriteFailure = false;
                return true;
            }
            NoteFailure(errno);
            if (!_loggedWriteFailure)
            {
                _loggedWriteFailure = true;
                ServiceLog.Warn($"[bulk-panel] write of {data.Length} bytes to EP 0x{pipeId:X2} failed on {_usb.DevNode}: errno {errno}");
            }
            return false;
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (_disposed || _readPipeId == 0 || buffer.Length == 0)
        {
            return -1;
        }
        lock (_readLock)
        {
            if (_disposed)
            {
                return -1;
            }
            var len = Math.Min(buffer.Length, LinuxUsbfsInterface.MaxTransferBytes);
            var n = _usb.Transfer(_readPipeId, buffer[..len], (uint)Math.Max(1, timeoutMs), out var errno);
            if (n >= 0)
            {
                return n;
            }
            if (errno == LinuxUsbfsInterface.ETIMEDOUT)
            {
                return 0;
            }
            NoteFailure(errno);
            return -1;
        }
    }

    private void NoteFailure(int errno)
    {
        if (errno is ENODEV or ESHUTDOWN)
        {
            _gone = true;
        }
    }

    public void Dispose()
    {
        lock (_ioLock)
        lock (_readLock)
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

public sealed class LinuxBulkUsbPipeFactory : IBulkUsbPipeFactory
{
    // Only the Universal Screen 8.8 is claimed on Linux: the claim detaches the kernel driver
    // and the other bulk panels are not validated over usbfs, so they keep the null pipe.
    private const int SupportedVendorId = 0x1CBE;

    private readonly string _sysfsRoot;

    public LinuxBulkUsbPipeFactory() : this(LinuxUsbSysfs.SysfsRoot)
    {
    }

    internal LinuxBulkUsbPipeFactory(string sysfsRoot)
    {
        _sysfsRoot = sysfsRoot;
    }

    public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId) =>
        Open(vendorId, productId, writePipeId, readPipeId, out _);

    public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId, out bool accessDenied)
    {
        accessDenied = false;
        if (vendorId != SupportedVendorId || productId != UniversalScreen88Protocol.ProductId)
        {
            return null;
        }
        var failures = new List<string>();
        foreach (var target in Targets(vendorId, productId, writePipeId))
        {
            try
            {
                return new LinuxBulkUsbPipe(target.DevNode, target.Interface, writePipeId, readPipeId, productId);
            }
            catch (LinuxUsbfsException ex)
            {
                accessDenied |= ex.IsAccessDenied;
                failures.Add(ex.Message);
            }
            catch (IOException ex)
            {
                failures.Add(ex.Message);
            }
        }
        if (failures.Count > 0)
        {
            ServiceLog.Info(accessDenied
                ? $"[bulk-panel] {vendorId:X4}:{productId:X4} is present but another program holds it: {string.Join("; ", failures)}"
                : $"[bulk-panel] {vendorId:X4}:{productId:X4} could not be opened: {string.Join("; ", failures)}");
        }
        return null;
    }

    internal List<(string DevNode, int Interface)> Targets(int vendorId, int productId, byte writePipeId)
    {
        var result = new List<(string, int)>();
        foreach (var node in LinuxUsbSysfs.Find(_sysfsRoot, vendorId, new[] { productId }))
        {
            var iface = LinuxUsbSysfs.FindInterfaceWithEndpoint(_sysfsRoot, node.SysName, writePipeId) ?? 0;
            result.Add((node.DevNode, iface));
        }
        return result;
    }
}
