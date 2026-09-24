using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.LianLiCp;

namespace Nexus.Service.Peripherals.LianLiTl;

public sealed class TlFanHub : IDisposable
{
    // Discovery Read timeout is longer (connect-time only).
    private const int DiscoverReadTimeoutMs = 1000;
    // Poll Read timeout is short so the lock is not held long during steady-state RPM reads.
    private const int PollReadTimeoutMs = 200;
    // A light command's reply is not needed to proceed, so it waits far less than a poll.
    private const int LightReadTimeoutMs = 50;
    // Reads left over from earlier commands are cleared with a non-blocking read.
    private const int FlushReadTimeoutMs = 1;
    private const int MaxFlushReads = 8;
    private int _connectGeneration;

    private readonly object _lock = new();
    private IHidDevice? _device;
    private bool _disposed;

    // Both fields are volatile so reads from HTTP threads / the cooling provider
    // see the latest values without acquiring _lock.
    private volatile bool _isConnected;
    private volatile TlHubSnapshot _snapshot = TlHubSnapshot.Empty;

    public bool IsConnected => _isConnected;

    /// <summary>Bumped on every successful discovery; a change means the controller lost its committed state.</summary>
    public int ConnectGeneration => Volatile.Read(ref _connectGeneration);

    // Callers must read this reference exactly once per operation and index all
    // arrays from that single snapshot to avoid cross-array length races.
    public TlHubSnapshot Snapshot => _snapshot;

    public int ChannelCount => _snapshot.ChannelCount;

    public void Attach(IHidDevice device)
    {
        lock (_lock)
        {
            // Dispose any previously-held device before replacing it.
            _device?.Dispose();
            _device = device;
        }
    }

    // Sends the handshake, builds the channel snapshot, and asserts host control.
    // All I/O is serialized inside _lock. Called from the worker thread only.
    public bool DiscoverFans()
    {
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }

            var request = TlFanProtocol.EncodeHandshakeRequest();
            FlushInputLocked();
            if (!_device.Write(request))
            {
                return false;
            }

            Span<byte> buf = stackalloc byte[CommandPacket.Length];
            int n = _device.Read(buf, DiscoverReadTimeoutMs);
            if (n <= 0)
            {
                return false;
            }

            var readings = TlFanProtocol.DecodeHandshake(buf);

            // Sort by (port, fanIndex) for deterministic channel order across connects.
            readings.Sort((a, b) =>
            {
                int cmp = a.Port.CompareTo(b.Port);
                return cmp != 0 ? cmp : a.FanIndex.CompareTo(b.FanIndex);
            });

            int count = readings.Count;
            var ports = new int[count];
            var fans = new int[count];
            var byAddress = new Dictionary<int, int>(count);

            for (int i = 0; i < count; i++)
            {
                ports[i] = readings[i].Port;
                fans[i] = readings[i].FanIndex;
                byAddress[TlFanProtocol.Address(readings[i].Port, readings[i].FanIndex)] = i;
            }

            // Publish snapshot before asserting IsConnected.
            _snapshot = new TlHubSnapshot(ports, fans, byAddress);

            for (int i = 0; i < count; i++)
            {
                var syncOff = TlFanProtocol.EncodeMotherboardSync(ports[i], fans[i], sync: false);
                FlushInputLocked();
                _device.Write(syncOff);
            }

            DeclareGroupsLocked(_snapshot);

            Interlocked.Increment(ref _connectGeneration);
            _isConnected = true;
            return true;
        }
    }

    // Sends a set-speed command under _lock. Safe to call concurrently with PollRpm.
    public bool SetSpeed(int channelIndex, int duty)
    {
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }
            var snap = _snapshot;
            if (channelIndex < 0 || channelIndex >= snap.ChannelCount)
            {
                return false;
            }
            var cmd = TlFanProtocol.EncodeSetFanSpeed(snap.Port[channelIndex], snap.FanIndex[channelIndex], duty);
            FlushInputLocked();
            bool ok = _device.Write(cmd);
            if (ok)
            {
                snap.Duty[channelIndex] = duty;
            }
            return ok;
        }
    }

    /// <summary>
    /// Drives one fan's lighting. The controller animates the mode on-chip from this
    /// single write, so callers commit on change rather than streaming.
    /// </summary>
    public bool SetFanLight(
        int port, int fanIndex, byte mode, int brightness, int speed, int direction,
        ReadOnlySpan<byte> colorBytes, int colorCount, bool disabled)
    {
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }
            var cmd = TlFanProtocol.EncodeSetFanLight(
                port, fanIndex, mode, brightness, speed, direction,
                colorBytes, colorCount, disabled, motherboardSync: false);
            FlushInputLocked();
            bool ok = _device.Write(cmd);
            DiscardReplyLocked(LightReadTimeoutMs);
            return ok;
        }
    }

    /// <summary>Drives one declared group - a port's top or bottom halves.</summary>
    public bool SetGroupLight(
        int group, byte mode, int brightness, int speed, int direction,
        ReadOnlySpan<byte> colorBytes, int colorCount, bool disabled)
    {
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }
            var cmd = TlFanProtocol.EncodeSetFanGroupLight(
                group, mode, brightness, speed, direction, colorBytes, colorCount, disabled);
            FlushInputLocked();
            bool ok = _device.Write(cmd);
            DiscardReplyLocked(LightReadTimeoutMs);
            return ok;
        }
    }

    /// <summary>
    /// Declares the two groups each populated port needs before a group light command
    /// can address it. Re-run on every connect: the controller loses the declaration
    /// across a re-enumeration.
    /// </summary>
    private void DeclareGroupsLocked(TlHubSnapshot snap)
    {
        if (_device == null)
        {
            return;
        }
        for (int port = 0; port < snap.PortFanCounts.Length; port++)
        {
            int fanCount = snap.PortFanCounts[port];
            if (fanCount <= 0)
            {
                continue;
            }
            var top = TlFanProtocol.EncodeSetFanGroup(TlFanProtocol.TopGroup(port), port, fanCount, topHalf: true);
            FlushInputLocked();
            bool topOk = _device.Write(top);
            DiscardReplyLocked(LightReadTimeoutMs);
            var bottom = TlFanProtocol.EncodeSetFanGroup(TlFanProtocol.BottomGroup(port), port, fanCount, topHalf: false);
            FlushInputLocked();
            bool bottomOk = _device.Write(bottom);
            DiscardReplyLocked(LightReadTimeoutMs);
        }
    }

    // Every command is request-response: the controller replies to each write, so a
    // reply left unread would be returned to the next Read and misparsed as its
    // result. Discarded, and a timeout is not an error.
    // The controller answers every command, so a reply left unread would be returned
    // to the next command's read. Clearing the queue before each write keeps a command
    // matched to its own reply no matter what earlier writes left behind.
    private void FlushInputLocked()
    {
        if (_device == null)
        {
            return;
        }
        Span<byte> buf = stackalloc byte[CommandPacket.Length];
        for (int i = 0; i < MaxFlushReads; i++)
        {
            if (_device.Read(buf, FlushReadTimeoutMs) <= 0)
            {
                return;
            }
        }
    }

    // Consumes the reply the controller sends for the command just written, so the
    // queue stays shallow between commands.
    private void DiscardReplyLocked(int timeoutMs)
    {
        if (_device == null)
        {
            return;
        }
        Span<byte> buf = stackalloc byte[CommandPacket.Length];
        _device.Read(buf, timeoutMs);
    }

    // Sends the handshake and matches the reply's RPM records to channels. Both the
    // Write and the Read are inside _lock so no other I/O interleaves on the HID pipe.
    // Returns false only when the device appears disconnected (Read returns negative).
    public bool PollRpm()
    {
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }
            var snap = _snapshot;

            var request = TlFanProtocol.EncodeHandshakeRequest();
            FlushInputLocked();
            if (!_device.Write(request))
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
                // Timeout with no data is not a disconnect.
                return true;
            }

            var readings = TlFanProtocol.DecodeHandshake(buf);
            for (int r = 0; r < readings.Count; r++)
            {
                int ch = snap.GetChannelIndex(readings[r].Port, readings[r].FanIndex);
                if (ch < 0)
                {
                    continue;
                }
                int rpm = readings[r].Rpm;
                // Plausibility range matches the Uni family: 0..6000.
                if (rpm >= 0 && rpm <= 6000)
                {
                    snap.Rpm[ch] = rpm;
                }
            }
            return true;
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            _isConnected = false;
            _snapshot = TlHubSnapshot.Empty;
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
            _snapshot = TlHubSnapshot.Empty;
            _device?.Dispose();
            _device = null;
        }
    }
}
