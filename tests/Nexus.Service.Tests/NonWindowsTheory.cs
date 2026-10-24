namespace Nexus.Service.Tests;

/// <summary>A <see cref="TheoryAttribute"/> that is skipped on Windows; see <see cref="NonWindowsFactAttribute"/>.</summary>
public sealed class NonWindowsTheoryAttribute : TheoryAttribute
{
    public NonWindowsTheoryAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix-only test (filesystem semantics or running-service file collision).";
    }
}
