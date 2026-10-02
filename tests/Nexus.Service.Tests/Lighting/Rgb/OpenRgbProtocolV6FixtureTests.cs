using System.Text.Json;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// REQUEST_CONTROLLER_DATA replies captured from the bundled OpenRGB 1.0 daemon
/// on one box (Corsair M65 PRO, B850I AORUS PRO split board, RTX 5080 FE) at
/// protocols 4, 5 and 6: every version must parse to the same device.
/// </summary>
public class OpenRgbProtocolV6FixtureTests
{
    private static List<byte[]> Load(int protocol)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine("Fixtures", "OpenRgb", $"controllers-v{protocol}.json")));
        var list = new List<byte[]>();
        foreach (var hex in doc.RootElement.GetProperty("controllers").EnumerateArray())
        {
            list.Add(Convert.FromHexString(hex.GetString()!));
        }
        return list;
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Every_protocol_parses_the_same_devices(int protocol)
    {
        var devices = Load(protocol).Select((b, i) => OpenRgbProtocol.ParseControllerData(i, b, (uint)protocol)).ToList();

        Assert.Equal(new[] { "Corsair M65 PRO", "B850I AORUS PRO", "NVIDIA GeForce RTX 5080 FE" }, devices.Select(d => d.Name));
        Assert.Equal(15, devices[0].LedCount);
        Assert.Equal("1500B038AF0618C9588F5187F5001941", devices[0].Serial);
        Assert.Equal(new[] { "ARGB_V2_1", "ARGB_V2_2", "LED_C" }, devices[1].Zones.Select(z => z.Name));
        Assert.Equal(3, devices[2].LedCount);
        Assert.All(devices, d => Assert.NotNull(d.FindCustomMode()));
    }

    [Fact]
    public void V6_parses_the_header_zones_at_their_configured_counts()
    {
        var board = OpenRgbProtocol.ParseControllerData(1, Load(6)[1], 6);
        var v4Board = OpenRgbProtocol.ParseControllerData(1, Load(4)[1], 4);

        Assert.Equal(v4Board.Zones.Select(z => z.LedCount), board.Zones.Select(z => z.LedCount));
        Assert.Equal(v4Board.LedCount, board.LedCount);
    }

    [Fact]
    public void V6_controller_count_reply_carries_controller_ids()
    {
        var body = Convert.FromHexString("03000000000000000100000002000000");

        Assert.Equal(new[] { 0, 1, 2 }, OpenRgbProtocol.ParseControllerAddresses(body, 6));
        Assert.Equal(new[] { 0, 1, 2 }, OpenRgbProtocol.ParseControllerAddresses(body.AsSpan(0, 4), 4));
    }
}
