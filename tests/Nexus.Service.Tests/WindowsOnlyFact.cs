namespace Nexus.Service.Tests;

/// <summary>A <see cref="FactAttribute"/> that only runs on Windows; reported as skipped elsewhere.</summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only test (Win32 job objects).";
    }
}
