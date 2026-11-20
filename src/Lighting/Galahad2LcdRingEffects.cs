namespace Nexus.Service.Lighting;

public sealed class Galahad2LcdRingEffect
{
    public string Id { get; init; } = "";
    public byte WireByte { get; init; }
    public int Colors { get; init; }
}

/// <summary>
/// Pump-ring firmware animations. Mode bytes follow sgtaziz/lian-li-linux
/// `to_hydroshift_lcd_mode_byte`; the colour slot counts are those L-Connect sent in a
/// USB capture, so a mode that capture never showed is not listed.
/// </summary>
public static class Galahad2LcdRingEffects
{
    public static readonly Galahad2LcdRingEffect[] All =
    {
        new() { Id = "rainbowMorph", WireByte = 0x02, Colors = 0 },
        new() { Id = "breathing", WireByte = 0x04, Colors = 1 },
        new() { Id = "runway", WireByte = 0x05, Colors = 2 },
        new() { Id = "meteor", WireByte = 0x06, Colors = 4 },
    };

    public static Galahad2LcdRingEffect? Find(string? id)
    {
        foreach (var e in All)
        {
            if (e.Id == id)
            {
                return e;
            }
        }
        return null;
    }
}
