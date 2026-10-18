namespace Nexus.Service.Tests;

/// <summary>A <see cref="FactAttribute"/> skipped on Windows and under root, for tests that rely on file mode bits denying a read.</summary>
public sealed class UnprivilegedUnixFactAttribute : FactAttribute
{
    public UnprivilegedUnixFactAttribute()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            Skip = "Needs file mode bits to deny access: not applicable on Windows or as root.";
    }
}
