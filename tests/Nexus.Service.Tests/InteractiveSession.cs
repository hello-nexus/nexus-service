namespace Nexus.Service.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> skipped on Windows when the process has no
/// interactive desktop (an SSH or service-launched run). The code under test
/// refuses to inject input or touch the clipboard there by design.
/// </summary>
public sealed class InteractiveSessionFactAttribute : FactAttribute
{
    public InteractiveSessionFactAttribute()
    {
        if (OperatingSystem.IsWindows() && !Environment.UserInteractive)
            Skip = "Needs an interactive Windows session.";
    }
}

/// <summary>A <see cref="TheoryAttribute"/> counterpart of <see cref="InteractiveSessionFactAttribute"/>.</summary>
public sealed class InteractiveSessionTheoryAttribute : TheoryAttribute
{
    public InteractiveSessionTheoryAttribute()
    {
        if (OperatingSystem.IsWindows() && !Environment.UserInteractive)
            Skip = "Needs an interactive Windows session.";
    }
}
