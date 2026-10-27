using System.IO;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public sealed class StreamFpsCapTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(20)]
    [InlineData(15)]
    [InlineData(10)]
    [InlineData(5)]
    [InlineData(1)]
    [InlineData(null)]
    public void Allowed_values_are_valid(int? cap) => Assert.True(StreamFpsCap.IsValid(cap));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(25)]
    [InlineData(60)]
    public void Other_values_are_invalid(int cap) => Assert.False(StreamFpsCap.IsValid(cap));

    [Theory]
    [InlineData(60, 30, 30)]
    [InlineData(20, 30, 20)]
    [InlineData(15, 15, 15)]
    [InlineData(60, null, 60)]
    public void Effective_never_raises_the_model_rate(int model, int? cap, int expected) =>
        Assert.Equal(expected, StreamFpsCap.Effective(model, cap));

    [Fact]
    public void Setting_defaults_to_30_and_round_trips_through_the_store()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexus-fpscap-" + System.Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var store = new JsonConfigStore(path))
            {
                Assert.Equal(30, store.Load().StreamedPanelFpsCap);
                store.Update(s => s.StreamedPanelFpsCap = 10);
                store.FlushNow();
            }
            using (var store = new JsonConfigStore(path))
            {
                Assert.Equal(10, store.Load().StreamedPanelFpsCap);
                store.Update(s => s.StreamedPanelFpsCap = null);
                store.FlushNow();
            }
            using (var store = new JsonConfigStore(path))
            {
                Assert.Null(store.Load().StreamedPanelFpsCap);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
