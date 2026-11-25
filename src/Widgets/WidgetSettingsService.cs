using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Persistence;

namespace Nexus.Service.Widgets;

/// <summary>
/// Reads + writes per-instance marketplace widget settings via the layout
/// stored on <see cref="IConfigStore"/>. Each placement has its own
/// <see cref="PanelWidgetDto.Config"/> dictionary; manifest defaults are
/// merged on read so widget code sees a complete document. Type-scoped
/// storage (the old <c>NexusSettings.Widgets</c> bag) was retired in schema v4.
/// </summary>
public sealed class WidgetSettingsService
{
    // Convention for installed-app widget placement types: native widget types
    // are bare ("clock", "monitoring", ...); app placements prefix with this so
    // the renderer can route to the declarative engine instead of a built-in
    // component.
    public const string AppTypePrefix = "app:";

    // Single-widget playlist stand-in: "playlist:<deviceId>:<type>". It sits on no
    // page; its config is the device layout's SingleWidgetConfigs[type].
    private const string PlaylistStandInPrefix = "playlist:";

    private readonly IConfigStore _store;
    private readonly AppRegistry _registry;

    public WidgetSettingsService(IConfigStore store, AppRegistry registry)
    {
        _store = store;
        _registry = registry;
        _store.OnChanged += OnStoreChanged;
    }

    /// <summary>
    /// Build the effective settings document for a single placement. Manifest
    /// defaults supply the full key set; the placement's persisted overrides
    /// (if any) win on key collision. Returns an empty document if the
    /// placement isn't found or its type doesn't resolve to a known
    /// marketplace widget.
    /// </summary>
    public WidgetSettingsDocument Get(string instanceId)
    {
        var doc = new WidgetSettingsDocument { AppId = instanceId };
        _store.Read(s =>
        {
            if (TryBuild(s, instanceId, out var built))
            {
                doc = built;
            }
            doc.Revision = Interlocked.Read(ref _revision);
        });
        return doc;
    }

    /// <summary>
    /// Apply a partial patch to a placement's per-instance config and return
    /// the new effective document. Unknown keys (not declared in the
    /// manifest) are dropped silently. The document that is broadcast is built
    /// inside the store lock, so it is exactly this write's state.
    /// </summary>
    public WidgetSettingsDocument Apply(string instanceId, WidgetSettingsPatch patch)
    {
        var pending = new List<WidgetSettingsDocument>();
        WidgetSettingsDocument? written = null;
        _store.Update(s =>
        {
            pending.Clear();
            written = null;
            if (!TryResolve(s, instanceId, out var widget, out var manifest)) return;
            widget!.Config ??= new Dictionary<string, JsonElement>();
            if (patch.Set is not null)
            {
                foreach (var kv in patch.Set)
                {
                    if (!HasManifestKey(manifest!, kv.Key)) continue;
                    widget.Config[kv.Key] = kv.Value.Clone();
                }
            }
            if (patch.Reset is not null)
            {
                foreach (var key in patch.Reset)
                {
                    widget.Config.Remove(key);
                }
            }
            if (widget.Config.Count == 0) widget.Config = null;
            StoreStandInConfig(s, instanceId, widget.Config);
            if (TryBuild(s, instanceId, out var doc))
            {
                doc.Revision = Interlocked.Increment(ref _revision);
                lock (_stateLock) { _fingerprints[instanceId] = Fingerprint(doc); }
                written = doc;
                pending.Add(doc);
            }
            bool primed;
            lock (_stateLock) { primed = _primed; }
            if (primed) Reconcile(s, pending);
        });
        foreach (var doc in pending)
        {
            SettingsChanged?.Invoke(doc.AppId, doc);
        }
        return written ?? Get(instanceId);
    }

    /// <summary>
    /// Raised with the instance id and the full effective document (carrying its revision) whenever an app
    /// placement's effective settings changed: through <see cref="Apply"/>, or any other write to the layouts
    /// (layout PUT, preset or profile apply, import).
    /// </summary>
    public event System.Action<string, WidgetSettingsDocument>? SettingsChanged;

    // Process-wide and seeded from the clock (milliseconds, so it stays inside the integer range a JS number holds exactly) so revisions keep increasing across service restarts.
    private static long _revision = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    // Guards _fingerprints and _primed. Taken inside the store lock by scans and on its own by the reset, never the other way round.
    private readonly object _stateLock = new();
    private readonly Dictionary<string, string> _fingerprints = new(System.StringComparer.Ordinal);

    /// <summary>Whether any client listens for settings frames. Null means always (no hub attached). Gates the per-write scan.</summary>
    public System.Func<bool>? HasListeners { get; set; }

    private bool _primed;

    private void OnStoreChanged()
    {
        if (HasListeners is { } has && !has())
        {
            // Nobody to tell: drop state so the next listener's first scan starts clean.
            lock (_stateLock)
            {
                _primed = false;
                _fingerprints.Clear();
            }
            return;
        }
        var pending = new List<WidgetSettingsDocument>();
        // Load manifests before taking the store lock so a first disk scan never holds it.
        _registry.TryGet(string.Empty, out _);
        _store.Read(s =>
        {
            lock (_stateLock)
            {
                _primed = true;
                Reconcile(s, pending);
            }
        });
        foreach (var doc in pending)
        {
            SettingsChanged?.Invoke(doc.AppId, doc);
        }
    }

    /// <summary>
    /// Compares every app placement's effective settings with the last seen state; a new or changed one gets a
    /// fresh revision and is added to <paramref name="changed"/> (the first scan after a listener appears therefore
    /// reports everything, with current values). A playlist stand-in whose remembered config vanished is still
    /// reported, with its default values, as long as its device and slot exist. Other entries that no longer exist are pruned.
    /// </summary>
    private void Reconcile(NexusSettings s, List<WidgetSettingsDocument> changed)
    {
        lock (_stateLock)
        {
            var ids = AppInstanceIds(s);
            foreach (var known in _fingerprints.Keys)
            {
                if (known.StartsWith(PlaylistStandInPrefix, System.StringComparison.Ordinal)) ids.Add(known);
            }
            var stale = new List<string>();
            foreach (var key in _fingerprints.Keys)
            {
                if (!ids.Contains(key)) stale.Add(key);
            }
            foreach (var key in stale) _fingerprints.Remove(key);
            foreach (var id in ids)
            {
                if (!TryBuild(s, id, out var doc))
                {
                    _fingerprints.Remove(id);
                    continue;
                }
                var fp = Fingerprint(doc);
                if (_fingerprints.TryGetValue(id, out var last) && last == fp) continue;
                _fingerprints[id] = fp;
                doc.Revision = Interlocked.Increment(ref _revision);
                changed.Add(doc);
            }
        }
    }

    private static HashSet<string> AppInstanceIds(NexusSettings s)
    {
        var ids = new HashSet<string>(System.StringComparer.Ordinal);
        void Collect(PanelLayoutDto? layout)
        {
            if (layout is null) return;
            foreach (var page in layout.Pages)
            {
                foreach (var w in page.Widgets)
                {
                    if (MarketplaceIdFromType(w.Type) is not null) ids.Add(w.Id);
                }
            }
        }
        Collect(s.Panel.DashboardLayout);
        foreach (var (deviceId, device) in s.PanelDevices)
        {
            foreach (var layout in PanelLayoutSlots.All(device)) Collect(layout);
            if (PanelLayoutSlots.Active(device)?.SingleWidgetConfigs is { } remembered)
            {
                foreach (var type in remembered.Keys)
                {
                    if (MarketplaceIdFromType(type) is not null) ids.Add(PlaylistStandInPrefix + deviceId + ":" + type);
                }
            }
        }
        return ids;
    }

    private static string Fingerprint(WidgetSettingsDocument doc)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var kv in doc.Values.OrderBy(k => k.Key, System.StringComparer.Ordinal))
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value.GetRawText()).Append('\n');
        }
        return sb.ToString();
    }

    private bool TryBuild(NexusSettings s, string instanceId, out WidgetSettingsDocument doc)
    {
        doc = new WidgetSettingsDocument { AppId = instanceId };
        if (!TryResolve(s, instanceId, out var widget, out var manifest)) return false;
        foreach (var entry in manifest!.Settings)
        {
            doc.Values[entry.Key] = Clone(entry.Default);
        }
        if (widget!.Config is { } overrides)
        {
            foreach (var kv in overrides)
            {
                doc.Values[kv.Key] = kv.Value.Clone();
            }
        }
        return true;
    }

    /// <summary>The app and version behind an app widget instance, false for any other id.</summary>
    public bool TryGetApp(string instanceId, out string appId, out string appVersion)
    {
        appId = "";
        appVersion = "";
        if (!TryResolve(_store.Load(), instanceId, out var widget, out var manifest)) return false;
        appId = manifest!.Id;
        appVersion = manifest.Version;
        return true;
    }

    /// <summary>
    /// Find the <see cref="PanelWidgetDto"/> with this id anywhere in the
    /// persisted layouts (active desktop dashboard + every registered panel
    /// device). Also resolves the marketplace manifest so callers don't need
    /// a second lookup. Returns false if either step fails.
    /// </summary>
    private bool TryResolve(NexusSettings s, string instanceId, out PanelWidgetDto? widget, out AppManifest? manifest)
    {
        widget = FindWidget(s, instanceId);
        manifest = null;
        if (widget is null) return false;
        var marketplaceId = MarketplaceIdFromType(widget.Type);
        if (marketplaceId is null) return false;
        if (!_registry.TryGet(marketplaceId, out var entry)) return false;
        manifest = entry.Manifest;
        return true;
    }

    private static PanelWidgetDto? FindWidget(NexusSettings s, string instanceId)
    {
        if (TryParseStandIn(instanceId, out var standInDevice, out var standInType))
        {
            if (!s.PanelDevices.TryGetValue(standInDevice, out var device) || PanelLayoutSlots.Active(device) is not { } shown) return null;
            var remembered = shown.SingleWidgetConfigs;
            return new PanelWidgetDto
            {
                Id = instanceId,
                Type = standInType,
                Config = remembered is not null && remembered.TryGetValue(standInType, out var config)
                    ? new Dictionary<string, JsonElement>(config)
                    : null,
            };
        }
        var dash = s.Panel.DashboardLayout;
        if (dash is not null)
        {
            var hit = FindInLayout(dash, instanceId);
            if (hit is not null) return hit;
        }
        foreach (var device in s.PanelDevices.Values)
        {
            // The slot the panel shows first, so an id the other slot still shares is the one the user is editing.
            var shown = PanelLayoutSlots.Active(device);
            foreach (var layout in PanelLayoutSlots.All(device).OrderBy(l => ReferenceEquals(l, shown) ? 0 : 1))
            {
                var hit = FindInLayout(layout, instanceId);
                if (hit is not null) return hit;
            }
        }
        return null;
    }

    private static PanelWidgetDto? FindInLayout(PanelLayoutDto layout, string instanceId)
    {
        foreach (var page in layout.Pages)
        {
            foreach (var w in page.Widgets)
            {
                if (w.Id == instanceId) return w;
            }
        }
        return null;
    }

    private static bool TryParseStandIn(string instanceId, out string deviceId, out string type)
    {
        deviceId = "";
        type = "";
        if (!instanceId.StartsWith(PlaylistStandInPrefix, System.StringComparison.Ordinal)) return false;
        var rest = instanceId.Substring(PlaylistStandInPrefix.Length);
        var split = rest.IndexOf(':');
        if (split <= 0 || split == rest.Length - 1) return false;
        deviceId = rest.Substring(0, split);
        type = rest.Substring(split + 1);
        return true;
    }

    private static void StoreStandInConfig(NexusSettings s, string instanceId, Dictionary<string, JsonElement>? config)
    {
        if (!TryParseStandIn(instanceId, out var deviceId, out var type)) return;
        if (!s.PanelDevices.TryGetValue(deviceId, out var device) || PanelLayoutSlots.Active(device) is not { } shown) return;
        shown.SingleWidgetConfigs ??= new Dictionary<string, Dictionary<string, JsonElement>>();
        shown.SingleWidgetConfigs[type] = config ?? new Dictionary<string, JsonElement>();
    }

    public static string? MarketplaceIdFromType(string type)
    {
        if (string.IsNullOrEmpty(type)) return null;
        return type.StartsWith(AppTypePrefix, System.StringComparison.Ordinal)
            ? type.Substring(AppTypePrefix.Length)
            : null;
    }

    private static bool HasManifestKey(AppManifest manifest, string key)
    {
        foreach (var entry in manifest.Settings)
        {
            if (entry.Key == key) return true;
        }
        return false;
    }

    private static JsonElement Clone(JsonElement? source)
    {
        if (source is null) return JsonElementNull();
        if (source.Value.ValueKind == JsonValueKind.Undefined) return JsonElementNull();
        return source.Value.Clone();
    }

    private static JsonElement JsonElementNull()
    {
        using var doc = JsonDocument.Parse("null");
        return doc.RootElement.Clone();
    }
}
