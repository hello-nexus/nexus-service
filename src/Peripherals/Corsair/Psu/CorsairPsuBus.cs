namespace Nexus.Service.Peripherals.Corsair.Psu;

/// <summary>
/// One lock around every command and reply exchange with a Corsair PSU. The sensor library polls the same
/// device on its own schedule, and a second reader sees the replies meant for the first, so its PSU update
/// and our fan writes both hold this.
/// </summary>
public static class CorsairPsuBus
{
    public static readonly object Gate = new();
}
