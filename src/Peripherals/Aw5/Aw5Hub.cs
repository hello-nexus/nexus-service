using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Aw5;

/// <summary>One present pump display: which ODM built it and the HID interface that takes its report.</summary>
public sealed record Aw5PanelTarget(Aw5Variant Variant, string Path, string? Serial);

/// <summary>
/// Owns the AW5 pump displays: finds the interface each variant renders from, holds
/// a handle per panel, and writes frames. A panel that fails a write is closed so the
/// next tick reopens it, rather than writing into a dead handle forever.
///
/// Only valid while no vendor driver runs. The vendor binary and this hub would be
/// two writers on one HID; the driver .exe path is disabled for exactly this reason
/// (see DriverExePolicy).
/// </summary>
public sealed class Aw5Hub : IDisposable
{
    private readonly IHidEnumerator _hid;
    private readonly Dictionary<string, IHidDevice> _open = new(StringComparer.OrdinalIgnoreCase);

    public Aw5Hub(IHidEnumerator hid)
    {
        _hid = hid;
    }

    /// <summary>
    /// Every AW5 panel on the bus. Levelplay is a composite whose renderable interface
    /// is the vendor-defined FF01 collection (the other seven are keyboard/consumer
    /// collections the panel also presents); CoolerMaster exposes a single HID, matched
    /// on its 64-byte output report.
    /// </summary>
    public IReadOnlyList<Aw5PanelTarget> Discover()
    {
        var found = new List<Aw5PanelTarget>();
        try
        {
            foreach (var i in _hid.Find(Aw5Protocol.IbpVid, Aw5Protocol.LevelplayPid))
            {
                if (i.UsagePage == Aw5Protocol.LevelplayUsagePage)
                    found.Add(new Aw5PanelTarget(Aw5Variant.Levelplay, i.Path, i.Serial));
            }
            foreach (var i in _hid.Find(Aw5Protocol.IbpVid, Aw5Protocol.CoolerMasterPid))
            {
                if (i.OutputReportByteLength == Aw5Protocol.ReportLength)
                    found.Add(new Aw5PanelTarget(Aw5Variant.CoolerMaster, i.Path, i.Serial));
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[aw5] discover failed: {ex.GetType().Name}: {ex.Message}");
        }
        return found;
    }

    /// <summary>
    /// Renders one reading onto one panel. Returns false when the panel could not be
    /// opened or refused a frame; the handle is dropped so the next call reopens.
    /// </summary>
    public async Task<bool> RenderAsync(Aw5PanelTarget target, Aw5PanelReading reading, CancellationToken ct)
    {
        var dev = Acquire(target.Path);
        if (dev is null) return false;

        try
        {
            if (target.Variant == Aw5Variant.Levelplay)
            {
                var frames = Aw5Protocol.BuildLevelplayCycle(
                    reading.TempC, reading.LoadPct, reading.Mhz);
                for (var i = 0; i < frames.Length; i++)
                {
                    if (!dev.SetFeature(frames[i])) return Drop(target.Path);
                    // Vendor spacing between the frames of one cycle; the panel has not
                    // been tested accepting them back to back.
                    if (i < frames.Length - 1)
                        await Task.Delay(Aw5Protocol.LevelplayInterFrameMs, ct);
                }
                return true;
            }

            var frame = Aw5Protocol.BuildCoolerMasterFrame(reading.LoadPct, reading.Mhz, reading.TempC);
            return dev.Write(frame) || Drop(target.Path);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[aw5] render {target.Variant} failed: {ex.GetType().Name}: {ex.Message}");
            return Drop(target.Path);
        }
    }

    /// <summary>Darkens a CoolerMaster panel at once. Levelplay has no known blank frame; it self-reverts.</summary>
    public void Blank(Aw5PanelTarget target)
    {
        if (target.Variant != Aw5Variant.CoolerMaster) return;
        var dev = Acquire(target.Path);
        if (dev is null) return;
        try { dev.Write(Aw5Protocol.BuildCoolerMasterBlankFrame()); }
        catch (Exception ex) { ServiceLog.Warn($"[aw5] blank failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private IHidDevice? Acquire(string path)
    {
        if (_open.TryGetValue(path, out var dev)) return dev;
        try
        {
            dev = _hid.Open(path);
            if (dev is null) return null;
            _open[path] = dev;
            return dev;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[aw5] open failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private bool Drop(string path)
    {
        if (_open.Remove(path, out var dev))
        {
            try { dev.Dispose(); } catch { /* already gone */ }
        }
        return false;
    }

    /// <summary>Closes handles for panels no longer on the bus, so an unplug does not leak one.</summary>
    public void CloseAbsent(IReadOnlyList<Aw5PanelTarget> present)
    {
        var live = present.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _open.Keys.ToArray())
        {
            if (!live.Contains(path)) Drop(path);
        }
    }

    public void CloseAll()
    {
        foreach (var path in _open.Keys.ToArray()) Drop(path);
    }

    public void Dispose() => CloseAll();
}
