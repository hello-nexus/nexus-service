using System;
using System.Collections.Generic;
using Nexus.Service.Sensors;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>What ANGLE is asked to render on: a DXGI adapter by LUID, or the WARP software rasterizer.</summary>
internal readonly record struct AngleTarget(string? Luid, bool Warp, string Description)
{
    public static AngleTarget WarpTarget { get; } = new(null, true, "WARP (software)");
}

/// <summary>
/// Maps the render-GPU choice onto a DXGI adapter. The OS-level GpuPreference
/// registry value cannot express an arbitrary adapter and ANGLE ignores it, so
/// the choice is resolved here and handed to ANGLE as a LUID.
/// </summary>
internal static class AngleAdapterSelector
{
    /// <summary>Class value for "let the OS default decide", the integrated card on a hybrid PC.</summary>
    public const int ClassIntegrated = 0;

    /// <summary>Class value for the high-performance card.</summary>
    public const int ClassDiscrete = 2;

    // The Basic Render Driver and indirect displays carry this vendor id; neither is a render target.
    private const uint MicrosoftVendorId = 0x1414;

    /// <summary>
    /// <paramref name="adapterClass"/> (the selection's probe order) wins when
    /// set. Otherwise "auto" is the integrated class and a name is matched
    /// against the adapter description, falling back to the discrete class for a
    /// name that no longer matches, the same default GpuRenderPreference uses.
    /// Null when no hardware adapter exists.
    /// </summary>
    public static AngleTarget? Choose(IReadOnlyList<GpuAdapterLuids.Adapter> adapters, string? renderGpu, int? adapterClass)
    {
        // "software" never resolves to hardware, whichever init path asks.
        if (GpuBackend.IsSoftwareChoice(renderGpu))
        {
            return AngleTarget.WarpTarget;
        }
        var usable = new List<GpuAdapterLuids.Adapter>(adapters.Count);
        foreach (var a in adapters)
        {
            if (a.VendorId != MicrosoftVendorId && a.Luid.Length > 0)
            {
                usable.Add(a);
            }
        }
        if (usable.Count == 0)
        {
            return null;
        }
        if (adapterClass is int cls)
        {
            return ByClass(usable, cls);
        }
        var name = renderGpu?.Trim() ?? "";
        if (name.Length == 0 || string.Equals(name, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return ByClass(usable, ClassIntegrated);
        }
        foreach (var a in usable)
        {
            if (string.Equals(a.Description.Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return Of(a);
            }
        }
        foreach (var a in usable)
        {
            var d = a.Description.Trim();
            if (d.Length > 0 && (name.Contains(d, StringComparison.OrdinalIgnoreCase)
                || d.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                return Of(a);
            }
        }
        return ByClass(usable, ClassDiscrete);
    }

    /// <summary>Integrated is the adapter with the least dedicated memory, discrete the most; the first in DXGI order breaks a tie, and a single adapter is both.</summary>
    private static AngleTarget ByClass(List<GpuAdapterLuids.Adapter> usable, int cls)
    {
        var pick = usable[0];
        foreach (var a in usable)
        {
            if (cls == ClassDiscrete ? a.DedicatedVramMb > pick.DedicatedVramMb : a.DedicatedVramMb < pick.DedicatedVramMb)
            {
                pick = a;
            }
        }
        return Of(pick);
    }

    private static AngleTarget Of(GpuAdapterLuids.Adapter a) => new(a.Luid, false, a.Description);

    /// <summary>"high:low" as GpuAdapterLuids formats it, to the two EGL attribute values.</summary>
    public static bool TryParseLuid(string? luid, out int high, out uint low)
    {
        high = 0;
        low = 0;
        var parts = luid?.Split(':');
        return parts is { Length: 2 }
            && int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out high)
            && uint.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out low);
    }
}
