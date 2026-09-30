using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Peripherals.Hyte.Np50;
using Xunit;

namespace Nexus.Service.Tests;

public class DfuProductKeyTests
{
    private static byte[] Slot(params byte[] head) =>
        head.Concat(Enumerable.Repeat((byte)0xFF, DfuProductKey.SlotLength - head.Length)).ToArray();

    [Theory]
    [InlineData(new byte[] { 0xDD, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00 }, 0x0400)] // Q60
    [InlineData(new byte[] { 0xDD, 0x00, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00 }, 0x0403)] // Q80
    [InlineData(new byte[] { 0xDD, 0x00, 0x01, 0x09, 0x00, 0x00, 0x00, 0x00 }, 0x0901)] // NP50 / Smart Hub
    public void ParseProductId_reads_the_little_endian_key_word(byte[] head, int expected)
    {
        Assert.Equal(expected, DfuProductKey.ParseProductId(Slot(head)));
    }

    [Fact]
    public void ParseProductId_is_null_for_an_erased_slot()
    {
        Assert.Null(DfuProductKey.ParseProductId(Slot()));
    }

    [Fact]
    public void ParseProductId_is_null_without_the_dfu_flag_byte()
    {
        Assert.Null(DfuProductKey.ParseProductId(Slot(0xDC, 0x00, 0x00, 0x04)));
    }

    [Theory]
    [InlineData(0x0400, "q60", "qseries")]
    [InlineData(0x0403, "q80", "qseries")]
    [InlineData(0x0900, "fan-hub", "fan-hub")]
    [InlineData(0x0B00, "cnvs-left", "cnvs")]
    [InlineData(0x0B01, "cnvs-v1", "cnvs")]
    [InlineData(0x0C00, "y70-touch", "y70")]
    [InlineData(0x0C01, "y70-infinite", "y70")]
    [InlineData(0x0C02, "y70-truly", "y70")]
    public void IdentityForProductId_maps_a_key_to_its_image_and_handler(int pid, string firmwareType, string handlerId)
    {
        var id = DfuProductKey.IdentityForProductId(pid);
        Assert.NotNull(id);
        Assert.Equal(firmwareType, id!.FirmwareType);
        Assert.Equal(handlerId, id.HandlerId);
    }

    [Theory]
    [InlineData(0x0901)] // NP50's PID and the Smart Hub's key
    [InlineData(0x0402)] // P60: no bundled image family
    [InlineData(0x0BFF)] // CNVS CES unit
    public void IdentityForProductId_refuses_ambiguous_or_unknown_keys(int pid)
    {
        Assert.Null(DfuProductKey.IdentityForProductId(pid));
    }

    [Fact]
    public void No_handler_claims_the_bootloader_pid()
    {
        var bootloader = new UsbDeviceEntry { VendorId = DfuRecoveryMonitor.DfuVendorId, ProductId = DfuRecoveryMonitor.DfuProductId };
        var np50 = new Np50Handler(new Np50Hub(new NoPorts(), _ => throw new InvalidOperationException()));
        IDeviceHandler[] handlers = { TestHandlers.FanHub(), np50 };
        foreach (var h in handlers)
            Assert.False(h.IsConnected(new List<UsbDeviceEntry> { bootloader }), h.Id);
    }

    private sealed class NoPorts : INp50PortDiscovery
    {
        public IReadOnlyList<Np50PortInfo> Discover() => Array.Empty<Np50PortInfo>();
    }
}
