using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Aw5;
using Nexus.Service.Peripherals.Hid;
using Xunit;

namespace Nexus.Service.Tests.Aw5;

/// <summary>
/// Pins the bench-verified blanking frame. The panel self-clears ~30s after frames
/// stop, so a wrong frame here is invisible on hardware - it looks like the fade.
/// </summary>
public class Aw5PanelBlankerTests
{
    private const int Vid = 0x3402;
    private const int CoolerMaster = 0x0407;
    private const int Levelplay = 0x0406;

    [Fact]
    public void BlankAll_sends_report_id_10_and_an_otherwise_zero_64_byte_payload()
    {
        var hid = new FakeHid((Vid, CoolerMaster, 64));

        new Aw5PanelBlanker(hid).BlankAll();

        var report = Assert.Single(hid.Writes);
        Assert.Equal(64, report.Length);
        Assert.Equal(0x10, report[0]);
        // Bench: zeroing bytes 1 and 12 alone does nothing; only the whole-zero
        // payload darkens the panel, so every byte after the id must stay zero.
        Assert.All(report.Skip(1), b => Assert.Equal(0, b));
    }

    [Fact]
    public void BlankAll_leaves_levelplay_alone()
    {
        // 0406 takes a different report over SET_REPORT and no blanking frame is
        // known for it; writing this one at it is a guess, not a fix.
        var hid = new FakeHid((Vid, Levelplay, 64));

        new Aw5PanelBlanker(hid).BlankAll();

        Assert.Empty(hid.Writes);
        Assert.DoesNotContain(Levelplay, hid.Queried);
    }

    [Fact]
    public void BlankAll_ignores_an_interface_that_cannot_carry_the_panel_report()
    {
        // The AW5 also presents keyboard collections; only the 64-byte one is the panel.
        var hid = new FakeHid((Vid, CoolerMaster, 9), (Vid, CoolerMaster, 2));

        new Aw5PanelBlanker(hid).BlankAll();

        Assert.Empty(hid.Writes);
    }

    [Fact]
    public void BlankAll_survives_an_absent_cooler_and_a_refusing_panel()
    {
        // Best-effort: the display self-clears anyway, so neither is worth throwing.
        new Aw5PanelBlanker(new FakeHid()).BlankAll();

        var refusing = new FakeHid((Vid, CoolerMaster, 64)) { WriteResult = false };
        new Aw5PanelBlanker(refusing).BlankAll();
        Assert.Single(refusing.Writes);

        var throwing = new FakeHid((Vid, CoolerMaster, 64)) { ThrowOnOpen = true };
        new Aw5PanelBlanker(throwing).BlankAll();
        Assert.Empty(throwing.Writes);
    }

    private sealed class FakeHid : IHidEnumerator
    {
        private readonly (int Vid, int Pid, int OutLen)[] _devices;
        public FakeHid(params (int Vid, int Pid, int OutLen)[] devices) { _devices = devices; }

        public List<byte[]> Writes { get; } = new();
        public List<int> Queried { get; } = new();
        public bool WriteResult { get; init; } = true;
        public bool ThrowOnOpen { get; init; }

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId)
        {
            Queried.Add(productId);
            return _devices
                .Where(d => d.Vid == vendorId && d.Pid == productId)
                .Select(d => new HidDeviceInfo
                {
                    Path = $"fake-{d.Pid:X4}-{d.OutLen}",
                    VendorId = d.Vid,
                    ProductId = d.Pid,
                    OutputReportByteLength = d.OutLen,
                })
                .ToArray();
        }

        public IReadOnlyList<HidDeviceInfo> FindAll() => Array.Empty<HidDeviceInfo>();

        public IHidDevice? Open(string path, bool forInput = false)
            => ThrowOnOpen ? throw new InvalidOperationException("open failed") : new FakeDevice(this);

        private sealed class FakeDevice : IHidDevice
        {
            private readonly FakeHid _owner;
            public FakeDevice(FakeHid owner) { _owner = owner; }
            public int VendorId => Vid;
            public int ProductId => CoolerMaster;
            public string Path => "fake";
            public string? Serial => null;
            public int UsagePage => 0xFF00;
            public int Usage => 1;
            public bool Write(ReadOnlySpan<byte> report) { _owner.Writes.Add(report.ToArray()); return _owner.WriteResult; }
            public bool SetFeature(ReadOnlySpan<byte> report) => throw new NotSupportedException();
            public bool GetFeature(Span<byte> buffer) => throw new NotSupportedException();
            public bool GetInputReport(Span<byte> buffer) => throw new NotSupportedException();
            public bool SetOutputReport(ReadOnlySpan<byte> report) => throw new NotSupportedException();
            public int Read(Span<byte> buffer, int timeoutMs) => throw new NotSupportedException();
            public void Dispose() { }
        }
    }
}
