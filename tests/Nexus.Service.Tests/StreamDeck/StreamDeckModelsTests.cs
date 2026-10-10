using System.Linq;
using Nexus.Service.Peripherals.StreamDeck;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// Sanity checks over the button-only capability table
/// (14 button-only models), cross-verified against the MIT elgato-streamdeck
/// crate's src/info.rs (fetched 2026-07-10).
/// </summary>
public class StreamDeckModelsTests
{
    [Fact]
    public void All_HasEighteenModels()
    {
        Assert.Equal(18, StreamDeckModels.All.Count);
    }

    [Fact]
    public void All_ProductIdsAreUnique()
    {
        var dups = StreamDeckModels.All.GroupBy(m => m.ProductId).Where(g => g.Count() > 1).ToList();
        Assert.Empty(dups);
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void Layout_RowsTimesColumns_MatchesKeyCount(StreamDeckModel model)
    {
        Assert.Equal(model.KeyCount, model.Rows * model.Columns);
    }

    [Fact]
    public void OnlyMiniIsVerified()
    {
        var verified = StreamDeckModels.All.Where(m => m.Verified).Select(m => m.ProductId).ToList();
        Assert.Equal(new[] { 0x0063 }, verified);
    }

    [Fact]
    public void ByProductId_FindsMini()
    {
        var model = StreamDeckModels.ByProductId(0x0063);
        Assert.NotNull(model);
        Assert.Equal("Mini", model!.Name);
    }

    [Fact]
    public void DisplayName_PrefixesTheBrand()
    {
        Assert.Equal("Elgato Stream Deck Mini", StreamDeckModels.ByProductId(0x0063)!.DisplayName);
        Assert.Equal("Elgato Stream Deck +", StreamDeckModels.ByProductId(0x0084)!.DisplayName);
        Assert.Equal("Elgato Stream Deck + XL", StreamDeckModels.ByProductId(0x00c6)!.DisplayName);
        Assert.Equal("Corsair Galleon K100 SD", StreamDeckModels.ByProductId(0x2b18)!.DisplayName);
    }

    [Fact]
    public void ByProductId_UnknownReturnsNull()
    {
        Assert.Null(StreamDeckModels.ByProductId(0xDEAD));
    }

    [Fact]
    public void Pedal_HasNoKeyImage()
    {
        var pedal = StreamDeckModels.ByProductId(0x0086)!;
        Assert.Equal(StreamDeckImageFormat.None, pedal.ImageFormat);
        Assert.Equal(0, pedal.KeyPixelSize);
    }

    [Theory]
    [InlineData(0x0063, "mirrorXRot90")] // Mini
    [InlineData(0x0090, "mirrorXRot90")] // Mini MK.2
    [InlineData(0x00b3, "mirrorXRot90")] // Mini Discord
    [InlineData(0x00b8, "mirrorXRot90")] // Mini MK.2 Module
    [InlineData(0x0060, "flipBoth")]     // Original (gen1, rot0 + mirror-both)
    [InlineData(0x006d, "flipBoth")]     // Original V2 (gen2)
    [InlineData(0x006c, "flipBoth")]     // XL
    [InlineData(0x0086, "none")]         // Pedal (no key image)
    public void Transform_MatchesTheWebsDeckKeyTransformDerivation(int productId, string expected)
    {
        Assert.Equal(expected, StreamDeckModels.ByProductId(productId)!.Transform);
    }

    [Fact]
    public void OnlyOriginal_HasRightToLeftRemap()
    {
        var withRemap = StreamDeckModels.All.Where(m => m.KeyIndexRightToLeft).Select(m => m.ProductId).ToList();
        Assert.Equal(new[] { 0x0060 }, withRemap);
    }

    [Fact]
    public void FlipWithinRow_IsSelfInverse()
    {
        for (var col = 0; col < 5; col++)
        {
            var flipped = StreamDeckModels.FlipWithinRow(col, 5);
            Assert.Equal(col, StreamDeckModels.FlipWithinRow(flipped, 5));
        }
    }

    [Fact]
    public void FlipWithinRow_ReversesEachRow()
    {
        // 3x5 grid: row 0 is keys 0..4, so key 0 <-> key 4, key 1 <-> key 3, key 2 stays.
        Assert.Equal(4, StreamDeckModels.FlipWithinRow(0, 5));
        Assert.Equal(3, StreamDeckModels.FlipWithinRow(1, 5));
        Assert.Equal(2, StreamDeckModels.FlipWithinRow(2, 5));
        Assert.Equal(1, StreamDeckModels.FlipWithinRow(3, 5));
        Assert.Equal(0, StreamDeckModels.FlipWithinRow(4, 5));
    }

    public static IEnumerable<object[]> Models() => StreamDeckModels.All.Select(m => new object[] { m });
}
