using System;
using System.IO;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxMediaStoreTests
{
    [Theory]
    [InlineData("")]
    [InlineData("../etc/passwd")]
    [InlineData("a/b.mp4")]
    [InlineData("a\\b.mp4")]
    [InlineData("..")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Exists_returns_false_for_unsafe_names(string name)
    {
        Assert.False(TryxMediaStore.Exists(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../etc/passwd")]
    [InlineData("a/b.mp4")]
    public void SaveCopy_is_noop_for_unsafe_names(string name)
    {
        var srcPath = Path.Combine(Path.GetTempPath(), "nexus-tryx-store-noop-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(srcPath, new byte[] { 1, 2, 3 });
        try
        {
            TryxMediaStore.SaveCopy(srcPath, name);
            Assert.False(TryxMediaStore.Exists(name));
        }
        finally
        {
            try { File.Delete(srcPath); } catch { }
        }
    }

    [Fact]
    public void Path_contains_StoreDir_and_fileName()
    {
        var name = "sample.mp4";
        var result = TryxMediaStore.Path(name);
        Assert.Contains(TryxMediaStore.StoreDir, result, StringComparison.Ordinal);
        Assert.Contains(name, result, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreDir_is_under_the_devices_tryx_root()
    {
        Assert.StartsWith(
            Nexus.Service.Media.MediaLibrary.DeviceStoreDir("tryx"),
            TryxMediaStore.StoreDir, StringComparison.Ordinal);
        Assert.EndsWith(
            System.IO.Path.Combine("devices", "tryx", "media"),
            TryxMediaStore.StoreDir, StringComparison.Ordinal);
    }
}
