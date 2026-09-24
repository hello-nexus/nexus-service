using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.Nollie;

/// <summary>One attached controller; owns its HID handle and a scratch report buffer so writes never allocate on the 30 Hz path.</summary>
public sealed class NollieController : IDisposable
{
    private readonly object _lock = new();
    private readonly byte[] _report;
    private IHidDevice? _device;
    private int _consecutiveWriteFailures;
    private bool _disposed;
    private bool _released;

    public NollieController(IHidDevice device, NollieDevice spec)
    {
        _device = device;
        Spec = spec;
        Serial = device.Serial ?? "";
        Path = device.Path;
        DeviceId = BuildDeviceId(Serial, device.Path);
        _report = new byte[spec.Transport == NollieTransport.Wide
            ? NollieProtocol.WideReportSize
            : NollieProtocol.ChunkedReportSize];
    }

    public NollieDevice Spec { get; }
    public string Serial { get; }
    public string Path { get; }

    /// <summary>Stable card-id prefix. Serial-derived where the device reports one, so ids survive a replug onto another port.</summary>
    public string DeviceId { get; private set; }

    /// <summary>Switches to the path-derived id after a serial clash; the path is unique per attached unit.</summary>
    internal void UsePathDerivedId() => DeviceId = $"nollie-p-{Sanitize(Path)}";

    public int ConsecutiveWriteFailures => Volatile.Read(ref _consecutiveWriteFailures);

    public bool IsAttached
    {
        get { lock (_lock) { return _device is not null; } }
    }

    private static string BuildDeviceId(string serial, string path)
        => !string.IsNullOrEmpty(serial) ? $"nollie-s-{Sanitize(serial)}" : $"nollie-p-{Sanitize(path)}";

    private static string Sanitize(string s)
    {
        var buf = new char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            buf[i] = char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_';
        }
        return new string(buf);
    }

    /// <summary>True once <see cref="Release"/> handed the strips to the firmware; colour writes stop so a late frame cannot take them back.</summary>
    public bool IsReleased
    {
        get { lock (_lock) { return _released; } }
    }

    /// <summary>Pushes one card's packed RGB triples; the protocol layer reorders per transport.</summary>
    public bool SendChannel(int cardIndex, ReadOnlySpan<byte> rgb)
    {
        var hardwareChannel = Spec.HardwareChannel(cardIndex);
        var ok = SendChannelLocked(hardwareChannel, rgb);

        // 8 ms settle after a flag-channel write, per OpenRGB
        // NollieController::SendPacket. Outside the lock: holding it here would
        // stall Dispose and the LED-count handshake behind the writer's tick.
        // Unverified on hardware; no wide-transport board has been bench-tested.
        if (ok && NollieProtocol.IsFlagChannel(Spec, hardwareChannel))
        {
            Thread.Sleep(NollieProtocol.FlagChannelSettleMs);
        }
        return ok;
    }

    private bool SendChannelLocked(int hardwareChannel, ReadOnlySpan<byte> rgb)
    {
        lock (_lock)
        {
            if (_device is null || _released) return false;

            if (Spec.Transport == NollieTransport.Wide)
            {
                NollieProtocol.WriteWide(_report, Spec, hardwareChannel, rgb);
                return Write();
            }

            var ledCount = rgb.Length / 3;
            var chunks = ledCount / NollieProtocol.LedsPerChunk
                + (ledCount % NollieProtocol.LedsPerChunk > 0 ? 1 : 0);
            var allOk = true;
            for (var chunk = 0; chunk < chunks; chunk++)
            {
                var start = chunk * NollieProtocol.LedsPerChunk * 3;
                var len = Math.Min(NollieProtocol.LedsPerChunk * 3, rgb.Length - start);
                NollieProtocol.WriteChunk(_report, Spec, hardwareChannel, chunk, rgb.Slice(start, len));
                if (!Write()) allOk = false;
            }
            return allOk;
        }
    }

    /// <summary>Latches a chunked update; the wide transport applies on receipt and takes no latch.</summary>
    public bool SendLatch()
    {
        lock (_lock)
        {
            if (_device is null || _released || Spec.Transport == NollieTransport.Wide) return false;
            NollieProtocol.WriteLatch(_report);
            return Write();
        }
    }

    /// <summary>
    /// Tells the firmware what to run on its own: a colour to hold, or its
    /// built-in effect. False when this firmware takes none.
    /// </summary>
    public bool SendStandalone(bool builtIn, bool mos, byte r, byte g, byte b)
    {
        lock (_lock)
        {
            if (_device is null) return false;
            if (!NollieProtocol.WriteStandalone(_report, Spec, builtIn, mos, r, g, b)) return false;
            return Write();
        }
    }

    /// <summary>
    /// Hands the strips to the firmware for good: the standalone settings go
    /// out once more so the hand-off carries them, then the release command.
    /// Colour writes are refused from here on. False when this firmware has
    /// no hand-off, in which case nothing is sent and frames keep flowing.
    /// </summary>
    public bool Release(bool builtIn, bool mos, byte r, byte g, byte b)
    {
        lock (_lock)
        {
            if (_device is null || _released) return false;
            if (!NollieProtocol.WriteStandalone(_report, Spec, builtIn, mos, r, g, b)) return false;
            Write();
            // Settle between the two, per the vendor driver. Held under the
            // lock on purpose: no colour frame may land between the settings
            // and the hand-off.
            Thread.Sleep(NollieProtocol.ReleaseSettleMs);
            NollieProtocol.WriteRelease(_report, Spec);
            var ok = Write();
            _released = true;
            return ok;
        }
    }

    /// <summary>Declares per-channel LED counts, for the one controller whose firmware takes them.</summary>
    public bool SendLedCounts(ReadOnlySpan<int> countsPerChannel)
    {
        lock (_lock)
        {
            if (_device is null || !Spec.WantsLedCountHandshake) return false;
            NollieProtocol.WriteLedCounts(_report, countsPerChannel);
            return Write();
        }
    }

    // Caller holds _lock.
    private bool Write()
    {
        var ok = _device!.Write(_report);
        if (ok) Volatile.Write(ref _consecutiveWriteFailures, 0);
        else Interlocked.Increment(ref _consecutiveWriteFailures);
        return ok;
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

/// <summary>The attached controllers, keyed by <see cref="NollieController.DeviceId"/> since several commonly share a machine.</summary>
public sealed class NollieHub : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, NollieController> _controllers = new(StringComparer.Ordinal);
    private NollieController[] _snapshot = Array.Empty<NollieController>();
    private bool _disposed;

    public bool IsConnected
    {
        get { lock (_lock) { return _controllers.Count > 0; } }
    }

    /// <summary>Attached controllers. Snapshot array so the 30 Hz writer iterates without holding the lock.</summary>
    public IReadOnlyList<NollieController> Controllers => Volatile.Read(ref _snapshot);

    /// <summary>True when this HID path already backs an attached controller.</summary>
    public bool HasPath(string path)
    {
        lock (_lock)
        {
            foreach (var c in _controllers.Values)
            {
                if (string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Adds a controller. An id already held by a DIFFERENT HID path means two
    /// units share a serial, so the newcomer falls back to its path-derived id
    /// rather than collapsing both onto one card and re-opening every poll.
    /// </summary>
    public void Attach(NollieController controller)
    {
        lock (_lock)
        {
            if (_controllers.TryGetValue(controller.DeviceId, out var existing))
            {
                if (string.Equals(existing.Path, controller.Path, StringComparison.OrdinalIgnoreCase))
                {
                    existing.Dispose();
                }
                else
                {
                    controller.UsePathDerivedId();
                    if (_controllers.TryGetValue(controller.DeviceId, out var samePath))
                    {
                        samePath.Dispose();
                    }
                }
            }
            _controllers[controller.DeviceId] = controller;
            Resnapshot();
        }
    }

    public void Detach(string deviceId)
    {
        lock (_lock)
        {
            if (_controllers.Remove(deviceId, out var controller))
            {
                controller.Dispose();
                Resnapshot();
            }
        }
    }

    public void DetachAll()
    {
        lock (_lock)
        {
            foreach (var c in _controllers.Values) c.Dispose();
            _controllers.Clear();
            Resnapshot();
        }
    }

    public NollieController? Find(string deviceId)
    {
        lock (_lock)
        {
            return _controllers.TryGetValue(deviceId, out var c) ? c : null;
        }
    }

    // Caller holds _lock.
    private void Resnapshot()
    {
        var array = new NollieController[_controllers.Count];
        _controllers.Values.CopyTo(array, 0);
        Array.Sort(array, static (a, b) => string.CompareOrdinal(a.DeviceId, b.DeviceId));
        Volatile.Write(ref _snapshot, array);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var c in _controllers.Values) c.Dispose();
            _controllers.Clear();
            Volatile.Write(ref _snapshot, Array.Empty<NollieController>());
        }
    }
}
