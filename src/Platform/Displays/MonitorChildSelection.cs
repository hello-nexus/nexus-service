using System.Collections.Generic;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Which of an adapter's monitor children is the one it is driving. Windows
/// lists every plugged-in monitor devnode as a child of the GDI adapter,
/// including ones that are not on the desktop (a direct-mode VR headset, a
/// TV parked on another input), and index 0 is whichever PnP enumerated
/// first that boot, so reading child 0 unconditionally can stamp an idle
/// sibling's EDID onto every real monitor. Platform-neutral so the rule is
/// unit-testable off Windows.
/// </summary>
internal static class MonitorChildSelection
{
    internal const uint DisplayDeviceActive = 0x00000001;

    /// <summary>
    /// Index of the first child flagged ACTIVE; 0 when none is, so a driver
    /// that never sets the flag keeps working; -1 for no children.
    /// </summary>
    internal static int Pick(IReadOnlyList<uint> childStateFlags)
    {
        for (var i = 0; i < childStateFlags.Count; i++)
        {
            if ((childStateFlags[i] & DisplayDeviceActive) != 0) return i;
        }
        return childStateFlags.Count == 0 ? -1 : 0;
    }
}
