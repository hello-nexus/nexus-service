using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel;

/// <summary>
/// Owns the per-device panel records persisted under
/// <c>NexusSettings.PanelDevices</c>. The records themselves (identity, display
/// binding, hardware settings) are global; the Panels profile category carries
/// only their looks (see <see cref="Nexus.Service.Persistence.PanelProfileData"/>).
/// Allocation, lookup, patching, removal all flow through here. Phone
/// pairing keeps its own auth-token registry; this is the identity +
/// layout surface that any panel device (Y70, kiosk, phone) maps onto via
/// the URL <c>/panel/{deviceId}</c>. Profile switches never add or
/// remove a record, so the connected panel never loses its own mid-session.
/// </summary>
public sealed class PanelDeviceRegistry
{
    private readonly IConfigStore _store;

    /// <summary>The Windows overlay and the macOS helper can host a see-through kiosk; the Linux kiosk is opaque.</summary>
    internal bool SeeThroughHost { get; init; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

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

    /// <summary>Raised after Allocate mints a new record (a reused single-instance record does not raise it), before the caller can customize it.</summary>
    public event Action<PanelDeviceRecord>? RecordCreated;

    /// <summary>Raised (record ids) after a record's display fingerprint was first recorded or changed, so profile files can pick up its match key.</summary>
    public event Action<IReadOnlyList<string>>? FingerprintsChanged;

    /// <summary>The family a record reuses under: a Kraken record stored without one is still the Kraken's.</summary>
    internal static string? ReuseFamily(PanelDeviceCapabilities? caps) =>
        caps?.Family ?? (string.Equals(caps?.Surface, PanelSurfaces.LegacyKraken, StringComparison.Ordinal) ? KrakenHub.DeviceId : null);

    /// <summary>
    /// The Kraken shares the lcd-round reuse pool with other round coolers but never swaps records with
    /// them. Every other pairing of families reuses as it always has.
    /// </summary>
    internal static bool CanShareRecord(PanelDeviceCapabilities? record, string? family)
    {
        var recordFamily = ReuseFamily(record);
        var krakenId = KrakenHub.DeviceId;
        return (recordFamily != krakenId && family != krakenId)
            || string.Equals(recordFamily, family, StringComparison.Ordinal);
    }

    /// <param name="canReuse">Narrows which record a single-instance surface may reuse; null allows any.</param>
    public PanelDeviceRecord Allocate(string? displayName, PanelDeviceCapabilities? capabilities, Func<PanelDeviceRecord, bool>? canReuse = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var surface = capabilities?.Surface;

        PanelDeviceRecord? result = null;
        var created = false;
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
                        && string.Equals(PanelSurfaces.Canonical(d.Capabilities?.Surface), PanelSurfaces.Canonical(surface), StringComparison.Ordinal)
                        && CanShareRecord(d.Capabilities, capabilities?.Family)
                        && (canReuse is null || canReuse(d)))
                    .OrderByDescending(d => d.LastSeenAt)
                    .FirstOrDefault();
                if (existing is not null)
                {
                    existing.LastSeenAt = now;
                    // A Y70 kiosk not yet on its panel sends the surface alone. Streamed
                    // panels encode "unsupported" as null, so a merge would keep a swapped-out
                    // model's flags: they replace.
                    existing.Capabilities = capabilities is null
                        ? existing.Capabilities
                        : surface == PanelSurfaces.Y70
                            ? MergeCapabilities(existing.Capabilities, capabilities)
                            : capabilities;
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
            created = true;
        });

        if (created) RecordCreated?.Invoke(result!);
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
    public (PanelDeviceRecord Record, bool Activated) AllocateForDisplay(string displayId, string? displayName, PanelDeviceCapabilities? capabilities, Nexus.Service.Models.Displays.DisplayFingerprint? fingerprint = null, bool primary = false)
    {
        var record = NewDisplayRecord(displayId, displayName, capabilities, fingerprint);
        if (primary) ApplyPrimaryDefaults(record);
        var now = record.FirstSeenAt;

        PanelDeviceRecord? result = null;
        var activated = false;
        _store.Update(s =>
        {
            foreach (var candidate in s.PanelDevices.Values)
            {
                if (!string.Equals(candidate.DisplayId, displayId, StringComparison.Ordinal)) continue;
                if (!PanelHosting.IsOn(candidate))
                {
                    // Turning the panel back on: same record, so layout,
                    // theme, name, and options persist through off/on
                    // cycles (a primary display fills only unset options with
                    // its defaults). Viewport hints refresh from the current OS
                    // facts (resolution/scale/touch may have changed).
                    if (primary) ApplyPrimaryDefaults(candidate);
                    else PanelHosting.Set(candidate, true);
                    candidate.Capabilities = capabilities ?? candidate.Capabilities;
                    candidate.LastSeenAt = now;
                    candidate.DisplayFingerprint ??= fingerprint;
                    activated = true;
                }
                result = Clone(candidate);
                return;
            }
            s.PanelDevices[record.Id] = record;
            result = Clone(record);
            activated = true;
        });

        // Panel on/off is rare and must survive an immediate service exit -
        // a write lost to the flush debounce would silently undo the toggle.
        _store.FlushNow();
        if (activated) DisplayRecordReady?.Invoke(result!);
        return (result!, activated);
    }

    // The primary display is the one the user works on: a panel there behaves like the desktop
    // (see-through, click-through, under windows, no guards) rather than a dedicated screen. A saved option
    // wins over the default. Hosting goes through Hosted so a build without it never sees a fullscreen kiosk
    // on the primary display.
    private void ApplyPrimaryDefaults(PanelDeviceRecord record)
    {
        if (SeeThroughHost)
        {
            record.Backdrop ??= "desktop";
            record.ClickThrough ??= true;
        }
        record.AlwaysOnTop ??= false;
        record.ReserveMonitor ??= false;
        record.KeepCursorOff ??= false;
        record.Hosted = true;
        record.Enabled = false;
    }

    private static bool ValidViewport(KioskViewportDto v) =>
        v.CssWidth is >= 1 and <= 32768 && v.CssHeight is >= 1 and <= 32768 && double.IsFinite(v.Dpr) && v.Dpr is >= 0.25 and <= 8;

    public const int MinGridSlots = 2;
    public const int MaxGridSlots = 64;

    internal static PanelDeviceRecord NewDisplayRecord(string displayId, string? displayName, PanelDeviceCapabilities? capabilities, Nexus.Service.Models.Displays.DisplayFingerprint? fingerprint)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new PanelDeviceRecord
        {
            Id = NewId(),
            DisplayName = NormalizeName(displayName) ?? DefaultName(now),
            FirstSeenAt = now,
            LastSeenAt = now,
            Capabilities = capabilities,
            DisplayId = displayId,
            DisplayFingerprint = fingerprint,
        };
    }

    /// <summary>Widget card opacity a migrated desktop overlay gets when it never set one.</summary>
    public const double FullWidgetOpacity = 1.0;

    /// <summary>Minimum gap between two identical weak plans before the second applies.</summary>
    internal const long WeakRelinkConfirmMs = 5000;

    internal Func<long> ClockMs { get; init; } = () => Environment.TickCount64;

    private readonly object _pendingLock = new();
    // In memory only: record id -> (target display id, first time this exact plan was seen).
    private Dictionary<string, (string Target, long FirstSeenMs)> _pendingWeak = new(StringComparer.Ordinal);

    /// <summary>
    /// Keeps display-bound records attached to their physical monitor: stamps a
    /// hardware fingerprint on records that lack one, and rewrites the
    /// DisplayId of a record whose display id no longer exists when its
    /// hardware is the single unclaimed attached monitor. A serial-backed match
    /// applies at once; a model-only match applies once the identical plan has
    /// been seen on two scans at least <see cref="WeakRelinkConfirmMs"/> apart,
    /// so a transient partial scan cannot hand a record to a lookalike.
    /// Ambiguity changes nothing. <c>PendingWeak</c> tells the caller to rescan.
    /// </summary>
    internal (IReadOnlyList<(string RecordId, string OldDisplayId, string NewDisplayId)> Applied, bool PendingWeak) ReconcileDisplayFingerprints(
        IReadOnlyList<RelinkDisplay> attached)
    {
        static List<RelinkRecord> Snapshot(IEnumerable<PanelDeviceRecord> records)
            => records
                .Where(r => !string.IsNullOrEmpty(r.DisplayId))
                .Select(r => new RelinkRecord(r.Id, r.DisplayId!, r.DisplayFingerprint, r.SiblingDisplayIds?.ToList()))
                .ToList();

        RelinkPlan precheck = null!;
        _store.Read(s => precheck = PanelDisplayRelink.Plan(Snapshot(s.PanelDevices.Values), attached));
        var allowed = new HashSet<(string, string)>();
        bool pending;
        lock (_pendingLock)
        {
            var now = ClockMs();
            var next = new Dictionary<string, (string Target, long FirstSeenMs)>(StringComparer.Ordinal);
            foreach (var move in precheck.Moves)
            {
                if (!move.Weak)
                {
                    allowed.Add((move.RecordId, move.NewDisplayId));
                    continue;
                }
                if (_pendingWeak.TryGetValue(move.RecordId, out var seen)
                    && string.Equals(seen.Target, move.NewDisplayId, StringComparison.Ordinal))
                {
                    if (now - seen.FirstSeenMs >= WeakRelinkConfirmMs) allowed.Add((move.RecordId, move.NewDisplayId));
                    else next[move.RecordId] = seen;
                }
                else
                {
                    next[move.RecordId] = (move.NewDisplayId, now);
                }
            }
            _pendingWeak = next;
            pending = next.Count > 0;
        }
        if (precheck.Backfills.Count == 0 && precheck.SiblingUpdates.Count == 0 && allowed.Count == 0)
            return (Array.Empty<(string, string, string)>(), pending);

        var applied = new List<(string RecordId, string OldDisplayId, string NewDisplayId)>();
        var fingerprinted = new List<string>();
        _store.Update(s =>
        {
            var plan = PanelDisplayRelink.Plan(Snapshot(s.PanelDevices.Values), attached);
            foreach (var (recordId, fingerprint) in plan.Backfills)
            {
                if (s.PanelDevices.TryGetValue(recordId, out var record))
                {
                    record.DisplayFingerprint = fingerprint;
                    fingerprinted.Add(recordId);
                }
            }
            foreach (var (recordId, siblings) in plan.SiblingUpdates)
            {
                if (s.PanelDevices.TryGetValue(recordId, out var record)) record.SiblingDisplayIds = siblings;
            }
            foreach (var move in plan.Moves)
            {
                if (!allowed.Contains((move.RecordId, move.NewDisplayId))) continue;
                if (!s.PanelDevices.TryGetValue(move.RecordId, out var record)) continue;
                record.DisplayId = move.NewDisplayId;
                record.DisplayFingerprint = move.Fingerprint;
                // The reported viewport belongs to the old display's kiosk.
                record.KioskViewport = null;
                fingerprinted.Add(move.RecordId);
                applied.Add((move.RecordId, move.OldDisplayId, move.NewDisplayId));
                ServiceLog.Info($"[panel-relink] record {move.RecordId} display '{move.OldDisplayId}' -> '{move.NewDisplayId}' "
                    + $"(fingerprint {move.Fingerprint.Manufacturer}:{move.Fingerprint.Product} serial {move.Fingerprint.Serial})");
            }
        });
        if (applied.Count > 0) _store.FlushNow();
        if (fingerprinted.Count > 0) FingerprintsChanged?.Invoke(fingerprinted);
        return (applied, pending);
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
                if (!PanelHosting.IsOn(record)) return;
                PanelHosting.Set(record, false);
                snapshot = Clone(record);
                return;
            }
        });
        // Same durability rule as AllocateForDisplay: the OFF must not be
        // lost to the debounce window if the service exits right after.
        _store.FlushNow();
        return snapshot;
    }

    /// <summary>Raised after a display's orientation was persisted, with its display id.</summary>
    internal event Action<string>? DisplayOrientationUpdated;

    /// <summary>
    /// Persist the last orientation applied through Nexus on the display's
    /// record (settings permanence, same model as the Y70's persisted
    /// orientation). The live OS rotation in /displays/topology stays the
    /// authoritative read; this survives service restarts and replug.
    /// </summary>
    public void UpdateDisplayOrientation(string displayId, string orientation)
    {
        if (string.IsNullOrWhiteSpace(displayId)) return;
        try { PersistDisplayOrientation(displayId, orientation); }
        finally { DisplayOrientationUpdated?.Invoke(displayId); }
    }

    private void PersistDisplayOrientation(string displayId, string orientation)
    {
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
            && string.Equals(a.Family, b.Family, StringComparison.Ordinal)
            && a.SupportsBrightness == b.SupportsBrightness
            && a.SupportsSecondaryMonitor == b.SupportsSecondaryMonitor
            && a.SupportsPortrait == b.SupportsPortrait
            && a.SupportsRenderScale == b.SupportsRenderScale;
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

    /// <summary>The hosted panel on the primary display (per <paramref name="isPrimary"/>), or null when there is none.</summary>
    public PanelDeviceRecord? FindPrimaryPanel(Func<string, bool> isPrimary)
    {
        foreach (var record in _store.Load().PanelDevices.Values)
        {
            if (!string.IsNullOrEmpty(record.DisplayId) && PanelHosting.IsOn(record) && isPrimary(record.DisplayId))
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
    /// <remarks>
    /// <paramref name="isPrimary"/> names the OS primary display. There the keep-off guards read false whatever the
    /// record stores: a panel on the display the user works on never evicts windows or fences the mouse.
    /// ClickThrough only reads true with a "desktop" backdrop, where a generic display (no curated family) always passes
    /// empty-space clicks. A generic display is on top only with that backdrop; the stored choices are kept.
    /// </remarks>
    public IReadOnlyList<(string DisplayId, string PanelDeviceId, bool ReserveMonitor, string Backdrop, bool KeepCursorOff, bool AlwaysOnTop, bool ClickThrough, bool IsPrimary)> ListAssignments(Func<string, bool>? isPrimary = null)
    {
        var assignments = new List<(string, string, bool, string, bool, bool, bool, bool)>();
        foreach (var record in _store.Load().PanelDevices.Values)
        {
            if (string.IsNullOrEmpty(record.DisplayId) || !PanelHosting.IsOn(record)) continue;
            var primary = isPrimary?.Invoke(record.DisplayId) ?? false;
            var backdrop = record.Backdrop ?? "";
            var seeThrough = backdrop == "desktop";
            var generic = string.IsNullOrEmpty(record.Capabilities?.Family);
            assignments.Add((
                record.DisplayId,
                record.Id,
                !primary && (record.ReserveMonitor ?? true),
                backdrop,
                !primary && (record.KeepCursorOff ?? false),
                (record.AlwaysOnTop ?? true) && (seeThrough || !generic),
                seeThrough && (generic || (record.ClickThrough ?? false)),
                primary));
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

    /// <summary>
    /// Puts one widget on the attached Y70 panel if it is not already there, so
    /// an app that ships with that panel is on screen without the user placing
    /// it. <see cref="Y70WidgetPlacement.NoPanel"/> means the panel has not
    /// registered yet (it does so when the kiosk first connects), so the caller
    /// should retry rather than treat the placement as done.
    /// </summary>
    /// <remarks>
    /// A null Layout means the record still shows <see cref="PanelLayoutDefaults"/>,
    /// so that default is materialized first; saving a layout holding only this
    /// widget would wipe the starter set the user sees.
    /// </remarks>
    public Y70WidgetPlacement EnsureY70Widget(string widgetType, string size, out string deviceId)
    {
        deviceId = "";
        if (string.IsNullOrWhiteSpace(widgetType)) return Y70WidgetPlacement.NoPanel;
        // Probed before Update, which marks settings dirty and fans out to every
        // subscriber even when the mutator changes nothing - this is called on a
        // retry cadence for as long as no panel has registered.
        if (FindNewestY70(_store.Load()) is null) return Y70WidgetPlacement.NoPanel;

        var result = Y70WidgetPlacement.NoPanel;
        var id = "";
        _store.Update(s =>
        {
            var newest = FindNewestY70(s);
            if (newest is null) return;
            id = newest.Id;

            var layout = newest.Layout
                ?? PanelLayoutDefaults.WithStableIds(PanelLayoutDefaults.ForSurface(PanelSurfaces.Y70), newest.Id);
            foreach (var page in layout.Pages)
            {
                foreach (var widget in page.Widgets)
                {
                    if (!string.Equals(widget.Type, widgetType, StringComparison.Ordinal)) continue;
                    result = Y70WidgetPlacement.AlreadyPresent;
                    return;
                }
            }

            if (layout.Pages.Count == 0)
                layout.Pages.Add(new PanelPageDto { Id = Guid.NewGuid().ToString() });
            // First free rect, spilling to a new page when every page is full.
            // Shared with the Nexus 2 import so both land a widget where the
            // editor would have; appending at a fixed cell overlaps whatever
            // already occupies it, and the client's repagination does not
            // repair a page that cannot be repacked to fit.
            var placing = new PanelWidgetDto
            {
                Id = Guid.NewGuid().ToString(),
                Type = widgetType,
                Size = size,
            };
            if (!Y70LayoutPlacement.Append(layout.Pages, placing, size))
            {
                result = Y70WidgetPlacement.NoRoom;
                return;
            }
            newest.Layout = layout;
            result = Y70WidgetPlacement.Placed;
        });
        deviceId = id;
        return result;
    }

    /// <summary>
    /// Drops every placement of one widget type from every panel record and
    /// returns the ids that changed, so an uninstall reaches panels holding the
    /// app rather than waiting on each client's own reconcile.
    /// </summary>
    public IReadOnlyList<string> RemoveWidgetType(string widgetType)
    {
        if (string.IsNullOrWhiteSpace(widgetType)) return Array.Empty<string>();
        // Probed before Update, which marks settings dirty and fans out to every
        // subscriber even when the mutator changes nothing.
        if (!_store.Load().PanelDevices.Values.Any(r => HasWidgetType(r, widgetType)))
            return Array.Empty<string>();

        var changed = new List<string>();
        _store.Update(s =>
        {
            changed.Clear();
            foreach (var record in s.PanelDevices.Values)
            {
                if (record.Layout is null) continue;
                var removed = 0;
                foreach (var page in record.Layout.Pages)
                    removed += page.Widgets.RemoveAll(w => string.Equals(w.Type, widgetType, StringComparison.Ordinal));
                if (removed == 0) continue;
                var mark = record.Layout.ImmersiveOnLoadWidgetId;
                if (!string.IsNullOrEmpty(mark)
                    && !record.Layout.Pages.Any(p => p.Widgets.Any(w => string.Equals(w.Id, mark, StringComparison.Ordinal))))
                {
                    record.Layout.ImmersiveOnLoadWidgetId = null;
                }
                changed.Add(record.Id);
            }
        });
        return changed;
    }

    private static bool HasWidgetType(PanelDeviceRecord record, string widgetType)
        => record.Layout is not null
           && record.Layout.Pages.Any(p => p.Widgets.Any(w => string.Equals(w.Type, widgetType, StringComparison.Ordinal)));

    internal static PanelDeviceRecord? FindNewestY70(NexusSettings settings)
    {
        PanelDeviceRecord? newest = null;
        foreach (var record in settings.PanelDevices.Values)
        {
            if (!string.Equals(record.Capabilities?.Surface, PanelSurfaces.Y70, StringComparison.Ordinal)) continue;
            if (newest is null || record.LastSeenAt > newest.LastSeenAt) newest = record;
        }
        return newest;
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

    private const long TouchIntervalMs = 30_000;

    // A single-instance surface is the record's identity, not a viewport sample:
    // a Y70 kiosk that first maps on the desktop monitor would rename itself
    // "phone". Every other field a patch omits keeps its stored value.
    private static PanelDeviceCapabilities MergeCapabilities(PanelDeviceCapabilities? existing, PanelDeviceCapabilities patch)
    {
        var keepSurface = existing?.Surface is PanelSurfaces.Y70 or PanelSurfaces.Q60 ? existing.Surface : null;
        return new PanelDeviceCapabilities
        {
            Surface = keepSurface ?? patch.Surface ?? existing?.Surface,
            Grid = patch.Grid ?? existing?.Grid,
            Touch = patch.Touch ?? existing?.Touch,
            Orientation = patch.Orientation ?? existing?.Orientation,
            CssWidth = patch.CssWidth ?? existing?.CssWidth,
            CssHeight = patch.CssHeight ?? existing?.CssHeight,
            Dpr = patch.Dpr ?? existing?.Dpr,
            Dpi = patch.Dpi ?? existing?.Dpi,
            Family = patch.Family ?? existing?.Family,
            SupportsBrightness = patch.SupportsBrightness ?? existing?.SupportsBrightness,
            SupportsSecondaryMonitor = patch.SupportsSecondaryMonitor ?? existing?.SupportsSecondaryMonitor,
            SupportsPortrait = patch.SupportsPortrait ?? existing?.SupportsPortrait,
            SupportsRenderScale = patch.SupportsRenderScale ?? existing?.SupportsRenderScale,
        };
    }

    /// <summary>Sets or clears the service-internal lock restore level without bumping LastSeenAt.</summary>
    public void SetLockRestoreBrightness(string id, int? value)
    {
        if (string.IsNullOrWhiteSpace(id) || _store.Load().PanelDevices.GetValueOrDefault(id)?.LockRestoreBrightness == value)
            return;
        _store.Update(s =>
        {
            if (s.PanelDevices.TryGetValue(id, out var record))
                record.LockRestoreBrightness = value;
        });
    }

    public PanelDeviceRecord? Touch(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        PanelDeviceRecord? snapshot = null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var current = _store.Load().PanelDevices.GetValueOrDefault(id);
        if (current is null) return null;
        if (now - current.LastSeenAt < TouchIntervalMs) return Clone(current);
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
                record.BackgroundEffect = NullIfEmpty(Nexus.Service.Lighting.SimplePatternMigration.Normalize(patch.BackgroundEffect));
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
            if (patch.BackgroundMediaAlpha.HasValue)
                record.BackgroundMediaAlpha = patch.BackgroundMediaAlpha.Value;
            if (patch.BackgroundMediaSlideshow.HasValue)
                record.BackgroundMediaSlideshow = patch.BackgroundMediaSlideshow.Value;
            if (patch.BackgroundMediaInterval.HasValue)
                record.BackgroundMediaInterval = patch.BackgroundMediaInterval.Value;
            if (patch.BackgroundMediaShuffle.HasValue)
                record.BackgroundMediaShuffle = patch.BackgroundMediaShuffle.Value;
            if (patch.BackgroundMediaFinishVideos.HasValue)
                record.BackgroundMediaFinishVideos = patch.BackgroundMediaFinishVideos.Value;
            if (patch.BackgroundMediaOrder is not null)
                record.BackgroundMediaOrder = new List<string>(patch.BackgroundMediaOrder);
            if (patch.BackgroundFrostLevel.HasValue)
                record.BackgroundFrostLevel = patch.BackgroundFrostLevel.Value;
            if (patch.ImmersiveBackdrop.HasValue)
                record.ImmersiveBackdrop = patch.ImmersiveBackdrop.Value;
            if (patch.LightingSelection is not null)
                record.LightingSelection = new List<string>(patch.LightingSelection);
            if (patch.CoolingSelection is not null)
                record.CoolingSelection = new List<string>(patch.CoolingSelection);
            if (patch.GaugeGradient is not null)
                record.GaugeGradient = CloneGaugeGradient(patch.GaugeGradient);
            if (patch.WidgetOpacity.HasValue)
                record.WidgetOpacity = patch.WidgetOpacity.Value;
            if (patch.WidgetLabels.HasValue)
                record.WidgetLabels = patch.WidgetLabels.Value;
            if (patch.WidgetPadding.HasValue)
                record.WidgetPadding = patch.WidgetPadding.Value;
            if (patch.TextColorMode is not null)
                record.TextColorMode = patch.TextColorMode;
            if (patch.TextColor is not null)
                record.TextColor = patch.TextColor;
            if (patch.Font is not null && PanelFontValue.TryNormalize(patch.Font, out var font))
                record.Font = font;
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
            if (patch.KeepCursorOff.HasValue && !string.IsNullOrEmpty(record.DisplayId))
                record.KeepCursorOff = patch.KeepCursorOff.Value;
            if (patch.AlwaysOnTop.HasValue && !string.IsNullOrEmpty(record.DisplayId))
                record.AlwaysOnTop = patch.AlwaysOnTop.Value;
            if (patch.ClickThrough.HasValue && !string.IsNullOrEmpty(record.DisplayId))
                record.ClickThrough = patch.ClickThrough.Value;
            if (patch.WidgetsLocked.HasValue && !string.IsNullOrEmpty(record.DisplayId))
                record.WidgetsLocked = patch.WidgetsLocked.Value;
            // Zero or less clears the override (a null in a patch means "leave it"); other invalid counts are ignored.
            if (patch.GridSlots is <= 0)
                record.GridSlots = null;
            else if (patch.GridSlots is { } slots && slots is >= MinGridSlots and <= MaxGridSlots && slots % 2 == 0)
                record.GridSlots = slots;
            if (patch.ClearDesktopImport == true)
                record.DesktopImport = null;
            // Orientation is a property of how the glass is physically mounted, so it
            // applies to any record; the transport ignores it on surfaces it cannot turn.
            if (patch.Flip180.HasValue)
                record.Flip180 = patch.Flip180.Value;
            if (patch.Mirror.HasValue)
                record.Mirror = patch.Mirror.Value;
            // Same reasoning: a backlight belongs to the glass, and transports that
            // cannot dim never read it.
            if (patch.LcdBrightness.HasValue)
                record.LcdBrightness = Math.Clamp(patch.LcdBrightness.Value, 0, 100);
            if (patch.SleepWhenLocked.HasValue)
                record.SleepWhenLocked = patch.SleepWhenLocked.Value;
            if (patch.SecondaryMonitor.HasValue)
                record.SecondaryMonitor = patch.SecondaryMonitor.Value;
            if (patch.Portrait.HasValue)
                record.Portrait = patch.Portrait.Value;
            if (patch.HighResolution.HasValue)
                record.HighResolution = patch.HighResolution.Value;
            if (patch.WidgetSize is PanelWidgetSizes.Large or PanelWidgetSizes.MidLarge or PanelWidgetSizes.Small or PanelWidgetSizes.Medium or PanelWidgetSizes.XSmall or PanelWidgetSizes.Compact)
                record.WidgetSize = patch.WidgetSize;
            if (patch.KioskViewport is { } viewport && !string.IsNullOrEmpty(record.DisplayId) && ValidViewport(viewport))
                record.KioskViewport = new KioskViewportDto { CssWidth = viewport.CssWidth, CssHeight = viewport.CssHeight, Dpr = viewport.Dpr };
            // Capabilities on display-bound records are owned by the topology
            // sync (rebuilt from OS facts); a client value would ping-pong
            // with the next sync pass.
            if (patch.Capabilities is not null && string.IsNullOrEmpty(record.DisplayId))
                record.Capabilities = MergeCapabilities(record.Capabilities, patch.Capabilities);

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
    /// the next read; a streamed strip stores its strip seed instead. Identity and hardware-scoped state survive - id, name,
    /// display binding, capabilities, enabled state, monitor behavior
    /// (ReserveMonitor/AutoOrient/KeepCursorOff/AlwaysOnTop - see <see cref="ResetHardwareSettings"/>),
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
            record.Layout = PanelLayoutDefaults.StripSeedFor(record);
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
            record.BackgroundMediaAlpha = null;
            record.BackgroundMediaSlideshow = null;
            record.BackgroundMediaInterval = null;
            record.BackgroundMediaShuffle = null;
            record.BackgroundMediaFinishVideos = null;
            record.BackgroundMediaOrder = null;
            record.BackgroundFrostLevel = null;
            record.ImmersiveBackdrop = null;
            record.LightingSelection = null;
            record.CoolingSelection = null;
            record.GaugeGradient = null;
            record.WidgetOpacity = null;
            record.WidgetLabels = null;
            record.WidgetPadding = null;
            record.TextColorMode = null;
            record.TextColor = null;
            record.Font = null;
            record.ThemeSyncWithDesktop = null;
            record.AccentSyncWithDesktop = null;
            record.WidgetSize = null;
            record.GridSlots = null;
            // Presets are personalization too, and they reference the media
            // the caller deletes.
            record.Presets = null;
            record.ActivePresetId = null;
            record.WidgetsLocked = null;
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
    /// null = default on; KeepCursorOff, null = default off; AlwaysOnTop,
    /// null = default on) and the recorded
    /// Xeneon DDC values. The route owns
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
            record.KeepCursorOff = null;
            record.AlwaysOnTop = null;
            record.ClickThrough = null;
            record.Flip180 = null;
            record.Mirror = null;
            record.LcdBrightness = null;
            record.SleepWhenLocked = null;
            record.LockRestoreBrightness = null;
            record.SecondaryMonitor = null;
            record.Portrait = null;
            record.HighResolution = null;
            record.XeneonEdgeSettings = null;
            record.LastSeenAt = now;
            snapshot = Clone(record);
        });
        if (snapshot is not null) _store.FlushNow();
        return snapshot;
    }

    /// <summary>The panel's presets and which one is loaded. Null when the id is unknown.</summary>
    public PanelPresetsResponse? ListPresets(string id)
    {
        var record = _store.Load().PanelDevices.GetValueOrDefault(id);
        return record is null ? null : PanelPresets.ToResponse(record);
    }

    /// <summary>
    /// Saves the live personalization as a new preset and loads it. Null when
    /// the id is unknown, or when the panel already holds
    /// <see cref="PanelPresets.Cap"/> presets (<paramref name="capped"/>).
    /// </summary>
    public PanelPresetsResponse? CreatePreset(string id, string name, out bool capped)
    {
        PanelPresetsResponse? result = null;
        var hitCap = false;
        _store.Update(s =>
        {
            if (!s.PanelDevices.TryGetValue(id, out var record))
                return;
            record.Presets ??= new List<PanelPreset>();
            if (record.Presets.Count >= PanelPresets.Cap)
            {
                hitCap = true;
                return;
            }
            PanelPresets.CaptureActive(record);
            var preset = new PanelPreset { Id = Guid.NewGuid().ToString("n"), Name = name };
            PanelPresets.Copy(record, preset);
            record.Presets.Add(preset);
            record.ActivePresetId = preset.Id;
            result = PanelPresets.ToResponse(record);
        });
        capped = hitCap;
        return result;
    }

    /// <summary>Null when either id is unknown.</summary>
    public PanelPresetsResponse? RenamePreset(string id, string presetId, string name)
    {
        PanelPresetsResponse? result = null;
        _store.Update(s =>
        {
            if (!s.PanelDevices.TryGetValue(id, out var record))
                return;
            var preset = record.Presets?.Find(p => p.Id == presetId);
            if (preset is null)
                return;
            preset.Name = name;
            result = PanelPresets.ToResponse(record);
        });
        return result;
    }

    /// <summary>Deleting the loaded preset leaves the live personalization as
    /// it is, with no preset loaded. Null when the panel id is unknown.</summary>
    public PanelPresetsResponse? DeletePreset(string id, string presetId)
    {
        PanelPresetsResponse? result = null;
        _store.Update(s =>
        {
            if (!s.PanelDevices.TryGetValue(id, out var record))
                return;
            record.Presets?.RemoveAll(p => p.Id == presetId);
            if (record.ActivePresetId == presetId)
                record.ActivePresetId = null;
            result = PanelPresets.ToResponse(record);
        });
        return result;
    }

    /// <summary>
    /// Loads a preset into the live record, first capturing the live fields
    /// into the preset being left. Null when either id is unknown. The caller
    /// broadcasts the device change.
    /// </summary>
    public PanelPresetsResponse? ActivatePreset(string id, string presetId)
    {
        PanelPresetsResponse? result = null;
        _store.Update(s =>
        {
            if (!s.PanelDevices.TryGetValue(id, out var record))
                return;
            var target = record.Presets?.Find(p => p.Id == presetId);
            if (target is null)
                return;
            PanelPresets.CaptureActive(record);
            PanelPresets.Copy(target, record);
            record.ActivePresetId = target.Id;
            result = PanelPresets.ToResponse(record);
        });
        return result;
    }

    /// <summary>
    /// Replaces a preset's app bindings. An app triggers one preset per panel,
    /// so a binding another of this panel's presets holds is refused
    /// (<paramref name="conflict"/>) rather than moved. Null when either id is
    /// unknown or on conflict.
    /// </summary>
    public PanelPresetsResponse? SetPresetApps(
        string id, string presetId, List<PresetAppBinding> apps, out (string AppName, string PresetName)? conflict)
    {
        PanelPresetsResponse? result = null;
        (string, string)? clash = null;
        _store.Update(s =>
        {
            if (!s.PanelDevices.TryGetValue(id, out var record))
                return;
            var preset = record.Presets?.Find(p => p.Id == presetId);
            if (preset is null)
                return;
            // Matched on the resolved process name as well as the id: the same
            // app can be picked off the running list or the installed list.
            foreach (var other in record.Presets!)
            {
                if (other.Id == presetId || other.Apps is null)
                    continue;
                foreach (var taken in other.Apps)
                {
                    var hit = apps.Find(a =>
                        string.Equals(a.Id, taken.Id, StringComparison.OrdinalIgnoreCase)
                        || (a.ProcessName.Length > 0 && string.Equals(a.ProcessName, taken.ProcessName, StringComparison.Ordinal)));
                    if (hit is not null)
                    {
                        clash = (hit.Name, other.Name);
                        return;
                    }
                }
            }
            preset.Apps = apps;
            result = PanelPresets.ToResponse(record);
        });
        conflict = clash;
        return result;
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

    /// <summary>True while a desktop-capable panel has no backdrop chosen.</summary>
    public bool HasUnsetDesktopBackdrop() =>
        SeeThroughHost && _store.Load().PanelDevices.Values.Any(CanLatchDesktop);

    /// <summary>Stores "desktop" on those panels and returns their ids; stored, not resolved on read, so it outlives its trigger.</summary>
    public IReadOnlyList<string> LatchDesktopBackdrop()
    {
        var latched = new List<string>();
        if (!HasUnsetDesktopBackdrop()) return latched;
        _store.Update(s =>
        {
            foreach (var record in s.PanelDevices.Values)
            {
                if (!CanLatchDesktop(record)) continue;
                record.Backdrop = "desktop";
                // A preset saved while unset would otherwise restore null on activation.
                if (record.Presets is { } presets)
                {
                    foreach (var preset in presets) preset.Backdrop ??= "desktop";
                }
                latched.Add(record.Id);
            }
        });
        if (latched.Count > 0) _store.FlushNow();
        return latched;
    }

    // The surfaces the web's supportsDesktopWallpaper admits, on a see-through host.
    private bool CanLatchDesktop(PanelDeviceRecord r)
    {
        if (r.Backdrop is not null || !SeeThroughHost) return false;
        var surface = r.Capabilities?.Surface;
        return (surface == PanelSurfaces.Y70 && Y70SeeThroughDefault())
            || (surface == PanelSurfaces.Monitor && !string.IsNullOrEmpty(r.DisplayId));
    }

    // Compatibility mode's --disable-direct-composition kiosk is unverified with
    // an alpha-0 see-through window, so those Y70s keep the client default.
    // Load re-enters the store's Monitor lock, so this is safe inside Update.
    private bool Y70SeeThroughDefault() =>
        SeeThroughHost && !_store.Load().Y70.CompatibilityRendering;

    private static string NewId()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(9))
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static List<PanelGaugeGradientStop> CloneGaugeGradient(List<PanelGaugeGradientStop> stops)
    {
        var copy = new List<PanelGaugeGradientStop>(stops.Count);
        foreach (var stop in stops)
            copy.Add(new PanelGaugeGradientStop { At = stop.At, Color = stop.Color });
        return copy;
    }

    /// <summary>
    /// True when the record holds anything beyond what a freshly minted one with the same
    /// identity and capabilities does: a layout, a theme, a background, a flag the user set.
    /// Compared as stored JSON, so a field added to the record later is covered without a list.
    /// </summary>
    public static bool IsCustomized(PanelDeviceRecord r)
    {
        var fresh = new PanelDeviceRecord
        {
            Id = r.Id,
            DisplayName = r.DisplayName,
            FirstSeenAt = r.FirstSeenAt,
            LastSeenAt = r.LastSeenAt,
            Capabilities = r.Capabilities,
        };
        return System.Text.Json.JsonSerializer.Serialize(r, Serialization.AppJsonContext.Default.PanelDeviceRecord)
            != System.Text.Json.JsonSerializer.Serialize(fresh, Serialization.AppJsonContext.Default.PanelDeviceRecord);
    }

    /// <summary>
    /// <see cref="IsCustomized(PanelDeviceRecord)"/> on the stored instance, not a response clone,
    /// which leaves out presets, the active preset and the display fields.
    /// </summary>
    public bool IsCustomized(string id)
    {
        var customized = false;
        _store.Read(s =>
        {
            if (s.PanelDevices.TryGetValue(id, out var stored))
            {
                customized = IsCustomized(stored);
            }
        });
        return customized;
    }

    // The response clone: DisplayFingerprint and SiblingDisplayIds stay in the store, off the wire.
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
            BackgroundMediaAlpha = r.BackgroundMediaAlpha,
            BackgroundMediaSlideshow = r.BackgroundMediaSlideshow,
            BackgroundMediaInterval = r.BackgroundMediaInterval,
            BackgroundMediaShuffle = r.BackgroundMediaShuffle,
            BackgroundMediaFinishVideos = r.BackgroundMediaFinishVideos,
            BackgroundMediaOrder = r.BackgroundMediaOrder is null ? null : new List<string>(r.BackgroundMediaOrder),
            BackgroundFrostLevel = r.BackgroundFrostLevel,
            ImmersiveBackdrop = r.ImmersiveBackdrop,
            LightingSelection = r.LightingSelection is null ? null : new List<string>(r.LightingSelection),
            CoolingSelection = r.CoolingSelection is null ? null : new List<string>(r.CoolingSelection),
            GaugeGradient = r.GaugeGradient is null ? null : CloneGaugeGradient(r.GaugeGradient),
            WidgetOpacity = r.WidgetOpacity,
            WidgetLabels = r.WidgetLabels,
            WidgetPadding = r.WidgetPadding,
            TextColorMode = r.TextColorMode,
            TextColor = r.TextColor,
            Font = r.Font,
            ThemeSyncWithDesktop = r.ThemeSyncWithDesktop,
            AccentSyncWithDesktop = r.AccentSyncWithDesktop,
            FirstSeenAt = r.FirstSeenAt,
            LastSeenAt = r.LastSeenAt,
            Capabilities = r.Capabilities,
            DisplayId = r.DisplayId,
            ReserveMonitor = r.ReserveMonitor,
            AutoOrient = r.AutoOrient,
            KeepCursorOff = r.KeepCursorOff,
            AlwaysOnTop = r.AlwaysOnTop,
            Flip180 = r.Flip180,
            Mirror = r.Mirror,
            LcdBrightness = r.LcdBrightness,
            SleepWhenLocked = r.SleepWhenLocked,
            LockRestoreBrightness = r.LockRestoreBrightness,
            SecondaryMonitor = r.SecondaryMonitor,
            Portrait = r.Portrait,
            HighResolution = r.HighResolution,
            WidgetSize = r.WidgetSize,
            KioskViewport = r.KioskViewport,
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
            // Hosted wins when set; otherwise Enabled passes through, so an untouched record still reads as uncustomized.
            Enabled = r.Hosted ?? r.Enabled,
            ClickThrough = r.ClickThrough,
            WidgetsLocked = r.WidgetsLocked,
            GridSlots = r.GridSlots is > 0 ? r.GridSlots : null,
            DesktopImport = r.DesktopImport,
        };
    }
}

/// <summary>Outcome of <see cref="PanelDeviceRegistry.EnsureY70Widget"/>.</summary>
public enum Y70WidgetPlacement
{
    /// <summary>No Y70 panel has registered yet; retry once the kiosk connects.</summary>
    NoPanel,
    /// <summary>The widget was already on the panel; nothing was written.</summary>
    AlreadyPresent,
    /// <summary>The widget was placed in the first free slot on the panel.</summary>
    Placed,
    /// <summary>Every page is full and the page cap is reached; nothing was written.</summary>
    NoRoom,
}
