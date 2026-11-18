using System;
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

    public HidCorsairPsuTransport(IHidEnumerator hid)
    {
        _hid = hid;
    }

    public CorsairPsuInfo? Find()
    {
        foreach (var info in _hid.FindAll())
        {
            if (CorsairPsuProtocol.IsSupported(info.VendorId, info.ProductId))
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
