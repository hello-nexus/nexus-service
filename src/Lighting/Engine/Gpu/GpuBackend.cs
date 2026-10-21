using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Which API creates the Windows lighting context. "angle" (OpenGL ES 3 on
/// Direct3D 11) is the default where the bundled ANGLE binaries exist; "wgl" stays
/// selectable as a fallback. Every caller that must agree on the
/// backend (the engine, the probe child, the selection) goes through
/// <see cref="Resolve"/> so they cannot disagree.
/// </summary>
internal static class GpuBackend
{
    public const string Angle = "angle";
    public const string Wgl = "wgl";

    /// <summary>Folder under the exe that holds libEGL and libGLESv2. Loaded by full path only.</summary>
    public static string AngleDirectory => Path.Combine(AppContext.BaseDirectory, "angle");

    /// <summary>ANGLE ships for win-x64 only, so arm64 and any build without the folder stays on WGL.</summary>
    public static bool AngleAvailable()
    {
#if WINDOWS
        try
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.X64
                && File.Exists(Path.Combine(AngleDirectory, "libEGL.dll"))
                && File.Exists(Path.Combine(AngleDirectory, "libGLESv2.dll"));
        }
        catch (Exception)
        {
            return false;
        }
#else
        return false;
#endif
    }

    /// <summary>The backend actually used for <paramref name="requested"/>: an explicit "wgl" is honored, anything else (including unset or a retired "glfw") is ANGLE when it is available and WGL otherwise.</summary>
    public static string Resolve(string? requested, bool angleAvailable)
    {
        var r = requested?.Trim();
        if (string.Equals(r, Wgl, StringComparison.OrdinalIgnoreCase))
        {
            return Wgl;
        }
        var resolved = angleAvailable ? Angle : Wgl;
        if (string.Equals(r, RetiredGlfw, StringComparison.OrdinalIgnoreCase)
            && Interlocked.Exchange(ref _retiredWarned, 1) == 0)
        {
            ServiceLog.Warn($"[gpu] the retired render backend \"{RetiredGlfw}\" is ignored; using {resolved}");
        }
        return resolved;
    }

    private const string RetiredGlfw = "glfw";
    private static int _retiredWarned;

    public static string Resolve(string? requested) => Resolve(requested, AngleAvailable());

    public static bool IsAngle(string? requested) =>
        string.Equals(Resolve(requested), Angle, StringComparison.Ordinal);

    /// <summary>The Lighting.RenderGpu value that renders shaders on the CPU (ANGLE WARP).</summary>
    public const string SoftwareChoice = "software";

    public static bool IsSoftwareChoice(string? renderGpu) =>
        string.Equals(renderGpu?.Trim(), SoftwareChoice, StringComparison.OrdinalIgnoreCase);

    /// <summary>Forced software rendering needs the ANGLE backend, since WARP is an ANGLE adapter.</summary>
    public static bool SoftwareRenderingAvailable(string? requestedBackend, bool angleAvailable) =>
        string.Equals(Resolve(requestedBackend, angleAvailable), Angle, StringComparison.Ordinal);

    /// <summary>True for this session when the user's "software" choice took effect at boot. Set once by the warmup.</summary>
    public static volatile bool SoftwareForced;
}
