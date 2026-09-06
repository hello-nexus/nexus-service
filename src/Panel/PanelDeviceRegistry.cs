using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Panel;

/// <summary>
/// Owns the per-device panel records persisted under
/// <c>NexusSettings.PanelDevices</c> (top-level, NOT profile-scoped).
/// Allocation, lookup, patching, removal all flow through here. Phone
/// pairing keeps its own auth-token registry; this is the identity +
/// layout surface that any panel device (Y70, kiosk, phone) maps onto via
/// the URL <c>/panel/{deviceId}</c>. Storage is global because device
/// identity describes physical hardware, not user preferences - profile
/// switches preserve the registry so the connected panel never loses its
/// own record mid-session.
/// </summary>
public sealed class PanelDeviceRegistry
{
    private readonly IConfigStore _store;

    public PanelDeviceRegistry(IConfigStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Raised after AllocateForDisplay creates or re-enables a display-bound
    /// record. Lets a device-specific worker whose own presence signal
    /// (e.g. USB attach) can arrive before the matching panel record exists
    /// (auto-promotion runs off a separately debounced topology event)
    /// retry once the record is there, instead of polling for it.
    /// </summary>
    public event Action<PanelDeviceRecord>? DisplayRecordReady;

    public PanelDeviceRecord Allocate(string? displayName, PanelDeviceCapabilities? capabilities)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var surface = capabilities?.Surface;

        PanelDeviceRecord? result = null;
        _store.Update(s =>
        {
            // Single-instance surfaces (Y70, Q-series) are one physical panel
            // per host with no OS displayId to key on. They self-register and
            // lean on the kiosk's localStorage to reuse their record; the
            // Q-series OEM WebView drops that cache across reconnects, so a
            // plain allocate would mint a fresh record every connect and orphan
            // the user's theme/layout. Reuse the existing record instead.
            if (PanelSurfaces.IsSingleInstance(surface))
            {
                var existing = s.PanelDevices.Values
                    .Where(d => string.IsNullOrEmpty(d.DisplayId)
                        && string.Equals(d.Capabilities?.Surface, surface, StringComparison.Ordinal))
                    .OrderByDescending(d => d.LastSeenAt)
                    .FirstOrDefault();
                if (existing is not null)
                {
                    existing.LastSeenAt = now;
                    existing.Capabilities = capabilities ?? existing.Capabilities;
                    result = Clone(existing);
                    return;
                }
            }

            var record = new PanelDeviceRecord
            {
                Id = NewId(),
                DisplayName = NormalizeName(displayName) ?? DefaultName(now),
                FirstSeenAt = now,
                LastSeenAt = now,
                Capabilities = capabilities,
            };
            s.PanelDevices[record.Id] = record;
            result = Clone(record);
        });

        return result!;
    }

    /// <summary>
    /// Allocate a record bound to an OS monitor (promoted via
    /// POST /displays/{id}/panel). The binding makes the record the layout +
    /// kiosk identity for that display until demoted. Uniqueness on
    /// <paramref name="displayId"/> is enforced inside the store transaction:
    /// the route's pre-check races with concurrent promotes (double-click,
    /// two dashboards), and a duplicate binding would host two kiosks on one
    /// monitor and orphan a record on demote. Returns
    /// <c>Created == false</c> with the existing record when already bound.
    /// </summary>
    public (PanelDeviceRecord Record, bool Activated) AllocateForDisplay(string displayId, string? displayName, PanelDeviceCapabilities? capabilities)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var record = new PanelDeviceRecord
        {
            Id = NewId(),
            DisplayName = NormalizeName(displayName) ?? DefaultName(now),
            FirstSeenAt = now,
            LastSeenAt = now,
            Capabilities = capabilities,
            DisplayId = displayId,
        };

        PanelDeviceRecord? result = null;
        var activated = false;
        _store.Update(s =>
        {
            foreach (var candidate in s.PanelDevices.Values)
            {
                if (!string.Equals(candidate.DisplayId, displayId, StringComparison.Ordinal)) continue;
                if (candidate.Enabled == false)
                {
                    // Turning the panel back on: same record, so layout,
                    // theme, name, and reserve persist through off/on
                    // cycles. Viewport hints refresh from the current OS
                    // facts (resolution/scale/touch may have changed).
                    candidate.Enabled = true;
                    candidate.Capabilities = capabilities ?? candidate.Capabilities;
                    candidate.LastSeenAt = now;
                    activated = true;
                }
                result = Clone(candidate);
                return;
            }
            s.PanelDevices[record.Id] = record;
        });

        // Panel on/off is rare and must survive an immediate service exit -
        // a write lost to the flush debounce would silently undo the toggle.
        _store.FlushNow();
        if (result is not null)
        {
            if (activated) DisplayRecordReady?.Invoke(result);
            return (result, activated);
        }
        DisplayRecordReady?.Invoke(record);
        return (record, true);
    }

    /// <summary>
    /// Turn a display's panel OFF without losing its configuration: the
    /// record (layout/theme/settings) stays; assignments stop listing it so
    /// the overlay closes the kiosk. Returns the disabled record, or null
    /// when the display has no active monitor-panel record.
    /// </summary>
    public PanelDeviceRecord? DisablePanelForDisplay(string displayId)
    {
        if (string.IsNullOrWhiteSpace(displayId)) return null;
        PanelDeviceRecord? snapshot = null;
        _store.Update(s =>
        {
            foreach (var record in s.PanelDevices.Values)
            {
                if (!string.Equals(record.DisplayId, displayId, StringComparison.Ordinal)) continue;
                if (record.Enabled == false) return;
                record.Enabled = false;
                snapshot = Clone(record);
                return;
            }
        });
        // Same durability rule as AllocateForDisplay: the OFF must not be
        // lost to the debounce window if the service exits right after.
        _store.FlushNow();
        return snapshot;
    }

    /// <summary>
    /// Persist the last orientation applied through Nexus on the display's
    /// record (settings permanence, same model as the Y70's persisted
    /// orientation). The live OS rotation in /displays/topology stays the
    /// authoritative read; this survives service restarts and replug.
    /// </summary>
    public void UpdateDisplayOrientation(string displayId, string orientation)
    {
        if (string.IsNullOrWhiteSpace(displayId)) return;
        _store.Update(s =>
        {
            foreach (var record in s.PanelDevices.Values)
            {
                if (!string.Equals(record.DisplayId, displayId, StringComparison.Ordinal)) continue;
                record.Capabilities ??= new PanelDeviceCapabilities();
                record.Capabilities.Orientation = orientation;
                return;
            }
        });
    }

    /// <summary>
    /// Replace the service-derived capabilities of a display-bound record
    /// (promoted monitors only; their kiosk never self-reports, so the whole
    /// object is safe to rebuild from OS facts). Returns true when stored
    /// state changed; an equal snapshot leaves the store untouched.
    /// </summary>
    public bool RefreshDisplayCapabilities(string displayId, PanelDeviceCapabilities capabilities)
    {
        if (string.IsNullOrWhiteSpace(displayId)) return false;
        // Pre-check outside the transaction so the topology-read path skips
        // the store write-lock when nothing changed (the common case).
        var existing = FindByDisplayId(displayId);
        if (existing is null || CapabilitiesEqual(existing.Capabilities, capabilities)) return false;
        var changed = false;
        _store.Update(s =>
        {
            foreach (var record in s.PanelDevices.Values)
            {
                if (!string.Equals(record.DisplayId, displayId, StringComparison.Ordinal)) continue;
                // Re-check inside the transaction: a raced removal or second
                // sync must not report (and broadcast) a phantom change.
                if (!CapabilitiesEqual(record.Capabilities, capabilities))
                {
                    record.Capabilities = capabilities;
                    changed = true;
                }
                return;
            }
        });
        return changed;
    }

    /// <summary>
    /// Merges the given Xeneon Edge control values into the display's
    /// persisted snapshot; fields left null in <paramref name="settings"/>
    /// keep their stored value unchanged (a POST that only sets one control
    /// must not blank the other five).
    /// </summary>
    public void UpdateXeneonEdgeSettings(string displayId, XeneonEdgeSettingsDto settings)
    {
        if (string.IsNullOrWhiteSpace(displayId)) return;
        _store.Update(s =>
        {
            foreach (var record in s.PanelDevices.Values)
            {
                if (!string.Equals(record.DisplayId, displayId, StringComparison.Ordinal)) continue;
                record.XeneonEdgeSettings ??= new XeneonEdgeSettingsDto();
                if (settings.Brightness.HasValue) record.XeneonEdgeSettings.Brightness = settings.Brightness;
                if (settings.Backlight.HasValue) record.XeneonEdgeSettings.Backlight = settings.Backlight;
                if (settings.Contrast.HasValue) record.XeneonEdgeSettings.Contrast = settings.Contrast;
                if (settings.Red.HasValue) record.XeneonEdgeSettings.Red = settings.Red;
                if (settings.Green.HasValue) record.XeneonEdgeSettings.Green = settings.Green;
                if (settings.Blue.HasValue) record.XeneonEdgeSettings.Blue = settings.Blue;
                return;
            }
        });
    }

    private static bool CapabilitiesEqual(PanelDeviceCapabilities? a, PanelDeviceCapabilities? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return string.Equals(a.Surface, b.Surface, StringComparison.Ordinal)
            && string.Equals(a.Grid, b.Grid, StringComparison.Ordinal)
            && a.Touch == b.Touch
            && string.Equals(a.Orientation, b.Orientation, StringComparison.Ordinal)
            && a.CssWidth == b.CssWidth
            && a.CssHeight == b.CssHeight
            && a.Dpr == b.Dpr
            && a.Dpi == b.Dpi
            && string.Equals(a.Family, b.Family, StringComparison.Ordinal);
    }

    public PanelDeviceRecord? FindByDisplayId(string displayId)
    {
        if (string.IsNullOrWhiteSpace(displayId))
            return null;
        foreach (var record in _store.Load().PanelDevices.Values)
        {
            if (string.Equals(record.DisplayId, displayId, StringComparison.Ordinal))
                return Clone(record);
        }
        return null;
    }

    /// <summary>
    /// Solid "#rrggbb" colour that best represents this panel's current
    /// background, for callers that need to blend with it without importing
    /// the full theme stack (the display-rotation cover). Prefers the light
    /// slot only when the panel's own ThemeMode is explicitly "light";
    /// ThemeSyncWithDesktop / "system" resolution is client-side only and is
    /// not replicated here. Empty when the panel has neither slot set.
    /// </summary>
    public static string ResolveCoverBackgroundHex(PanelDeviceRecord? record)
    {
        if (record is null) return "";
        var preferLight = string.Equals(record.ThemeMode, "light", StringComparison.OrdinalIgnoreCase);
        var color = preferLight
            ? record.BackgroundColorLight ?? record.BackgroundColor
            : record.BackgroundColor ?? record.BackgroundColorLight;
        return color ?? "";
    }

    /// <summary>Active displayId -> panelDeviceId bindings (kiosk reconcile
    /// input). Disabled panels keep their record but host no kiosk.</summary>
    public IReadOnlyList<(string DisplayId, string PanelDeviceId, bool ReserveMonitor, string Backdrop)> ListAssignments()
    {
        var assignments = new List<(string, string, bool, string)>();
        foreach (var record in _store.Load().PanelDevices.Values)
        {
            if (!string.IsNullOrEmpty(record.DisplayId) && record.Enabled != false)
                assignments.Add((record.DisplayId, record.Id, record.ReserveMonitor ?? true, record.Backdrop ?? ""));
        }
        return assignments;
    }

    /// <summary>
    /// Backdrop of the Y70's own record, or "" when it has never registered.
    /// The Y70 kiosk is opened from hardware detection rather than a display
    /// assignment, so the host has no device id to read it from.
    /// </summary>
    public string GetY70Backdrop()
    {
        PanelDeviceRecord? newest = null;
        foreach (var record in _store.Load().PanelDevices.Values)
        {
            if (!string.Equals(record.Capabilities?.Surface, PanelSurfaces.Y70, StringComparison.Ordinal)) continue;
            if (newest is null || record.LastSeenAt > newest.LastSeenAt) newest = record;
        }
        return newest?.Backdrop ?? "";
    }

    public IReadOnlyList<PanelDeviceRecord> List()
    {
        var devices = _store.Load().PanelDevices;
        if (devices.Count == 0)
            return Array.Empty<PanelDeviceRecord>();
        return devices.Values
            .OrderByDescending(d => d.LastSeenAt)
            .Select(Clone)
            .ToList();
    }

    public PanelDeviceRecord? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        var devices = _store.Load().PanelDevices;
        if (!devices.TryGetValue(id, out var record))
            return null;
        return Clone(record);
    }

    public PanelDeviceRecord? Touch(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        PanelDeviceRecord? snapshot = null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _store.Update(s =>
        {
            if (!s.PanelDevices.TryGetValue(id, out var record))
                return;
            record.LastSeenAt = now;
            snapshot = Clone(record);
        });
        return snapshot;
    }

    public PanelDeviceRecord? Patch(string id, PanelDevicePatch patch)
    {
        if (string.IsNullOrWhiteSpace(id) || patch is null)
            return null;

        PanelDeviceRecord? snapshot = null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _store.Update(s =>
        {
            if (!s.PanelDevices.TryGetValue(id, out var record))
                return;

            if (patch.DisplayName is not null)
            {
                var name = NormalizeName(patch.DisplayName);
                if (!string.IsNullOrEmpty(name))
                    record.DisplayName = name;
            }
            if (patch.Layout is not null)
                record.Layout = patch.Layout;
            if (patch.ThemeMode is not null)
                record.ThemeMode = NullIfEmpty(patch.ThemeMode);
            if (patch.AccentColor is not null)
                record.AccentColor = NullIfEmpty(patch.AccentColor);
            if (patch.BackgroundColor is not null)
                record.BackgroundColor = NullIfEmpty(patch.BackgroundColor);
            if (patch.BackgroundColorLight is not null)
                record.BackgroundColorLight = NullIfEmpty(patch.BackgroundColorLight);
            if (patch.BackgroundMode is not null)
                record.BackgroundMode = NullIfEmpty(patch.BackgroundMode);
            if (patch.BackgroundEffect is not null)
                record.BackgroundEffect = NullIfEmpty(patch.BackgroundEffect);
            if (patch.BackgroundTemplate.HasValue)
                record.BackgroundTemplate = patch.BackgroundTemplate.Value;
            if (patch.BackgroundTemplates is not null)
                record.BackgroundTemplates = new Dictionary<string, int>(patch.BackgroundTemplates);
            if (patch.BackgroundOpacity.HasValue)
                record.BackgroundOpacity = patch.BackgroundOpacity.Value;
            // An unrecognised value is ignored rather than stored: clearing
            // would silently drop the panel's current mode on a client typo.
            if (NormalizeBackdrop(patch.Backdrop) is { } backdrop)
                record.Backdrop = backdrop;
            if (patch.BackgroundMediaId is not null)
                record.BackgroundMediaId = NullIfEmpty(patch.BackgroundMediaId);
            if (patch.BackgroundMediaType is not null)
                record.BackgroundMediaType = NullIfEmpty(patch.BackgroundMediaType);
            if (patch.BackgroundFrostLevel.HasValue)
                record.BackgroundFrostLevel = patch.BackgroundFrostLevel.Value;
            if (patch.WidgetOpacity.HasValue)
                record.WidgetOpacity = patch.WidgetOpacity.Value;
            if (patch.WidgetLabels.HasValue)
                record.WidgetLabels = patch.WidgetLabels.Value;
            if (patch.WidgetPadding.HasValue)
                record.WidgetPadding = patch.WidgetPadding.Value;
            if (patch.ThemeSyncWithDesktop.HasValue)
                record.ThemeSyncWithDesktop = patch.ThemeSyncWithDesktop.Value;
            if (patch.AccentSyncWithDesktop.HasValue)
                record.AccentSyncWithDesktop = patch.AccentSyncWithDesktop.Value;
            // Reserve only makes sense for display-bound (promoted monitor)
            // records; the Y70 kiosk uses the global preference.
            if (patch.ReserveMonitor.HasValue && !string.IsNullOrEmpty(record.DisplayId))
                record.ReserveMonitor = patch.ReserveMonitor.Value;
            // AutoOrient only makes sense for display-bound records (the
            // sensor worker resolves a record by DisplayId).
            if (patch.AutoOrient.HasValue && !string.IsNullOrEmpty(record.DisplayId))
                record.AutoOrient = patch.AutoOrient.Value;
            // Orientation is a property of how the glass is physically mounted, so it
            // applies to any record; the transport ignores it on surfaces it cannot turn.
            if (patch.Flip180.HasValue)
                record.Flip180 = patch.Flip180.Value;
            if (patch.Mirror.HasValue)
                record.Mirror = patch.Mirror.Value;
            // Capabilities on display-bound records are owned by the topology
            // sync (rebuilt from OS facts); a client value would ping-pong
            // with the next sync pass.
            if (patch.Capabilities is not null && string.IsNullOrEmpty(record.DisplayId))
            {
                // A single-instance surface is the record's identity, not a
                // viewport sample: a Y70 kiosk that maps on the desktop monitor
                // before the compositor moves it reports that monitor's shape
                // and would rename the Y70 record "phone"; it re-reports the
                // real size on the resize that follows. Keep the surface.
                var keep = record.Capabilities?.Surface;
                if (keep is PanelSurfaces.Y70 or PanelSurfaces.Q60
                    && !string.Equals(patch.Capabilities.Surface, keep, StringComparison.Ordinal))
                {
                    patch.Capabilities.Surface = keep;
                }
                record.Capabilities = patch.Capabilities;
            }

            record.LastSeenAt = now;
            snapshot = Clone(record);
        });
        return snapshot;
    }

    public bool Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var removed = false;
        _store.Update(s =>
        {
            removed = s.PanelDevices.Remove(id);
        });
        return removed;
    }

    /// <summary>
    /// Personalization reset: layout (including single-widget configs),
    /// theme, background, and widget fields all clear, so defaults reseed on
    /// the next read. Identity and hardware-scoped state survive - id, name,
    /// display binding, capabilities, enabled state, monitor behavior
    /// (ReserveMonitor/AutoOrient - see <see cref="ResetHardwareSettings"/>),
    /// and the persisted orientation / Xeneon DDC record. Uploaded media is
    /// the caller's to delete (PanelBgLibrary); the cleared BackgroundMediaId
    /// is what unreferences it here. Returns null when the id is unknown.
    /// </summary>
    public PanelDeviceRecord? ResetToDefaults(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        PanelDeviceRecord? snapshot = null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _store.Update(s =>
        {
            if (!s.PanelDevices.TryGetValue(id, out var record))
                return;
            record.Layout = null;
            record.ThemeMode = null;
            record.AccentColor = null;
            record.BackgroundColor = null;
            record.BackgroundColorLight = null;
            record.BackgroundMode = null;
            record.BackgroundEffect = null;
            record.BackgroundTemplate = null;
            record.BackgroundTemplates = null;
            record.BackgroundOpacity = null;
            record.Backdrop = null;
            record.BackgroundMediaId = null;
            record.BackgroundMediaType = null;
            record.BackgroundFrostLevel = null;
            record.WidgetOpacity = null;
            record.WidgetLabels = null;
            record.WidgetPadding = null;
            record.ThemeSyncWithDesktop = null;
            record.AccentSyncWithDesktop = null;
            record.LastSeenAt = now;
            snapshot = Clone(record);
        });
        // A reset is rare and destructive; it must survive an immediate
        // service exit, same durability rule as the panel on/off toggle.
        if (snapshot is not null) _store.FlushNow();
        return snapshot;
    }

    /// <summary>
    /// Hardware-scoped counterpart of <see cref="ResetToDefaults"/>: clears
    /// the record's monitor-behavior fields (ReserveMonitor / AutoOrient,
    /// null = default on) and the recorded Xeneon DDC values. The route owns
    /// applying family defaults to the actual hardware; this only resets what
    /// the record stores. Returns null when the id is unknown.
    /// </summary>
    public PanelDeviceRecord? ResetHardwareSettings(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        PanelDeviceRecord? snapshot = null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _store.Update(s =>
        {
            if (!s.PanelDevices.TryGetValue(id, out var record))
                return;
            record.ReserveMonitor = null;
            record.AutoOrient = null;
            record.Flip180 = null;
            record.Mirror = null;
            record.XeneonEdgeSettings = null;
            record.LastSeenAt = now;
            snapshot = Clone(record);
        });
        if (snapshot is not null) _store.FlushNow();
        return snapshot;
    }

    private static string DefaultName(long now)
    {
        var when = DateTimeOffset.FromUnixTimeMilliseconds(now).LocalDateTime;
        return $"Panel {when:MMM d HH:mm}";
    }

    private static string? NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var trimmed = raw.Trim();
        return trimmed.Length > 60 ? trimmed[..60] : trimmed;
    }

    private static string? NullIfEmpty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Unknown values store as null so the client falls back to its
    /// per-surface default rather than to a mode the panel cannot render.</summary>
    private static string? NormalizeBackdrop(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "theme" => "theme",
        "wallpaper" => "wallpaper",
        "desktop" => "desktop",
        _ => null,
    };

    private static string NewId()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(9))
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static PanelDeviceRecord Clone(PanelDeviceRecord r)
    {
        return new PanelDeviceRecord
        {
            Id = r.Id,
            DisplayName = r.DisplayName,
            Layout = r.Layout,
            ThemeMode = r.ThemeMode,
            AccentColor = r.AccentColor,
            BackgroundColor = r.BackgroundColor,
            BackgroundColorLight = r.BackgroundColorLight,
            BackgroundMode = r.BackgroundMode,
            BackgroundEffect = r.BackgroundEffect,
            BackgroundTemplate = r.BackgroundTemplate,
            BackgroundTemplates = r.BackgroundTemplates is null
                ? null
                : new Dictionary<string, int>(r.BackgroundTemplates),
            BackgroundOpacity = r.BackgroundOpacity,
            Backdrop = r.Backdrop,
            BackgroundMediaId = r.BackgroundMediaId,
            BackgroundMediaType = r.BackgroundMediaType,
            BackgroundFrostLevel = r.BackgroundFrostLevel,
            WidgetOpacity = r.WidgetOpacity,
            WidgetLabels = r.WidgetLabels,
            WidgetPadding = r.WidgetPadding,
            ThemeSyncWithDesktop = r.ThemeSyncWithDesktop,
            AccentSyncWithDesktop = r.AccentSyncWithDesktop,
            FirstSeenAt = r.FirstSeenAt,
            LastSeenAt = r.LastSeenAt,
            Capabilities = r.Capabilities,
            DisplayId = r.DisplayId,
            ReserveMonitor = r.ReserveMonitor,
            AutoOrient = r.AutoOrient,
            Flip180 = r.Flip180,
            Mirror = r.Mirror,
            XeneonEdgeSettings = r.XeneonEdgeSettings is null
                ? null
                : new XeneonEdgeSettingsDto
                {
                    Brightness = r.XeneonEdgeSettings.Brightness,
                    Backlight = r.XeneonEdgeSettings.Backlight,
                    Contrast = r.XeneonEdgeSettings.Contrast,
                    Red = r.XeneonEdgeSettings.Red,
                    Green = r.XeneonEdgeSettings.Green,
                    Blue = r.XeneonEdgeSettings.Blue,
                },
            Enabled = r.Enabled,
        };
    }
}
