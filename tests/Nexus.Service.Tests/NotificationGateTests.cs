using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Service.Notifications;
using Xunit;

namespace Nexus.Service.Tests;

// Other tests (onboarding and focus mode routes) drive the same static gate, so this
// collection runs with nothing else in flight.
[CollectionDefinition("NotificationGate", DisableParallelization = true)]
public class NotificationGateCollection;

[Collection("NotificationGate")]
public class NotificationGateTests
{
    [Fact]
    public async Task HeldNotificationsGoOutInOrderWhenOnboardingEnds()
    {
        NotificationGate.ResetForTests();
        var sent = new List<string>();
        NotificationGate.TryHold(() => { sent.Add("a"); return Task.CompletedTask; }, out _);
        NotificationGate.TryHold(() => { sent.Add("b"); return Task.CompletedTask; }, out _);
        Assert.Empty(sent);

        await NotificationGate.ReleaseAsync(NotificationGate.ReasonOnboarding);
        Assert.Equal(new[] { "a", "b" }, sent);
    }

    [Fact]
    public void TryHold_ReportsWhetherItHeld()
    {
        NotificationGate.ResetForTests();
        Assert.True(NotificationGate.TryHold(() => Task.CompletedTask, out _));
        NotificationGate.Initialize(onboardingComplete: true);
        Assert.False(NotificationGate.TryHold(() => Task.CompletedTask, out _));
    }

    [Fact]
    public async Task AnOpenGateNeitherHoldsNorSends()
    {
        // The caller sends when this returns false, so sending here too would
        // deliver every notification twice on any completed install.
        NotificationGate.ResetForTests();
        NotificationGate.Initialize(onboardingComplete: true);
        var sends = 0;
        var held = NotificationGate.TryHold(() => { sends++; return Task.CompletedTask; }, out var task);
        await task;

        Assert.False(held);
        Assert.Equal(0, sends);
        Assert.Equal(0, NotificationGate.HeldCount);

        // And nothing was queued for a later release either.
        await NotificationGate.ReleaseAsync(NotificationGate.ReasonOnboarding);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task AThrowingNoticeDoesNotStrandTheRest()
    {
        NotificationGate.ResetForTests();
        var sent = new List<string>();
        NotificationGate.TryHold(() => throw new System.InvalidOperationException("boom"), out _);
        NotificationGate.TryHold(() => { sent.Add("after"); return Task.CompletedTask; }, out _);

        await NotificationGate.ReleaseAsync(NotificationGate.ReasonOnboarding);
        Assert.Equal(new[] { "after" }, sent);
    }

    [Fact]
    public async Task ReleaseIsIdempotent()
    {
        NotificationGate.ResetForTests();
        var count = 0;
        NotificationGate.TryHold(() => { count++; return Task.CompletedTask; }, out _);
        await NotificationGate.ReleaseAsync(NotificationGate.ReasonOnboarding);
        await NotificationGate.ReleaseAsync(NotificationGate.ReasonOnboarding);
        Assert.Equal(1, count);
    }

    [Fact]
    public void AResetClosesTheGateAgainForAReplayedOnboarding()
    {
        NotificationGate.ResetForTests();
        NotificationGate.Initialize(onboardingComplete: true);
        Assert.True(NotificationGate.IsOpen);

        // What POST /onboarding/reset does.
        NotificationGate.Initialize(onboardingComplete: false);
        Assert.False(NotificationGate.IsOpen);
        Assert.True(NotificationGate.TryHold(() => Task.CompletedTask, out _));
    }

    [Fact]
    public async Task TwoReasonsBothHaveToClearBeforeAnythingGoesOut()
    {
        NotificationGate.ResetForTests();
        NotificationGate.Hold(NotificationGate.ReasonFocusMode);
        var sent = 0;
        NotificationGate.TryHold(() => { sent++; return Task.CompletedTask; }, out _);

        await NotificationGate.ReleaseAsync(NotificationGate.ReasonOnboarding);
        Assert.Equal(0, sent);
        Assert.False(NotificationGate.IsOpen);

        await NotificationGate.ReleaseAsync(NotificationGate.ReasonFocusMode);
        Assert.Equal(1, sent);
        Assert.True(NotificationGate.IsOpen);
    }

    [Fact]
    public async Task FocusModeHoldsOnAnAlreadyOnboardedInstall()
    {
        NotificationGate.ResetForTests();
        NotificationGate.Initialize(onboardingComplete: true);
        NotificationGate.Hold(NotificationGate.ReasonFocusMode);

        var sent = 0;
        Assert.True(NotificationGate.TryHold(() => { sent++; return Task.CompletedTask; }, out _));
        Assert.Equal(0, sent);

        await NotificationGate.ReleaseAsync(NotificationGate.ReasonFocusMode);
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task ReleasingAReasonThatWasNeverHeldDoesNotFlush()
    {
        NotificationGate.ResetForTests();
        NotificationGate.Initialize(onboardingComplete: true);
        NotificationGate.Hold(NotificationGate.ReasonFocusMode);
        var sent = 0;
        NotificationGate.TryHold(() => { sent++; return Task.CompletedTask; }, out _);

        await NotificationGate.ReleaseAsync(NotificationGate.ReasonOnboarding);
        Assert.Equal(0, sent);
    }

    [Fact]
    public void TheBacklogIsBounded()
    {
        NotificationGate.ResetForTests();
        for (var i = 0; i < 40; i++)
        {
            NotificationGate.TryHold(() => Task.CompletedTask, out _);
        }
        Assert.Equal(16, NotificationGate.HeldCount);
    }
}
