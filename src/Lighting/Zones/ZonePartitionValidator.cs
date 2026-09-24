using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Zones;

/// <summary>
/// Pure validation of a user-submitted zone partition against a device's
/// segments. Encodes the index-stability invariant:
///   1. the partition must tile every segment exactly (full cover, no overlap)
///   2. a zone that touches a RESIZABLE segment must be exactly that whole segment
///   3. a zone may span multiple segments (or parts of them) only across FIXED
///      segments, and its slices must be contiguous in device order
/// plus zone-name hygiene (trimmed non-empty, shared length cap).
/// Inputs are expected pre-normalized via <see cref="ZoneResolution.NormalizeDefs"/>
/// so whole-resizable slices already carry the live segment count.
/// </summary>
public static class ZonePartitionValidator
{
    /// <summary>Zone names share the mapping-schema group-name cap (zones are the successor of groups).</summary>
    public const int MaxZoneNameLength = MappingSchema.MaxGroupNameLength;

    public sealed class Result
    {
        public bool Ok => Errors.Count == 0;
        public List<string> Errors { get; } = new();
    }

    public static Result Validate(IReadOnlyList<StructureSegment> segments, IReadOnlyList<ZoneDef> zones)
        => Validate(segments, zones, chainOwnedSegments: null);

    /// <param name="chainOwnedSegments">
    /// Segments whose LED count is owned by a product chain. Rule 2 is lifted for
    /// these: the chain writes the count and the partition together, so the
    /// drift that rule 2 exists to prevent cannot happen. Everything else,
    /// including the full-cover rule, still applies.
    /// </param>
    public static Result Validate(IReadOnlyList<StructureSegment> segments, IReadOnlyList<ZoneDef> zones,
        IReadOnlySet<int>? chainOwnedSegments)
    {
        var result = new Result();
        if (zones is null || zones.Count == 0)
        {
            result.Errors.Add("partition must contain at least one zone");
            return result;
        }

        // Per-segment cover bookkeeping for rule 1.
        var coverage = new List<(int start, int end)>[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            coverage[i] = new List<(int, int)>();
        }

        for (int z = 0; z < zones.Count; z++)
        {
            var zone = zones[z];
            var label = $"zone {z}";

            var name = zone.Name?.Trim() ?? "";
            if (name.Length == 0)
            {
                result.Errors.Add($"{label}: name must be non-empty");
            }
            else if (name.Length > MaxZoneNameLength)
            {
                result.Errors.Add($"{label}: name exceeds {MaxZoneNameLength} characters");
            }

            if (zone.Slices is null || zone.Slices.Count == 0)
            {
                result.Errors.Add($"{label}: must contain at least one slice");
                continue;
            }

            var touchesResizable = false;
            var slicesOk = true;
            foreach (var slice in zone.Slices)
            {
                if (slice.Segment < 0 || slice.Segment >= segments.Count)
                {
                    result.Errors.Add($"{label}: slice references unknown segment {slice.Segment}");
                    slicesOk = false;
                    continue;
                }
                var seg = segments[slice.Segment];
                if (slice.Start < 0 || slice.Count < 0 || slice.Start + slice.Count > seg.LedCount)
                {
                    result.Errors.Add($"{label}: slice [{slice.Start},{slice.Start + slice.Count}) exceeds segment {slice.Segment} bounds");
                    slicesOk = false;
                    continue;
                }
                // Zero-length slices are only meaningful as the whole of an
                // unconfigured resizable segment; elsewhere they are noise.
                if (slice.Count == 0 && !(seg.Resizable && seg.LedCount == 0))
                {
                    result.Errors.Add($"{label}: slice on segment {slice.Segment} has zero length");
                    slicesOk = false;
                    continue;
                }
                if (seg.Resizable)
                {
                    touchesResizable = true;
                }
                coverage[slice.Segment].Add((slice.Start, slice.Start + slice.Count));
            }
            if (!slicesOk)
            {
                continue;
            }

            // Rule 2: resizable walls, lifted for a segment a chain owns.
            var chainOwned = chainOwnedSegments is not null
                && zone.Slices.Count > 0
                && chainOwnedSegments.Contains(zone.Slices[0].Segment);
            if (touchesResizable && !chainOwned)
            {
                var whole = zone.Slices.Count == 1
                    && zone.Slices[0].Start == 0
                    && zone.Slices[0].Count == segments[zone.Slices[0].Segment].LedCount;
                if (!whole)
                {
                    result.Errors.Add($"{label}: a zone touching a resizable segment must be exactly that whole segment");
                    continue;
                }
            }

            // Rule 3: device-order contiguity for spanning zones.
            if (zone.Slices.Count > 1)
            {
                var prevEnd = -1;
                foreach (var slice in zone.Slices)
                {
                    var deviceStart = SegmentOffset(segments, slice.Segment) + slice.Start;
                    if (prevEnd >= 0 && deviceStart != prevEnd)
                    {
                        result.Errors.Add($"{label}: slices must be contiguous in device order");
                        break;
                    }
                    prevEnd = deviceStart + slice.Count;
                }
            }
        }

        // Rule 1: exact tiling of every segment.
        for (int s = 0; s < segments.Count; s++)
        {
            var seg = segments[s];
            var spans = coverage[s];
            spans.Sort((a, b) => a.start.CompareTo(b.start));
            var pos = 0;
            var tiled = true;
            foreach (var (start, end) in spans)
            {
                if (start != pos)
                {
                    tiled = false;
                    break;
                }
                pos = end;
            }
            if (tiled && pos != seg.LedCount)
            {
                tiled = false;
            }
            if (!tiled)
            {
                result.Errors.Add($"segment {s}: partition must tile the segment exactly (no gaps or overlaps)");
            }
        }

        return result;
    }

    /// <summary>Device-space offset (in effective counts) of a segment's first LED.</summary>
    internal static int SegmentOffset(IReadOnlyList<StructureSegment> segments, int segmentIndex)
    {
        var offset = 0;
        for (int i = 0; i < segmentIndex && i < segments.Count; i++)
        {
            offset += segments[i].LedCount;
        }
        return offset;
    }
}
