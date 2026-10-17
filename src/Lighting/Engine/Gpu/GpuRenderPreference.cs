using System;
using System.Collections.Generic;
using Nexus.Service.Models.Sensors;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Applies the user's lighting render-GPU choice at the OS level. On Windows it
/// writes the per-app DirectX GPU preference; the graphics stack reads that value
/// when the process creates its GL/D3D device, so the choice only takes effect on
/// the next service start (restart-to-apply). Windows can only express the
/// integrated-vs-discrete class, not an arbitrary adapter, so a chosen name maps
/// to its <see cref="GpuReadout.Integrated"/> class. Linux honors the choice
/// directly in LinuxEglContext at GL init, so this is a no-op there; macOS is
/// single-GPU and ignores it.
/// </summary>
internal static class GpuRenderPreference
{
    public static void Apply(string renderGpu, IReadOnlyList<GpuReadout> gpus)
    {
#if WINDOWS
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return;
            }

            // LocalSystem service -> its own user hive is S-1-5-18.
            using var key = Registry.Users.CreateSubKey(
                @"S-1-5-18\Software\Microsoft\DirectX\UserGpuPreferences");
            if (key is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(renderGpu) || renderGpu == "auto" || GpuBackend.IsSoftwareChoice(renderGpu))
            {
                key.DeleteValue(exe, throwOnMissingValue: false);
                return;
            }

            // GpuPreference: 1 = power saving (integrated), 2 = high performance
            // (discrete). Default to discrete when the name no longer matches a
            // present GPU.
            int pref = 2;
            foreach (var g in gpus)
            {
                if (string.Equals(g.Name, renderGpu, StringComparison.OrdinalIgnoreCase))
                {
                    pref = g.Integrated ? 1 : 2;
                    break;
                }
            }
            key.SetValue(exe, $"GpuPreference={pref};", RegistryValueKind.String);
        }
        catch
        {
            // Best-effort: a registry failure must never break lighting.
        }
#else
        _ = renderGpu;
        _ = gpus;
#endif
    }
}
