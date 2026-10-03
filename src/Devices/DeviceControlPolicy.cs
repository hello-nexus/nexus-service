using System;
using System.Collections.Generic;
using Nexus.Service.Devices.Handlers;

namespace Nexus.Service.Devices;

/// <summary>
/// Brand policy for the Nexus Control gate. A hub in ConflictAppByHandler
/// defaults off because its vendor app also drives it, and stays off whether or
/// not that app runs until the user turns Nexus Control on. A device whose
/// competing app starts whitelisted
/// (<see cref="Conflicts.ConflictAppDefinition.DefaultWhitelisted"/>) defaults
/// off: that app keeps the device until the user turns Nexus Control on.
/// Hyte/iBUYPOWER hardware, streamed panels (which have no on/off row to
/// re-enable), and shared buses (whose monitoring nothing else provides)
/// default on, as does any handler in neither ConflictAppByHandler nor
/// UnverifiedHandlers. One source of truth for both facts. A device the user
/// has on waits while its app runs only when that app is whitelisted
/// (<see cref="VendorAppControlPause"/>).
/// </summary>
public static class DeviceControlPolicy
{
    private static readonly Dictionary<string, string> ConflictAppByHandler = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lianli"] = "lian-li-l-connect",
        ["lianli-tl"] = "lian-li-l-connect",
        ["lianli-wireless"] = "lian-li-l-connect",
        ["lianli-aio"] = "lian-li-l-connect",
        ["lianli-hydroshift-lcd"] = "lian-li-l-connect",
        ["strimer"] = "lian-li-l-connect",
        ["lianli-screen88"] = "lian-li-l-connect",
        ["lianli-galahad2-lcd"] = "lian-li-l-connect",
        ["corsair"] = "icue",
        ["corsair-link-lcd"] = "icue",
    };

    // Hyte + iBUYPOWER hardware: the brands Nexus is built for. Every other
    // first-party handler outside StableThirdPartyHandlers drives third-party
    // hardware whose support is experimental (surfaced with a badge in the UI).
    private static readonly HashSet<string> FirstPartyHandlers = new(StringComparer.OrdinalIgnoreCase)
    {
        "cnvs", "keeb", "np50", "smarthub", "y70", "qseries", "fan-hub", "aw5",
        "ibp-keyboard", "ibp-mouse",
    };

    private static readonly HashSet<string> StableThirdPartyHandlers = new(StringComparer.OrdinalIgnoreCase)
    {
        "nzxt-kraken", "streamdeck", "tryx", "zmatrices-lcd",
    };

    /// <summary>
    /// Handlers that default off for a reason other than a competing app: hardware whose
    /// protocol we transcribed but have never run against a unit. A wrong guess here drives
    /// someone's cooler, so these wait for an explicit opt-in even though nothing else is
    /// holding the device.
    /// </summary>
    private static readonly HashSet<string> UnverifiedHandlers = new(StringComparer.OrdinalIgnoreCase)
    {
        "lianli-galahad2-lcd",
        "corsair-xc7-lcd", "corsair-capellix-lcd", "idcooling-fx-lcd",
        "asrock-lcd", "corsair-link-lcd",
        "thermalright-lcd", "asus-ryujin-lcd", "lianli-screen88",
    };

    /// <summary>Feeds <see cref="ConflictAppFor"/> alone: names a competing app for the UI hint; the handler defaults on unless that app starts whitelisted.</summary>
    private static readonly Dictionary<string, string> HintOnlyConflictAppByHandler = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tryx"] = "tryx-kanali",
        ["nzxt-kraken"] = "nzxt-cam",
        ["zmatrices-lcd"] = "zmatrices",
        ["streamdeck"] = StreamDeckHandler.ElgatoConflictAppId,
    };

    private static readonly Dictionary<string, string> BusByHandler = new(StringComparer.OrdinalIgnoreCase)
    {
        [SmbusDramHandler.HandlerId] = "smbus",
    };

    public static bool DefaultOn(string handlerId) =>
        !ConflictAppByHandler.ContainsKey(handlerId)
        && !UnverifiedHandlers.Contains(handlerId)
        && !(HintOnlyConflictAppByHandler.TryGetValue(handlerId, out var app)
            && Conflicts.ConflictWatcher.FindById(app)?.DefaultWhitelisted == true);

    /// <summary>Every handler with a competing app, from both maps.</summary>
    public static IEnumerable<string> HandlersWithConflictApp()
    {
        foreach (var handler in ConflictAppByHandler.Keys) yield return handler;
        foreach (var handler in HintOnlyConflictAppByHandler.Keys) yield return handler;
    }

    public static string? ConflictAppFor(string handlerId)
        => ConflictAppByHandler.TryGetValue(handlerId, out var id) ? id
            : HintOnlyConflictAppByHandler.TryGetValue(handlerId, out var hintApp) ? hintApp
            : null;

    /// <summary>Every handler that competes with <paramref name="appId"/>, from both maps.</summary>
    public static List<string> HandlersFor(string appId)
    {
        var handlers = new List<string>();
        foreach (var (handler, app) in ConflictAppByHandler)
        {
            if (string.Equals(app, appId, StringComparison.OrdinalIgnoreCase)) handlers.Add(handler);
        }
        foreach (var (handler, app) in HintOnlyConflictAppByHandler)
        {
            if (string.Equals(app, appId, StringComparison.OrdinalIgnoreCase)) handlers.Add(handler);
        }
        return handlers;
    }

    /// <summary>"usb" for every USB handler, "smbus" for the chipset-bus pseudo-device.</summary>
    public static string BusFor(string handlerId)
        => BusByHandler.TryGetValue(handlerId, out var bus) ? bus : "usb";

    /// <summary>
    /// True for handlers driving third-party hardware whose support is
    /// experimental. Drives the "Experimental" badge in the UI.
    /// </summary>
    public static bool IsExperimental(string handlerId) =>
        !FirstPartyHandlers.Contains(handlerId) && !StableThirdPartyHandlers.Contains(handlerId) && !BusByHandler.ContainsKey(handlerId);
}
