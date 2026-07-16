using System;
using System.IO;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>Persistent host-side store for transcoded Tryx Panorama media files.</summary>
public static class TryxMediaStore
{
    public static string StoreDir { get; } = System.IO.Path.Combine(
        Nexus.Service.Media.MediaLibrary.DeviceStoreDir("tryx"), "media");

    public static string Path(string deviceFileName)
        => System.IO.Path.Combine(StoreDir, deviceFileName);

    public static bool Exists(string deviceFileName)
    {
        if (!TryxThumbnailCache.IsSafeDeviceName(deviceFileName))
        {
            return false;
        }
        return File.Exists(Path(deviceFileName));
    }

    /// <summary>Copies <paramref name="sourcePath"/> into the store under <paramref name="deviceFileName"/>;
    /// overwrites any prior copy. No-op for unsafe names. Best-effort.</summary>
    public static void SaveCopy(string sourcePath, string deviceFileName)
    {
        if (!TryxThumbnailCache.IsSafeDeviceName(deviceFileName))
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(StoreDir);
            File.Copy(sourcePath, Path(deviceFileName), overwrite: true);
        }
        catch { /* best effort */ }
    }
}
