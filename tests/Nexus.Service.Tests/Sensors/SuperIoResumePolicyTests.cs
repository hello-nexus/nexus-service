using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class SuperIoResumePolicyTests
{
    private static readonly TimeSpan[] Offsets = SuperIoResumePolicy.CheckOffsets;

    // deadScript: what the window reads at each check; reopenScript: whether each reopen recovers. Last entry repeats.
    private static async Task<(SuperIoResumePolicy.Outcome Outcome, List<string> Log, List<double> Waits, int Reopens)> Run(
        bool[] deadScript, bool[] reopenScript, Func<int, bool>? superseded = null)
    {
        var log = new List<string>();
        var waits = new List<double>();
        var checks = 0;
        var reopens = 0;
        var outcome = await SuperIoResumePolicy.RunAsync(
            Offsets,
            d => { waits.Add(d.TotalSeconds); return Task.CompletedTask; },
            () => superseded?.Invoke(waits.Count) ?? false,
            () => deadScript[Math.Min(checks++, deadScript.Length - 1)],
            () => reopenScript[Math.Min(reopens++, reopenScript.Length - 1)],
            log.Add);
        return (outcome, log, waits, reopens);
    }

    [Fact]
    public async Task HealthyAtEveryCheck_NeverReopens()
    {
        var r = await Run(new[] { false }, new[] { true });
        Assert.Equal(SuperIoResumePolicy.Outcome.Healthy, r.Outcome);
        Assert.Equal(0, r.Reopens);
        Assert.Equal(3, r.Log.Count);
    }

    [Fact]
    public async Task ChecksAreSpacedByTheOffsetsFromResume()
    {
        var r = await Run(new[] { false }, new[] { true });
        Assert.Equal(new[] { 10d, 20d, 30d }, r.Waits);
    }

    [Fact]
    public async Task DeadThenRecovered_StopsAtTheFirstSuccess()
    {
        var r = await Run(new[] { true }, new[] { true });
        Assert.Equal(SuperIoResumePolicy.Outcome.Recovered, r.Outcome);
        Assert.Equal(1, r.Reopens);
        Assert.Single(r.Waits);
        Assert.Contains(r.Log, l => l.Contains("recovered"));
    }

    [Fact]
    public async Task FirstReopenFails_RetriesAtTheNextOffset()
    {
        var r = await Run(new[] { true }, new[] { false, true });
        Assert.Equal(SuperIoResumePolicy.Outcome.Recovered, r.Outcome);
        Assert.Equal(2, r.Reopens);
        Assert.Equal(new[] { 10d, 20d }, r.Waits);
    }

    [Fact]
    public async Task EveryReopenFails_GivesUpAfterTheLastOffset()
    {
        var r = await Run(new[] { true }, new[] { false });
        Assert.Equal(SuperIoResumePolicy.Outcome.GaveUp, r.Outcome);
        Assert.Equal(Offsets.Length, r.Reopens);
        Assert.Equal(Offsets.Length * 2, r.Log.Count);
    }

    [Fact]
    public async Task WindowDiesAfterAHealthyCheck_ReopensThen()
    {
        var r = await Run(new[] { false, true }, new[] { true });
        Assert.Equal(SuperIoResumePolicy.Outcome.Recovered, r.Outcome);
        Assert.Equal(1, r.Reopens);
        Assert.Equal(new[] { 10d, 20d }, r.Waits);
    }

    [Fact]
    public async Task ANewerResume_StopsTheOlderRunBeforeItReopens()
    {
        var r = await Run(new[] { true }, new[] { true }, superseded: waited => waited >= 1);
        Assert.Equal(SuperIoResumePolicy.Outcome.Superseded, r.Outcome);
        Assert.Equal(0, r.Reopens);
        Assert.Empty(r.Log);
    }

    [Theory]
    [InlineData("AsusFanControlService", true)]
    [InlineData("ArmouryCrate.UserSessionHelper", true)]
    [InlineData("Aac3572MbHal_x86", true)]
    [InlineData("LightingService", false)]
    [InlineData("explorer", false)]
    public void IsAsusRelatedProcess_MatchesAsusArmouryAndAacNames(string name, bool expected)
    {
        Assert.Equal(expected, SuperIoResumePolicy.IsAsusRelatedProcess(name));
    }
}
