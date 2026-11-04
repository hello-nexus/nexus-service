using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Displays;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Panel;

internal readonly record struct RelinkRecord(
    string RecordId,
    string DisplayId,
    DisplayFingerprint? Fingerprint,
    IReadOnlyList<string>? Siblings = null);

internal readonly record struct RelinkDisplay(string Id, DisplayFingerprint? Fingerprint, bool IsPrimary = false, int Width = 0, int Height = 0);

/// <param name="Weak">The match rests on model alone (a serial is missing or placeholder on a side); such moves need confirmation across scans.</param>
internal readonly record struct RelinkMove(string RecordId, string OldDisplayId, string NewDisplayId, DisplayFingerprint Fingerprint, bool Weak);

internal sealed class RelinkPlan
{
    public List<(string RecordId, DisplayFingerprint Fingerprint)> Backfills { get; } = new();
    public List<(string RecordId, List<string> Siblings)> SiblingUpdates { get; } = new();
    public List<RelinkMove> Moves { get; } = new();
}

/// <summary>
/// Decides which display-bound panel records follow their monitor to a new
/// OS display id. Pure: callers apply the plan.
/// </summary>
internal static class PanelDisplayRelink
{
    internal const int MaxSiblings = 16;
    private const long PlaceholderSerial = 0x01010101;

    public static RelinkPlan Plan(IReadOnlyList<RelinkRecord> records, IReadOnlyList<RelinkDisplay> displays)
    {
        var plan = new RelinkPlan();
        var shown = Demote(displays);
        var attached = new Dictionary<string, DisplayFingerprint?>(StringComparer.Ordinal);
        var sizes = new Dictionary<string, (int Width, int Height)>(StringComparer.Ordinal);
        var primary = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in shown)
        {
            if (string.IsNullOrEmpty(d.Id)) continue;
            attached.TryAdd(d.Id, d.Fingerprint);
            sizes.TryAdd(d.Id, (d.Width, d.Height));
            if (d.IsPrimary) primary.Add(d.Id);
        }
        var claimed = new HashSet<string>(records.Select(r => r.DisplayId), StringComparer.Ordinal);

        var orphans = new List<RelinkRecord>();
        foreach (var r in records)
        {
            if (string.IsNullOrEmpty(r.DisplayId)) continue;
            if (attached.TryGetValue(r.DisplayId, out var live))
            {
                if (live is not null && (r.Fingerprint is null || Score(live) > Score(r.Fingerprint)))
                    plan.Backfills.Add((r.RecordId, live));
                var merged = MergeSiblings(r.Siblings, attached.Keys, r.DisplayId);
                if (merged is not null) plan.SiblingUpdates.Add((r.RecordId, merged));
                continue;
            }
            if (r.Fingerprint is not null) orphans.Add(r);
        }

        var unclaimed = attached
            .Where(kv => kv.Value is not null && !claimed.Contains(kv.Key))
            .Select(kv => new RelinkDisplay(kv.Key, kv.Value, false, sizes[kv.Key].Width, sizes[kv.Key].Height))
            .ToList();

        // A monitor that was attached beside the record's own display is a different one.
        // A generic EDID shared with other monitors only follows a display that still passes its resolution gate. Keyed on the fingerprint, not the record's stored family, which is cleared at non-native modes.
        static bool Eligible(RelinkRecord orphan, RelinkDisplay d)
            => (orphan.Siblings is null || !orphan.Siblings.Contains(d.Id, StringComparer.Ordinal))
               && (!KnownPanelDisplays.NeedsResolutionGate(orphan.Fingerprint!.Manufacturer, orphan.Fingerprint.Product)
                   || (d.Fingerprint is not null
                       && KnownPanelDisplays.Match(d.Fingerprint.Manufacturer, d.Fingerprint.Product, null, d.Width, d.Height) is not null));

        foreach (var orphan in orphans.Where(o => IsStrong(o.Fingerprint!)).ToList())
        {
            var sameOrphans = orphans.Count(o => SameDevice(o.Fingerprint!, orphan.Fingerprint!));
            var candidates = unclaimed.Where(d => Eligible(orphan, d) && SameDevice(d.Fingerprint!, orphan.Fingerprint!)).ToList();
            if (sameOrphans != 1 || candidates.Count != 1) continue;
            Move(plan, orphan, candidates[0], weak: false);
            orphans.Remove(orphan);
            unclaimed.Remove(candidates[0]);
        }

        foreach (var group in orphans.GroupBy(o => ModelKey(o.Fingerprint!)))
        {
            if (group.Count() != 1) continue;
            var orphan = group.First();
            // Two strong serials that differ are two physical monitors; the primary display is never guessed.
            var candidates = unclaimed
                .Where(d => Eligible(orphan, d)
                    && ModelKey(d.Fingerprint!) == group.Key
                    && !primary.Contains(d.Id)
                    && !(IsStrong(orphan.Fingerprint!) && IsStrong(d.Fingerprint!)))
                .ToList();
            if (candidates.Count != 1) continue;
            Move(plan, orphan, candidates[0], weak: true);
        }
        return plan;
    }

    /// <summary>Strong fingerprints shared by several attached monitors (placeholder serial text) identify none of them.</summary>
    private static IReadOnlyList<RelinkDisplay> Demote(IReadOnlyList<RelinkDisplay> displays)
    {
        var result = new List<RelinkDisplay>(displays.Count);
        foreach (var d in displays)
        {
            var f = d.Fingerprint;
            if (f is not null && IsStrong(f)
                && displays.Count(o => o.Fingerprint is not null && SameDevice(o.Fingerprint, f)) > 1)
            {
                f = new DisplayFingerprint { Manufacturer = f.Manufacturer, Product = f.Product };
            }
            result.Add(d with { Fingerprint = f });
        }
        return result;
    }

    /// <summary>
    /// Newest last: ids attached in this scan move to the end, the oldest others
    /// are trimmed from the front, and an id attached now is never evicted (the
    /// list may exceed the cap for that scan). Null when the sequence is unchanged.
    /// </summary>
    private static List<string>? MergeSiblings(IReadOnlyList<string>? stored, IEnumerable<string> attachedIds, string ownId)
    {
        var current = stored ?? Array.Empty<string>();
        var now = new HashSet<string>(attachedIds.Where(id => id != ownId), StringComparer.Ordinal);
        var merged = current.Where(id => !now.Contains(id)).ToList();
        var keep = merged.Count;
        merged.AddRange(current.Where(now.Contains));
        merged.AddRange(now.Where(id => !current.Contains(id, StringComparer.Ordinal)));
        var excess = merged.Count - MaxSiblings;
        if (excess > 0) merged.RemoveRange(0, Math.Min(excess, keep));
        return merged.SequenceEqual(current, StringComparer.Ordinal) ? null : merged;
    }

    private static int Score(DisplayFingerprint f)
        => ((f.Serial != 0 && f.Serial != PlaceholderSerial) ? 1 : 0) + (string.IsNullOrEmpty(f.SerialText) ? 0 : 1);

    private static void Move(RelinkPlan plan, RelinkRecord record, RelinkDisplay target, bool weak)
        => plan.Moves.Add(new RelinkMove(record.RecordId, record.DisplayId, target.Id, target.Fingerprint!, weak));

    private static bool IsStrong(DisplayFingerprint f) => Score(f) > 0;

    private static string ModelKey(DisplayFingerprint f)
        => $"{f.Manufacturer.ToUpperInvariant()}:{f.Product.ToUpperInvariant()}";

    private static bool SameDevice(DisplayFingerprint a, DisplayFingerprint b)
        => ModelKey(a) == ModelKey(b)
           && IsStrong(a) && IsStrong(b)
           && a.Serial == b.Serial
           && string.Equals(a.SerialText ?? "", b.SerialText ?? "", StringComparison.Ordinal);
}
