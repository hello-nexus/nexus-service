using System;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.Nollie;

/// <summary>
/// Resolves a controller's standalone lighting from settings and pushes it:
/// the mode and colour the user chose, and the MOS bit the vendor driver
/// derives from the GPU Strimer cable (set for a dual 8-pin, clear for a
/// triple or an empty port).
/// </summary>
public static class NollieStandalone
{
    private const string GpuPortSlug = "strimer-gpu";
    private const int DualHarnessLanes = 4;

    /// <summary>The persisted choice, or a fresh default (a held black) for a board never configured.</summary>
    public static NollieStandaloneSettings For(NexusSettings settings, string deviceId)
        => settings.Devices.Nollie.Standalone.TryGetValue(deviceId, out var s) ? s : new NollieStandaloneSettings();

    /// <summary>Sends the current standalone settings before the first frame, at attach.</summary>
    public static bool Apply(NollieController controller, NexusSettings settings)
    {
        if (!Resolve(controller, settings, out var builtIn, out var mos, out var r, out var g, out var b)) return false;
        return controller.SendStandalone(builtIn, mos, r, g, b);
    }

    /// <summary>
    /// Re-sends the settings while frames are streaming, for a changed
    /// choice or a changed GPU harness. Only where the vendor driver does the
    /// same; elsewhere the choice is persisted and goes out at the hand-off.
    /// </summary>
    public static bool Refresh(NollieController controller, NexusSettings settings)
        => NollieProtocol.TakesStandaloneMidStream(controller.Spec) && Apply(controller, settings);

    /// <summary>Hands the strips to the firmware carrying the current standalone settings.</summary>
    public static bool Release(NollieController controller, NexusSettings settings)
    {
        if (!Resolve(controller, settings, out var builtIn, out var mos, out var r, out var g, out var b)) return false;
        return controller.Release(builtIn, mos, r, g, b);
    }

    private static bool Resolve(NollieController controller, NexusSettings settings,
        out bool builtIn, out bool mos, out byte r, out byte g, out byte b)
    {
        builtIn = false;
        mos = false;
        r = g = b = 0;
        if (!NollieProtocol.SupportsStandalone(controller.Spec)) return false;

        var chosen = For(settings, controller.DeviceId);
        builtIn = NollieProtocol.SupportsBuiltInEffect(controller.Spec)
            && string.Equals(chosen.Mode, NollieStandaloneSettings.ModeBuiltIn, StringComparison.Ordinal);
        if (!builtIn && !TryParseHexColor(chosen.Color, out r, out g, out b))
        {
            r = g = b = 0;
        }
        mos = GpuHarnessIsDual(controller, settings);
        return true;
    }

    /// <summary>A GPU port holding one to four lanes is the dual 8-pin harness.</summary>
    private static bool GpuHarnessIsDual(NollieController controller, NexusSettings settings)
    {
        foreach (var port in controller.Spec.Ports)
        {
            if (port.Slug != GpuPortSlug) continue;
            var total = NollieLightingDeviceProvider.DeclaredLedCount(
                settings.Devices.ZoneLedCounts, NollieLightingDeviceProvider.PortId(controller.DeviceId, port), port);
            return total > 0 && port.LaneLeds(total, DualHarnessLanes) == 0;
        }
        return false;
    }

    /// <summary>"#RRGGBB" or "RRGGBB"; anything else is rejected.</summary>
    public static bool TryParseHexColor(string? input, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrEmpty(input)) return false;
        var hex = input.StartsWith('#') ? input.AsSpan(1) : input.AsSpan();
        if (hex.Length != 6) return false;
        return byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out r)
            && byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out g)
            && byte.TryParse(hex[4..], System.Globalization.NumberStyles.HexNumber, null, out b);
    }
}
