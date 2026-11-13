using System;
using System.IO;
using System.Linq;
using Nexus.Service.Peripherals.Corsair.Galleon;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Corsair;

/// <summary>The per-device "save colors to the keyboard" setting round-trips through the real settings file and parses the onboard objects.</summary>
public class GalleonSdSaveToDeviceSettingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-test-galleon-" + Guid.NewGuid().ToString("N")[..8]);

    public GalleonSdSaveToDeviceSettingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Default_is_off_and_a_stored_id_survives_a_reopen()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new JsonConfigStore(path);
        try
        {
            Assert.Empty(store.Load().Devices.SaveToDeviceLightingDevices);
            store.Update(s => s.Devices.SaveToDeviceLightingDevices = new() { "corsair-galleon:SN1" });
        }
        finally
        {
            store.Dispose();
        }

        var reopened = new JsonConfigStore(path);
        try
        {
            Assert.Equal(new[] { "corsair-galleon:SN1" }, reopened.Load().Devices.SaveToDeviceLightingDevices);
        }
        finally
        {
            reopened.Dispose();
        }
    }

    [Fact]
    public void Parser_finds_registry_profile_layer_list_in_a_reply_with_or_without_the_report_id_byte()
    {
        var body = new byte[] { 0x13, 0x00, 0x02, 0x00, 0x60, 0x6D, 0x65, 0x6D };
        var reply = new byte[128];
        body.CopyTo(reply, 8);
        Assert.True(GalleonSdOnboardProbe.TryParseRegistry(reply, out var refs));
        Assert.Equal(new ushort[] { 0x6D60, 0x6D65 }, refs);

        var withId = new byte[129];
        reply.CopyTo(withId, 1);
        Assert.True(GalleonSdOnboardProbe.TryParseRegistry(withId, out refs));
        Assert.Equal(new ushort[] { 0x6D60, 0x6D65 }, refs);

        var profile = new byte[128];
        new byte[] { 0x1E, 0xF1, 1, 2, 3, 4, 0x66, 0x6D, 0x61, 0x6D, 0, 0, 0x62, 0x6D }.CopyTo(profile, 8);
        Assert.True(GalleonSdOnboardProbe.TryParseProfileLayerList(profile, out var list));
        Assert.Equal((ushort)0x6D62, list);

        var layers = new byte[128];
        new byte[] { 0x69, 0x6C, 0x02, 0x00, 0x08, 0x00, 0x6B, 0x6D, 0x08, 0x00, 0x6F, 0x6D }.CopyTo(layers, 8);
        Assert.True(GalleonSdOnboardProbe.TryParseLayerList(layers, out var ids));
        Assert.Equal(new ushort[] { 0x6D6B, 0x6D6F }, ids);

        Assert.False(GalleonSdOnboardProbe.TryParseRegistry(new byte[128], out _));
        Assert.False(GalleonSdOnboardProbe.TryParseLayerList(new byte[128], out _));
    }

    [Fact]
    public void Read_only_builders_use_the_open_size_read_close_commands_and_never_a_write()
    {
        Assert.Equal(new byte[] { 0, 1, 0, 0x0D, 0, 0x0F, 0, 0 }, GalleonSdProtocol.StartTransaction(0, 0x000F)[..8]);
        Assert.Equal(new byte[] { 0, 1, 0, 0x09, 0 }, GalleonSdProtocol.ResourceSize(0)[..5]);
        Assert.Equal(new byte[] { 0, 1, 0, 0x08, 0 }, GalleonSdProtocol.ResourceRead(0)[..5]);
        Assert.Equal(new byte[] { 0, 1, 0, 0x05, 1, 0 }, GalleonSdProtocol.StopTransaction(0)[..6]);
    }
}
