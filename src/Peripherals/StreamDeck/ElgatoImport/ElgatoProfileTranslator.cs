using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nexus.Service.Deck;
using Nexus.Service.Models.Peripherals.StreamDeck;

namespace Nexus.Service.Peripherals.StreamDeck.ElgatoImport;

/// <summary>
/// Translates a read ElgatoProfile into a Nexus DeckConfig plus a best-effort
/// import report. Mapping rules follow elgato-ground-truth.md exactly; every
/// key that cannot be represented becomes a placeholder slot (label + icon
/// kept where available, no action) and an ElgatoUnmappedEntry.
/// </summary>
public sealed class ElgatoProfileTranslator
{
    /// <summary>Matches nexus-web's MAX_DECK_PAGES (deckLayout.ts).</summary>
    private const int MaxPages = 10;

    private readonly DeckImageStore _images;

    public ElgatoProfileTranslator(DeckImageStore images)
    {
        _images = images;
    }

    public (DeckConfig Config, ElgatoImportReport Report) Translate(ElgatoProfile profile)
    {
        var (cols, rows, _) = ElgatoModelCatalog.Resolve(profile.Model, profile.MaxColSeen, profile.MaxRowSeen);
        var report = new ElgatoImportReport();
        var config = new DeckConfig();

        var originalIndex = 0;
        foreach (var topId in profile.TopPageIds)
        {
            originalIndex++;
            if (!profile.PagesById.TryGetValue(topId, out var page))
            {
                continue;
            }

            if (config.Pages.Count >= MaxPages)
            {
                report.Unmapped.Add(new ElgatoUnmappedEntry { Page = originalIndex, Reason = "pageLimit" });
                continue;
            }

            var beforeKeys = report.TotalKeys;
            var beforeUnmappedCount = report.Unmapped.Count;
            var pendingPageNumber = config.Pages.Count + 1;
            var ancestorStack = new HashSet<string>(StringComparer.Ordinal) { topId };
            var slots = FlattenPage(page, cols, rows, profile, pendingPageNumber, ancestorStack, report);

            if (report.TotalKeys == beforeKeys)
            {
                // Zero non-skipped actions on this page - drop it and any
                // tentative notes (e.g. an encoder-only page) tagged above.
                report.Unmapped.RemoveRange(beforeUnmappedCount, report.Unmapped.Count - beforeUnmappedCount);
                continue;
            }

            config.Pages.Add(new DeckPage { Slots = slots });
        }

        if (config.Pages.Count == 0)
        {
            config.Pages.Add(new DeckPage());
        }

        return (config, report);
    }

    private List<DeckSlot> FlattenPage(
        ElgatoPageData page, int cols, int rows, ElgatoProfile profile,
        int reportPageNumber, HashSet<string> ancestorStack, ElgatoImportReport report)
    {
        if (page.HasEncoderController)
        {
            report.Unmapped.Add(new ElgatoUnmappedEntry { Page = reportPageNumber, Reason = "encoder" });
        }

        var slots = new List<DeckSlot>(cols * rows);
        for (var i = 0; i < cols * rows; i++)
        {
            slots.Add(new DeckSlot());
        }

        foreach (var kv in page.Actions)
        {
            var (col, row) = kv.Key;
            if (col >= cols || row >= rows)
            {
                continue; // outside the resolved grid; tolerated
            }
            var action = kv.Value;
            if (ElgatoActionTypes.IsSkippable(action.Uuid))
            {
                continue;
            }

            report.TotalKeys++;
            var built = BuildSlot(action, profile, cols, rows, reportPageNumber, ancestorStack, report);
            var slot = built.Slot;
            if (!built.Mapped && slot.Action is null && slot.Folder is null
                && string.IsNullOrEmpty(slot.Label) && slot.Icon is null
                && !string.IsNullOrEmpty(action.Name))
            {
                // A placeholder for an untranslatable key that carried neither
                // title nor image would render as an empty slot; surface the
                // Elgato action name so the user can find and rebind it.
                slot.Label = action.Name;
            }
            slots[row * cols + col] = slot;
            if (built.Mapped)
            {
                report.MappedKeys++;
            }
            if (built.Reason is not null)
            {
                report.Unmapped.Add(new ElgatoUnmappedEntry
                {
                    Page = reportPageNumber,
                    Position = $"{col},{row}",
                    Name = action.Name,
                    Reason = built.Reason,
                    Detail = built.Detail,
                });
            }
        }

        return slots;
    }

    private (DeckSlot Slot, bool Mapped, string? Reason, string? Detail) BuildSlot(
        ElgatoActionData action, ElgatoProfile profile, int cols, int rows,
        int reportPageNumber, HashSet<string> ancestorStack, ElgatoImportReport report)
    {
        var slot = new DeckSlot();
        var state = action.States.Count > 0
            ? action.States[Math.Clamp(action.ActiveState, 0, action.States.Count - 1)]
            : null;
        ApplyLabelAndIcon(slot, state);

        if (action.Uuid == ElgatoActionTypes.OpenChild)
        {
            return BuildOpenChildSlot(action, slot, profile, cols, rows, reportPageNumber, ancestorStack, report);
        }
        if (action.Uuid is ElgatoActionTypes.Routine2 or ElgatoActionTypes.MultiActionLegacy)
        {
            return BuildMultiActionSlot(action, slot);
        }

        var (leaf, failureReason, note) = TryBuildLeafAction(action);
        if (leaf is not null)
        {
            slot.Action = leaf;
            return (slot, true, note, null);
        }
        if (failureReason is not null)
        {
            return (slot, false, failureReason, null);
        }

        // Neither a leaf system action nor folder/multi-action: a
        // third-party plugin, or a first-party builtin we don't map
        // (system.hotkeyswitch with no decodable settings, keybrightness,
        // sleep, close, pagination, dial, etc).
        var reason = action.Uuid.StartsWith(ElgatoActionTypes.BuiltinPrefix, StringComparison.Ordinal) ? "unsupported" : "plugin";
        return (slot, false, reason, action.Uuid);
    }

    private void ApplyLabelAndIcon(DeckSlot slot, ElgatoActionStateData? state)
    {
        if (state is null)
        {
            return;
        }

        if (state.ShowTitle && !string.IsNullOrEmpty(state.Title))
        {
            var parts = state.Title.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var collapsed = string.Join(' ', parts);
            if (collapsed.Length > 0)
            {
                slot.Label = collapsed;
                slot.Title = BuildTitleStyle(state);
            }
        }

        if (!string.IsNullOrEmpty(state.ImagePath))
        {
            var id = IngestImage(state.ImagePath);
            if (id is not null)
            {
                slot.Icon = new DeckIcon { Kind = "image", Value = id };
            }
        }
    }

    private static DeckTitleStyle BuildTitleStyle(ElgatoActionStateData state)
    {
        var style = new DeckTitleStyle { Show = true };
        if (!string.IsNullOrEmpty(state.TitleAlignment))
        {
            style.Align = state.TitleAlignment.ToLowerInvariant();
        }
        if (!string.IsNullOrEmpty(state.TitleColor))
        {
            style.Color = state.TitleColor;
        }
        return style;
    }

    private string? IngestImage(string absolutePath)
    {
        try
        {
            if (!File.Exists(absolutePath))
            {
                return null;
            }
            var bytes = ElgatoReadOnlyIo.ReadAllBytes(absolutePath);
            return _images.Store(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private (DeckSlot, bool, string?, string?) BuildOpenChildSlot(
        ElgatoActionData action, DeckSlot slot, ElgatoProfile profile, int cols, int rows,
        int reportPageNumber, HashSet<string> ancestorStack, ElgatoImportReport report)
    {
        var childUuid = ElgatoJson.GetString(action.Settings, "ProfileUUID");
        if (string.IsNullOrEmpty(childUuid))
        {
            return (slot, false, "unsupported", "openchild missing target");
        }
        childUuid = childUuid.ToLowerInvariant();

        if (!profile.PagesById.TryGetValue(childUuid, out var childPage))
        {
            return (slot, false, "unsupported", "openchild target missing");
        }
        if (!ancestorStack.Add(childUuid))
        {
            return (slot, false, "unsupported", "openchild cycle");
        }
        try
        {
            var flat = FlattenPage(childPage, cols, rows, profile, reportPageNumber, ancestorStack, report);
            // Nexus reserves folder key 0 for its own Back key, so one Elgato
            // cell must drop out. Elgato places backtoparent at (0,0), but find
            // its real position rather than assume index 0, so a folder with a
            // genuine action at (0,0) keeps it.
            var backIndex = 0;
            foreach (var kv in childPage.Actions)
            {
                if (kv.Value.Uuid == ElgatoActionTypes.BackToParent && kv.Key.Col < cols && kv.Key.Row < rows)
                {
                    backIndex = kv.Key.Row * cols + kv.Key.Col;
                    break;
                }
            }
            var folderSlots = new List<DeckSlot>(flat);
            if (folderSlots.Count > 0 && backIndex < folderSlots.Count)
            {
                folderSlots.RemoveAt(backIndex);
            }
            slot.Folder = new DeckFolder { Slots = folderSlots };
            return (slot, true, null, null);
        }
        finally
        {
            ancestorStack.Remove(childUuid);
        }
    }

    private static (DeckSlot, bool, string?, string?) BuildMultiActionSlot(ElgatoActionData action, DeckSlot slot)
    {
        if (action.ActionsGroup is not { ValueKind: JsonValueKind.Array } groups)
        {
            return (slot, false, "multiStep", null);
        }

        var steps = new List<DeckSequenceStep>();
        var dropped = 0;
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object ||
                !group.TryGetProperty("Actions", out var innerActionsEl) ||
                innerActionsEl.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var innerEl in innerActionsEl.EnumerateArray())
            {
                var inner = ParseInlineAction(innerEl);
                var (leaf, _, _) = inner is null ? (null, null, null) : TryBuildLeafAction(inner);
                if (leaf is null)
                {
                    dropped++;
                    continue;
                }
                steps.Add(new DeckSequenceStep { Action = leaf });
            }
        }

        if (steps.Count == 0)
        {
            return (slot, false, "multiStep", dropped > 0 ? $"{dropped} step(s) unsupported" : null);
        }

        slot.Action = new DeckAction { Type = "sequence", Steps = steps };
        return (slot, true, dropped > 0 ? "multiStep" : null, dropped > 0 ? $"{dropped} step(s) dropped" : null);
    }

    private static ElgatoActionData? ParseInlineAction(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var uuid = ElgatoJson.GetString(el, "UUID");
        if (string.IsNullOrEmpty(uuid))
        {
            return null;
        }
        return new ElgatoActionData
        {
            Uuid = uuid,
            Name = ElgatoJson.GetString(el, "Name") ?? "",
            Settings = el.TryGetProperty("Settings", out var settingsEl) ? settingsEl : default,
        };
    }

    /// <summary>
    /// Leaf action types representable as a bare DeckAction (no icon/label/folder),
    /// so the same dispatch backs both a top-level slot and a multi-action step.
    /// Returns (null Action, null FailureReason) when the UUID is not a leaf
    /// type at all, leaving the caller to fall to its own folder/multi-action/
    /// plugin handling.
    /// </summary>
    private static (DeckAction? Action, string? FailureReason, string? Note) TryBuildLeafAction(ElgatoActionData action)
    {
        switch (action.Uuid)
        {
            case ElgatoActionTypes.Hotkey:
                return BuildHotkeyAction(action);
            case ElgatoActionTypes.Open:
            {
                var built = BuildOpenAction(action);
                return built is not null ? (built, null, null) : (null, "open", null);
            }
            case ElgatoActionTypes.OpenApp:
            {
                // Unverified settings shape; only translate when it carries
                // the same "path" key system.open uses, else it is an
                // unmapped first-party builtin like any other.
                var built = BuildOpenAction(action);
                return built is not null ? (built, null, null) : (null, "unsupported", null);
            }
            case ElgatoActionTypes.Website:
            {
                var url = ElgatoJson.GetString(action.Settings, "path");
                return string.IsNullOrEmpty(url)
                    ? (null, "website", null)
                    : (new DeckAction { Type = "openUrl", Url = url }, null, null);
            }
            case ElgatoActionTypes.Text:
                return BuildTextAction(action);
            case ElgatoActionTypes.Multimedia:
            {
                var built = BuildMultimediaAction(action);
                return built is not null ? (built, null, null) : (null, "media", null);
            }
            case ElgatoActionTypes.PageNext:
                return (new DeckAction { Type = "page", Op = "next" }, null, null);
            case ElgatoActionTypes.PagePrevious:
                return (new DeckAction { Type = "page", Op = "prev" }, null, null);
            case ElgatoActionTypes.LhmReading:
                return BuildLhmReadingAction(action);
            case ElgatoActionTypes.Weather:
                return BuildWeatherAction(action);
            case ElgatoActionTypes.PlayAudio:
                return BuildPlayAudioAction(action);
            default:
                return (null, null, null);
        }
    }

    /// <summary>Always mapped, even with no configured file: an empty path still gives the user a bound key they can point at a sound, noted so they know to finish configuring it.</summary>
    private static (DeckAction?, string?, string?) BuildPlayAudioAction(ElgatoActionData action)
    {
        var path = ElgatoJson.GetString(action.Settings, "path") ?? "";
        var deckAction = new DeckAction
        {
            Type = "playAudio",
            Path = path,
            Volume = ElgatoJson.GetInt(action.Settings, "volume"),
        };
        return (deckAction, null, path.Length == 0 ? "audioPath" : null);
    }

    /// <summary>Reads Kanali's nested location object first (lat/lon/city/country), falling back to the top-level city when location is absent. Always mapped, units default to auto so the key follows the host's own C/F preference.</summary>
    private static (DeckAction?, string?, string?) BuildWeatherAction(ElgatoActionData action)
    {
        double? lat = null;
        double? lon = null;
        string? city = null;
        string? country = null;
        if (action.Settings.ValueKind == JsonValueKind.Object &&
            action.Settings.TryGetProperty("location", out var locationEl) &&
            locationEl.ValueKind == JsonValueKind.Object)
        {
            lat = ElgatoJson.GetDouble(locationEl, "lat");
            lon = ElgatoJson.GetDouble(locationEl, "lon");
            city = ElgatoJson.GetString(locationEl, "city");
            country = ElgatoJson.GetString(locationEl, "country");
        }
        if (string.IsNullOrEmpty(city))
        {
            city = ElgatoJson.GetString(action.Settings, "city");
        }

        var deckAction = new DeckAction
        {
            Type = "weather",
            Lat = lat,
            Lon = lon,
            City = city,
            Cc = country,
            Units = "auto",
        };
        return (deckAction, null, null);
    }

    /// <summary>
    /// The com.moeilijk.lhm plugin pins a reading by (sensorUid, readingId),
    /// where sensorUid is the hardware-level LHM identifier ("/amdcpu/0") and
    /// readingId is an opaque value minted by its own lhm-bridge.exe. Neither
    /// is a HardwareSensor.Id, and the sibling readingLabel ("Core #1") is
    /// ambiguous across sensor types, so the sensor cannot be derived here;
    /// Sensor stays empty and SensorSnapshotResolver.ResolveOrDefault lands
    /// the category default until the user picks one. The plugin's min/max are
    /// dropped with it: they bound a reading that was never imported, and
    /// would seed the inspector's fixed-range fields the moment a user sets
    /// that scale. Style is the deck's own default rather than the plugin's
    /// number readout, since the fallback reading suits a trend. Always mapped
    /// (never a failure reason) - the category guess is a usable start.
    /// </summary>
    private static (DeckAction?, string?, string?) BuildLhmReadingAction(ElgatoActionData action)
    {
        var sensorUid = ElgatoJson.GetString(action.Settings, "sensorUid") ?? "";
        var deckAction = new DeckAction
        {
            Type = "monitoring",
            Category = ResolveLhmCategory(sensorUid),
            Sensor = "",
            Style = "line",
        };
        return (deckAction, null, "monitoringSensor");
    }

    /// <summary>Maps LHM's hardware-tree sensorUid prefix to a deck monitoring category; an unrecognized or absent prefix falls to the quick summary set.</summary>
    private static string ResolveLhmCategory(string sensorUid)
    {
        if (sensorUid.StartsWith("/amdcpu", StringComparison.OrdinalIgnoreCase) ||
            sensorUid.StartsWith("/intelcpu", StringComparison.OrdinalIgnoreCase))
        {
            return "cpu";
        }
        if (sensorUid.StartsWith("/gpu", StringComparison.OrdinalIgnoreCase))
        {
            return "gpu";
        }
        if (sensorUid.StartsWith("/ram", StringComparison.OrdinalIgnoreCase))
        {
            return "memory";
        }
        if (sensorUid.StartsWith("/nvme", StringComparison.OrdinalIgnoreCase) ||
            sensorUid.StartsWith("/hdd", StringComparison.OrdinalIgnoreCase) ||
            sensorUid.StartsWith("/ssd", StringComparison.OrdinalIgnoreCase) ||
            sensorUid.StartsWith("/storage", StringComparison.OrdinalIgnoreCase))
        {
            return "storage";
        }
        if (sensorUid.StartsWith("/lpc", StringComparison.OrdinalIgnoreCase) ||
            sensorUid.StartsWith("/mobo", StringComparison.OrdinalIgnoreCase) ||
            sensorUid.StartsWith("/superio", StringComparison.OrdinalIgnoreCase))
        {
            return "motherboard";
        }
        return "quick";
    }

    private static (DeckAction?, string?, string?) BuildHotkeyAction(ElgatoActionData action)
    {
        if (action.Settings.ValueKind != JsonValueKind.Object ||
            !action.Settings.TryGetProperty("Hotkeys", out var hotkeysEl) ||
            hotkeysEl.ValueKind != JsonValueKind.Array ||
            hotkeysEl.GetArrayLength() == 0)
        {
            return (null, "hotkey", null);
        }

        var keys = DecodeHotkey(hotkeysEl[0]);
        if (keys is null)
        {
            return (null, "hotkey", null);
        }

        var extraPopulated = false;
        for (var i = 1; i < hotkeysEl.GetArrayLength(); i++)
        {
            if (IsPopulatedHotkeySlot(hotkeysEl[i]))
            {
                extraPopulated = true;
                break;
            }
        }

        return (new DeckAction { Type = "hotkey", Keys = keys }, null, extraPopulated ? "hotkeyExtraSlots" : null);
    }

    /// <summary>Decodes one Hotkeys[] slot via the platform-neutral QTKeyCode into DeckActionExecutor.ParseHotkey's "mod+mod+key" grammar. Null when the slot is the empty sentinel or the key has no representable token.</summary>
    private static string? DecodeHotkey(JsonElement slotEl)
    {
        var qtCode = ElgatoJson.GetInt(slotEl, "QTKeyCode");
        if (qtCode is null || qtCode == 0x01FFFFFF || qtCode == -1)
        {
            return null;
        }
        var key = QtKeyToToken(qtCode.Value);
        if (key is null)
        {
            return null;
        }

        var modifiers = ElgatoJson.GetInt(slotEl, "KeyModifiers") ?? 0;
        var parts = new List<string>(5);
        if ((modifiers & 0x2) != 0)
        {
            parts.Add("ctrl");
        }
        if ((modifiers & 0x1) != 0)
        {
            parts.Add("shift");
        }
        if ((modifiers & 0x4) != 0)
        {
            parts.Add("alt");
        }
        if ((modifiers & 0x8) != 0)
        {
            parts.Add("meta");
        }
        parts.Add(key);
        return string.Join('+', parts);
    }

    private static bool IsPopulatedHotkeySlot(JsonElement slotEl)
    {
        var qt = ElgatoJson.GetInt(slotEl, "QTKeyCode");
        return qt is not null && qt != 0x01FFFFFF;
    }

    /// <summary>Qt::Key value to a ParseHotkey-compatible token, or null when Nexus has no representable key (e.g. punctuation like '.').</summary>
    private static string? QtKeyToToken(int code)
    {
        if (code is >= 0x30 and <= 0x39)
        {
            return ((char)code).ToString();
        }
        if (code is >= 0x41 and <= 0x5A)
        {
            return char.ToLowerInvariant((char)code).ToString();
        }
        if (code == 0x20)
        {
            return "space";
        }
        if (code == 0x2E)
        {
            return ".";
        }
        if (code is >= 0x01000030 and <= 0x01000047)
        {
            return "f" + (code - 0x01000030 + 1);
        }
        return code switch
        {
            0x01000000 => "escape",
            0x01000001 => "tab",
            0x01000003 => "backspace",
            0x01000004 => "return",
            0x01000005 => "enter",
            0x01000006 => "insert",
            0x01000007 => "delete",
            0x01000009 => "printscreen",
            0x01000010 => "home",
            0x01000011 => "end",
            0x01000012 => "left",
            0x01000013 => "up",
            0x01000014 => "right",
            0x01000015 => "down",
            0x01000016 => "pageup",
            0x01000017 => "pagedown",
            _ => null,
        };
    }

    private static DeckAction? BuildOpenAction(ElgatoActionData action)
    {
        var raw = ElgatoJson.GetString(action.Settings, "path");
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        var path = StripOuterQuotes(raw);
        if (path.Length == 0)
        {
            return null;
        }
        // A .app bundle is a directory on disk, but the OS "open" launches it
        // like a file - never route it to openFolder.
        var isDir = !path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) &&
            (path.EndsWith('/') || path.EndsWith('\\') || (Path.IsPathRooted(path) && Directory.Exists(path)));
        return new DeckAction { Type = isDir ? "openFolder" : "openFile", Path = path };
    }

    private static string StripOuterQuotes(string s) =>
        s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    private static (DeckAction?, string?, string?) BuildTextAction(ElgatoActionData action)
    {
        var text = ElgatoJson.GetString(action.Settings, "pastedText");
        if (string.IsNullOrEmpty(text))
        {
            return (null, "text", null);
        }
        // DeckActionExecutor's text action always pastes immediately; there
        // is no press-enter concept to carry isSendingEnter into.
        var sendEnter = ElgatoJson.GetBool(action.Settings, "isSendingEnter", false);
        return (new DeckAction { Type = "text", Text = text }, null, sendEnter ? "textEnterIgnored" : null);
    }

    private static DeckAction? BuildMultimediaAction(ElgatoActionData action)
    {
        var idx = ElgatoJson.GetInt(action.Settings, "actionIdx");
        if (idx is null)
        {
            return null;
        }
        var op = idx switch
        {
            0 => "mediaPrev",
            1 => "mediaPlayPause",
            2 => "mediaNext",
            4 => "muteToggle",
            5 => "volumeUp",
            6 => "volumeDown",
            7 => "brightnessUp",
            8 => "brightnessDown",
            _ => null,
        };
        return op is null ? null : new DeckAction { Type = "system", SystemAction = new DeckSystemAction { Op = op } };
    }
}
