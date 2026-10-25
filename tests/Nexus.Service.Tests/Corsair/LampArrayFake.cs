using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hid.LampArray;

namespace Nexus.Service.Tests.Corsair;

/// <summary>A scripted LampArray collection: value caps, attribute reports and a log of every feature report sent.</summary>
internal sealed class FakeLampArrayDevice : IHidDevice, IHidFeatureCaps
{
    public const int Attr = 1, Req = 2, Resp = 3, Multi = 4, Range = 5, Ctrl = 6;

    public int LampCount { get; set; } = 4;
    public bool FailSets { get; set; }
    public bool FailGets { get; set; }
    public List<byte[]> Sets { get; } = new();
    public bool Disposed { get; private set; }
    private int _requested;

    public int VendorId => 0x1B1C;
    public int ProductId => 0x2B18;
    public string Path => "dials";
    public string? Serial => "SD1";
    public int UsagePage => 0x59;
    public int Usage => 1;

    public static HidValueCap Cap(int report, int linkUsage, int usage, int bits, int count) =>
        new(report, 0x59, usage, 0x59, linkUsage, bits, count);

    public IReadOnlyList<HidValueCap>? CapsOverride { get; set; }

    public IReadOnlyList<HidValueCap>? ReadFeatureValueCaps() => CapsOverride ?? new[]
    {
        Cap(Attr, 0x02, 0x03, 16, 1), Cap(Attr, 0x02, 0x04, 32, 1),
        Cap(Req, 0x20, 0x21, 16, 1),
        Cap(Resp, 0x22, 0x21, 16, 1), Cap(Resp, 0x22, 0x23, 32, 1),
        Cap(Multi, 0x50, 0x03, 8, 1), Cap(Multi, 0x50, 0x55, 8, 1), Cap(Multi, 0x50, 0x21, 16, 8), Cap(Multi, 0x50, 0x51, 8, 8),
        Cap(Range, 0x60, 0x55, 8, 1), Cap(Range, 0x60, 0x61, 16, 1),
        Cap(Ctrl, 0x70, 0x71, 8, 1),
    };

    public bool SetFeature(ReadOnlySpan<byte> report)
    {
        if (FailSets) return false;
        Sets.Add(report.ToArray());
        if (report[0] == Req) _requested = report[1] | (report[2] << 8);
        return true;
    }

    public bool GetFeature(Span<byte> buffer)
    {
        if (FailGets) return false;
        if (buffer[0] == Attr)
        {
            buffer[1] = (byte)LampCount;
            buffer[2] = (byte)(LampCount >> 8);
            buffer[3] = 0x10;
            return true;
        }
        if (buffer[0] == Resp)
        {
            buffer[1] = (byte)_requested;
            buffer[3] = (byte)(_requested * 10);
            buffer[7] = (byte)(_requested * 5);
            buffer[23] = buffer[24] = buffer[25] = buffer[26] = 255;
            _requested++;
            return true;
        }
        return false;
    }

    public bool GetInputReport(Span<byte> buffer) => false;
    public bool Write(ReadOnlySpan<byte> report) => true;
    public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
    public int Read(Span<byte> buffer, int timeoutMs) => 0;
    public void Dispose() => Disposed = true;
}

internal sealed class FakeLampArrayHid : IHidEnumerator
{
    public FakeLampArrayDevice Device { get; } = new();
    public int UsagePage { get; set; } = 0x59;
    public int Opens { get; private set; }
    public bool RefuseOpen { get; set; }
    public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => FindAll();
    public IReadOnlyList<HidDeviceInfo> FindAll() => new[]
    {
        new HidDeviceInfo { VendorId = 0x1B1C, ProductId = 0x2B18, Path = "sd", Serial = "SD1", UsagePage = 0x0C, Usage = 1 },
        new HidDeviceInfo { VendorId = 0x1B1C, ProductId = 0x2B18, Path = "dials", Serial = "SD1", UsagePage = UsagePage, Usage = 1, FeatureReportByteLength = 51 },
    };
    public IHidDevice? Open(string path, bool forInput = false)
    {
        if (path != "dials" || RefuseOpen) return null;
        Opens++;
        return Device;
    }
}
