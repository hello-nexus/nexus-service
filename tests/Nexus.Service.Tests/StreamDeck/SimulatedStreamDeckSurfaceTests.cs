using Nexus.Service.Peripherals.StreamDeck;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>Behavior specific to the in-memory simulator (poke/peek test hooks, not part of the shared contract).</summary>
public class SimulatedStreamDeckSurfaceTests
{
    private static SimulatedStreamDeckSurface Mini() =>
        new(StreamDeckModels.ByProductId(0x0063)!, "sim-0001");

    [Fact]
    public void Poke_QueuesASnapshotForReadInput()
    {
        var deck = Mini();
        deck.Poke(2, true);

        var states = deck.ReadInput(0)?.Keys;

        Assert.NotNull(states);
        Assert.Equal(new[] { false, false, true, false, false, false }, states);
    }

    [Fact]
    public void ReadInput_ReturnsNullWhenNothingChangedSincePreviousRead()
    {
        var deck = Mini();
        deck.Poke(0, true);
        Assert.NotNull(deck.ReadInput(0)); // consumes the pending snapshot

        Assert.Null(deck.ReadInput(0));
    }

    [Fact]
    public void Poke_TwoTransitionsBeforeAnyRead_BothDeliveredInOrder()
    {
        var deck = Mini();
        deck.Poke(0, true);
        deck.Poke(0, false);

        var first = deck.ReadInput(0)?.Keys;
        Assert.NotNull(first);
        Assert.True(first![0]);

        var second = deck.ReadInput(0)?.Keys;
        Assert.NotNull(second);
        Assert.False(second![0]);

        Assert.Null(deck.ReadInput(0));
    }

    [Fact]
    public void Poke_ReleaseThenReadInput_ReflectsRelease()
    {
        var deck = Mini();
        deck.Poke(1, true);
        deck.ReadInput(0);

        deck.Poke(1, false);
        var states = deck.ReadInput(0)?.Keys;

        Assert.NotNull(states);
        Assert.False(states![1]);
    }

    [Fact]
    public void Poke_OutOfRangeIndex_IsIgnored()
    {
        var deck = Mini();
        deck.Poke(99, true);
        Assert.Null(deck.ReadInput(0));
    }

    [Fact]
    public void SetKeyImage_ThenPeek_ReturnsPushedBytes()
    {
        var deck = Mini();
        var bytes = new byte[] { 9, 8, 7 };

        Assert.True(deck.SetKeyImage(3, bytes));

        Assert.Equal(bytes, deck.PeekKeyImage(3));
    }

    [Fact]
    public void ClearKey_RemovesPushedImage()
    {
        var deck = Mini();
        deck.SetKeyImage(0, new byte[] { 1, 2, 3 });

        Assert.True(deck.ClearKey(0));

        Assert.Null(deck.PeekKeyImage(0));
    }

    [Fact]
    public void Reset_ClearsAllKeyImagesAndRestoresDefaultBrightness()
    {
        var deck = Mini();
        deck.SetKeyImage(0, new byte[] { 1 });
        deck.SetBrightness(10);

        Assert.True(deck.Reset());

        Assert.Null(deck.PeekKeyImage(0));
        Assert.Equal(100, deck.Brightness);
        Assert.Equal(1, deck.ResetCount);
    }

    [Fact]
    public void SetBrightness_ClampsAndStoresValue()
    {
        var deck = Mini();
        deck.SetBrightness(150);
        Assert.Equal(100, deck.Brightness);

        deck.SetBrightness(-10);
        Assert.Equal(0, deck.Brightness);
    }

    [Fact]
    public void SimulateDisconnect_FailsEverySubsequentOperation()
    {
        var deck = Mini();
        deck.SimulateDisconnect();

        Assert.False(deck.IsConnected);
        Assert.False(deck.SetBrightness(50));
        Assert.False(deck.SetKeyImage(0, new byte[] { 1 }));
        Assert.False(deck.ClearKey(0));
        Assert.False(deck.Reset());
        Assert.Null(deck.ReadInput(0));
    }
}
