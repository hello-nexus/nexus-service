using System.Collections.Generic;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Cross-platform abstraction for enumerating system displays and reading/writing
/// per-display brightness (and other DDC/CI VCP codes). Each OS gets its own
/// implementation; non-supported platforms fall back to <see cref="StubDisplayBrightnessProvider"/>.
/// </summary>
public interface IDisplayBrightnessProvider
{
    /// <summary>One-line hint shown in the widget when no displays are returned (e.g. "ddcutil missing").</summary>
    string Hint { get; }

    /// <summary>
    /// Enumerate all attached displays with their capability flags.
    /// <paramref name="excludedIds"/> are displays the user turned brightness
    /// control off for; an implementation must not issue any transaction to
    /// those, not even a capability probe - that opt-out exists because
    /// probing is what hangs a fragile monitor.
    /// </summary>
    IReadOnlyList<DisplayDto> Enumerate(IReadOnlyCollection<string>? excludedIds = null);

    /// <summary>Read the current brightness as 0..100; returns null if the display is unknown or unsupported.</summary>
    int? GetBrightness(string id);

    /// <summary>Write the brightness as 0..100 and return the actual applied/read-back state.</summary>
    DisplayBrightnessDto SetBrightness(string id, int percent);

    /// <summary>Provider-specific write behavior for a display. Used by the service controller, not the panel.</summary>
    DisplayBrightnessWritePolicy GetBrightnessWritePolicy(string id);

    /// <summary>Read a raw DDC/CI VCP code value (current + max). Null when unsupported.</summary>
    DisplayVcpDto? GetVcp(string id, byte code);

    /// <summary>Write a raw DDC/CI VCP code value.</summary>
    bool SetVcp(string id, byte code, int value);

    /// <summary>
    /// Find the id of the first attached display whose PnP/EDID hardware id
    /// contains any of <paramref name="nameFragments"/> (case-insensitive), or
    /// null if none match. Used to drive a specific panel (e.g. a DDC-only Y70)
    /// by its controller name rather than a user-facing display id.
    /// </summary>
    string? FindDisplayIdByHardwareName(IReadOnlyList<string> nameFragments);

    /// <summary>Asks a caching provider to rescan now, on the calling thread; the provider drops the request when it scanned recently.</summary>
    void RequestRescan() { }
}
