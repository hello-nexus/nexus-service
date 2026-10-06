#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Windows display brightness via the in-box DDC/CI APIs in dxva2.dll
/// (GetMonitorBrightness / SetMonitorBrightness / Get/SetVCPFeature). Works for
/// external monitors that expose DDC over the video cable. Internal laptop
/// panels are not handled here; they need WmiMonitorBrightnessMethods or
/// IOCTL_VIDEO_*.
/// </summary>
public sealed class WindowsDisplayBrightnessProvider : IDisplayBrightnessProvider
{
    private const int DefaultDdcWriteCooldownMs = 160;

    public string Hint => "";

    // Capability-probe cache. Enumerate() runs on every GET /displays, and the
    // Displays panel widget polls that on a timer for as long as it is placed -
    // so probing VCP 0x10/0x12 per display per call meant hundreds of DDC
    // transactions an hour to answer a question the hardware answers the same
    // way every time. What a monitor supports is a property of the attached
    // set, so the probe is cached until that set changes. The trade-off is the
    // cached Current: a brightness change made on the monitor's own OSD is not
    // noticed until the next topology change, while every change made through
    // Nexus updates the cache on the way past.
    private readonly object _capsGate = new();
    private string _capsSignature = "";
    private readonly Dictionary<string, DisplayDto> _capsCache = new(StringComparer.Ordinal);

    /// <summary>
    /// This provider runs in the user-session helper, whose stdout goes
    /// nowhere (see <see cref="Nexus.Service.Platform.HelperLog"/>), so every
    /// line here has to reach nexus-helper.log to exist at all.
    /// </summary>
    private static void Log(string message) => Nexus.Service.Platform.HelperLog.Write($"[ddc] {message}");

    /// <summary>Reason of the last skip, so a locked hour is one line rather
    /// than seven hundred.</summary>
    private string _lastSkipReason = "";

    private bool Skip(string op, string id)
    {
        if (!DdcGate.ShouldSkip(out var reason))
        {
            _lastSkipReason = "";
            return false;
        }
        if (reason != _lastSkipReason)
        {
            _lastSkipReason = reason;
            Log($"skipping transactions: {reason} (first was {op} id={id})");
        }
        return true;
    }

    public IReadOnlyList<DisplayDto> Enumerate(IReadOnlyCollection<string>? excludedIds = null)
    {
        var results = new List<DisplayDto>();
        try
        {
            var monitors = EnumerateHMonitors();
            InvalidateCapsIfTopologyChanged(monitors);
            var gated = DdcGate.ShouldSkip(out var gateReason);
            if (gated) Log($"enumerate: probing suppressed ({gateReason})");
            for (int idx = 0; idx < monitors.Count; idx++)
            {
                var entry = monitors[idx];
                var (id, name, manufacturer, model, isInternal) = WindowsDisplayIdentity.ResolveIdentity(entry.AdapterDevice);
                // Show "Display 1", "Display 2", ... when no friendly name is
                // available (EnumDisplayDevices left DeviceString empty / fell
                // back to the raw \\.\DISPLAYn adapter token).
                if (string.IsNullOrEmpty(name) || name.StartsWith(@"\\.\", StringComparison.Ordinal))
                {
                    name = $"Display {idx + 1}";
                }
                var dto = new DisplayDto
                {
                    Id = id,
                    Name = name,
                    Manufacturer = manufacturer,
                    Model = model,
                    PhysicalDescription = manufacturer.Equals("DEL", StringComparison.OrdinalIgnoreCase)
                        ? GetPhysicalDescription(entry.HMonitor) : "",
                    IsInternal = isInternal,
                };
                if (manufacturer.Equals("DEL", StringComparison.OrdinalIgnoreCase)
                    && !dto.PhysicalDescription.Contains("AW3225QF", StringComparison.OrdinalIgnoreCase))
                {
                    var edidName = WindowsDisplayIdentity.ReadEdidMonitorName(entry.AdapterDevice);
                    if (edidName.Contains("AW3225QF", StringComparison.OrdinalIgnoreCase))
                        dto.PhysicalDescription = edidName;
                }

                dto.BrightnessControl.UnsupportedReason = "Brightness control is not available for this display.";

                // The user's own opt-out. Enforced here rather than at the
                // route because this is where the transaction would happen:
                // a display switched off must not even be probed.
                if (excludedIds is not null && excludedIds.Contains(dto.Id))
                {
                    dto.DdcEnabled = false;
                    dto.BrightnessControl.UnsupportedReason = "Brightness control is turned off for this display.";
                    results.Add(dto);
                    continue;
                }

                if (TryServeFromCache(dto))
                {
                    results.Add(dto);
                    continue;
                }

                // No cached answer yet, and the gate says the panels are not in
                // a state worth talking to. Report unsupported for now; the
                // next poll after the session settles fills the cache in.
                if (gated)
                {
                    dto.BrightnessControl.UnsupportedReason = "Brightness control is paused while the session is locked.";
                    results.Add(dto);
                    continue;
                }

                // Try to open physical monitors and check brightness support via
                // the same control path used for writes. DDC/CI VCP 0x10 is the
                // canonical luminance feature; GetMonitorBrightness is only a
                // compatibility fallback.
                if (TryGetPhysicalMonitor(entry.HMonitor, out var phys))
                {
                    try
                    {
                        if (TryGetVcp(phys, 0x10, out var cur, out var max))
                        {
                            dto.IsDdcCapable = true;
                            dto.Capabilities.Brightness = true;
                            dto.BrightnessControl = BuildSupportedBrightnessControl(
                                RawToPercent(cur, max),
                                DisplayBrightnessControlPaths.DdcCi);
                            dto.Capabilities.Contrast = TryGetVcp(phys, 0x12, out _, out _);
                        }
                        else if (TryGetMonitorBrightness(phys, out var min2, out var cur2, out var max2))
                        {
                            dto.IsDdcCapable = true;
                            dto.Capabilities.Brightness = true;
                            dto.BrightnessControl = BuildSupportedBrightnessControl(
                                RangeToPercent(cur2, min2, max2),
                                DisplayBrightnessControlPaths.DdcCi);
                            dto.Capabilities.Contrast = TryGetVcp(phys, 0x12, out _, out _);
                        }
                    }
                    finally
                    {
                        DestroyPhysicalMonitor(phys);
                    }
                }
                Log($"probed id={dto.Id} capable={dto.IsDdcCapable} brightness={dto.Capabilities.Brightness} contrast={dto.Capabilities.Contrast}");
                // Only a successful probe is cached. A failure is transient far
                // more often than it is real - a monitor still waking, bus
                // contention - and caching it would pin the display to "no
                // brightness control" until the attached set changes, where
                // before the next poll simply retried.
                if (dto.IsDdcCapable) StoreCaps(dto);
                results.Add(dto);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-win] enumerate failed: {ex.Message}");
        }
        return results;
    }

    public int? GetBrightness(string id)
    {
        if (Skip("get", id)) return null;
        if (!TryOpenById(id, out var phys)) return null;
        try
        {
            // Read VCP 0x10 (luminance) directly. GetVCPFeatureAndVCPFeatureReply
            // round-trips DDC to the monitor each time instead of returning the
            // OS-cached value that GetMonitorBrightness can serve, so the read
            // here actually reflects the panel's current backlight setting.
            if (TryGetVcp(phys, 0x10, out var cur, out var max)) return RawToPercent(cur, max);
            return TryGetMonitorBrightness(phys, out var min2, out var cur2, out var max2)
                ? RangeToPercent(cur2, min2, max2)
                : null;
        }
        finally { DestroyPhysicalMonitor(phys); }
    }

    public DisplayBrightnessDto SetBrightness(string id, int percent)
    {
        var requested = ClampPercent(percent);
        if (Skip("set", id))
        {
            return FailedBrightness(id, requested, "Brightness control is paused while the session is locked.");
        }
        if (!TryOpenById(id, out var phys))
        {
            return FailedBrightness(id, requested, "Display not found or brightness control unavailable.");
        }

        try
        {
            // Prefer the raw VCP path because it is the same cross-monitor
            // control exposed by DDC/CI. Some drivers make the DXVA brightness
            // wrapper look successful while not updating the physical monitor.
            if (TryGetVcp(phys, 0x10, out _, out var max) && max > 0)
            {
                var raw = PercentToRaw(requested, max);
                if (SetVCPFeature(phys, 0x10, raw))
                {
                    var applied = TryGetVcp(phys, 0x10, out var curAfter, out var maxAfter)
                        ? RawToPercent(curAfter, maxAfter)
                        : requested;
                    UpdateCachedBrightness(id, applied);
                    return AppliedBrightness(id, requested, applied);
                }
            }

            if (TryGetMonitorBrightness(phys, out var min2, out _, out var max2))
            {
                var raw = PercentToRange(requested, min2, max2);
                if (SetMonitorBrightness(phys, raw))
                {
                    var applied = TryGetMonitorBrightness(phys, out var minAfter, out var curAfter, out var maxAfter)
                        ? RangeToPercent(curAfter, minAfter, maxAfter)
                        : requested;
                    UpdateCachedBrightness(id, applied);
                    return AppliedBrightness(id, requested, applied);
                }
            }

            return FailedBrightness(id, requested, "Monitor rejected the brightness write.");
        }
        finally { DestroyPhysicalMonitor(phys); }
    }

    public DisplayBrightnessWritePolicy GetBrightnessWritePolicy(string id)
    {
        // Served from the probe cache when we have one: the policy is derived
        // from the same 0x10 capability Enumerate already established, and this
        // is called on the write path where an extra round-trip is pure cost.
        lock (_capsGate)
        {
            if (_capsCache.TryGetValue(id, out var cached) && cached.Capabilities.Brightness)
            {
                return new DisplayBrightnessWritePolicy
                {
                    ControlPath = DisplayBrightnessControlPaths.DdcCi,
                    WriteMode = DisplayBrightnessWriteModes.Coalesced,
                    MinWriteIntervalMs = DefaultDdcWriteCooldownMs,
                    ReadAfterWriteDelayMs = 0,
                    VerifyAfterWrite = false,
                };
            }
        }
        if (Skip("policy", id)) return new DisplayBrightnessWritePolicy();
        if (!TryOpenById(id, out var phys)) return new DisplayBrightnessWritePolicy();
        try
        {
            if (TryGetVcp(phys, 0x10, out _, out _) ||
                TryGetMonitorBrightness(phys, out _, out _, out _))
            {
                return new DisplayBrightnessWritePolicy
                {
                    ControlPath = DisplayBrightnessControlPaths.DdcCi,
                    WriteMode = DisplayBrightnessWriteModes.Coalesced,
                    MinWriteIntervalMs = DefaultDdcWriteCooldownMs,
                    ReadAfterWriteDelayMs = 0,
                    VerifyAfterWrite = false,
                };
            }
        }
        finally { DestroyPhysicalMonitor(phys); }

        return new DisplayBrightnessWritePolicy();
    }

    public DisplayVcpDto? GetVcp(string id, byte code)
    {
        if (Skip($"getVcp 0x{code:X2}", id)) return null;
        if (!TryOpenById(id, out var phys)) return null;
        try
        {
            if (TryGetVcp(phys, code, out var cur, out var max))
            {
                return new DisplayVcpDto { Id = id, Code = code, Value = (int)cur, MaxValue = (int)max };
            }
            return null;
        }
        finally { DestroyPhysicalMonitor(phys); }
    }

    public bool SetVcp(string id, byte code, int value)
    {
        if (value < 0) return false;
        if (Skip($"setVcp 0x{code:X2}", id)) return false;
        if (!TryOpenById(id, out var phys)) return false;
        try
        {
            // Clamp to the monitor's reported MaxValue when available - some
            // monitors latch out-of-range writes into a state that requires a
            // power-cycle to recover.
            var write = (uint)value;
            if (TryGetVcp(phys, code, out _, out var max) && max > 0 && write > max)
            {
                write = max;
            }
            return SetVCPFeature(phys, code, write);
        }
        finally { DestroyPhysicalMonitor(phys); }
    }

    public string? FindDisplayIdByHardwareName(IReadOnlyList<string> nameFragments)
    {
        if (nameFragments is null || nameFragments.Count == 0) return null;
        try
        {
            foreach (var entry in EnumerateHMonitors())
            {
                var rawDeviceId = WindowsDisplayIdentity.ReadMonitorDeviceId(entry.AdapterDevice);
                if (string.IsNullOrEmpty(rawDeviceId)) continue;
                foreach (var fragment in nameFragments)
                {
                    if (rawDeviceId.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // Return the same stable id SetVcp/GetBrightness key off.
                        var (id, _, _, _, _) = WindowsDisplayIdentity.ResolveIdentity(entry.AdapterDevice);
                        return id;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-win] find by hardware name failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>Drop every cached probe when the attached set changes - a
    /// replug, a mode change, a monitor arriving. The signature is built from
    /// registry-backed identities, so computing it costs no DDC.</summary>
    private void InvalidateCapsIfTopologyChanged(List<MonitorEntry> monitors)
    {
        var ids = new List<string>(monitors.Count);
        foreach (var entry in monitors)
        {
            var (id, _, _, _, _) = WindowsDisplayIdentity.ResolveIdentity(entry.AdapterDevice);
            ids.Add(id);
        }
        ids.Sort(StringComparer.Ordinal);
        var signature = string.Join("|", ids);
        lock (_capsGate)
        {
            if (_capsSignature == signature) return;
            if (_capsSignature.Length > 0)
            {
                Log("attached displays changed; dropping the probe cache");
            }
            _capsSignature = signature;
            _capsCache.Clear();
        }
    }

    /// <summary>Copy a cached probe onto a freshly built DTO. Returns false on
    /// a miss, which is the only path that issues a transaction.</summary>
    private bool TryServeFromCache(DisplayDto dto)
    {
        lock (_capsGate)
        {
            if (!_capsCache.TryGetValue(dto.Id, out var cached)) return false;
            dto.IsDdcCapable = cached.IsDdcCapable;
            dto.Capabilities.Brightness = cached.Capabilities.Brightness;
            dto.Capabilities.Contrast = cached.Capabilities.Contrast;
            dto.BrightnessControl = CloneControl(cached.BrightnessControl);
            return true;
        }
    }

    private void StoreCaps(DisplayDto dto)
    {
        lock (_capsGate)
        {
            _capsCache[dto.Id] = new DisplayDto
            {
                Id = dto.Id,
                IsDdcCapable = dto.IsDdcCapable,
                Capabilities = new DisplayCapabilitiesDto
                {
                    Brightness = dto.Capabilities.Brightness,
                    Contrast = dto.Capabilities.Contrast,
                },
                BrightnessControl = CloneControl(dto.BrightnessControl),
            };
        }
    }

    /// <summary>Keep the cached Current honest for changes made through Nexus,
    /// so a poll can be served without a read-back.</summary>
    private void UpdateCachedBrightness(string id, int percent)
    {
        lock (_capsGate)
        {
            if (_capsCache.TryGetValue(id, out var cached)) cached.BrightnessControl.Current = percent;
        }
    }

    private static DisplayBrightnessControlDto CloneControl(DisplayBrightnessControlDto src) => new()
    {
        Supported = src.Supported,
        Min = src.Min,
        Max = src.Max,
        Current = src.Current,
        ControlPath = src.ControlPath,
        WriteMode = src.WriteMode,
        WriteCooldownMs = src.WriteCooldownMs,
        VerifyAfterWrite = src.VerifyAfterWrite,
        UnsupportedReason = src.UnsupportedReason,
    };

    // Resolve an id back to a freshly-opened physical monitor handle. Caller
    // owns the handle via DestroyPhysicalMonitor. Lazy approach: re-enumerate
    // and match by id, since dxva2 handles aren't safe to cache.
    private bool TryOpenById(string id, out IntPtr phys)
    {
        phys = IntPtr.Zero;
        foreach (var entry in EnumerateHMonitors())
        {
            var (eid, _, _, _, _) = WindowsDisplayIdentity.ResolveIdentity(entry.AdapterDevice);
            if (!string.Equals(eid, id, StringComparison.Ordinal)) continue;
            return TryGetPhysicalMonitor(entry.HMonitor, out phys);
        }
        return false;
    }

    private static bool TryGetPhysicalMonitor(IntPtr hMonitor, out IntPtr phys)
    {
        phys = IntPtr.Zero;
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0) return false;
        var arr = new PHYSICAL_MONITOR[count];
        if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, arr)) return false;
        phys = arr[0].hPhysicalMonitor;
        // Free the description-bearing entries we don't keep.
        for (uint i = 1; i < count; i++) DestroyPhysicalMonitor(arr[i].hPhysicalMonitor);
        return phys != IntPtr.Zero;
    }

    private static string GetPhysicalDescription(IntPtr hMonitor)
    {
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0) return "";
        var monitors = new PHYSICAL_MONITOR[count];
        if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors)) return "";
        try { return monitors[0].szPhysicalMonitorDescription ?? ""; }
        finally
        {
            foreach (var monitor in monitors) DestroyPhysicalMonitor(monitor.hPhysicalMonitor);
        }
    }

    private static bool TryGetMonitorBrightness(IntPtr phys, out uint min, out uint cur, out uint max)
    {
        min = 0; cur = 0; max = 0;
        try { return GetMonitorBrightness(phys, out min, out cur, out max); }
        catch { return false; }
    }

    private static bool TryGetVcp(IntPtr phys, byte code, out uint cur, out uint max)
    {
        cur = 0; max = 0;
        try
        {
            return GetVCPFeatureAndVCPFeatureReply(phys, code, IntPtr.Zero, out cur, out max);
        }
        catch { return false; }
    }

    private readonly record struct MonitorEntry(IntPtr HMonitor, string AdapterDevice);

    private static List<MonitorEntry> EnumerateHMonitors()
    {
        var list = new List<MonitorEntry>();
        bool Cb(IntPtr hMonitor, IntPtr _, IntPtr __, IntPtr ___)
        {
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(hMonitor, ref info))
            {
                list.Add(new MonitorEntry(hMonitor, info.szDevice));
            }
            return true;
        }
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Cb, IntPtr.Zero);
        return list;
    }

    // Identity resolution (stable id, EDID parsing) lives in
    // WindowsDisplayIdentity - shared with the topology provider.

    // -- P/Invoke -----------------------------------------------------------

    private const int CCHDEVICENAME = 32;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("dxva2.dll")]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll")]
    private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

    [DllImport("dxva2.dll")]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll")]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("dxva2.dll")]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte bVCPCode, IntPtr pvct, out uint pdwCurrentValue, out uint pdwMaximumValue);

    [DllImport("dxva2.dll")]
    private static extern bool SetVCPFeature(IntPtr hMonitor, byte bVCPCode, uint dwNewValue);

    private static DisplayBrightnessControlDto BuildSupportedBrightnessControl(int current, string controlPath) => new()
    {
        Supported = true,
        Min = 0,
        Max = 100,
        Current = ClampPercent(current),
        ControlPath = controlPath,
        WriteMode = DisplayBrightnessWriteModes.Coalesced,
        WriteCooldownMs = DefaultDdcWriteCooldownMs,
        VerifyAfterWrite = false,
    };

    private static DisplayBrightnessDto AppliedBrightness(string id, int requested, int applied)
    {
        applied = ClampPercent(applied);
        return new DisplayBrightnessDto
        {
            Id = id,
            RequestedBrightness = requested,
            AppliedBrightness = applied,
            Brightness = applied,
            Status = DisplayBrightnessWriteStatuses.Applied,
        };
    }

    private static DisplayBrightnessDto FailedBrightness(string id, int requested, string error) => new()
    {
        Id = id,
        RequestedBrightness = requested,
        AppliedBrightness = 0,
        Brightness = 0,
        Status = DisplayBrightnessWriteStatuses.Failed,
        Error = error,
    };

    private static int ClampPercent(int value) => value < 0 ? 0 : value > 100 ? 100 : value;

    private static int RawToPercent(uint current, uint max)
    {
        if (max == 0) return ClampPercent((int)current);
        return ClampPercent((int)Math.Round(current * 100.0 / max));
    }

    private static uint PercentToRaw(int percent, uint max)
    {
        if (max == 0) return (uint)ClampPercent(percent);
        return (uint)Math.Round(ClampPercent(percent) * max / 100.0);
    }

    private static int RangeToPercent(uint current, uint min, uint max)
    {
        if (max <= min) return ClampPercent((int)current);
        var clamped = current < min ? min : current > max ? max : current;
        return ClampPercent((int)Math.Round((clamped - min) * 100.0 / (max - min)));
    }

    private static uint PercentToRange(int percent, uint min, uint max)
    {
        if (max <= min) return (uint)ClampPercent(percent);
        return min + (uint)Math.Round(ClampPercent(percent) * (max - min) / 100.0);
    }
}
#endif
