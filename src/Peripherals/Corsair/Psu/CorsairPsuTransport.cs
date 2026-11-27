using System;
using System.Collections.Generic;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.Corsair.Psu;

public sealed record CorsairPsuInfo(int ProductId, string Path, string? Serial);

public interface ICorsairPsuTransport
{
    /// <summary>The first supported PSU present, or null.</summary>
    CorsairPsuInfo? Find();

    /// <summary>
    /// Sends one frame and returns the 64-byte reply, or null when the device cannot be opened, the write
    /// fails or no reply arrives in time. Callers hold <see cref="CorsairPsuBus.Gate"/>.
    /// </summary>
    byte[]? Exchange(CorsairPsuInfo device, ReadOnlySpan<byte> frame);
}

/// <summary>Opens the device per exchange and closes it after the reply, so no handle is held between commands.</summary>
public sealed class HidCorsairPsuTransport : ICorsairPsuTransport
{
    private const int ReplyTimeoutMs = 1000;

    private readonly IHidEnumerator _hid;
    private readonly HardwarePresence _presence;

    public HidCorsairPsuTransport(IHidEnumerator hid, HardwarePresence presence)
    {
        _hid = hid;
        _presence = presence;
    }

    public CorsairPsuInfo? Find()
    {
        // Presence reads the PnP-invalidated cache and never touches a device, so a PC with no
        // Corsair PSU opens no HID interface at all. The lookups are vid/pid filtered so a
        // present PSU never causes another vendor's device (a headset dongle) to be opened.
        // Look up only the PSU pids the bus reports (normally one), so the per-pid scans stay minimal.
        var present = new List<int>();
        foreach (var entry in _presence.UsbEntriesFor(CorsairPsuProtocol.VendorId))
        {
            if (CorsairPsuProtocol.IsSupported(entry.VendorId, entry.ProductId) && !present.Contains(entry.ProductId))
            {
                present.Add(entry.ProductId);
            }
        }
        foreach (var pid in present)
        {
            foreach (var info in _hid.Find(CorsairPsuProtocol.VendorId, pid))
            {
                return new CorsairPsuInfo(info.ProductId, info.Path, info.Serial);
            }
        }
        return null;
    }

    public byte[]? Exchange(CorsairPsuInfo device, ReadOnlySpan<byte> frame)
    {
        using var dev = _hid.OpenQuiet(device.Path, forInput: true);
        if (dev is null)
        {
            return null;
        }
        var report = new byte[1 + CorsairPsuProtocol.FrameLength];
        frame.CopyTo(report.AsSpan(1));
        if (!dev.Write(report))
        {
            return null;
        }
        var buffer = new byte[1 + CorsairPsuProtocol.FrameLength];
        var read = dev.Read(buffer, ReplyTimeoutMs);
        if (read < CorsairPsuProtocol.FrameLength)
        {
            return null;
        }
        // Windows prefixes an unnumbered report with its zero id.
        var offset = read > CorsairPsuProtocol.FrameLength ? 1 : 0;
        return buffer.AsSpan(offset, CorsairPsuProtocol.FrameLength).ToArray();
    }
}
