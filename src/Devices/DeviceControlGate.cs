using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    /// Raised after <see cref="SetEnabled"/> persists a choice and when a running
    /// competing app holds a device off or releases it (<see cref="SetPausedByApp"/>).
    /// Fired outside any lock on the caller's thread, so notifications can arrive in
    /// a different order than the state changed; a subscriber that must not act on a
    /// stale value re-reads <see cref="IsEnabled"/>.
    /// </summary>
    public event Action<string, bool>? Changed;

    // Read lock-free from ~13 worker threads. Safe because every write replaces
    // each list reference (never mutates in place) and adds to the destination
    // list before removing from the other, so a reader mid-swap sees the id in
    // BOTH lists, never in neither. Disabled wins that transient, so a
    // just-enabled third-party device reads off for a cycle (fail-closed) and an
    // explicit choice is never readable as unset.
    /// <summary>The user's choice, and off while the device's whitelisted competing app runs.</summary>
    public bool IsEnabled(string handlerId) =>
        IsEnabled(_store.Load().Devices, handlerId) && !Volatile.Read(ref _pausedByApp).ContainsKey(handlerId);

    // Handler id -> running competing app id; replaced whole, never mutated, so lock-free reads see one set or the other.
    private Dictionary<string, string> _pausedByApp = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pauseLock = new();

    /// <summary>The user's choice alone, ignoring a running competing app.</summary>
    public bool IsChosenOn(string handlerId) => IsEnabled(_store.Load().Devices, handlerId);

    /// <summary>The competing app holding this handler off right now, or null.</summary>
    public string? PausedByApp(string handlerId) =>
        Volatile.Read(ref _pausedByApp).TryGetValue(handlerId, out var appId) ? appId : null;

    /// <summary>Replaces the handlers held off by a running competing app and raises <see cref="Changed"/> for each one the user has on that flipped.</summary>
    public void SetPausedByApp(IReadOnlyDictionary<string, string> paused)
    {
        List<(string Handler, bool Enabled)> flipped = new();
        lock (_pauseLock)
        {
            var before = _pausedByApp;
            var after = new Dictionary<string, string>(paused, StringComparer.OrdinalIgnoreCase);
            foreach (var handler in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var wasPaused = before.ContainsKey(handler);
                var isPaused = after.ContainsKey(handler);
                if (wasPaused == isPaused || !IsChosenOn(handler)) continue;
                flipped.Add((handler, !isPaused));
                Console.WriteLine(isPaused
                    ? $"[device-control] '{handler}' waiting: {after[handler]} is running"
                    : $"[device-control] '{handler}' taken: {before[handler]} exited or left the whitelist");
            }
            Volatile.Write(ref _pausedByApp, after);
        }
        foreach (var (handler, enabled) in flipped)
        {
            RaiseChanged(handler, enabled);
        }
    }

    private static bool IsEnabled(DevicesSettings devices, string handlerId)
    {
        if (DeviceControlPolicy.RequiresBeta(handlerId))
        {
            return false;
        }
        if (devices.NexusControlDisabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        if (devices.NexusControlEnabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }
        // A Uni hub past the first follows the first hub's choice until it has one of its own.
        if (Nexus.Service.Peripherals.LianLi.LianLiHubSet.SlotOf(handlerId) > 0)
        {
            return IsEnabled(devices, Nexus.Service.Peripherals.LianLi.LianLiHubSet.PrimaryId);
        }
        return DeviceControlPolicy.DefaultOn(handlerId);
    }

    /// <summary>Persists the choice and, in the same mutation, takes the device's competing app off the conflict whitelist (on) or puts it on once none of that app's devices is still Nexus-controlled (off).</summary>
    public void SetEnabled(string handlerId, bool enabled)
    {
        // Leaves the stored choice alone, so it applies again on a beta build.
        if (DeviceControlPolicy.RequiresBeta(handlerId))
        {
            return;
        }
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
        RaiseChanged(handlerId, enabled && PausedByApp(handlerId) is null);
    }

    // The choice is persisted; a throwing subscriber must not fail the request.
    private void RaiseChanged(string handlerId, bool enabled)
    {
        try
        {
            Changed?.Invoke(handlerId, enabled);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[device-control] '{handlerId}' change handler failed: {ex.Message}");
        }
    }

    private static List<string> WithId(List<string> ids, string handlerId) =>
        ids.Contains(handlerId, StringComparer.OrdinalIgnoreCase) ? ids : new List<string>(ids) { handlerId };

    private static List<string> WithoutId(List<string> ids, string handlerId) =>
        ids.Contains(handlerId, StringComparer.OrdinalIgnoreCase)
            ? ids.Where(id => !string.Equals(id, handlerId, StringComparison.OrdinalIgnoreCase)).ToList()
            : ids;
}
