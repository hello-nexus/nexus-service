using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Nexus.Service.Peripherals.StreamDeck.ElgatoImport;

/// <summary>Action type UUIDs the translator dispatches on. See elgato-ground-truth for the verified set.</summary>
internal static class ElgatoActionTypes
{
    public const string Hotkey = "com.elgato.streamdeck.system.hotkey";
    public const string Open = "com.elgato.streamdeck.system.open";
    public const string OpenApp = "com.elgato.streamdeck.system.openapp";
    public const string Website = "com.elgato.streamdeck.system.website";
    public const string Text = "com.elgato.streamdeck.system.text";
    public const string Multimedia = "com.elgato.streamdeck.system.multimedia";
    public const string PageNext = "com.elgato.streamdeck.page.next";
    public const string PagePrevious = "com.elgato.streamdeck.page.previous";
    public const string OpenChild = "com.elgato.streamdeck.profile.openchild";
    public const string BackToParent = "com.elgato.streamdeck.profile.backtoparent";
    public const string Routine2 = "com.elgato.streamdeck.multiactions.routine2";
    public const string MultiActionLegacy = "com.elgato.streamdeck.multiactions.multiaction";
    public const string LhmReading = "com.moeilijk.lhm.reading";
    public const string Weather = "com.elgato.weather.weather";
    public const string PlayAudio = "com.elgato.streamdeck.soundboard.playaudio";
    public const string TutorialPrefix = "com.elgato.tutorial.";
    public const string BuiltinPrefix = "com.elgato.streamdeck.";

    /// <summary>True for a key that never counts toward totalKeys/mappedKeys and never becomes a slot.</summary>
    public static bool IsSkippable(string uuid) =>
        uuid.StartsWith(TutorialPrefix, StringComparison.Ordinal) || uuid == BackToParent;
}

/// <summary>Small JSON accessors shared by the reader and the translator; tolerate a missing/wrong-kind property instead of throwing.</summary>
internal static class ElgatoJson
{
    public static string? GetString(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    public static int? GetInt(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var el) &&
        el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)
            ? v
            : null;

    public static double? GetDouble(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var el) &&
        el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var v)
            ? v
            : null;

    public static bool GetBool(JsonElement obj, string prop, bool defaultValue) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? el.ValueKind == JsonValueKind.True
            : defaultValue;
}

/// <summary>One resolved Elgato profile bundle: identity plus every reachable page (top-level and folders, resolved through openchild).</summary>
public sealed class ElgatoProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public List<string> TopPageIds { get; } = new();
    public Dictionary<string, ElgatoPageData> PagesById { get; } = new(StringComparer.Ordinal);
    public int MaxColSeen { get; set; }
    public int MaxRowSeen { get; set; }
}

/// <summary>One page or folder manifest's Keypad controller, keyed by (col, row).</summary>
public sealed class ElgatoPageData
{
    public Dictionary<(int Col, int Row), ElgatoActionData> Actions { get; } = new();
    public bool HasEncoderController { get; set; }
}

public sealed class ElgatoActionData
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The action's own Settings object; ValueKind is Undefined when the manifest had none.</summary>
    public JsonElement Settings { get; set; }
    /// <summary>The multiactions.routine2 sibling "Actions" array (a group list), cloned; null for any other action type.</summary>
    public JsonElement? ActionsGroup { get; set; }
    public int ActiveState { get; set; }
    public List<ElgatoActionStateData> States { get; } = new();
}

public sealed class ElgatoActionStateData
{
    /// <summary>Absolute path under the owning page's dir, or null when the state has no image or it resolved outside that dir.</summary>
    public string? ImagePath { get; set; }
    public string? Title { get; set; }
    public bool ShowTitle { get; set; } = true;
    public string? TitleAlignment { get; set; }
    public string? TitleColor { get; set; }
}

/// <summary>
/// Reads Elgato ProfilesV3 bundles. Every file access under the store is
/// read-only (see ElgatoReadOnlyIo); a corrupt or missing manifest is
/// tolerated by skipping the bundle/page rather than throwing, since a
/// stray malformed entry must not blank the whole profile list.
/// </summary>
public static class ElgatoProfileReader
{
    public static List<ElgatoProfile> ReadProfiles(string profilesV3Root)
    {
        var result = new List<ElgatoProfile>();
        if (string.IsNullOrEmpty(profilesV3Root) || !Directory.Exists(profilesV3Root))
        {
            return result;
        }
        foreach (var bundleDir in Directory.EnumerateDirectories(profilesV3Root, "*.sdProfile"))
        {
            var profile = ReadBundle(bundleDir);
            if (profile is not null)
            {
                result.Add(profile);
            }
        }
        return result;
    }

    public static ElgatoProfile? ReadBundle(string bundleDir)
    {
        var manifestPath = Path.Combine(bundleDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(ElgatoReadOnlyIo.ReadAllText(manifestPath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var name = Path.GetFileName(bundleDir);
            if (name.EndsWith(".sdProfile", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^".sdProfile".Length];
            }

            var profile = new ElgatoProfile
            {
                Id = name.ToLowerInvariant(),
                Name = ElgatoJson.GetString(root, "Name") ?? "",
            };

            if (root.TryGetProperty("Device", out var deviceEl) && deviceEl.ValueKind == JsonValueKind.Object)
            {
                profile.Model = ElgatoJson.GetString(deviceEl, "Model") ?? "";
            }

            if (root.TryGetProperty("Pages", out var pagesEl) && pagesEl.ValueKind == JsonValueKind.Object &&
                pagesEl.TryGetProperty("Pages", out var pageListEl) && pageListEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var idEl in pageListEl.EnumerateArray())
                {
                    if (idEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }
                    var pageId = idEl.GetString();
                    if (!string.IsNullOrEmpty(pageId))
                    {
                        profile.TopPageIds.Add(pageId.ToLowerInvariant());
                    }
                }
            }

            var dirByLowerUuid = BuildProfileDirMap(Path.Combine(bundleDir, "Profiles"));
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var topId in profile.TopPageIds)
            {
                ResolvePageRecursive(topId, dirByLowerUuid, visited, profile);
            }
            return profile;
        }
    }

    /// <summary>
    /// Recursive key count across every reachable page (top-level and
    /// folders), for the profile list's summary keyCount - no translation,
    /// no image ingestion. Uses a whole-profile visited set (not path-scoped)
    /// since a total count does not need to revisit a shared folder twice.
    /// </summary>
    public static int CountKeys(ElgatoProfile profile)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var topId in profile.TopPageIds)
        {
            total += CountPageKeys(profile, topId, visited);
        }
        return total;
    }

    private static int CountPageKeys(ElgatoProfile profile, string pageId, HashSet<string> visited)
    {
        if (!visited.Add(pageId) || !profile.PagesById.TryGetValue(pageId, out var page))
        {
            return 0;
        }
        var count = 0;
        foreach (var action in page.Actions.Values)
        {
            if (ElgatoActionTypes.IsSkippable(action.Uuid))
            {
                continue;
            }
            count++;
            if (action.Uuid == ElgatoActionTypes.OpenChild)
            {
                var childId = ElgatoJson.GetString(action.Settings, "ProfileUUID");
                if (!string.IsNullOrEmpty(childId))
                {
                    count += CountPageKeys(profile, childId.ToLowerInvariant(), visited);
                }
            }
        }
        return count;
    }

    private static Dictionary<string, string> BuildProfileDirMap(string profilesDir)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(profilesDir))
        {
            return map;
        }
        foreach (var dir in Directory.EnumerateDirectories(profilesDir))
        {
            map[Path.GetFileName(dir).ToLowerInvariant()] = dir;
        }
        return map;
    }

    private static void ResolvePageRecursive(string uuidLower, Dictionary<string, string> dirMap, HashSet<string> visited, ElgatoProfile profile)
    {
        if (!visited.Add(uuidLower) || !dirMap.TryGetValue(uuidLower, out var pageDir))
        {
            return;
        }
        var page = ReadPageManifest(pageDir);
        if (page is null)
        {
            return;
        }
        profile.PagesById[uuidLower] = page;
        foreach (var kv in page.Actions)
        {
            profile.MaxColSeen = Math.Max(profile.MaxColSeen, kv.Key.Col);
            profile.MaxRowSeen = Math.Max(profile.MaxRowSeen, kv.Key.Row);
            var action = kv.Value;
            if (action.Uuid != ElgatoActionTypes.OpenChild)
            {
                continue;
            }
            var childId = ElgatoJson.GetString(action.Settings, "ProfileUUID");
            if (!string.IsNullOrEmpty(childId))
            {
                ResolvePageRecursive(childId.ToLowerInvariant(), dirMap, visited, profile);
            }
        }
    }

    private static ElgatoPageData? ReadPageManifest(string pageDir)
    {
        var manifestPath = Path.Combine(pageDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(ElgatoReadOnlyIo.ReadAllText(manifestPath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var page = new ElgatoPageData();
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Controllers", out var controllersEl) ||
                controllersEl.ValueKind != JsonValueKind.Array)
            {
                return page;
            }

            foreach (var controller in controllersEl.EnumerateArray())
            {
                if (controller.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var type = ElgatoJson.GetString(controller, "Type");
                if (type == "Encoder")
                {
                    page.HasEncoderController = true;
                    continue;
                }
                if (type != "Keypad" ||
                    !controller.TryGetProperty("Actions", out var actionsEl) ||
                    actionsEl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                foreach (var prop in actionsEl.EnumerateObject())
                {
                    if (!TryParseColRow(prop.Name, out var col, out var row))
                    {
                        continue;
                    }
                    var action = ParsePageAction(prop.Value, pageDir);
                    if (action is not null)
                    {
                        page.Actions[(col, row)] = action;
                    }
                }
            }
            return page;
        }
    }

    private static ElgatoActionData? ParsePageAction(JsonElement actionEl, string pageDir)
    {
        if (actionEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var action = new ElgatoActionData
        {
            Uuid = ElgatoJson.GetString(actionEl, "UUID") ?? "",
            Name = ElgatoJson.GetString(actionEl, "Name") ?? "",
            Settings = actionEl.TryGetProperty("Settings", out var settingsEl) ? settingsEl.Clone() : default,
            ActionsGroup = actionEl.TryGetProperty("Actions", out var actionsArrEl) && actionsArrEl.ValueKind == JsonValueKind.Array
                ? actionsArrEl.Clone()
                : null,
            ActiveState = ElgatoJson.GetInt(actionEl, "State") ?? 0,
        };

        if (actionEl.TryGetProperty("States", out var statesEl) && statesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var stateEl in statesEl.EnumerateArray())
            {
                action.States.Add(ParseActionState(stateEl, pageDir));
            }
        }

        return action;
    }

    private static ElgatoActionStateData ParseActionState(JsonElement stateEl, string pageDir)
    {
        string? imagePath = null;
        var rel = ElgatoJson.GetString(stateEl, "Image");
        if (!string.IsNullOrEmpty(rel))
        {
            var normalizedPageDir = Path.GetFullPath(pageDir) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(pageDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            // A relative path in a corrupt or hand-edited manifest must not resolve outside the page dir.
            if (candidate.StartsWith(normalizedPageDir, StringComparison.Ordinal))
            {
                imagePath = candidate;
            }
        }

        return new ElgatoActionStateData
        {
            ImagePath = imagePath,
            Title = ElgatoJson.GetString(stateEl, "Title"),
            ShowTitle = ElgatoJson.GetBool(stateEl, "ShowTitle", true),
            TitleAlignment = ElgatoJson.GetString(stateEl, "TitleAlignment"),
            TitleColor = ElgatoJson.GetString(stateEl, "TitleColor"),
        };
    }

    private static bool TryParseColRow(string key, out int col, out int row)
    {
        col = 0;
        row = 0;
        var parts = key.Split(',');
        return parts.Length == 2 &&
            int.TryParse(parts[0], out col) && int.TryParse(parts[1], out row) &&
            col >= 0 && row >= 0;
    }
}
