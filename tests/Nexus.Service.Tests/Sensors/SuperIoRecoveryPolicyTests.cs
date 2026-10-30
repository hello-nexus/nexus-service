using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Sensors;

public class SuperIoRecoveryPolicyTests
{
    private const string Chip = "/lpc/nct6701d/0";
    private static readonly float?[] Dead = { 0f, 0f, 0f };
    private static readonly float?[] Full = { 100f, 100f };

    private static SuperIoDecision Stall(SuperIoRecoveryPolicy p, long now, string chip = Chip, bool? signature = true) => p.Observe(chip, SuperIoChipState.Stalled, signature, now, out _);

    [Fact]
    public void Classify_StalledNeedsZeroFansAndFullControls() =>
        Assert.Equal(SuperIoChipState.Stalled, SuperIoRecoveryPolicy.Classify(Dead, Full));

    [Fact]
    public void Classify_SpinningFanIsSane() =>
        Assert.Equal(SuperIoChipState.Sane, SuperIoRecoveryPolicy.Classify(new float?[] { 0f, 900f }, Full));

    [Fact]
    public void Classify_ControlBelowFullIsSane() =>
        Assert.Equal(SuperIoChipState.Sane, SuperIoRecoveryPolicy.Classify(Dead, new float?[] { 100f, 60f }));

    [Fact]
    public void Classify_UnreadControlIsUnknown() =>
        Assert.Equal(SuperIoChipState.Unknown, SuperIoRecoveryPolicy.Classify(Dead, new float?[] { 100f, null }));

    [Theory]
    [InlineData(0, 2)]
    [InlineData(3, 0)]
    public void Classify_NoFansOrControlsIsUnknown(int fans, int controls) =>
        Assert.Equal(SuperIoChipState.Unknown, SuperIoRecoveryPolicy.Classify(new float?[fans], new float?[controls].Select(_ => (float?)100f).ToArray()));

    [Fact]
    public void Idle_NeedsAFanToHaveSpun()
    {
        var p = new SuperIoRecoveryPolicy();
        Assert.Equal(SuperIoDecision.None, Stall(p, 0));
        p.Observe(Chip, SuperIoChipState.Sane, null, 1, out _);
        Assert.Equal(SuperIoDecision.Reopen, Stall(p, 2));
    }

    [Fact]
    public void Idle_RateLimitedAndCapped()
    {
        var p = new SuperIoRecoveryPolicy();
        p.Observe(Chip, SuperIoChipState.Sane, null, 0, out _);
        long t = 10;
        for (var i = 0; i < SuperIoRecoveryPolicy.MaxAttempts; i++)
        {
            Assert.Equal(SuperIoDecision.Reopen, Stall(p, t));
            p.CommitAttempt(Chip, t);
            if (i < SuperIoRecoveryPolicy.MaxAttempts - 1) Assert.Equal(SuperIoDecision.None, Stall(p, t + SuperIoRecoveryPolicy.IdleSpacingMs - 1));
            t += SuperIoRecoveryPolicy.IdleSpacingMs;
        }
        Assert.Equal(SuperIoDecision.GaveUp, Stall(p, t));
        Assert.Equal(SuperIoDecision.None, Stall(p, t + 1));
    }

    [Fact]
    public void Idle_SaneReadingRestoresTheBudget()
    {
        var p = new SuperIoRecoveryPolicy();
        p.Observe(Chip, SuperIoChipState.Sane, null, 0, out _);
        for (var i = 0; i < SuperIoRecoveryPolicy.MaxAttempts; i++) p.CommitAttempt(Chip, i);
        p.Observe(Chip, SuperIoChipState.Sane, null, 100, out _);
        Assert.Equal(SuperIoDecision.Reopen, Stall(p, SuperIoRecoveryPolicy.IdleSpacingMs + 1_000));
    }

    [Fact]
    public void Resume_StallAloneTriggersWithoutPriorSpin()
    {
        var p = new SuperIoRecoveryPolicy();
        p.NoteResume(1_000);
        Assert.Equal(SuperIoDecision.Reopen, Stall(p, 1_001));
    }

    [Fact]
    public void Resume_RetriesAtWindowSpacingThenGivesUp()
    {
        var p = new SuperIoRecoveryPolicy();
        p.NoteResume(0);
        long t = 1;
        for (var i = 0; i < SuperIoRecoveryPolicy.MaxAttempts; i++)
        {
            Assert.Equal(SuperIoDecision.Reopen, Stall(p, t));
            p.CommitAttempt(Chip, t);
            if (i < SuperIoRecoveryPolicy.MaxAttempts - 1) Assert.Equal(SuperIoDecision.None, Stall(p, t + SuperIoRecoveryPolicy.WindowSpacingMs - 1));
            t += SuperIoRecoveryPolicy.WindowSpacingMs;
        }
        Assert.Equal(SuperIoDecision.GaveUp, Stall(p, t));
    }

    [Fact]
    public void BusySingleFlightIsNotAnAttempt()
    {
        var p = new SuperIoRecoveryPolicy();
        p.NoteResume(0);
        for (var i = 0; i < 10; i++) Assert.Equal(SuperIoDecision.Reopen, Stall(p, 1 + i));
    }

    [Fact]
    public void WindowExpiryFallsBackToTheIdleRule()
    {
        var p = new SuperIoRecoveryPolicy();
        p.NoteResume(0);
        Assert.Equal(SuperIoDecision.None, Stall(p, SuperIoRecoveryPolicy.RecoveryWindowMs + 1));
    }

    [Fact]
    public void Suspect_SetOnlyWhileStalledAndEligible()
    {
        var p = new SuperIoRecoveryPolicy();
        p.Observe(Chip, SuperIoChipState.Stalled, null, 0, out var early);
        Assert.False(early);
        p.NoteResume(0);
        p.Observe(Chip, SuperIoChipState.Stalled, true, 1, out var inWindow);
        Assert.True(inWindow);
        p.Observe(Chip, SuperIoChipState.Unknown, null, 2, out var unknown);
        Assert.False(unknown);
    }

    [Theory]
    [InlineData(-1, 5, false, SuperIoWriteGate.Allowed)]
    [InlineData(5, 5, false, SuperIoWriteGate.Allowed)]
    [InlineData(4, 5, false, SuperIoWriteGate.Stale)]
    [InlineData(5, 5, true, SuperIoWriteGate.Suspect)]
    [InlineData(4, 5, true, SuperIoWriteGate.Stale)]
    public void CheckWrite_SeparatesStaleFromSuspect(int mapping, int current, bool suspect, SuperIoWriteGate expected) =>
        Assert.Equal(expected, SuperIoRecoveryPolicy.CheckWrite(mapping, current, suspect));

    [Fact]
    public void Budget_IsPerChipAndSaneReadingResetsOnlyThatChip()
    {
        var p = new SuperIoRecoveryPolicy();
        p.NoteResume(0);
        for (var i = 0; i < SuperIoRecoveryPolicy.MaxAttempts; i++) p.CommitAttempt("/lpc/a", i);
        p.Observe("/lpc/b", SuperIoChipState.Sane, null, 5, out _);
        Assert.Equal(SuperIoDecision.GaveUp, Stall(p, 10, "/lpc/a"));
        Assert.Equal(SuperIoDecision.Reopen, Stall(p, 10, "/lpc/b"));
    }

    [Fact]
    public void Budget_SpacingIsPerChip()
    {
        var p = new SuperIoRecoveryPolicy();
        p.NoteResume(0);
        p.CommitAttempt("/lpc/a", 1);
        Assert.Equal(SuperIoDecision.None, Stall(p, 2, "/lpc/a"));
        Assert.Equal(SuperIoDecision.Reopen, Stall(p, 2, "/lpc/b"));
    }

    [Fact]
    public void Signature_FalseNeverMarksSuspectOrReopens()
    {
        var p = new SuperIoRecoveryPolicy();
        p.NoteResume(0);
        p.Observe(Chip, SuperIoChipState.Sane, null, 0, out _);
        var d = p.Observe(Chip, SuperIoChipState.Stalled, false, 1, out var suspect);
        Assert.Equal(SuperIoDecision.None, d);
        Assert.False(suspect);
    }

    [Fact]
    public void Signature_UnknownInWindowNeedsAFanToHaveSpun()
    {
        var p = new SuperIoRecoveryPolicy();
        p.NoteResume(0);
        Assert.Equal(SuperIoDecision.None, Stall(p, 1, signature: null));
        p.Observe(Chip, SuperIoChipState.Sane, null, 2, out _);
        Assert.Equal(SuperIoDecision.Reopen, Stall(p, 3, signature: null));
    }

    [Fact]
    public void AllOnesSignature_NeedsMinusOneTempsAndRawMaxVoltages()
    {
        Assert.True(SuperIoRecoveryPolicy.HasAllOnesSignature(new float?[] { -1f, -1f }, new float?[] { 2.04f, 4.08f }));
        Assert.False(SuperIoRecoveryPolicy.HasAllOnesSignature(new float?[] { -1f, 38f }, new float?[] { 2.04f }));
        Assert.False(SuperIoRecoveryPolicy.HasAllOnesSignature(new float?[] { -1f }, new float?[] { 1.2f }));
        Assert.True(SuperIoRecoveryPolicy.HasAllOnesSignature(new float?[] { -1f }, new float?[0]));
        Assert.Null(SuperIoRecoveryPolicy.HasAllOnesSignature(new float?[0], new float?[0]));
        Assert.Null(SuperIoRecoveryPolicy.HasAllOnesSignature(new float?[] { null }, new float?[0]));
    }

    [Fact]
    public void Format_ListsEverySampleCompactly()
    {
        var text = SuperIoRecoveryPolicy.Format("/lpc/nct6701d/0", new[]
        {
            new SuperIoSample('T', 0, -1f),
            new SuperIoSample('V', 1, 2.04f),
            new SuperIoSample('F', 2, 0f),
            new SuperIoSample('C', 2, 100f),
            new SuperIoSample('F', 3, null),
        });
        Assert.Equal("/lpc/nct6701d/0 T0=-1 V1=2.04 F2=0 C2=100 F3=null", text);
    }
}
