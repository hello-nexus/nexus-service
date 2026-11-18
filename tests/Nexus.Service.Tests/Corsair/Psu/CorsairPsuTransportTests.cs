using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Corsair.Psu;
using Nexus.Service.Peripherals.Hid;
using Xunit;

namespace Nexus.Service.Tests.Corsair.Psu;

public class CorsairPsuTransportTests
{
    private sealed class RecordingEnumerator : IHidEnumerator
    {
        public int FindAllCalls;
        public int LoudOpens;
        public int QuietOpens;
        public List<HidDeviceInfo> Devices { get; } = new();

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => throw new InvalidOperationException("Find must not be used");
        public IReadOnlyList<HidDeviceInfo> FindAll() { FindAllCalls++; return Devices; }
        public IHidDevice? Open(string path, bool forInput = false) { LoudOpens++; return null; }
        public IHidDevice? OpenQuiet(string path, bool forInput = false) { QuietOpens++; return null; }
    }

    [Fact]
    public void Find_makes_one_enumeration_and_picks_the_first_supported_device()
    {
        var hid = new RecordingEnumerator();
        hid.Devices.Add(new HidDeviceInfo { VendorId = 0x1B1C, ProductId = 0x1B4F, Path = "other" });
        hid.Devices.Add(new HidDeviceInfo { VendorId = 0x1B1C, ProductId = 0x1C1F, Path = "psu", Serial = "S1" });

        var found = new HidCorsairPsuTransport(hid).Find();

        Assert.Equal(new CorsairPsuInfo(0x1C1F, "psu", "S1"), found);
        Assert.Equal(1, hid.FindAllCalls);
    }

    [Fact]
    public void Exchange_opens_without_the_per_open_log_line()
    {
        var hid = new RecordingEnumerator();

        var reply = new HidCorsairPsuTransport(hid).Exchange(new CorsairPsuInfo(0x1C1F, "psu", null), CorsairPsuProtocol.BuildReadMode());

        Assert.Null(reply);
        Assert.Equal(1, hid.QuietOpens);
        Assert.Equal(0, hid.LoudOpens);
    }
}
