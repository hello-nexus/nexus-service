using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Qos.Service.Serialization;

namespace Qos.Service.Persistence;

/// <summary>
/// File-backed QosSettings store.
/// Path: ~/Library/Application Support/Qos/settings.json on macOS,
///       %LOCALAPPDATA%/Qos/settings.json on Windows,
///       $XDG_CONFIG_HOME/Qos/settings.json (or ~/.config/Qos) on Linux.
///
/// Concurrency: a single global lock around load/save. Updates mutate the in-memory
/// doc synchronously, but the disk write is coalesced to a short debounce window so
/// lighting slider drags (10-20 Hz) don't serialize and fsync the whole settings
/// JSON on every frame. Dispose() flushes any pending write.
///
/// Atomic write: serialize -> write to settings.json.tmp -> File.Replace into place.
/// If the destination doesn't exist yet (first run), File.Move handles that path.
/// </summary>
public sealed class JsonConfigStore : IConfigStore, IDisposable
{
    private const int FlushDebounceMs = 300;

    private readonly object _lock = new();
    private QosSettings? _cached;
    private Timer? _flushTimer;
    private bool _dirty;
    private bool _disposed;

    public JsonConfigStore()
    {
        SettingsPath = ResolveSettingsPath();
    }

    // Test-only ctor: lets unit tests point the store at a throwaway path
    // instead of clobbering the user's real settings.json. Prod wiring uses
    // the parameterless ctor registered in DI.
    internal JsonConfigStore(string settingsPath)
    {
        SettingsPath = settingsPath;
    }

    public string SettingsPath { get; }

    public QosSettings Load()
    {
        lock (_lock)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            if (!File.Exists(SettingsPath))
            {
                _cached = new QosSettings();
                Persist(_cached);
                return _cached;
            }

            try
            {
                var json = File.ReadAllText(SettingsPath);
                // Pre-deserialize migrations run in order:
                //   1. Rename legacy `desktop*` keys to `overlay*` (early-dev rename).
                //   2. Nest flat Ui.{theme/panel/overlay/monitoring} fields out of
                //      UiSettings into matching top-level POCOs (schema v1 → v2).
                var afterOverlay = MigrateLegacyOverlayKeys(json);
                var afterV1 = MigrateV1ToV2(afterOverlay);
                var afterV2 = MigrateV2ToV3(afterV1);
                var afterV3 = MigrateV3ToV4(afterV2);
                var afterV4 = MigrateV4ToV5(afterV3);
                var migratedJson = MigrateV5ToV6(afterV4);
                if (!ReferenceEquals(migratedJson, json))
                {
                    _dirty = true;
                    ScheduleFlushLocked();
                }
                _cached = JsonSerializer.Deserialize(migratedJson, PersistenceJsonContext.Default.QosSettings) ?? new QosSettings();
                // Only relevant when we actually read an existing file: a
                // freshly-constructed QosSettings has Ui.PanelDevices
                // null, so the migration would no-op anyway.
                if (Qos.Service.Panel.PanelDeviceRegistry.HasLegacyPanelDevices(_cached))
                {
                    Qos.Service.Panel.PanelDeviceRegistry.MigrateLegacyPanelDevices(_cached);
                    _dirty = true;
                    ScheduleFlushLocked();
                }
                if (HasLegacyStationBlock(json))
                {
                    _dirty = true;
                    ScheduleFlushLocked();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qos-service] settings.json corrupted, starting fresh: {ex.Message}");
                _cached = new QosSettings();
            }

            return _cached;
        }
    }

    public event Action? OnChanged;

    public void Update(Action<QosSettings> mutator)
    {
        lock (_lock)
        {
            var doc = _cached ?? Load();
            mutator(doc);
            _dirty = true;
            ScheduleFlushLocked();
        }
        try
        { OnChanged?.Invoke(); }
        catch (Exception ex) { Console.Error.WriteLine($"[config-store] OnChanged handler threw: {ex.Message}"); }
    }

    public void Reload()
    {
        lock (_lock)
        { _cached = null; _dirty = false; }
    }

    /// <summary>Force any pending debounced write to run now. Safe to call from any thread.</summary>
    public void FlushNow() => FlushPending();

    private void ScheduleFlushLocked()
    {
        if (_disposed)
        {
            return;
        }
        _flushTimer ??= new Timer(_ => FlushPending(), null, Timeout.Infinite, Timeout.Infinite);
        _flushTimer.Change(FlushDebounceMs, Timeout.Infinite);
    }

    private void FlushPending()
    {
        // Serialize under the lock (produces a string - fast) so no other Update
        // can mutate the doc mid-serialization. Do the disk write outside the
        // lock so sliders aren't blocked on fsync.
        string? json;
        lock (_lock)
        {
            if (!_dirty || _cached is null)
            {
                return;
            }
            json = JsonSerializer.Serialize(_cached, PersistenceJsonContext.Default.QosSettings);
            _dirty = false;
        }

        try
        { WriteAtomic(json); }
        catch (Exception ex) { Console.Error.WriteLine($"[config-store] flush failed: {ex.Message}"); }
    }

    private void Persist(QosSettings doc)
    {
        // Used only for the first-run synchronous write (Load sees no settings
        // file and writes defaults immediately). Runtime updates go through the
        // debounced flush path.
        var json = JsonSerializer.Serialize(doc, PersistenceJsonContext.Default.QosSettings);
        WriteAtomic(json);
    }

    private void WriteAtomic(string json)
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        AtomicJsonFile.Write(SettingsPath, json);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
        // Ensure the last in-memory update hits disk on shutdown.
        FlushPending();
    }

    private static string ResolveSettingsPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "Qos", "settings.json");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Machine-scope: settings belong to the LocalSystem service, not the
            // logged-in user. CommonApplicationData = %ProgramData%.
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Qos", "settings.json");
        }

        // Linux / others
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(xdg, "Qos", "settings.json");
    }

    // Station was removed in AMP-98. System.Text.Json silently drops unknown
    // properties on deserialize, but the on-disk file still holds the legacy
    // block until the next save. Detect it so we force a flush and the file
    // gets rewritten without it.
    private static bool HasLegacyStationBlock(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("station", out _);
        }
        catch
        {
            return false;
        }
    }

    // The floating-widget surface was renamed from `desktop*` to `overlay*`
    // mid-development. Settings files written under the old shape need their
    // top-level keys remapped so the new typed model picks up the user's
    // prior pins, scale, always-on-top toggle, and enabled flag. The keys
    // are unique enough that a string-level swap is safe; nested DTO fields
    // (id/type/size/monitor/col/row/config) didn't change.
    private static readonly (string Old, string New)[] LegacyOverlayKeyMap = new[]
    {
        ("\"desktopWidgetsEnabled\"",      "\"overlayWidgetsEnabled\""),
        ("\"desktopWidgetsAlwaysOnTop\"",  "\"overlayWidgetsAlwaysOnTop\""),
        ("\"desktopWidgetScale\"",         "\"overlayWidgetScale\""),
        ("\"desktopLayout\"",              "\"overlayLayout\""),
    };

    /// <summary>
    /// Runs every legacy migration on a raw JSON string. Exposed publicly so
    /// per-profile JSON files loaded by <see cref="ProfileManager"/> get the
    /// same treatment as the master settings.json. Idempotent: re-running on
    /// already-migrated JSON returns the input unchanged.
    /// </summary>
    public static string MigrateLegacyJson(string json)
    {
        return MigrateV5ToV6(MigrateV4ToV5(MigrateV3ToV4(MigrateV2ToV3(MigrateV1ToV2(MigrateLegacyOverlayKeys(json))))));
    }

    private static string MigrateLegacyOverlayKeys(string json)
    {
        var changed = false;
        var current = json;
        foreach (var (oldKey, newKey) in LegacyOverlayKeyMap)
        {
            if (current.Contains(oldKey, StringComparison.Ordinal) && !current.Contains(newKey, StringComparison.Ordinal))
            {
                current = current.Replace(oldKey, newKey, StringComparison.Ordinal);
                changed = true;
            }
        }
        return changed ? current : json;
    }

    // v1 (flat) → v2 (nested) migration. v1 stores theme/panel/overlay/monitoring
    // fields flat on Ui (Ui.PanelThemeMode, Ui.OverlayWidgetsEnabled, etc.); v2
    // moves them into matching top-level POCOs that mirror install-defaults.json
    // (Theme.ThemeMode, Panel.ThemeMode, Overlay.Enabled, Monitoring.ShowAverage,
    // etc.) and shifts Ui.FanChannelOrder into Cooling.FanChannelOrder.
    //
    // Triggered when the persisted SchemaVersion is below 2; for files that
    // predate the SchemaVersion field entirely the absent property reads as 0
    // and migration still runs. Idempotent: re-running on already-migrated data
    // is a no-op (the flat ui.panel* keys are gone, so MoveField returns early).
    private static string MigrateV1ToV2(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return json; }
        if (root is not JsonObject obj) return json;

        int sv = 0;
        if (obj.TryGetPropertyValue("schemaVersion", out var svNode) && svNode is JsonValue v && v.TryGetValue<int>(out var parsed))
            sv = parsed;
        if (sv >= 2) return json;

        if (obj["ui"] is JsonObject ui)
        {
            var theme = obj["theme"] as JsonObject ?? new JsonObject();
            MoveField(ui, "language",    theme, "language");
            MoveField(ui, "themeMode",   theme, "themeMode");
            MoveField(ui, "accentColor", theme, "accentColor");
            if (theme.Count > 0 && obj["theme"] is null) obj["theme"] = theme;

            var panel = obj["panel"] as JsonObject ?? new JsonObject();
            MoveField(ui, "panelAutoLaunch",            panel, "autoLaunch");
            MoveField(ui, "panelThemeSyncWithDesktop",  panel, "themeSyncWithDesktop");
            MoveField(ui, "panelThemeMode",             panel, "themeMode");
            MoveField(ui, "panelAccentSyncWithDesktop", panel, "accentSyncWithDesktop");
            MoveField(ui, "panelAccentColor",           panel, "accentColor");
            MoveField(ui, "panelBackgroundColor",       panel, "backgroundColor");
            MoveField(ui, "panelBackgroundColorLight",  panel, "backgroundColorLight");
            MoveField(ui, "panelBackgroundMode",        panel, "backgroundMode");
            MoveField(ui, "panelBackgroundEffect",      panel, "backgroundEffect");
            MoveField(ui, "panelBackgroundTemplate",    panel, "backgroundTemplate");
            MoveField(ui, "panelBackgroundOpacity",     panel, "backgroundOpacity");
            MoveField(ui, "panelWidgetOpacity",         panel, "widgetOpacity");
            MoveField(ui, "panelWidgetLabels",          panel, "widgetLabels");
            MoveField(ui, "dashboardLayout",            panel, "dashboardLayout");
            if (panel.Count > 0 && obj["panel"] is null) obj["panel"] = panel;

            var overlay = obj["overlay"] as JsonObject ?? new JsonObject();
            MoveField(ui, "overlayWidgetsEnabled",     overlay, "enabled");
            MoveField(ui, "overlayWidgetsAlwaysOnTop", overlay, "alwaysOnTop");
            MoveField(ui, "overlayWidgetScale",        overlay, "scale");
            MoveField(ui, "overlayWidgetOpacity",      overlay, "opacity");
            MoveField(ui, "overlayWidgetsMonitor",     overlay, "monitor");
            MoveField(ui, "overlayLayout",             overlay, "layout");
            if (overlay.Count > 0 && obj["overlay"] is null) obj["overlay"] = overlay;

            var monitoring = obj["monitoring"] as JsonObject ?? new JsonObject();
            MoveField(ui, "monitoringShowAverage",        monitoring, "showAverage");
            MoveField(ui, "monitoringDetailedCollapsed",  monitoring, "detailedCollapsed");
            MoveField(ui, "showMacStatusBarIcon",         monitoring, "showMacStatusBarIcon");
            MoveField(ui, "showWindowsTrayIcon",          monitoring, "showWindowsTrayIcon");
            if (monitoring.Count > 0 && obj["monitoring"] is null) obj["monitoring"] = monitoring;

            var cooling = obj["cooling"] as JsonObject ?? new JsonObject();
            MoveField(ui, "fanChannelOrder", cooling, "fanChannelOrder");
            if (cooling.Count > 0 && obj["cooling"] is null) obj["cooling"] = cooling;
        }

        obj["schemaVersion"] = 2;
        return obj.ToJsonString();
    }

    private static void MoveField(JsonObject src, string srcKey, JsonObject dst, string dstKey)
    {
        if (!src.TryGetPropertyValue(srcKey, out var node) || node is null)
        {
            src.Remove(srcKey); // null-valued field — drop it
            return;
        }
        // DeepClone detaches from src so the assignment to dst is legal.
        var clone = node.DeepClone();
        src.Remove(srcKey);
        dst[dstKey] = clone;
    }

    // v2 (typed-union widget config) → v3 (raw-JSON widget config). v2 stored
    // every per-widget config value as `{ "s": "x" }` / `{ "n": 42 }` /
    // `{ "b": true }` so the source-generated `PanelConfigValue` POCO could be
    // AOT-deserialized; v3 stores values raw (string / number / bool / object
    // / array) and the wire type is `Dictionary<string, JsonElement>`.
    //
    // Algorithm: every JsonObject named "config" anywhere in the document gets
    // its entries unwrapped — a single-property `{ s }`/`{ n }`/`{ b }` wrapper
    // is replaced by its raw value; anything else (a structured object the
    // user already migrated, or a marketplace widget value) passes through
    // untouched. Idempotent: re-running on v3 data is a no-op.
    private static string MigrateV2ToV3(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return json; }
        if (root is not JsonObject obj) return json;

        int sv = 0;
        if (obj.TryGetPropertyValue("schemaVersion", out var svNode) && svNode is JsonValue v && v.TryGetValue<int>(out var parsed))
            sv = parsed;
        if (sv >= 3) return json;

        UnwrapConfigsRecursive(obj);
        obj["schemaVersion"] = 3;
        return obj.ToJsonString();
    }

    private static void UnwrapConfigsRecursive(JsonNode node)
    {
        if (node is JsonObject jo)
        {
            // First, recurse into children so we hit nested config dicts.
            // Snapshot keys because we may mutate the object's entries.
            var keys = new List<string>();
            foreach (var kv in jo) keys.Add(kv.Key);
            foreach (var key in keys)
            {
                var child = jo[key];
                if (child is null) continue;
                if (key == "config" && child is JsonObject cfg)
                {
                    UnwrapConfigDict(cfg);
                }
                else
                {
                    UnwrapConfigsRecursive(child);
                }
            }
        }
        else if (node is JsonArray ja)
        {
            foreach (var item in ja)
            {
                if (item is not null) UnwrapConfigsRecursive(item);
            }
        }
    }

    private static void UnwrapConfigDict(JsonObject cfg)
    {
        // Snapshot keys before mutation.
        var keys = new List<string>();
        foreach (var kv in cfg) keys.Add(kv.Key);
        foreach (var key in keys)
        {
            if (cfg[key] is not JsonObject wrapper) continue;
            if (wrapper.Count != 1) continue;
            JsonNode? inner = null;
            if (wrapper.TryGetPropertyValue("s", out var s) && s is not null) inner = s;
            else if (wrapper.TryGetPropertyValue("n", out var n) && n is not null) inner = n;
            else if (wrapper.TryGetPropertyValue("b", out var b) && b is not null) inner = b;
            if (inner is null) continue;
            cfg[key] = inner.DeepClone();
        }
    }

    // v3 (type-scoped marketplace widget settings) → v4 (per-instance config
    // on every placement). v3 stored marketplace settings in a top-level
    // `widgets` map keyed by marketplace id, where the value was a flat
    // `Dictionary<key, jsonString>`; one config bag shared across every
    // placement of the same widget. v4 deletes that map and copies each
    // entry's key-values into every PanelWidgetDto.Config whose Type is
    // `"marketplace:" + marketplaceId`, across the desktop dashboard, every
    // PanelDevices[*].Layout, and dock entries.
    private static string MigrateV3ToV4(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return json; }
        if (root is not JsonObject obj) return json;

        int sv = 0;
        if (obj.TryGetPropertyValue("schemaVersion", out var svNode) && svNode is JsonValue v && v.TryGetValue<int>(out var parsed))
            sv = parsed;
        if (sv >= 4) return json;

        if (obj["widgets"] is JsonObject widgetsBag)
        {
            // Build a marketplaceId → parsed-config dictionary so we can fan
            // out cheaply. Each stored value is a JSON-encoded string; parse
            // back to a JsonNode tree once per marketplace id.
            var fanout = new Dictionary<string, JsonObject>(System.StringComparer.Ordinal);
            foreach (var kv in widgetsBag)
            {
                if (kv.Value is not JsonObject inner) continue;
                var parsed2 = new JsonObject();
                foreach (var kv2 in inner)
                {
                    if (kv2.Value is JsonValue stored && stored.TryGetValue<string>(out var raw))
                    {
                        try
                        {
                            var node = JsonNode.Parse(raw);
                            if (node is not null) parsed2[kv2.Key] = node;
                        }
                        catch (JsonException) { /* skip corrupt entries */ }
                    }
                }
                if (parsed2.Count > 0) fanout[kv.Key] = parsed2;
            }

            // Walk every PanelWidgetDto and apply the fan-out by type prefix.
            ApplyMarketplaceFanout(obj, fanout);
        }

        obj.Remove("widgets");
        obj["schemaVersion"] = 4;
        return obj.ToJsonString();
    }

    private static void ApplyMarketplaceFanout(JsonObject root, Dictionary<string, JsonObject> fanout)
    {
        // Desktop dashboard: panel.dashboardLayout.pages[*].widgets[*]
        if (root["panel"] is JsonObject panel && panel["dashboardLayout"] is JsonObject dash)
        {
            ApplyFanoutToLayout(dash, fanout);
        }
        // Per-device layouts: panelDevices.*.layout.{pages|dock.widgets}
        if (root["panelDevices"] is JsonObject devices)
        {
            foreach (var kv in devices)
            {
                if (kv.Value is JsonObject device && device["layout"] is JsonObject deviceLayout)
                {
                    ApplyFanoutToLayout(deviceLayout, fanout);
                }
            }
        }
    }

    private static void ApplyFanoutToLayout(JsonObject layout, Dictionary<string, JsonObject> fanout)
    {
        if (layout["pages"] is JsonArray pages)
        {
            foreach (var page in pages)
            {
                if (page is JsonObject po && po["widgets"] is JsonArray widgets)
                {
                    foreach (var w in widgets)
                    {
                        if (w is JsonObject wo) ApplyFanoutToWidget(wo, fanout);
                    }
                }
            }
        }
        if (layout["dock"] is JsonObject dock && dock["widgets"] is JsonArray dockWidgets)
        {
            foreach (var w in dockWidgets)
            {
                if (w is JsonObject wo) ApplyFanoutToWidget(wo, fanout);
            }
        }
    }

    private static void ApplyFanoutToWidget(JsonObject widget, Dictionary<string, JsonObject> fanout)
    {
        if (widget["type"] is not JsonValue tv || !tv.TryGetValue<string>(out var type)) return;
        const string prefix = "marketplace:";
        if (!type.StartsWith(prefix, System.StringComparison.Ordinal)) return;
        var marketplaceId = type.Substring(prefix.Length);
        if (!fanout.TryGetValue(marketplaceId, out var seed)) return;

        // Merge into existing config if present, otherwise create. Existing
        // per-instance keys win — a user who already started using v4-style
        // per-placement config keeps their edits.
        var cfg = widget["config"] as JsonObject ?? new JsonObject();
        foreach (var kv in seed)
        {
            if (cfg.ContainsKey(kv.Key)) continue;
            cfg[kv.Key] = kv.Value!.DeepClone();
        }
        if (cfg.Count > 0) widget["config"] = cfg;
    }

    // v4 → v5: renames the "performance" cooling preset to "turbo". Touches
    // cooling.activePreset, every cooling.curves[*].preset string, and the
    // canonical curve id "preset-performance" → "preset-turbo".
    private static string MigrateV4ToV5(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return json; }
        if (root is not JsonObject obj) return json;

        int sv = 0;
        if (obj.TryGetPropertyValue("schemaVersion", out var svNode) && svNode is JsonValue v && v.TryGetValue<int>(out var parsed))
            sv = parsed;
        if (sv >= 5) return json;

        if (obj["cooling"] is JsonObject cooling)
        {
            if (cooling["activePreset"] is JsonValue ap && ap.TryGetValue<string>(out var apStr) && apStr == "performance")
            {
                cooling["activePreset"] = "turbo";
            }
            if (cooling["curves"] is JsonArray curves)
            {
                foreach (var curve in curves)
                {
                    if (curve is not JsonObject co) continue;
                    if (co["preset"] is JsonValue pv && pv.TryGetValue<string>(out var pStr) && pStr == "performance")
                    {
                        co["preset"] = "turbo";
                    }
                    if (co["id"] is JsonValue iv && iv.TryGetValue<string>(out var iStr) && iStr == "preset-performance")
                    {
                        co["id"] = "preset-turbo";
                    }
                }
            }
        }

        obj["schemaVersion"] = 5;
        return obj.ToJsonString();
    }

    // v5 → v6: splits the key-reactive overlay out of `keeb.firmwareLighting` into
    // `keeb.passiveLighting`. v5 stored `keyReactive`, `keyReactiveMask`,
    // `keyReactiveMode`, `keyReactiveColor` flat alongside the firmware effect
    // fields; v6 moves them into a sibling object because the firmware applies
    // them through a separate code path. Idempotent: re-running on v6 data is a
    // no-op.
    private static string MigrateV5ToV6(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return json; }
        if (root is not JsonObject obj) return json;

        int sv = 0;
        if (obj.TryGetPropertyValue("schemaVersion", out var svNode) && svNode is JsonValue v && v.TryGetValue<int>(out var parsed))
            sv = parsed;
        if (sv >= 6) return json;

        if (obj["keeb"] is JsonObject keeb)
        {
            // The firmware-lighting block was the home of the key-reactive
            // overlay fields. Move them into a sibling passiveLighting block.
            // If both blocks already exist (idempotency edge case) preserve
            // whatever's in passiveLighting.
            var passive = keeb["passiveLighting"] as JsonObject ?? new JsonObject();
            if (keeb["firmwareLighting"] is JsonObject fw)
            {
                MoveField(fw, "keyReactive",      passive, "keyReactive");
                MoveField(fw, "keyReactiveMask",  passive, "keyReactiveMask");
                MoveField(fw, "keyReactiveMode",  passive, "keyReactiveMode");
                MoveField(fw, "keyReactiveColor", passive, "keyReactiveColor");
            }
            if (passive.Count > 0 && keeb["passiveLighting"] is null)
            {
                keeb["passiveLighting"] = passive;
            }
        }

        obj["schemaVersion"] = 6;
        return obj.ToJsonString();
    }
}
