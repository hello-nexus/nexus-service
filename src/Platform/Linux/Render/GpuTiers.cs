using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nexus.Service.Platform.Linux.Render;

/// <summary>How the headless browser renders: Vulkan through ANGLE, EGL/GL through ANGLE, or the CPU.</summary>
public enum GpuTier
{
    Software,
    Vulkan,
    Egl,
}

public static class GpuTiers
{
    /// <summary>Tried in order; Software is the fallback that always works.</summary>
    internal static readonly GpuTier[] HardwareOrder = { GpuTier.Vulkan, GpuTier.Egl };

    private static readonly string[] SoftwareMarkers =
    {
        "swiftshader", "llvmpipe", "softpipe", "software", "subzero", "lavapipe",
    };

    internal static IReadOnlyList<string> Arguments(GpuTier tier) => tier switch
    {
        GpuTier.Vulkan => new[]
        {
            "--ignore-gpu-blocklist",
            "--enable-gpu-rasterization",
            "--use-angle=vulkan",
            "--enable-features=Vulkan,VulkanFromANGLE,DefaultANGLEVulkan",
            "--disable-vulkan-surface",
        },
        GpuTier.Egl => new[]
        {
            "--ignore-gpu-blocklist",
            "--enable-gpu-rasterization",
            "--use-gl=angle",
            "--use-angle=gl-egl",
        },
        _ => Array.Empty<string>(),
    };

    /// <summary>True when the WebGL renderer string is absent or names a CPU rasterizer.</summary>
    internal static bool IsSoftwareRenderer(string? renderer)
    {
        if (string.IsNullOrWhiteSpace(renderer) || renderer == "none")
        {
            return true;
        }
        foreach (var marker in SoftwareMarkers)
        {
            if (renderer.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// First hardware tier whose <paramref name="works"/> check passes, else Software. A tier that
    /// throws is skipped like one that fails, so a driver that kills the browser costs one tier.
    /// </summary>
    internal static async Task<GpuTier> SelectAsync(Func<GpuTier, Task<bool>> works, Action<string>? log = null)
    {
        foreach (var tier in HardwareOrder)
        {
            try
            {
                if (await works(tier).ConfigureAwait(false))
                {
                    return tier;
                }
                log?.Invoke($"{tier} did not give hardware rendering");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Invoke($"{tier} unavailable: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return GpuTier.Software;
    }
}
