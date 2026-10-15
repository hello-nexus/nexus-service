using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Which API creates the Windows lighting context. "angle" (OpenGL ES 3 on
/// Direct3D 11) is the default where the bundled ANGLE binaries exist; "wgl" and
/// "glfw" stay selectable as fallbacks. Every caller that must agree on the
/// backend (the engine, the probe child, the selection) goes through
/// <see cref="Resolve"/> so they cannot disagree.
/// </summary>
internal static class GpuBackend
{
    public const string Angle = "angle";
    public const string Wgl = "wgl";
    public const string Glfw = "glfw";

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

    /// <summary>The backend actually used for <paramref name="requested"/>: an explicit "wgl" or "glfw" is honored, anything else (including unset) is ANGLE when it is available and WGL otherwise.</summary>
    public static string Resolve(string? requested, bool angleAvailable)
    {
        var r = requested?.Trim();
        // Customer test build on the v3.1.2-beta.1 schema: the stored "wgl" was the old default, so it means ANGLE here.
        if (string.Equals(r, Wgl, StringComparison.OrdinalIgnoreCase) && !angleAvailable)
        {
            return Wgl;
        }
        if (string.Equals(r, Glfw, StringComparison.OrdinalIgnoreCase))
        {
            return Glfw;
        }
        return angleAvailable ? Angle : Wgl;
    }

    public static string Resolve(string? requested) => Resolve(requested, AngleAvailable());

    public static bool IsAngle(string? requested) =>
        string.Equals(Resolve(requested), Angle, StringComparison.Ordinal);
}
