using System;
using System.Collections.Generic;
using Nexus.Service.Devices.Handlers;

namespace Nexus.Service.Devices;

/// <summary>
/// Brand policy for the Nexus Control gate. A third-party hub defaults off
/// precisely when a competing vendor app also drives it, so Nexus does not fight
/// that app until the user opts in after closing it (the device page surfaces
/// which app to close). Hyte/iBUYPOWER hardware, streamed panels (which have no
/// on/off row to re-enable), and any handler without a mapped competitor default
/// on. One source of truth for both facts.
/// </summary>
public static class DeviceControlPolicy
{
    private static readonly Dictionary<string, string> ConflictAppByHandler = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lianli"] = "lian-li-l-connect",
        ["lianli-tl"] = "lian-li-l-connect",
        ["lianli-wireless"] = "lian-li-l-connect",
        ["lianli-aio"] = "lian-li-l-connect",
        ["strimer"] = "lian-li-l-connect",
        ["corsair"] = "icue",
        ["tryx"] = "tryx-kanali",
        ["nzxt-kraken"] = "nzxt-cam",
        ["streamdeck"] = StreamDeckHandler.ElgatoConflictAppId,
    };

    // Hyte + iBUYPOWER hardware: the brands Nexus is built for. Every other
    // first-party handler drives third-party hardware whose support is
    // experimental (surfaced with a badge in the UI).
    private static readonly HashSet<string> FirstPartyHandlers = new(StringComparer.OrdinalIgnoreCase)
    {
        "cnvs", "keeb", "np50", "smarthub", "y70", "qseries", "fan-hub", "aw5",
        "ibp-keyboard", "ibp-mouse",
    };

    /// <summary>
    /// Handlers that default off for a reason other than a competing app: hardware whose
    /// protocol we transcribed but have never run against a unit. A wrong guess here drives
    /// someone's cooler, so these wait for an explicit opt-in even though nothing else is
    /// holding the device.
    /// </summary>
    private static readonly HashSet<string> UnverifiedHandlers = new(StringComparer.OrdinalIgnoreCase)
    {
        "lianli-galahad2-lcd", "corsair-xc7-lcd", "corsair-capellix-lcd", "idcooling-fx-lcd",
        "asrock-lcd", "corsair-link-lcd",
        "thermalright-lcd", "asus-ryujin-lcd", "lianli-screen88",
    };

    public static bool DefaultOn(string handlerId) =>
        !ConflictAppByHandler.ContainsKey(handlerId) && !UnverifiedHandlers.Contains(handlerId);

    public static string? ConflictAppFor(string handlerId)
        => ConflictAppByHandler.TryGetValue(handlerId, out var id) ? id : null;

    /// <summary>
    /// True for handlers driving non-Hyte/iBUYPOWER hardware, whose support is
    /// experimental. Drives the "Experimental" badge in the UI.
    /// </summary>
    public static bool IsExperimental(string handlerId) => !FirstPartyHandlers.Contains(handlerId);
}
