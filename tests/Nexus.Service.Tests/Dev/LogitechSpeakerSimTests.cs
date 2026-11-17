#if DEV_TOOLS
using Nexus.Service.Dev;
using Nexus.Service.Peripherals.LogitechAudio;
using Xunit;

namespace Nexus.Service.Tests.Dev;

public class LogitechSpeakerSimTests
{
    private sealed class Inner : ILogitechSpeakerApo, ILogitechSpeakerBass
    {
        public int Generation => 3;
        public IReadOnlyList<LogitechSpeakerEndpoint> FindEndpoints() => new[] { new LogitechSpeakerEndpoint("real", "Real", "G560") };
        public LogitechSpeakerApoValues Read(string endpointId) => new() { SelectedEq = 7 };
        public bool Write(string endpointId, LogitechSpeakerApoChange change) => true;
        public float? GetVolume(string endpointId) => 0.9f;
        public bool SetVolume(string endpointId, float level) => true;
        public bool PlayTestSound(string endpointId, int channel) => true;
        public bool IsPresent() => true;
        public int? GetLevel() => 11;
        public bool SetLevel(int level) => true;
    }

    [Fact]
    public void Inactive_HandsEverythingToTheRealImplementation()
    {
        var inner = new Inner();
        var sim = new LogitechSpeakerSim(inner, inner);

        Assert.Equal("real", Assert.Single(sim.FindEndpoints()).Id);
        Assert.Equal(7, sim.Read("real").SelectedEq);
        Assert.Equal(0.9f, sim.GetVolume("real"));
        Assert.Equal(11, sim.GetLevel());
        Assert.Equal(3, sim.Generation);
    }

    [Fact]
    public void WithoutARealImplementation_InactiveIsAbsent()
    {
        var sim = new LogitechSpeakerSim(null, null);

        Assert.Empty(sim.FindEndpoints());
        Assert.False(sim.IsPresent());
        Assert.False(sim.Write("x", new LogitechSpeakerApoChange()));
        Assert.Null(sim.GetLevel());
    }

    [Fact]
    public void Active_HoldsClampedStateAndLeavesTheRealOneAlone()
    {
        var sim = new LogitechSpeakerSim(new Inner(), new Inner());
        sim.Start();
        var endpoint = Assert.Single(sim.FindEndpoints());

        Assert.True(sim.Write(endpoint.Id, new LogitechSpeakerApoChange
        {
            AdvancedBands = new float[] { 99, -99, 0, 0, 0, 0, 0, 0, 0, 0 },
            ChannelGains = new float[] { 9, 0, 1, 1, 1, 1, 1, 1 },
            DtsOn = true,
        }));

        var values = sim.Read(endpoint.Id);
        Assert.Equal(LogitechSpeakerProtocol.BandMaxDb, values.AdvancedBands![0]);
        Assert.Equal(LogitechSpeakerProtocol.BandMinDb, values.AdvancedBands[1]);
        Assert.Equal(LogitechSpeakerProtocol.ChannelMaxGain, values.ChannelGains![0]);
        Assert.True(values.DtsOn);
        Assert.NotEqual(3, sim.Generation);

        Assert.True(sim.PlayTestSound(endpoint.Id, 4));
        Assert.False(sim.PlayTestSound(endpoint.Id, 8));
        Assert.Equal(new LogitechSpeakerSimDto { Active = true, TestsPlayed = 1, LastTestChannel = 4 }, sim.Get(), new SimDtoComparer());

        sim.Stop();
        Assert.Equal("real", Assert.Single(sim.FindEndpoints()).Id);
    }

    private sealed class SimDtoComparer : IEqualityComparer<LogitechSpeakerSimDto>
    {
        public bool Equals(LogitechSpeakerSimDto? a, LogitechSpeakerSimDto? b) =>
            a is not null && b is not null && a.Active == b.Active && a.TestsPlayed == b.TestsPlayed && a.LastTestChannel == b.LastTestChannel;

        public int GetHashCode(LogitechSpeakerSimDto d) => d.TestsPlayed;
    }
}
#endif
