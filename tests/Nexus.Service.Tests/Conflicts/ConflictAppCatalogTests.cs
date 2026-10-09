using Nexus.Service.Conflicts;
using Nexus.Service.Devices.Handlers;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

public class ConflictAppCatalogTests
{
    [Fact]
    public void FindById_ElgatoStreamDeck_ResolvesWithExpectedFields()
    {
        var def = ConflictWatcher.FindById("elgato-stream-deck");

        Assert.NotNull(def);
        Assert.Equal(StreamDeckHandler.ElgatoConflictAppId, def!.Id);
        Assert.Equal("Elgato Stream Deck", def.DisplayName);
        Assert.Equal("peripherals", def.Category);
        Assert.Contains("StreamDeck", def.ProcessNames);
        Assert.Contains("Stream Deck", def.ProcessNames);
        Assert.Empty(def.WindowsServiceNames);
    }

    [Fact]
    public void FindById_IsCaseInsensitive()
    {
        Assert.NotNull(ConflictWatcher.FindById("ELGATO-STREAM-DECK"));
    }
}
