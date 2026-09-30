using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Tests.Hid;

public class HidPathIdsTests
{
    private const string Headset = @"\\?\hid#vid_1b1c&pid_0a3e&mi_03&col01#9&18ce1461&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string Nollie = @"\\?\HID#VID_16D5&PID_2A08&MI_02#a&1cc9ae2&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string Bluetooth = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002046d_pid&b01a#8&2a4c1e3b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    [Fact]
    public void Reads_the_usb_ids_in_either_case()
    {
        Assert.True(HidPathIds.TryParse(Headset, out var vid, out var pid));
        Assert.Equal((0x1B1C, 0x0A3E), (vid, pid));
        Assert.True(HidPathIds.TryParse(Nollie, out vid, out pid));
        Assert.Equal((0x16D5, 0x2A08), (vid, pid));
    }

    [Fact]
    public void Another_vendors_usb_device_is_skipped_before_it_is_opened()
    {
        Assert.False(HidPathIds.MayMatch(Headset, 0x16D5, 0x2A08));
        Assert.True(HidPathIds.MayMatch(Nollie, 0x16D5, 0x2A08));
    }

    [Theory]
    [InlineData(Bluetooth)]
    [InlineData(@"\\?\hid#hidclass&col01#1&2d595ca7&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}")]
    [InlineData(@"\\?\hid#vid_12")]
    [InlineData(@"\\?\hid#vid_zzzz&pid_0001#x")]
    public void A_path_without_readable_usb_ids_falls_back_to_the_attribute_check(string path)
    {
        Assert.False(HidPathIds.TryParse(path, out _, out _));
        Assert.True(HidPathIds.MayMatch(path, 0x16D5, 0x2A08));
    }
}
