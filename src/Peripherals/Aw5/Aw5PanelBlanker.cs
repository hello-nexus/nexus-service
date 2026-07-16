using System;
using System.Linq;
using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Aw5;

/// <summary>
/// Darkens the AW5's pump display. The cooler renders whatever the host last sent
/// and only self-clears about 30s after the frames stop, so killing the vendor
/// driver alone leaves a stale reading on the glass; one blanking report clears it
/// at once.
///
/// Only valid with no vendor driver running - it and this would be two writers on
/// one HID.
/// </summary>
public sealed class Aw5PanelBlanker : IDriverGateStopHook
{
    private const int IbpVid = 0x3402;

    // Levelplay (0x0406) is absent: it takes a different report over SET_REPORT
    // with its own layout, and no blanking frame is known for it.
    private static readonly int[] BlankablePids = { 0x0407 };

    // A report carrying only its id blanks the panel: bench-verified by switching
    // a live frame stream to this one with no gap, which darkened the glass while
    // writes kept succeeding. Zeroing the payload's own constants (bytes 1 and 12)
    // does nothing, so this is the whole command.
    private const byte ReportId = 0x10;

    /// <summary>The panel report is 64 bytes; 0407 exposes exactly one interface carrying it.</summary>
    private const int ReportLength = 64;

    private readonly IHidEnumerator _hid;

    public Aw5PanelBlanker(IHidEnumerator hid)
    {
        _hid = hid;
    }

    public string DeviceId => Aw5Handler.HandlerId;

    public void OnGatedOff() => BlankAll();

    /// <summary>
    /// Blanks every blankable AW5 on the bus. Best-effort: a cooler that is gone,
    /// or whose panel refuses, needs no recovery - the display self-clears anyway.
    /// </summary>
    public void BlankAll()
    {
        foreach (var pid in BlankablePids)
        {
            foreach (var info in Find(pid))
            {
                try
                {
                    using var dev = _hid.Open(info.Path);
                    if (dev is null) continue;
                    var report = new byte[ReportLength];
                    report[0] = ReportId;
                    if (dev.Write(report)) ServiceLog.Info($"[aw5] panel blanked ({pid:X4})");
                }
                catch (Exception ex)
                {
                    ServiceLog.Warn($"[aw5] blank {pid:X4} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>The interface that takes the panel report, if the cooler is present.</summary>
    private HidDeviceInfo[] Find(int pid)
    {
        try
        {
            return _hid.Find(IbpVid, pid).Where(i => i.OutputReportByteLength == ReportLength).ToArray();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[aw5] hid enumerate {pid:X4} failed: {ex.GetType().Name}: {ex.Message}");
            return Array.Empty<HidDeviceInfo>();
        }
    }
}
