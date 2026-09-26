using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Persistence;

namespace Nexus.Service.Devices;

/// <summary>
/// Per-handler Nexus Control on/off gate. Tri-state: a handler id in the disabled
/// list is off, in the enabled list is on, and in neither uses the brand default
/// (<see cref="DeviceControlPolicy.DefaultOn"/>). Connection workers consult this before claiming a
/// port so a toggled-off device stays detectable (USB enumeration still sees it) but
/// unclaimed.
/// </summary>
public sealed class DeviceControlGate
{
    private readonly IConfigStore _store;

    public DeviceControlGate(IConfigStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Raised after <see cref="SetEnabled"/> persists a choice, for consumers
    /// holding hardware open. Fired outside the mutation on the caller's thread,
    /// so concurrent writers can notify in the opposite order to the one they
    /// persisted in; a subscriber that must not act on a stale value re-reads
    /// <see cref="IsEnabled"/>. <see cref="TryAdopt"/> does not raise it, and no
    /// shared-bus handler is adoptable.
    /// </summary>
    public event Action<string, bool>? Changed;

    // Read lock-free from ~13 worker threads. Safe because every write replaces
    // each list reference (never mutates in place) and adds to the destination
    // list before removing from the other, so a reader mid-swap sees the id in
    // BOTH lists, never in neither. Disabled wins that transient, so a
    // just-enabled third-party device reads off for a cycle (fail-closed) and an
    // explicit choice is never readable as unset.
    public bool IsEnabled(string handlerId) => IsEnabled(_store.Load().Devices, handlerId);

    private static bool IsEnabled(DevicesSettings devices, string handlerId)
    {
        if (devices.NexusControlDisabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        if (devices.NexusControlEnabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }
        return DeviceControlPolicy.DefaultOn(handlerId);
    }

    /// <summary>True when the user has made no explicit on/off choice for this handler (in neither list).</summary>
    public bool IsUnset(string handlerId)
    {
        var devices = _store.Load().Devices;
        return !devices.NexusControlDisabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase)
            && !devices.NexusControlEnabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Persists the choice and, in the same mutation, takes the device's competing app off the conflict whitelist (on) or puts it on once none of that app's devices is still Nexus-controlled (off).</summary>
    public void SetEnabled(string handlerId, bool enabled)
    {
        _store.Update(s =>
        {
            var devices = s.Devices;
            if (enabled)
            {
                devices.NexusControlEnabled = WithId(devices.NexusControlEnabled, handlerId);
                devices.NexusControlDisabled = WithoutId(devices.NexusControlDisabled, handlerId);
            }
            else
            {
                devices.NexusControlDisabled = WithId(devices.NexusControlDisabled, handlerId);
                devices.NexusControlEnabled = WithoutId(devices.NexusControlEnabled, handlerId);
            }

            if (DeviceControlPolicy.ConflictAppFor(handlerId) is not { } appId)
            {
                return;
            }
            if (enabled)
            {
                s.Ui.ConflictAutoKillExclusions = WithoutId(s.Ui.ConflictAutoKillExclusions, appId);
            }
            else if (!DeviceControlPolicy.HandlersFor(appId).Any(h => IsEnabled(devices, h)))
            {
                s.Ui.ConflictAutoKillExclusions = WithId(s.Ui.ConflictAutoKillExclusions, appId);
            }
        });
        // The choice is persisted; a throwing subscriber must not fail the request.
        try
        {
            Changed?.Invoke(handlerId, enabled);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[device-control] '{handlerId}' change handler failed: {ex.Message}");
        }
    }

    /// <summary>True when the device's auto-adopting competing app is on the conflict whitelist: the user keeps that app, so adoption must not take its device.</summary>
    public bool IsAdoptionAppWhitelisted(string handlerId) =>
        DeviceControlPolicy.AdoptionConflictAppFor(handlerId) is { } appId
        && _store.Load().Ui.ConflictAutoKillExclusions.Contains(appId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Puts a never-set handler on the enabled list, for
    /// <see cref="DeviceAdoptionService"/>. The set-check and the write are one
    /// store mutation, so a concurrent <see cref="SetEnabled"/> cannot be read as
    /// unset and then overwritten - an explicit off stays off. Returns false when
    /// the user has already chosen on or off.
    /// </summary>
    public bool TryAdopt(string handlerId)
    {
        var adopted = false;
        _store.Update(s =>
        {
            var devices = s.Devices;
            if (devices.NexusControlDisabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase)
                || devices.NexusControlEnabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
            devices.NexusControlEnabled = WithId(devices.NexusControlEnabled, handlerId);
            adopted = true;
        });
        return adopted;
    }

    private static List<string> WithId(List<string> ids, string handlerId) =>
        ids.Contains(handlerId, StringComparer.OrdinalIgnoreCase) ? ids : new List<string>(ids) { handlerId };

    private static List<string> WithoutId(List<string> ids, string handlerId) =>
        ids.Contains(handlerId, StringComparer.OrdinalIgnoreCase)
            ? ids.Where(id => !string.Equals(id, handlerId, StringComparison.OrdinalIgnoreCase)).ToList()
            : ids;
}
