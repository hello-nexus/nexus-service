using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Decides whether a settled OpenRGB list that lacks previously seen SMBus DRAM
/// devices deserves another detection pass. The chipset bus can fail to answer for
/// a stretch of a session and answer later, so a few spaced passes recover sticks
/// a single boot-time pass missed. Nothing is scheduled for a PC that never had one.
/// </summary>
internal static class DramRedetectPlan
{
    /// <summary>Wait before each pass. Each is armed when the list settles after the previous pass (or the first settle), so the real offsets from boot run longer than these.</summary>
    internal static readonly TimeSpan[] Delays =
    {
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(4),
        TimeSpan.FromMinutes(10),
    };

    /// <summary>Sessions a known device may stay missing through a whole series before it is forgotten.</summary>
    internal const int MaxMissedSessions = 3;

    /// <summary>The daemon names an SMBus device's location after the bus driver, which keeps these ids apart from GPU and USB I2C ones.</summary>
    internal static bool IsSmbusDeviceId(string id)
        => id.StartsWith("openrgb-l-I2C", StringComparison.Ordinal)
            && id.Contains("SMBus", StringComparison.Ordinal);

    /// <summary>
    /// A card id back to its device id. Split motherboards add "-N" and custom partitions
    /// ":zN" to the device id; an SMBus device id ends in its hex address, so a trailing
    /// run of digits after "-" or ":z" is a card suffix.
    /// </summary>
    internal static string DeviceIdOf(string cardId)
    {
        var colon = cardId.LastIndexOf(":z", StringComparison.Ordinal);
        if (colon > 0 && IsAllDigits(cardId.AsSpan(colon + 2)))
        {
            cardId = cardId[..colon];
        }
        var dash = cardId.LastIndexOf('-');
        if (dash > 0 && IsAllDigits(cardId.AsSpan(dash + 1)))
        {
            cardId = cardId[..dash];
        }
        return cardId;
    }

    private static bool IsAllDigits(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty)
        {
            return false;
        }
        foreach (var c in s)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The SMBus device ids an earlier build left in the card-id list, normalized to device ids.</summary>
    internal static List<string> SeedFromCardIds(IEnumerable<string> cardIds)
    {
        var seeded = new List<string>();
        foreach (var card in cardIds)
        {
            var id = DeviceIdOf(card);
            if (IsSmbusDeviceId(id) && !seeded.Contains(id))
            {
                seeded.Add(id);
            }
        }
        return seeded;
    }

    /// <summary>
    /// The known set after a settled list: DRAM devices it held are known with their miss count
    /// cleared, and a known id the list holds as a non-DRAM device (a seeded motherboard
    /// controller) is forgotten.
    /// </summary>
    internal static Dictionary<string, int> Reconcile(IReadOnlyDictionary<string, int> known, IEnumerable<string> dramPresent, IEnumerable<string> otherPresent)
    {
        var next = new Dictionary<string, int>(known, StringComparer.Ordinal);
        foreach (var id in otherPresent)
        {
            next.Remove(id);
        }
        foreach (var id in dramPresent)
        {
            next[id] = 0;
        }
        return next;
    }

    /// <summary>Known ids the settled list does not hold, less those the user took out of detection.</summary>
    internal static List<string> MissingKnown(IEnumerable<string> knownIds, ICollection<string> presentIds, ICollection<string> excludedIds)
    {
        var missing = new List<string>();
        foreach (var id in knownIds)
        {
            if (IsSmbusDeviceId(id) && !presentIds.Contains(id) && !excludedIds.Contains(id) && !missing.Contains(id))
            {
                missing.Add(id);
            }
        }
        return missing;
    }

    /// <summary>Counts one more missed session for each id and drops those that reach the cap.</summary>
    internal static Dictionary<string, int> RecordMissedSession(IReadOnlyDictionary<string, int> known, IEnumerable<string> missing)
    {
        var next = new Dictionary<string, int>(known, StringComparer.Ordinal);
        foreach (var id in missing)
        {
            var count = (next.TryGetValue(id, out var c) ? c : 0) + 1;
            if (count >= MaxMissedSessions)
            {
                next.Remove(id);
            }
            else
            {
                next[id] = count;
            }
        }
        return next;
    }

    internal static bool SameCounts(IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>The wait before the next pass, or null when none is due: nothing missing, or every pass already spent.</summary>
    internal static TimeSpan? NextDelay(int attemptsDone, int missingCount)
        => missingCount > 0 && attemptsDone < Delays.Length ? Delays[attemptsDone] : null;
}
