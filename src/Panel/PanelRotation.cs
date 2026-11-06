using System;
using System.Collections.Generic;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Panel;

/// <summary>
/// A panel record's mount rotation: clockwise degrees, one of 0/90/180/270. Which of those a
/// glass can take is derived from its capabilities and never stored.
/// </summary>
public static class PanelRotation
{
    private static readonly int[] None = [0];
    private static readonly int[] HalfTurn = [0, 180];
    private static readonly int[] Quarter = [0, 90, 180, 270];

    /// <summary>
    /// Rotations the record's glass takes. Pushed-frame glass that is square turns a quarter at the
    /// same size; other pushed-frame glass takes the half turn only. The 8.8 inch strip maps its
    /// quarter turns onto its firmware portrait mode. A surface that does not run through an
    /// orientation filter (kiosk displays, phones, the Kraken) takes none.
    /// </summary>
    public static IReadOnlyList<int> SupportedFor(PanelDeviceCapabilities? caps)
    {
        if (caps is null)
        {
            return None;
        }
        if (caps.SupportsPortrait == true)
        {
            return Quarter;
        }
        // The raw value: a Kraken record keeps its legacy surface and its transport never turns frames.
        if (string.Equals(caps.Surface, PanelSurfaces.LegacyKraken, StringComparison.Ordinal))
        {
            return None;
        }
        if (caps.Surface is not (PanelSurfaces.LcdRound or PanelSurfaces.LcdSquare or PanelSurfaces.LcdWide))
        {
            return None;
        }
        return caps.CssWidth is > 0 && caps.CssWidth == caps.CssHeight ? Quarter : HalfTurn;
    }

    /// <summary>True when the rotation is one of the four mount turns.</summary>
    public static bool IsValid(int rotation) => rotation is 0 or 90 or 180 or 270;

    /// <summary>The glass runs tall: the 8.8 inch strip's firmware portrait mode follows 90 and 270.</summary>
    public static bool IsPortrait(int rotation) => rotation is 90 or 270;

    /// <summary>The quarter turns clockwise that rotation names, 0 to 3.</summary>
    public static int QuarterTurns(int rotation) => (rotation / 90) & 3;

    /// <summary>The rotation a stored legacy Flip180/Portrait pair produced, 0 when it produced none.</summary>
    public static int FromLegacy(bool? flip180, bool? portrait, PanelDeviceCapabilities? caps)
    {
        var portraitOn = portrait == true && caps?.SupportsPortrait == true;
        var rotation = portraitOn ? (flip180 == true ? 270 : 90) : (flip180 == true ? 180 : 0);
        return Contains(SupportedFor(caps), rotation) ? rotation : 0;
    }

    /// <summary>The rotation a stored record asks for, folding in legacy fields a pre-rotation build wrote.</summary>
    public static int Effective(PanelDeviceRecord record)
    {
        if (record.Rotation is { } stored)
        {
            return IsValid(stored) ? stored : 0;
        }
        return FromLegacy(record.Flip180, record.Portrait, record.Capabilities);
    }

    /// <summary>
    /// Resolves a patch's rotation request against the record. An explicit <c>rotation</c> wins; the
    /// legacy <c>flip180</c> and <c>portrait</c> aliases change one component and keep the other.
    /// Returns false when the request is not one of the record's supported rotations.
    /// </summary>
    public static bool TryResolve(PanelDeviceRecord current, PanelDevicePatch patch, out int? rotation)
    {
        rotation = null;
        if (patch.Rotation is null && patch.Flip180 is null && patch.Portrait is null)
        {
            return true;
        }
        int target;
        if (patch.Rotation is { } requested)
        {
            target = requested;
        }
        else
        {
            var now = Effective(current);
            var flip = patch.Flip180 ?? (now >= 180);
            var portrait = current.Capabilities?.SupportsPortrait == true && (patch.Portrait ?? IsPortrait(now));
            target = portrait ? (flip ? 270 : 90) : (flip ? 180 : 0);
        }
        if (!Contains(SupportedFor(current.Capabilities), target))
        {
            return false;
        }
        rotation = target;
        return true;
    }

    private static bool Contains(IReadOnlyList<int> list, int value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return true;
            }
        }
        return false;
    }
}
