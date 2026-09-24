using Nexus.Service.Conflicts;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

/// <summary>
/// The startup shutdown terminates other vendors' apps, so when it may run is
/// load-bearing: on by default, but never before onboarding has shown the
/// user the conflict step.
/// </summary>
public class ConflictStartupShutdownSettingsTests
{
    private static NexusSettings Onboarded(bool switchOn = true) => new()
    {
        OnboardingCompleted = true,
        FeaturesOnboardingCompleted = true,
        LightingOnboardingCompleted = true,
        Ui = { AutoKillConflictsAtStartup = switchOn },
    };

    [Fact]
    public void TheStartupShutdownIsOnByDefault()
    {
        Assert.True(new UiSettings().AutoKillConflictsAtStartup);
    }

    [Fact]
    public void AFreshInstallDoesNotSweepBeforeOnboardingHasRun()
    {
        Assert.False(ConflictStartupShutdown.SweepAllowed(new NexusSettings()));
    }

    [Fact]
    public void SweepsOnceEveryOnboardingStepIsDone()
    {
        Assert.True(ConflictStartupShutdown.SweepAllowed(Onboarded()));
    }

    [Fact]
    public void AnUnfinishedSequenceStillHoldsTheSweep()
    {
        var settings = Onboarded();
        settings.LightingOnboardingCompleted = false;

        Assert.False(ConflictStartupShutdown.SweepAllowed(settings));
    }

    [Fact]
    public void TheSwitchOffWinsOverOnboarding()
    {
        Assert.False(ConflictStartupShutdown.SweepAllowed(Onboarded(switchOn: false)));
    }

    [Fact]
    public void NoAppIsExcludedByDefault()
    {
        // Empty, never null: the client merges an absent field as "keep the
        // value the last profile had", so a profile that never excluded
        // anything must still send an empty list rather than nothing.
        Assert.Empty(new UiSettings().ConflictAutoKillExclusions);
    }
}
