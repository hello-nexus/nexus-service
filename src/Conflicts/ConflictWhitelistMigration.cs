using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Conflicts;

/// <summary>Schema v19, for an upgrading install only (a fresh one starts from the new defaults).</summary>
public static class ConflictWhitelistMigration
{
    // The catalog's DefaultWhitelisted set when v19 shipped; later additions reach fresh installs only.
    private static readonly string[] V19Whitelist =
    {
        "msi-companion", "msi-game-bar-tool", "msi-super-charger", "tryx-kanali",
        Devices.Handlers.StreamDeckHandler.ElgatoConflictAppId, "evga-precision-x-server",
    };

    public static void Apply(NexusSettings doc)
    {
        doc.Devices ??= new DevicesSettings();
        doc.Ui ??= new UiSettings();
        doc.StreamDeck ??= new StreamDeckSettings();

        // Both defaulted on before v19. A deck entry exists only once Nexus has
        // driven that deck. Tryx has no such record; keeping it on is harmless
        // without the panel, and Kanali only runs where the panel is.
        if (doc.StreamDeck.Decks.Count > 0) KeepOn(doc.Devices, "streamdeck");
        KeepOn(doc.Devices, "tryx");

        // A whitelisted app must not share a device with Nexus.
        foreach (var appId in V19Whitelist)
        {
            if (Contains(doc.Ui.ConflictAutoKillExclusions, appId)) continue;
            if (DeviceControlPolicy.HandlersFor(appId).Any(h => Contains(doc.Devices.NexusControlEnabled, h))) continue;
            doc.Ui.ConflictAutoKillExclusions = doc.Ui.ConflictAutoKillExclusions.Append(appId).ToList();
        }
    }

    /// <summary>Schema v20: the Kraken and ZMatrices LCD defaults turned off with their apps whitelisted, so an upgrading install that never chose keeps driving them.</summary>
    public static void KeepThirdPartyDevicesOn(NexusSettings doc)
    {
        doc.Devices ??= new DevicesSettings();
        KeepOn(doc.Devices, "nzxt-kraken");
        KeepOn(doc.Devices, "zmatrices-lcd");
    }

    /// <summary>Runs on every load. Turning a device on took its app off the whitelist; while every device of the app is locked to beta builds, the app goes back on so it is neither ended nor announced. Once any of them is controllable again (a beta build, or a family promoted to stable), exactly that entry comes off. True when it changed anything.</summary>
    public static bool ReconcileBetaLock(NexusSettings doc)
    {
        doc.Devices ??= new DevicesSettings();
        doc.Ui ??= new UiSettings();
        var changed = false;
        foreach (var app in DeviceControlPolicy.AllHandlersByApp())
        {
            var locked = app.All(DeviceControlPolicy.RequiresBeta);
            var chosenOn = app.Any(h => Contains(doc.Devices.NexusControlEnabled, h));
            if (Contains(doc.Devices.BetaLockWhitelisted, app.Key))
            {
                if (locked) continue;
                if (chosenOn) doc.Ui.ConflictAutoKillExclusions = Without(doc.Ui.ConflictAutoKillExclusions, app.Key);
                doc.Devices.BetaLockWhitelisted = Without(doc.Devices.BetaLockWhitelisted, app.Key);
                changed = true;
            }
            else if (locked && chosenOn && !Contains(doc.Ui.ConflictAutoKillExclusions, app.Key))
            {
                doc.Ui.ConflictAutoKillExclusions = doc.Ui.ConflictAutoKillExclusions.Append(app.Key).ToList();
                doc.Devices.BetaLockWhitelisted = doc.Devices.BetaLockWhitelisted.Append(app.Key).ToList();
                changed = true;
            }
        }
        return changed;
    }

    private static List<string> Without(List<string> ids, string id) =>
        ids.Where(x => !string.Equals(x, id, StringComparison.OrdinalIgnoreCase)).ToList();

    private static void KeepOn(DevicesSettings devices, string handlerId)
    {
        if (Contains(devices.NexusControlDisabled, handlerId) || Contains(devices.NexusControlEnabled, handlerId)) return;
        devices.NexusControlEnabled = devices.NexusControlEnabled.Append(handlerId).ToList();
    }

    private static bool Contains(List<string> ids, string id) => ids.Contains(id, StringComparer.OrdinalIgnoreCase);
}
