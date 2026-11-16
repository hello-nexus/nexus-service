using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hid;

/// <summary>
/// Enumerates HID devices matching a VID/PID filter and opens them for vendor-protocol
/// communication. Platform-specific (Windows uses SetupAPI + hid.dll).
/// </summary>
public interface IHidEnumerator
{
    /// <summary>Returns metadata for all HID interfaces matching (vendorId, productId). Does not open.</summary>
    IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId);

    /// <summary>
    /// <see cref="Find"/> for a rescan: reports every matching path (<c>Paths</c>) but reads details only for the
    /// ones not in <paramref name="knownPaths"/> (<c>Fresh</c>), so a live controller is not reopened just to prove it is still there.
    /// The default reads everything.
    /// </summary>
    (IReadOnlyList<string> Paths, IReadOnlyList<HidDeviceInfo> Fresh) Scan(int vendorId, int productId, IReadOnlyCollection<string> knownPaths)
    {
        var all = Find(vendorId, productId);
        var paths = new List<string>(all.Count);
        var fresh = new List<HidDeviceInfo>(all.Count);
        foreach (var i in all)
        {
            paths.Add(i.Path);
            if (!knownPaths.Contains(i.Path)) fresh.Add(i);
        }
        return (paths, fresh);
    }

    /// <summary>Returns metadata for every present HID interface, no VID/PID filter. Does not open.</summary>
    IReadOnlyList<HidDeviceInfo> FindAll();

    /// <summary>
    /// Opens the device at the given path. Returns null if open fails. Set
    /// <paramref name="forInput"/> for a handle used with <see cref="IHidDevice.Read"/>:
    /// on Windows this opens for overlapped I/O so the read honors its timeout instead
    /// of busy-spinning. No-op on Linux (poll() already bounds the read).
    /// </summary>
    IHidDevice? Open(string path, bool forInput = false);
}

public sealed class HidDeviceInfo
{
    public int VendorId { get; init; }
    public int ProductId { get; init; }
    public string Path { get; init; } = "";
    public string? Serial { get; init; }
    public int UsagePage { get; init; }
    public int Usage { get; init; }
    public int InputReportByteLength { get; init; }
    public int OutputReportByteLength { get; init; }
    public int FeatureReportByteLength { get; init; }
}
