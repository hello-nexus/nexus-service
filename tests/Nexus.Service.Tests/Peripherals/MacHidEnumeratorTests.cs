using System;
using System.Linq;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.StreamDeck;
using Xunit;

namespace Nexus.Service.Tests.Peripherals;

public class MacHidEnumeratorTests
{
    [Theory]
    [InlineData("iokit:1000766e2", true, 0x1000766e2UL)]
    [InlineData("iokit:0", true, 0UL)]
    [InlineData("1000766e2", false, 0UL)]
    [InlineData("iokit:", false, 0UL)]
    [InlineData("iokit:zz", false, 0UL)]
    [InlineData(@"\\?\hid#vid_0fd9", false, 0UL)]
    public void TryParseEntryId(string path, bool ok, ulong expected)
    {
        Assert.Equal(ok, MacHidEnumerator.TryParseEntryId(path, out var id));
        Assert.Equal(expected, id);
    }

    [MacOnlyFact]
    public void FindAll_ListsIoKitPathsWithVendorAndProduct()
    {
        var all = new MacHidEnumerator().FindAll();

        // Any Mac has at least its own HID devices (keyboard/trackpad/IR) in the IORegistry.
        Assert.NotEmpty(all);
        Assert.All(all, d => Assert.True(MacHidEnumerator.TryParseEntryId(d.Path, out _), d.Path));
        Assert.Contains(all, d => d.VendorId != 0 && d.ProductId != 0);
    }

    /// <summary>Hardware-conditional: exercises open, feature get/set and a second independent handle when a Stream Deck is plugged in; a no-op otherwise.</summary>
    [MacOnlyFact]
    public void StreamDeck_WhenPresent_OpensReadsFirmwareAndSetsBrightness()
    {
        var hid = new MacHidEnumerator();
        var deck = StreamDeckModels.All
            .Select(m => (Model: m, Info: hid.Find(StreamDeckModels.VendorId, m.ProductId).FirstOrDefault()))
            .FirstOrDefault(x => x.Info is not null);
        if (deck.Info is null)
        {
            return;
        }

        using var control = hid.Open(deck.Info.Path);
        using var input = hid.Open(deck.Info.Path, forInput: true);
        Assert.NotNull(control);
        Assert.NotNull(input);
        Assert.Equal(deck.Info.Serial, control!.Serial);

        var request = deck.Model.Protocol == StreamDeckProtocolGeneration.Gen1
            ? StreamDeckProtocol.BuildFirmwareFeatureRequest()
            : StreamDeckProtocol.BuildGen2FirmwareFeatureRequest();
        Assert.True(control.GetFeature(request));
        var firmware = deck.Model.Protocol == StreamDeckProtocolGeneration.Gen1
            ? StreamDeckProtocol.ExtractAsciiString(request)
            : StreamDeckProtocol.ExtractAsciiString(request, StreamDeckProtocol.Gen2FirmwareStringOffset);
        Assert.Matches(@"^\d+\.\d+", firmware);

        var brightness = deck.Model.Protocol == StreamDeckProtocolGeneration.Gen1
            ? StreamDeckProtocol.BuildBrightnessFeature(80)
            : StreamDeckProtocol.BuildGen2BrightnessFeature(80);
        Assert.True(control.SetFeature(brightness));

        // No key is pressed during a unit test: an idle read times out cleanly, never errors.
        Assert.Equal(0, input!.Read(new byte[deck.Model.InputReportBufferLength], 150));
    }
}
