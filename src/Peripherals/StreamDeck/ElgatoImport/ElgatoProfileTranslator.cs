using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Activity;
using Nexus.Service.Deck;
using Nexus.Service.Models.Activity;
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
    /// <summary>Stream Deck + XL, the most encoders on any supported deck.</summary>
    private const int MaxDials = 6;
    /// <summary>Multimedia's actionIdx for the dial's system volume control.</summary>
    private const int MultimediaVolumeDialIdx = 18;

    private readonly DeckImageStore _images;
    private readonly IAudioDeviceProvider? _devices;

    public ElgatoProfileTranslator(DeckImageStore images, IAudioDeviceProvider? devices = null)
    {
        _images = images;
        _devices = devices;
    }

    public (DeckConfig Config, ElgatoImportReport Report) Translate(ElgatoProfile profile)
    {
        var (cols, rows, _) = ElgatoModelCatalog.Resolve(profile.Model, profile.MaxColSeen, profile.MaxRowSeen);
        var report = new ElgatoImportReport();
        var config = new DeckConfig();
        // Elgato page position (0-based in Pages.Pages) to the Nexus page it became.
        var pageMap = new Dictionary<int, int>();

        // A Default that is also in the page list is an ordinary page; only one outside the list is the shared dial layer.
        var defaultPage = profile.DefaultPageId.Length > 0 && !profile.TopPageIds.Contains(profile.DefaultPageId) &&
            profile.PagesById.TryGetValue(profile.DefaultPageId, out var defaultData)
            ? defaultData
            : null;
        // Dial positions of the Default page that at least one kept page inherits; only those are reported (as Page 0).
        var inheritedDials = new HashSet<int>();

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
            var pageInherited = new HashSet<int>();
            var dials = BuildDials(page, defaultPage, pendingPageNumber, report, null, pageInherited);

            if (report.TotalKeys == beforeKeys && pageInherited.Count == 0)
            {
                // Zero non-skipped actions on this page - drop it and any
                // tentative notes tagged above.
                report.Unmapped.RemoveRange(beforeUnmappedCount, report.Unmapped.Count - beforeUnmappedCount);
                continue;
            }

            pageMap[originalIndex - 1] = config.Pages.Count;
            config.Pages.Add(new DeckPage { Slots = slots, Dials = dials });
            inheritedDials.UnionWith(pageInherited);
        }

        if (defaultPage is not null && inheritedDials.Count > 0)
        {
            // Counted and reported once (Page 0); the pages above inherit these dials without recounting.
            BuildDials(defaultPage, null, 0, report, inheritedDials, null);
        }

        if (config.Pages.Count == 0)
        {
            config.Pages.Add(new DeckPage());
        }

        RemapPageTargets(config, pageMap);
        return (config, report);
    }

    /// <summary>
    /// Page goto targets are built as Elgato page positions; dropped and
    /// capped pages shift the Nexus numbering, so each is resolved here. A
    /// target whose page was not kept falls to the first page.
    /// </summary>
    private static void RemapPageTargets(DeckConfig config, Dictionary<int, int> pageMap)
    {
        void Remap(DeckAction? action)
        {
            if (action is null)
            {
                return;
            }
            if (action is { Type: "page", Op: "goto", Target: { } target })
            {
                action.Target = pageMap.TryGetValue(target, out var mapped) ? mapped : 0;
            }
            foreach (var step in action.Steps ?? new List<DeckSequenceStep>())
            {
                Remap(step.Action);
            }
        }

        void RemapSlots(List<DeckSlot> slots, List<DeckDial>? dials)
        {
            foreach (var slot in slots)
            {
                Remap(slot.Action);
                if (slot.Folder is not null)
                {
                    RemapSlots(slot.Folder.Slots, slot.Folder.Dials);
                }
            }
            foreach (var action in DeckDials.AllActions(dials))
            {
                Remap(action);
            }
        }

        foreach (var page in config.Pages)
        {
            RemapSlots(page.Slots, page.Dials);
        }
    }

    private List<DeckSlot> FlattenPage(
        ElgatoPageData page, int cols, int rows, ElgatoProfile profile,
        int reportPageNumber, HashSet<string> ancestorStack, ElgatoImportReport report)
    {
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
                    Uuid = action.Uuid,
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

    /// <summary>
    /// The page's (or folder's) dials, null when it has none so a folder keeps its page's dials.
    /// A dial position the page leaves empty inherits the whole stack of <paramref name="fallback"/>
    /// (the Default page); a position the page defines at any row replaces it entirely. Inherited
    /// entries are built but not counted or reported, and their positions land in <paramref name="inheritedOut"/>.
    /// <paramref name="only"/> limits the build to those dial positions.
    /// Every entry of a stacked dial is imported; the row the router points at comes first.
    /// </summary>
    private List<DeckDial>? BuildDials(ElgatoPageData page, ElgatoPageData? fallback, int reportPageNumber, ElgatoImportReport report, HashSet<int>? only, HashSet<int>? inheritedOut)
    {
        var dials = new List<DeckDial>();
        var own = DialEntriesOf(page);
        var inheritable = fallback is null ? new Dictionary<(int Dial, int Row), ElgatoActionData>() : DialEntriesOf(fallback);
        var indexes = own.Keys.Concat(inheritable.Keys).Select(k => k.Dial);

        foreach (var index in indexes.Distinct().OrderBy(i => i))
        {
            if (index >= MaxDials || (only is not null && !only.Contains(index)))
            {
                continue;
            }

            var rows = new SortedDictionary<int, (ElgatoActionData Action, bool Inherited)>();
            var definedHere = own.Keys.Any(k => k.Dial == index);
            foreach (var kv in (definedHere ? own : inheritable).Where(kv => kv.Key.Dial == index))
            {
                rows[kv.Key.Row] = (kv.Value, !definedHere);
            }
            var ordered = rows.Where(kv => !ElgatoActionTypes.IsSkippable(kv.Value.Action.Uuid)).ToList();
            if (ordered.Count == 0)
            {
                continue;
            }
            if (page.DialRouters.TryGetValue(index, out var activeRow) && ordered.FindIndex(kv => kv.Key == activeRow) is > 0 and var at)
            {
                ordered = ordered.Skip(at).Concat(ordered.Take(at)).ToList();
            }

            if (!definedHere)
            {
                inheritedOut?.Add(index);
            }

            var built = new List<DeckDial>();
            DeckDial? placeholder = null;
            foreach (var (_, (action, inherited)) in ordered)
            {
                var (dial, mapped, reason, detail) = BuildDial(action);
                if (mapped)
                {
                    if (dial.Stack is { Count: > 0 } nested)
                    {
                        built.AddRange(nested);
                    }
                    else
                    {
                        built.Add(dial);
                    }
                }
                else
                {
                    placeholder ??= dial;
                }
                if (inherited)
                {
                    continue;
                }

                report.TotalKeys++;
                if (mapped)
                {
                    report.MappedKeys++;
                }
                if (reason is not null)
                {
                    report.Unmapped.Add(new ElgatoUnmappedEntry
                    {
                        Page = reportPageNumber,
                        Dial = index + 1,
                        Name = action.Name,
                        Uuid = action.Uuid,
                        Reason = reason,
                        Detail = detail,
                    });
                }
            }

            while (dials.Count <= index)
            {
                dials.Add(new DeckDial());
            }
            dials[index] = built.Count switch
            {
                0 => placeholder ?? new DeckDial(),
                1 => built[0],
                _ => new DeckDial { Stack = built },
            };
        }
        return dials.Count > 0 ? dials : null;
    }

    /// <summary>Every dial entry of a page; Dials entries absent from DialEntries count as row 0.</summary>
    private static Dictionary<(int Dial, int Row), ElgatoActionData> DialEntriesOf(ElgatoPageData page)
    {
        var entries = new Dictionary<(int Dial, int Row), ElgatoActionData>(page.DialEntries);
        foreach (var (dial, action) in page.Dials)
        {
            entries.TryAdd((dial, 0), action);
        }
        return entries;
    }

    private (DeckDial Dial, bool Mapped, string? Reason, string? Detail) BuildDial(ElgatoActionData action)
    {
        if (action.Uuid == ElgatoActionTypes.DialStack)
        {
            return BuildDialStack(action);
        }
        var dial = BuildDialFace(action);

        var (dialAction, failureReason, note) = TryBuildDialAction(action);
        if (dialAction is not null)
        {
            dial.Action = dialAction;
            return (dial, true, note, null);
        }

        // Placeholder keeps the face; with neither title nor icon the Elgato
        // action name stands in so the user can find and rebind it.
        if (string.IsNullOrEmpty(dial.Label) && dial.Icon is null && !string.IsNullOrEmpty(action.Name))
        {
            dial.Label = action.Name;
        }
        if (failureReason is not null)
        {
            return (dial, false, failureReason, null);
        }
        var reason = action.Uuid.StartsWith(ElgatoActionTypes.BuiltinPrefix, StringComparison.Ordinal) ? "unsupported" : "plugin";
        return (dial, false, reason, action.Uuid);
    }

    private DeckDial BuildDialFace(ElgatoActionData action)
    {
        var dial = new DeckDial();
        var state = action.States.Count > 0
            ? action.States[Math.Clamp(action.ActiveState, 0, action.States.Count - 1)]
            : null;
        if (state is { ShowTitle: true } && !string.IsNullOrEmpty(state.Title))
        {
            var collapsed = string.Join(' ', state.Title.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (collapsed.Length > 0)
            {
                dial.Label = collapsed;
            }
        }
        if (action.EncoderIconPath is not null && IngestImage(action.EncoderIconPath) is { } id)
        {
            dial.Icon = new DeckIcon { Kind = "image", Value = id };
        }
        return dial;
    }

    /// <summary>Entries that cannot be translated are dropped; a single survivor becomes the dial itself.</summary>
    private (DeckDial, bool, string?, string?) BuildDialStack(ElgatoActionData action)
    {
        var entries = new List<DeckDial>();
        foreach (var child in action.Children)
        {
            var (entryAction, _, _) = TryBuildDialAction(child);
            if (entryAction is null)
            {
                continue;
            }
            var entry = BuildDialFace(child);
            entry.Action = entryAction;
            entries.Add(entry);
        }

        var dropped = action.Children.Count - entries.Count;
        if (entries.Count == 0)
        {
            var dial = BuildDialFace(action);
            if (string.IsNullOrEmpty(dial.Label) && !string.IsNullOrEmpty(action.Name))
            {
                dial.Label = action.Name;
            }
            return (dial, false, "multiStep", dropped > 0 ? $"{dropped} step(s) unsupported" : null);
        }

        var result = entries.Count == 1 ? entries[0] : new DeckDial { Stack = entries };
        return (result, true, dropped > 0 ? "multiStep" : null, dropped > 0 ? $"{dropped} step(s) dropped" : null);
    }

    /// <summary>
    /// One Encoder-controller action as a DeckDialAction. Returns (null, null)
    /// for a type with no dial translation at all, leaving the caller to
    /// classify it as unsupported or plugin.
    /// </summary>
    private (DeckDialAction? Action, string? FailureReason, string? Note) TryBuildDialAction(ElgatoActionData action)
    {
        switch (action.Uuid)
        {
            case ElgatoActionTypes.Hotkey:
                return BuildHotkeyDialAction(action);
            case ElgatoActionTypes.Multimedia:
                return ElgatoJson.GetInt(action.Settings, "actionIdx") == MultimediaVolumeDialIdx
                    ? (new DeckDialAction { Type = DeckDialTypes.Volume }, null, null)
                    : (null, "media", null);
            case ElgatoActionTypes.KeyBrightness:
                return (new DeckDialAction { Type = DeckDialTypes.DeckBrightness }, null, null);
            case ElgatoActionTypes.VolumeOutput:
                return (new DeckDialAction { Type = DeckDialTypes.Volume, Step = ReadVolumeStep(action.Settings), DeviceId = ReadDeviceId(action.Settings, false) }, null, null);
            case ElgatoActionTypes.VolumeInput:
                return (new DeckDialAction { Type = DeckDialTypes.MicVolume, Step = ReadVolumeStep(action.Settings), DeviceId = ReadDeviceId(action.Settings, true) }, null, null);
            case ElgatoActionTypes.SpotifyDial:
                return BuildSpotifyDialAction(action);
            case ElgatoActionTypes.KeysAdaptor:
                return BuildActionTriggerDialAction(action);
            default:
                return (null, null, null);
        }
    }

    /// <summary>Hotkeys[] on a dial is rotate CCW, rotate CW, press/tap (Elgato's own field order); slot 3 is unused.</summary>
    private static (DeckDialAction?, string?, string?) BuildHotkeyDialAction(ElgatoActionData action)
    {
        if (action.Settings.ValueKind != JsonValueKind.Object ||
            !action.Settings.TryGetProperty("Hotkeys", out var hotkeysEl) ||
            hotkeysEl.ValueKind != JsonValueKind.Array)
        {
            return (null, "hotkey", null);
        }

        var undecoded = false;
        DeckAction? Slot(int i)
        {
            if (i >= hotkeysEl.GetArrayLength())
            {
                return null;
            }
            if (DecodeHotkey(hotkeysEl[i]) is { } keys)
            {
                return new DeckAction { Type = "hotkey", Keys = keys };
            }
            undecoded |= IsPopulatedHotkeySlot(hotkeysEl[i]);
            return null;
        }

        var custom = new DeckDialAction { Type = DeckDialTypes.Custom, TurnLeft = Slot(0), TurnRight = Slot(1), Push = Slot(2) };
        if (custom.TurnLeft is null && custom.TurnRight is null && custom.Push is null)
        {
            return (null, "hotkey", null);
        }
        return (custom, null, undecoded ? "hotkey" : null);
    }

    /// <summary>Action Trigger's Actions[] is rotate CCW, press, rotate CW, each a key action.</summary>
    private static (DeckDialAction?, string?, string?) BuildActionTriggerDialAction(ElgatoActionData action)
    {
        string? partNote = null;
        DeckAction? Part(int i)
        {
            if (i >= action.Children.Count)
            {
                return null;
            }
            var (leaf, _, note) = TryBuildLeafAction(action.Children[i]);
            partNote ??= leaf is null ? null : note;
            return leaf;
        }

        var custom = new DeckDialAction { Type = DeckDialTypes.Custom, TurnLeft = Part(0), Push = Part(1), TurnRight = Part(2) };
        var mappedParts = (custom.TurnLeft is null ? 0 : 1) + (custom.Push is null ? 0 : 1) + (custom.TurnRight is null ? 0 : 1);
        if (mappedParts == 0)
        {
            return (null, "multiStep", null);
        }
        return (custom, null, mappedParts < Math.Min(3, action.Children.Count) ? "multiStep" : partNote);
    }

    /// <summary>Turn is system volume and press is play/pause; the plugin's double and triple press (next and previous track) have no dial slot, so they are noted.</summary>
    private static (DeckDialAction?, string?, string?) BuildSpotifyDialAction(ElgatoActionData action)
    {
        var step = ElgatoJson.GetDouble(action.Settings, "volumeStep");
        DeckAction Volume(string op) => new() { Type = "system", SystemAction = new DeckSystemAction { Op = op, Step = step } };
        var custom = new DeckDialAction
        {
            Type = DeckDialTypes.Custom,
            TurnLeft = Volume("volumeDown"),
            TurnRight = Volume("volumeUp"),
            Push = new DeckAction { Type = "system", SystemAction = new DeckSystemAction { Op = "mediaPlayPause" } },
        };
        var gestures = !string.IsNullOrEmpty(ElgatoJson.GetString(action.Settings, "doublePress")) ||
            !string.IsNullOrEmpty(ElgatoJson.GetString(action.Settings, "triplePress"));
        return (custom, null, gestures ? "dialPartial" : null);
    }

    /// <summary>
    /// The chosen endpoint id; null for the system default so the dial keeps following it. An id this
    /// machine does not list (the profile came from another PC) also becomes the default. With no
    /// device list to check against, the id is kept.
    /// </summary>
    private string? ReadDeviceId(JsonElement settings, bool capture)
    {
        var id = ElgatoJson.GetString(settings, "deviceId");
        if (string.IsNullOrEmpty(id) || id == "default")
        {
            return null;
        }
        List<AudioDevice>? known = null;
        try
        {
            var list = _devices?.ListDevices();
            known = list is null ? null : capture ? list.Inputs : list.Outputs;
        }
        catch (Exception)
        {
            return id;
        }
        return known is { Count: > 0 } && !known.Exists(d => string.Equals(d.Id, id, StringComparison.Ordinal)) ? null : id;
    }

    /// <summary>Volume Controller stores volumeStep as a numeric string; a number is tolerated.</summary>
    private static double? ReadVolumeStep(JsonElement settings)
    {
        if (ElgatoJson.GetDouble(settings, "volumeStep") is { } number)
        {
            return number;
        }
        return double.TryParse(ElgatoJson.GetString(settings, "volumeStep"), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
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
            // The import is synchronous, so a video (which needs an async poster frame) is not carried over.
            return DeckImageStore.SniffExtension(bytes) is { } ext && DeckImageStore.IsVideoExtension(ext) ? null : _images.Store(bytes);
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
            slot.Folder = new DeckFolder { Slots = folderSlots, Dials = BuildDials(childPage, null, reportPageNumber, report, null, null) };
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
                var built = BuildOpenAppAction(action, out var openNote);
                return built is not null ? (built, null, openNote) : (null, "unsupported", null);
            }
            case ElgatoActionTypes.KeyBrightness:
            {
                // actionIdx 0 raises brightness and 1 lowers it.
                var op = ElgatoJson.GetInt(action.Settings, "actionIdx") switch { 0 => "up", 1 => "down", _ => null };
                return op is null ? (null, "unsupported", null) : (new DeckAction { Type = "deckBrightness", Op = op }, null, null);
            }
            case ElgatoActionTypes.Sleep:
            {
                return (new DeckAction { Type = "deckSleep" }, null, null);
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
            case ElgatoActionTypes.PageGoto:
            {
                // Elgato's PageIndex is 1-based; Translate remaps it to the Nexus page.
                var index = ElgatoJson.GetInt(action.Settings, "PageIndex");
                return index is >= 1
                    ? (new DeckAction { Type = "page", Op = "goto", Target = index.Value - 1 }, null, null)
                    : (null, "unsupported", null);
            }
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
            Sensor = sensorUid.StartsWith("/gpu-powermonitor/", StringComparison.OrdinalIgnoreCase) ? sensorUid : "",
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
        if (sensorUid.StartsWith("/gpu-powermonitor/", StringComparison.OrdinalIgnoreCase))
        {
            return "psu";
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
        // KeyModifiers can carry flag bits above the Int32 range, so it is read as a long.
        var modifiers = slotEl.ValueKind == JsonValueKind.Object && slotEl.TryGetProperty("KeyModifiers", out var modEl) &&
            modEl.ValueKind == JsonValueKind.Number && modEl.TryGetInt64(out var modBits)
            ? modBits
            : 0;
        var key = (modifiers & KeypadFlag) != 0 ? KeypadToken(qtCode.Value) ?? QtKeyToToken(qtCode.Value) : QtKeyToToken(qtCode.Value);
        if (key is null)
        {
            return null;
        }

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

    /// <summary>KeyModifiers bit Elgato sets on a numeric keypad key.</summary>
    private const long KeypadFlag = 0x4000;

    /// <summary>Qt::Key of a keypad key to its keypad token (digits and the Qt codes for the operator and Enter keys); null when it is none of those.</summary>
    private static string? KeypadToken(int code) => code switch
    {
        >= 0x30 and <= 0x39 => "numpad" + (char)code,
        0x2B => "numpadadd",
        0x2D => "numpadsubtract",
        0x2A => "numpadmultiply",
        0x2F => "numpaddivide",
        0x2E or 0x2C => "numpaddecimal",
        0x3D => "numpadequal",
        0x01000004 or 0x01000005 => "numpadenter",
        _ => null,
    };

    private static bool IsPopulatedHotkeySlot(JsonElement slotEl)
    {
        var qt = ElgatoJson.GetInt(slotEl, "QTKeyCode");
        return qt is not null && qt != 0x01FFFFFF;
    }

    /// <summary>Qt::Key value to a ParseHotkey-compatible token, or null when Nexus has no representable key (a shifted symbol such as '+').</summary>
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
        var punctuation = code switch
        {
            0x2E => ".",
            0x2C => ",",
            0x2D => "-",
            0x3D => "=",
            0x2F => "/",
            0x3B => ";",
            0x27 => "'",
            0x5B => "[",
            0x5D => "]",
            0x5C => "\\",
            0x60 => "`",
            _ => null,
        };
        if (punctuation is not null)
        {
            return punctuation;
        }
        if (code == 0x01000025)
        {
            return "numlock";
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

    /// <summary>
    /// Open Application stores exec and bundle_path (older builds stored path). A macOS exec inside
    /// a .app resolves to the bundle; a launcher that needs args goes through its Start Menu shortcut.
    /// </summary>
    private static DeckAction? BuildOpenAppAction(ElgatoActionData action, out string? note)
    {
        note = null;
        var exec = ElgatoJson.GetString(action.Settings, "exec");
        var bundle = ElgatoJson.GetString(action.Settings, "bundle_path");
        var target = !string.IsNullOrEmpty(exec) && exec.Contains(".app/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(bundle)
            ? bundle
            : !string.IsNullOrEmpty(exec) ? exec : bundle;
        if (!string.IsNullOrEmpty(ElgatoJson.GetString(action.Settings, "args")))
        {
            var source = ElgatoJson.GetString(action.Settings, "source");
            if (!string.IsNullOrEmpty(source) && source.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                target = source;
            }
            else
            {
                note = "openAppArgs";
            }
        }
        if (string.IsNullOrEmpty(target))
        {
            return BuildOpenAction(action);
        }
        var path = StripOuterQuotes(target);
        return IsLocalRootedPath(path) ? new DeckAction { Type = "openFile", Path = path } : null;
    }

    /// <summary>
    /// True for a drive-letter or POSIX absolute path. UNC shares, device paths and relative paths are
    /// refused because the profile is untrusted and they would reach a remote host or resolve against the working directory.
    /// </summary>
    private static bool IsLocalRootedPath(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        {
            return true;
        }
        return path.StartsWith('/');
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
        // A freshly dropped Multimedia key stores no actionIdx and plays or pauses.
        var idx = ElgatoJson.GetInt(action.Settings, "actionIdx") ?? 1;
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
