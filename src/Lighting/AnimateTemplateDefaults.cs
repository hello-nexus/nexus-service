using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Lighting;

/// <summary>
/// Canonical default template bundles (<see cref="SlotCount"/> preset slots per
/// animate effect), loaded once from the embedded data/default-animate-templates.json. The
/// persisted settings hold only user deltas - a stored slot that equals its
/// default is pruned to null, an untouched bundle is omitted entirely - and
/// every reader resolves a missing slot through this table. nexus-web fetches
/// the same data from GET /lighting/animate/defaults instead of bundling its
/// own copy.
/// </summary>
public static class AnimateTemplateDefaults
{
    /// <summary>Preset slots per effect (the 1/2/3/4 button row).</summary>
    public const int SlotCount = 4;

    /// <summary>Float comparison tolerance for "matches default": wide enough
    /// to absorb JSON float round-trip noise, far below any slider step.</summary>
    private const float Epsilon = 1e-4f;

    private static readonly Lazy<(Dictionary<string, AnimateEffectTemplates> Table, byte[] Json, string ETag)> _data = new(Load);

    /// <summary>Treat as immutable - the bundles and slots are shared process-wide
    /// singletons (also handed out by <see cref="Slot"/>); mutating any nested
    /// property corrupts the canonical defaults for the process lifetime.</summary>
    public static IReadOnlyDictionary<string, AnimateEffectTemplates> All => _data.Value.Table;

    /// <summary>Raw embedded JSON, served verbatim by GET /lighting/animate/defaults.</summary>
    public static byte[] SerializedJson => _data.Value.Json;

    /// <summary>Strong ETag over the defaults content; changes only when the data file does.</summary>
    public static string ETag => _data.Value.ETag;

    /// <summary>Default state for the given effect + slot, or null for an unknown effect.
    /// Slot index is clamped into the bundle like every other slot reader.</summary>
    public static AnimateEffectState? Slot(string effect, int slot)
    {
        if (!_data.Value.Table.TryGetValue(effect, out var bundle) || bundle.Slots.Count == 0)
        {
            return null;
        }
        return bundle.Slots[Math.Clamp(slot, 0, bundle.Slots.Count - 1)];
    }

    /// <summary>Effective look for an effect's preset slot against a sparse stored
    /// template dict: the stored user delta when present, else the canonical
    /// default. Null when the effect is unknown to both. Tolerates a null dict
    /// and null bundle values (explicit JSON nulls in foreign settings).</summary>
    public static AnimateEffectState? ResolveSlot(Dictionary<string, AnimateEffectTemplates>? templates, string effect, int slot)
    {
        slot = Math.Clamp(slot, 0, SlotCount - 1);
        if (templates is not null && templates.TryGetValue(effect, out var bundle) && bundle is not null
            && bundle.Slots is { } slots && slot < slots.Count && slots[slot] is { } stored)
        {
            return stored;
        }
        return Slot(effect, slot);
    }

    /// <summary>A start that replays what the effect last ran: its saved state, else its selected preset slot, else plain defaults. For callers that name only the effect, whose shader defaults are not any preset.</summary>
    public static Nexus.Service.Models.Lighting.AnimateHeadlessStart SavedStart(LightingSettings lighting, string effect)
    {
        var states = StaticEffectCatalog.Contains(effect) ? lighting.Static.States : lighting.Animate.States;
        if (!states.TryGetValue(effect, out var look) || look is null)
        {
            look = ResolveSelected(lighting.Animate.Templates, effect) ?? new AnimateEffectState();
        }
        var start = new Nexus.Service.Models.Lighting.AnimateHeadlessStart
        {
            Effect = effect,
            Speed = look.Speed,
            Intensity = look.Intensity,
            Hue = look.Hue,
            Colorize = look.Colorize,
            Saturation = look.Saturation,
            Contrast = look.Contrast,
            Sensors = look.Sensors,
            Filter = "none",
        };
        if (look.Params is not null)
        {
            foreach (var (name, value) in look.Params)
            {
                start.Params.Add(new Nexus.Service.Models.Lighting.ShaderParam { Name = name, Value = value });
            }
        }
        return start;
    }

    /// <summary>The effect's currently-selected preset slot look, resolved through the sparse store.</summary>
    public static AnimateEffectState? ResolveSelected(Dictionary<string, AnimateEffectTemplates>? templates, string effect)
    {
        var selected = templates is not null && templates.TryGetValue(effect, out var bundle) && bundle is not null
            ? bundle.Selected
            : 0;
        return ResolveSlot(templates, effect, selected);
    }

    /// <summary>
    /// Drop activation-state entries that are redundant: null values and states
    /// equal to the effect's resolved selected-slot look. Applied by the v10
    /// load migration and by profile apply/import, so dense States written by
    /// older builds (or carried by old profile files and cloud-synced payloads)
    /// cannot re-inflate a pruned settings.json.
    /// </summary>
    public static void PruneStates(AnimateSettings animate)
    {
        if (animate.States is null)
        {
            animate.States = new();
            return;
        }
        var redundant = new List<string>();
        foreach (var (effect, state) in animate.States)
        {
            if (state is null
                || (ResolveSelected(animate.Templates, effect) is { } look && StateEquals(state, look)))
            {
                redundant.Add(effect);
            }
        }
        foreach (var effect in redundant)
        {
            animate.States.Remove(effect);
        }
    }

    /// <summary>
    /// Reduce a full template dict to user deltas. An entirely-default bundle
    /// with Selected == 0 is dropped; in a kept bundle, non-selected slots equal
    /// to their default become null and trailing nulls are trimmed. The selected
    /// slot is always kept dense: pre-v9 peers (cloud profile sync, exported
    /// profiles) dereference Slots[Selected] unguarded, and a missing bundle is
    /// the only sparse shape they handle. Unknown effects are kept verbatim -
    /// they are user data from a different service version, not ours to judge.
    /// Null in (a pre-v9 settings.json can carry an explicit null dict) yields
    /// an empty dict.
    /// </summary>
    public static Dictionary<string, AnimateEffectTemplates> Prune(Dictionary<string, AnimateEffectTemplates>? templates)
    {
        var result = new Dictionary<string, AnimateEffectTemplates>();
        if (templates is null)
        {
            return result;
        }
        foreach (var (effect, bundle) in templates)
        {
            if (bundle is null)
            {
                continue;
            }
            if (!_data.Value.Table.TryGetValue(effect, out var defaults))
            {
                result[effect] = bundle;
                continue;
            }
            // A foreign doc can carry an explicit null slots list; treat it as
            // an empty sparse bundle rather than throwing (a Migrate throw
            // routes Load through the corrupt-file reset).
            var bundleSlots = bundle.Slots ?? new List<AnimateEffectState?>();
            // A null element in already-sparse input means "the default", so it
            // counts as default here; that keeps Prune idempotent (a re-applied
            // post-v9 profile's all-default sparse bundle drops instead of
            // getting a slot pinned to a copy of today's default).
            var isDefault = new bool[bundleSlots.Count];
            var allDefault = true;
            for (var i = 0; i < bundleSlots.Count; i++)
            {
                var slot = bundleSlots[i];
                // Clamped like Slot(): a stored index past the canonical bundle
                // (a fill written when fills carried a full preset row) renders
                // as the clamped default, so it prunes by the same rule.
                var def = defaults.Slots.Count == 0
                    ? null
                    : defaults.Slots[Math.Clamp(i, 0, defaults.Slots.Count - 1)];
                isDefault[i] = def is not null && (slot is null || StateEquals(slot, def));
                allDefault &= isDefault[i];
            }
            if (allDefault && bundle.Selected == 0)
            {
                continue;
            }
            var selected = Math.Clamp(bundle.Selected, 0, SlotCount - 1);
            var slots = new List<AnimateEffectState?>(bundleSlots.Count);
            for (var i = 0; i < bundleSlots.Count; i++)
            {
                slots.Add(isDefault[i] && i != selected ? null : bundleSlots[i]);
            }
            // Already-sparse input (a post-v9 profile file re-applied) can carry a
            // null or truncated-away selected slot; pad to it and materialize so
            // the dense-selected guarantee holds at the true index. Cloned - the
            // stored doc must never alias the shared defaults.
            while (slots.Count <= selected)
            {
                slots.Add(null);
            }
            if (slots[selected] is null && defaults.Slots.Count > 0
                && defaults.Slots[Math.Clamp(selected, 0, defaults.Slots.Count - 1)] is { } defSlot)
            {
                slots[selected] = CloneState(defSlot);
            }
            while (slots.Count > 0 && slots[^1] is null)
            {
                slots.RemoveAt(slots.Count - 1);
            }
            result[effect] = new AnimateEffectTemplates { Selected = bundle.Selected, Slots = slots };
        }
        return result;
    }

    /// <summary>True when every render-affecting field matches within <see cref="Epsilon"/>.
    /// Params must carry the same key set; a missing vs present key is a difference
    /// even at the default value, mirroring slotMatchesDefault in nexus-web.</summary>
    internal static bool StateEquals(AnimateEffectState a, AnimateEffectState b)
    {
        if (Math.Abs(a.Speed - b.Speed) > Epsilon) return false;
        if (Math.Abs(a.Intensity - b.Intensity) > Epsilon) return false;
        if (Math.Abs(a.Hue - b.Hue) > Epsilon) return false;
        if (Math.Abs(a.Colorize - b.Colorize) > Epsilon) return false;
        if (Math.Abs(a.Saturation - b.Saturation) > Epsilon) return false;
        if (Math.Abs(a.Contrast - b.Contrast) > Epsilon) return false;
        var ap = a.Params ?? new();
        var bp = b.Params ?? new();
        if (ap.Count != bp.Count) return false;
        foreach (var (key, value) in ap)
        {
            if (!bp.TryGetValue(key, out var other) || Math.Abs(value - other) > Epsilon)
            {
                return false;
            }
        }
        return SensorBinding.MapEquals(a.Sensors, b.Sensors);
    }

    private static AnimateEffectState CloneState(AnimateEffectState s) => new()
    {
        Speed = s.Speed,
        Intensity = s.Intensity,
        Hue = s.Hue,
        Colorize = s.Colorize,
        Saturation = s.Saturation,
        Contrast = s.Contrast,
        Params = s.Params is not null ? new Dictionary<string, float>(s.Params) : new(),
        Sensors = SensorBinding.CopyMap(s.Sensors),
    };

    private static (Dictionary<string, AnimateEffectTemplates>, byte[], string) Load()
    {
        var asm = typeof(AnimateTemplateDefaults).Assembly;
        using var stream = asm.GetManifestResourceStream("default-animate-templates.json");
        if (stream is null)
        {
            Console.Error.WriteLine("[lighting] default-animate-templates.json resource not found; defaults unavailable");
            return (new(), "{}"u8.ToArray(), "\"empty\"");
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        Dictionary<string, AnimateEffectTemplates>? table = null;
        try
        {
            table = JsonSerializer.Deserialize(bytes, AppJsonContext.Default.DictionaryStringAnimateEffectTemplates);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[lighting] failed to parse default-animate-templates.json: {ex.Message}");
        }
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(bytes))[..16]}\"";
        return (table ?? new(), bytes, etag);
    }
}
