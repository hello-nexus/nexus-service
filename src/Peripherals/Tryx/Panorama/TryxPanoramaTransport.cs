using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

public interface ITryxPanoramaPanelDiscovery
{
    IReadOnlyList<TryxPanoramaPortInfo> Discover();
}

public sealed class TryxPanoramaPortInfo
{
    public required string PortName { get; init; }
    public string Serial { get; init; } = "";
    public int ProductId { get; init; }
    public string AdbSerial { get; init; } = "";
}

public interface ITryxPanoramaTransport : IDisposable
{
    bool IsOpen { get; }
    string Serial { get; }
    string PortName { get; }
    void Write(ReadOnlySpan<byte> data);

    /// <summary>Wallpaper preset ids (e.g. "default_01") the panel reported it has
    /// stored on device. Empty when the transport has not received or does not
    /// support the media-list push (see <see cref="TryxMediaList"/>).</summary>
    IReadOnlyList<string> AvailableMediaIds => Array.Empty<string>();

    /// <summary>All media filenames the panel last reported it has stored (preset,
    /// download, and custom), or empty if it has not pushed its list this session.</summary>
    IReadOnlyList<string> AvailableMediaFilenames => Array.Empty<string>();

    /// <summary>Custom user uploads only (panel /userdata/user/), excluding presets and cloud
    /// downloads (/userdata/default/). The library list; empty until a push arrives.</summary>
    IReadOnlyList<string> AvailableCustomMediaFilenames => Array.Empty<string>();

    /// <summary>Per-file sizes from the panel's last media-list push, keyed by basename;
    /// empty until a list is received (see <see cref="TryxMediaList.ParseMediaEntries"/>).</summary>
    IReadOnlyDictionary<string, long> MediaFileSizes => EmptyMediaFileSizes;

    /// <summary>Increments every time <see cref="MediaFileSizes"/> is replaced by a fresh
    /// panel push, so a caller can tell a re-sync is needed without diffing the dictionary.</summary>
    int MediaListVersion => 0;

    /// <summary>The panel's own serial_number (BYZL...) from its device_info reply, empty until
    /// received. Required as the sn on CMD_Get_FileList and other locked-serial commands; it is
    /// not the USB chip id.</summary>
    string PanelSerial => "";

    /// <summary>Sends a file_pull_request and waits for its response chunk (data still masked);
    /// not Ok on a panel error reply, null on timeout or when the transport cannot pull.</summary>
    TryxMediaList.FilePullChunk? PullFileChunk(string deviceFileName, long offset, int timeoutMs) => null;

    private static readonly IReadOnlyDictionary<string, long> EmptyMediaFileSizes = new Dictionary<string, long>();
}
