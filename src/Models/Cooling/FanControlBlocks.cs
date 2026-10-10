namespace Nexus.Service.Models.Cooling;

/// <summary>Wire values of <see cref="FanChannel.ControlBlocked"/>; the panel maps each to a warning on the fan card.</summary>
public static class FanControlBlocks
{
    /// <summary>AMD Software's automatic GPU tuning is on, so the driver refuses fan curve and Zero RPM writes.</summary>
    public const string AmdAutoTuning = "amd-auto-tuning";
}
