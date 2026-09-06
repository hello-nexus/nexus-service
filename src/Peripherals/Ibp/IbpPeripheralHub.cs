using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Ibp;

/// <summary>One open iBUYPOWER keyboard or mouse, as the lighting stack sees it.</summary>
public sealed class IbpAttachedPeripheral
{
    public IbpAttachedPeripheral(IbpPeripheralModel model, string serial, string path)
    {
        Model = model;
        Serial = serial;
        Path = path;
        DeviceId = $"{IbpPeripheralProtocol.DeviceIdPrefix}{model.Id}:{serial}";
    }

    public IbpPeripheralModel Model { get; }
    public string Serial { get; }
    public string Path { get; }
    /// <summary>"ibp:&lt;model&gt;:&lt;serial&gt;" - the parent id of every card / frame for this unit.</summary>
    public string DeviceId { get; }
}

/// <summary>
/// Owns the vendor HID collection of every attached iBUYPOWER keyboard and
/// mouse and pushes frames to them. Mirrors <see cref="Hyte.Keeb.KeebHub"/>
/// but holds a list: a KM7 kit is a keyboard AND a mouse, each its own USB
/// device. Handle IO is serialised on <c>_io</c> so the frame writer and the
/// connection worker never interleave on a handle or dispose one mid-frame;
/// HID enumeration runs off the lock so a unit that will not open cannot
/// stall the units that stream. The bundled OpenRGB has no detector for these
/// PIDs, so nothing else drives them.
/// </summary>
public sealed class IbpPeripheralHub : IDisposable
{
    private sealed class Slot
    {
        public Slot(IbpAttachedPeripheral info, IHidDevice device)
        {
            Info = info;
            Device = device;
            Frame = new byte[info.Model.FrameBytes];
        }

        public IbpAttachedPeripheral Info { get; }
        public IHidDevice Device { get; }
        /// <summary>Reused encode buffer (no per-frame allocation at 30 Hz).</summary>
        public byte[] Frame { get; }
        /// <summary>True once the host has taken the LEDs; reset when handed back to firmware.</summary>
        public bool SoftwareMode { get; set; }
        public int Failures { get; set; }
    }

    private const int ConsecutiveWriteFailureThreshold = 5;

    private readonly IHidEnumerator _hid;
    private readonly object _io = new();
    private readonly List<Slot> _slots = new();
    // Paths whose open already failed and was logged; cleared when the path
    // opens. Touched off _io, so it carries its own lock.
    private readonly HashSet<string> _openFailed = new(StringComparer.OrdinalIgnoreCase);
    private IbpAttachedPeripheral[] _snapshot = Array.Empty<IbpAttachedPeripheral>();
    private int _version;
    private bool _disposed;

    public IbpPeripheralHub(IHidEnumerator hid)
    {
        _hid = hid;
    }

    /// <summary>Currently open units. Immutable snapshot, safe to iterate off-lock.</summary>
    public IReadOnlyList<IbpAttachedPeripheral> Attached => Volatile.Read(ref _snapshot);

    public bool IsConnected => Attached.Count > 0;

    /// <summary>Bumps whenever <see cref="Attached"/> changes, so a poller can detect churn without diffing.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>
    /// Close every open unit whose model is not in <paramref name="allowed"/>
    /// (LEDs returned to firmware first) and open every enumerated unit of an
    /// allowed model that is not open yet. One HID enumeration per call, taken
    /// off the IO lock. Returns true when the attached set changed.
    /// </summary>
    public bool Reconcile(IReadOnlySet<IbpPeripheralModel> allowed)
    {
        bool changed;
        lock (_io)
        {
            if (_disposed) return false;
            changed = CloseDisallowedLocked(allowed);
        }

        // FindAll walks every HID interface on the box (SetupDi + CreateFile +
        // caps per interface): off _io.
        IReadOnlyList<HidDeviceInfo> infos;
        try { infos = _hid.FindAll(); }
        catch (Exception ex)
        {
            ServiceLog.Error($"[ibp] HID enumeration failed: {ex.GetType().Name}: {ex.Message}");
            return changed;
        }

        var opened = new List<Slot>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var attached = Attached;
        foreach (var info in infos)
        {
            if (info.VendorId != IbpPeripheralProtocol.VendorId) continue;
            var model = IbpPeripheralProtocol.ForProductId(info.ProductId);
            if (model is null || !allowed.Contains(model)) continue;
            // Windows only: LinuxHidEnumerator leaves the report lengths at 0,
            // so no collection matches there.
            if (info.FeatureReportByteLength != model.ReportLength) continue;
            var serial = !string.IsNullOrWhiteSpace(info.Serial) ? info.Serial! : StableIdFromPath(info.Path);
            // One slot per physical unit: a second matching collection on the
            // same serial would otherwise mint a duplicate device id.
            if (!claimed.Add(model.Id + "|" + serial)) continue;
            if (IsAttached(attached, model, serial, info.Path)) continue;

            IHidDevice? dev = null;
            try { dev = _hid.Open(info.Path); }
            catch (Exception ex)
            {
                ServiceLog.Error($"[ibp] open threw for {model.Id}: {ex.GetType().Name}: {ex.Message}");
            }
            if (dev is null)
            {
                bool first;
                lock (_openFailed) first = _openFailed.Add(info.Path);
                if (first) ServiceLog.Error($"[ibp] open failed for {model.Id} at {info.Path}");
                continue;
            }
            lock (_openFailed) _openFailed.Remove(info.Path);
            opened.Add(new Slot(new IbpAttachedPeripheral(model, serial, info.Path), dev));
        }
        if (opened.Count == 0) return changed;

        lock (_io)
        {
            foreach (var slot in opened)
            {
                // A Dispose, or a slot for the same unit adopted by a concurrent
                // failure-recovery, can land between phases: drop the extra handle.
                if (_disposed || IsAttached(_slots, slot.Info.Model, slot.Info.Serial, slot.Info.Path))
                {
                    try { slot.Device.Dispose(); } catch { /* best effort */ }
                    continue;
                }
                _slots.Add(slot);
                changed = true;
                ServiceLog.Info($"[ibp] connected {slot.Info.Model.Id} (serial={slot.Info.Serial}, path={slot.Info.Path})");
            }
            if (changed) RebuildSnapshotLocked();
        }
        return changed;
    }

    /// <summary>Hand every unit back to firmware and close it.</summary>
    public void Disconnect()
    {
        lock (_io) CloseAllLocked();
    }

    /// <summary>
    /// Stream one frame (<paramref name="leds"/> in the model's frame order).
    /// Takes the LEDs from firmware on the first frame after a connect or a
    /// release. False when the unit is not open or the write failed.
    /// </summary>
    public bool WriteFrame(string deviceId, ReadOnlySpan<RgbColor> leds)
    {
        lock (_io)
        {
            var slot = FindLocked(deviceId);
            if (slot is null) return false;
            var model = slot.Info.Model;
            try
            {
                if (!slot.SoftwareMode)
                {
                    var take = IbpPeripheralProtocol.SoftwareModeReport(model);
                    if (take is not null && !slot.Device.SetFeature(take))
                        return RecordFailureLocked(slot, "software-mode");
                    slot.SoftwareMode = true;
                }
                IbpPeripheralProtocol.EncodeFrame(model, leds, slot.Frame);
                for (var r = 0; r < model.ReportCount; r++)
                {
                    if (!slot.Device.SetFeature(slot.Frame.AsSpan(r * model.ReportLength, model.ReportLength)))
                        return RecordFailureLocked(slot, "frame");
                }
                slot.Failures = 0;
                return true;
            }
            catch (ObjectDisposedException) { return false; }
            catch (Exception ex)
            {
                ServiceLog.Error($"[ibp] frame write failed on {model.Id}: {ex.GetType().Name}: {ex.Message}");
                return RecordFailureLocked(slot, ex.GetType().Name);
            }
        }
    }

    /// <summary>Return one unit's LEDs to its onboard animation. No-op unless the host holds them.</summary>
    public void ReleaseToFirmware(string deviceId)
    {
        lock (_io)
        {
            var slot = FindLocked(deviceId);
            if (slot is not null) ReleaseLocked(slot);
        }
    }

    /// <summary>Return every unit's LEDs to firmware. No-op for units the host does not hold.</summary>
    public void ReleaseAllToFirmware()
    {
        lock (_io)
        {
            foreach (var slot in _slots) ReleaseLocked(slot);
        }
    }

    // Caller must hold _io.
    private bool CloseDisallowedLocked(IReadOnlySet<IbpPeripheralModel> allowed)
    {
        var changed = false;
        for (var i = _slots.Count - 1; i >= 0; i--)
        {
            var slot = _slots[i];
            if (allowed.Contains(slot.Info.Model)) continue;
            ReleaseLocked(slot);
            try { slot.Device.Dispose(); } catch { /* best effort */ }
            _slots.RemoveAt(i);
            changed = true;
            ServiceLog.Info($"[ibp] released {slot.Info.Model.Id} ({slot.Info.Serial})");
        }
        if (changed) RebuildSnapshotLocked();
        return changed;
    }

    // Caller must hold _io.
    private void CloseAllLocked()
    {
        if (_slots.Count == 0) return;
        foreach (var slot in _slots)
        {
            ReleaseLocked(slot);
            try { slot.Device.Dispose(); } catch { /* best effort */ }
        }
        _slots.Clear();
        RebuildSnapshotLocked();
    }

    // Caller must hold _io.
    private void ReleaseLocked(Slot slot)
    {
        if (!slot.SoftwareMode) return;
        slot.SoftwareMode = false;
        var give = IbpPeripheralProtocol.FirmwareModeReport(slot.Info.Model);
        if (give is null) return;
        try
        {
            if (!slot.Device.SetFeature(give))
                ServiceLog.Warn($"[ibp] firmware handback refused on {slot.Info.Model.Id}");
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[ibp] firmware handback threw on {slot.Info.Model.Id}: {ex.GetType().Name}");
        }
    }

    // Caller must hold _io.
    private bool RecordFailureLocked(Slot slot, string where)
    {
        var n = ++slot.Failures;
        if (n >= ConsecutiveWriteFailureThreshold)
        {
            ServiceLog.Error($"[ibp] {n} consecutive IO failures ({where}) on {slot.Info.Model.Id} - dropping interface");
            try { slot.Device.Dispose(); } catch { /* best effort */ }
            _slots.Remove(slot);
            RebuildSnapshotLocked();
        }
        return false;
    }

    private Slot? FindLocked(string deviceId)
    {
        foreach (var slot in _slots)
        {
            if (slot.Info.DeviceId == deviceId) return slot;
        }
        return null;
    }

    private static bool IsAttached(IReadOnlyList<IbpAttachedPeripheral> attached, IbpPeripheralModel model, string serial, string path)
    {
        foreach (var a in attached)
        {
            if (string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)) return true;
            if (ReferenceEquals(a.Model, model) && a.Serial == serial) return true;
        }
        return false;
    }

    private static bool IsAttached(List<Slot> slots, IbpPeripheralModel model, string serial, string path)
    {
        foreach (var s in slots)
        {
            if (string.Equals(s.Info.Path, path, StringComparison.OrdinalIgnoreCase)) return true;
            if (ReferenceEquals(s.Info.Model, model) && s.Info.Serial == serial) return true;
        }
        return false;
    }

    private void RebuildSnapshotLocked()
    {
        var next = new IbpAttachedPeripheral[_slots.Count];
        for (var i = 0; i < _slots.Count; i++) next[i] = _slots[i].Info;
        Volatile.Write(ref _snapshot, next);
        Interlocked.Increment(ref _version);
    }

    // No serial reported - derive a stable short id from the device path so
    // the device id survives reconnects of the same physical port.
    private static string StableIdFromPath(string path)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in path) { h ^= c; h *= 16777619; }
            return $"p-{h:x8}";
        }
    }

    public void Dispose()
    {
        lock (_io)
        {
            if (_disposed) return;
            _disposed = true;
            CloseAllLocked();
        }
    }
}
