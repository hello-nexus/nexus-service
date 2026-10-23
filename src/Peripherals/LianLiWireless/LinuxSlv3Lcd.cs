using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Nexus.Service.Peripherals.LinuxUsb;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiWireless;

public sealed class LinuxSlv3LcdDiscovery : ISlv3LcdDiscovery
{
    private static readonly int[] ProductIds = { Slv3LcdProtocol.ProductIdSl, Slv3LcdProtocol.ProductIdTl };

    private readonly string _root;

    public LinuxSlv3LcdDiscovery() : this(LinuxUsbSysfs.SysfsRoot)
    {
    }

    internal LinuxSlv3LcdDiscovery(string root)
    {
        _root = root;
    }

    public IReadOnlyList<Slv3LcdPortInfo> Discover()
    {
        var nodes = LinuxUsbSysfs.Find(_root, Slv3LcdProtocol.VendorId, ProductIds);
        var result = new List<Slv3LcdPortInfo>(nodes.Count);
        foreach (var node in nodes)
        {
            result.Add(new Slv3LcdPortInfo
            {
                PortName = node.DevNode,

                Serial = string.IsNullOrEmpty(node.Serial) ? node.SysName : node.Serial,
                ProductId = node.ProductId,
            });
        }
        return result;
    }
}

public sealed class LinuxSlv3LcdTransport : ISlv3LcdTransport
{
    private const int LcdInterface = 0;

    private const uint WriteTimeoutMs = 3000;
    private const int AckReadTimeoutMs = 500;

    private const uint DrainReadTimeoutMs = 200;
    private const int DrainErrorBackoffMs = 50;

    private const int ENODEV = 19;
    private const int EPIPE = 32;
    private const int ESHUTDOWN = 108;

    private readonly LinuxUsbfsInterface _usb;
    private readonly object _ioLock = new();
    private readonly BlockingCollection<byte[]> _rxQueue = new(boundedCapacity: 32);
    private readonly Thread _drainThread;
    private volatile bool _disposed;
    private volatile bool _dead;
    private bool _loggedFirstPush;
    private bool _loggedWriteFailure;

    public LinuxSlv3LcdTransport(string devNode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devNode);
        PortName = devNode;
        _usb = new LinuxUsbfsInterface(devNode, LcdInterface);

        _usb.ClearHalt(Slv3LcdProtocol.WritePipeId);
        _usb.ClearHalt(Slv3LcdProtocol.ReadPipeId);

        _drainThread = new Thread(DrainLoop) { IsBackground = true, Name = "slv3-lcd-drain" };
        _drainThread.Start();

        if (!WriteArgCommand(Slv3LcdProtocol.CmdType.SetFrameRate, 120))
        {
            ServiceLog.Warn($"[lianli-wireless-lcd] SetFrameRate on open failed for {devNode}");
        }
    }

    public bool IsOpen => !_disposed && !_dead && _usb.IsOpen;

    public string PortName { get; }

    private void DrainLoop()
    {
        var buf = new byte[10240];
        var loggedError = false;
        while (!_disposed && !_dead)
        {
            var n = _usb.Transfer(Slv3LcdProtocol.ReadPipeId, buf, DrainReadTimeoutMs, out var errno);
            if (n > 0)
            {
                var frame = buf[..n];
                try
                {
                    while (!_rxQueue.TryAdd(frame) && _rxQueue.TryTake(out _))
                    {
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                continue;
            }
            if (n == 0 || errno == LinuxUsbfsInterface.ETIMEDOUT)
            {
                continue;
            }
            if (errno == ENODEV || errno == ESHUTDOWN)
            {
                _dead = true;
                return;
            }
            if (errno == EPIPE)
            {
                _usb.ClearHalt(Slv3LcdProtocol.ReadPipeId);
            }
            if (!loggedError)
            {
                loggedError = true;
                ServiceLog.Warn($"[lianli-wireless-lcd] read on {PortName} failed: errno {errno}");
            }
            Thread.Sleep(DrainErrorBackoffMs);
        }
    }

    public bool PushImage(byte[] jpeg)
    {
        if (_disposed)
        {
            return false;
        }
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var buffer = Slv3LcdProtocol.BuildPushJpgBuffer(timestamp, jpeg);
        lock (_ioLock)
        {
            if (_disposed)
            {
                return false;
            }
            var ok = WriteLocked(buffer);
            if (ok && !_loggedFirstPush)
            {
                _loggedFirstPush = true;
                ServiceLog.Info($"[lianli-wireless-lcd] PushImage OK ({buffer.Length} bytes) on {PortName}");
            }
            return ok;
        }
    }

    public bool SetBrightness(byte value) => WriteArgCommand(Slv3LcdProtocol.CmdType.BrigthSet, value);

    public bool SetRotation(byte value) => WriteArgCommand(Slv3LcdProtocol.CmdType.Rotate, value);

    public bool TryGetPosition(out byte groupIndex)
    {
        groupIndex = 0;
        if (_disposed)
        {
            return false;
        }
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var header = Slv3LcdProtocol.BuildArgCommandHeader(Slv3LcdProtocol.CmdType.GetPosIndex, timestamp, 0);
        lock (_ioLock)
        {
            if (_disposed)
            {
                return false;
            }
            try
            {
                while (_rxQueue.TryTake(out _))
                {
                }
                if (!WriteLocked(header))
                {
                    return false;
                }
                if (!_rxQueue.TryTake(out var ack, AckReadTimeoutMs) || ack.Length < 9)
                {
                    return false;
                }
                groupIndex = ack[8];
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    private bool WriteArgCommand(Slv3LcdProtocol.CmdType cmd, byte value)
    {
        if (_disposed)
        {
            return false;
        }
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var header = Slv3LcdProtocol.BuildArgCommandHeader(cmd, timestamp, value);
        lock (_ioLock)
        {
            return !_disposed && WriteLocked(header);
        }
    }

    private bool WriteLocked(byte[] buffer)
    {
        if (_dead)
        {
            return false;
        }
        if (_usb.WriteAll(Slv3LcdProtocol.WritePipeId, buffer, WriteTimeoutMs, out var errno))
        {
            _loggedWriteFailure = false;
            return true;
        }
        if (errno == ENODEV || errno == ESHUTDOWN)
        {
            _dead = true;
        }
        else if (errno == EPIPE)
        {
            _usb.ClearHalt(Slv3LcdProtocol.WritePipeId);
        }
        if (!_loggedWriteFailure)
        {
            _loggedWriteFailure = true;
            ServiceLog.Warn($"[lianli-wireless-lcd] write of {buffer.Length} bytes failed on {PortName}: errno {errno}");
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (Thread.CurrentThread != _drainThread)
        {
            _drainThread.Join();
        }
        lock (_ioLock)
        {
            _usb.Dispose();
            _rxQueue.Dispose();
        }
    }
}
