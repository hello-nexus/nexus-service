using System.Collections.Generic;

namespace Nexus.Service.Cooling;

/// <summary>
/// Detects a Super I/O whose hardware-monitor window stopped decoding. Another vendor service
/// can leave the window dead, every register then reads all ones, and the library reports every
/// control at full duty and every tach at zero. That reading is not a stalled fan.
/// </summary>
internal static class SuperIoReadGuard
{
    internal const float FullDutyFloor = 99.5f;

    /// <summary>True when every control reads full duty, every fan reads zero, the chip has several controls, and a fan on it has spun before.</summary>
    internal static bool IsAllOnesRead(IReadOnlyList<float?> controls, IReadOnlyList<float?> fans, bool fanSpunBefore)
    {
        if (!fanSpunBefore || controls.Count < 2 || fans.Count == 0)
        {
            return false;
        }
        foreach (var c in controls)
        {
            if (c is not { } duty || duty < FullDutyFloor)
            {
                return false;
            }
        }
        foreach (var f in fans)
        {
            if ((f ?? 0f) != 0f)
            {
                return false;
            }
        }
        return true;
    }
}
