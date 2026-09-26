using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Conflicts;

/// <summary>Schema v19, for an upgrading install only (a fresh one starts from the new defaults).</summary>
public static class ConflictWhitelistMigration
{
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
        foreach (var def in ConflictAppCatalog.All)
        {
            if (!def.DefaultWhitelisted || Contains(doc.Ui.ConflictAutoKillExclusions, def.Id)) continue;
            if (DeviceControlPolicy.HandlersFor(def.Id).Any(h => Contains(doc.Devices.NexusControlEnabled, h))) continue;
            doc.Ui.ConflictAutoKillExclusions = doc.Ui.ConflictAutoKillExclusions.Append(def.Id).ToList();
        }
    }

    private static void KeepOn(DevicesSettings devices, string handlerId)
    {
        if (Contains(devices.NexusControlDisabled, handlerId) || Contains(devices.NexusControlEnabled, handlerId)) return;
        devices.NexusControlEnabled = devices.NexusControlEnabled.Append(handlerId).ToList();
    }

    private static bool Contains(List<string> ids, string id) => ids.Contains(id, StringComparer.OrdinalIgnoreCase);
}
