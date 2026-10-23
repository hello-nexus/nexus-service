namespace Nexus.Service.Models.Sensors;

/// <summary>
/// The PCI subsystem identity of one discrete GPU, as the board partner programmed it. Name is the same string
/// the platform reports in <c>GetGpuModels</c>, which is how a board is matched back to its adapter.
/// </summary>
public readonly record struct GpuBoard(string Name, int SubsystemVendorId, int SubsystemDeviceId);

/// <summary>One GPU as the specs surfaces describe it: the chip plus, where the board could be read, its maker.</summary>
public sealed record GpuCard(string Chip, string? Maker = null, int SubsystemVendorId = 0, int SubsystemDeviceId = 0)
{
    /// <summary>The chip name with the maker in front of it, or the chip alone when the maker is unknown or already leads the name.</summary>
    public string Display => Maker is null || Chip.StartsWith(Maker, System.StringComparison.OrdinalIgnoreCase)
        ? Chip
        : $"{Maker} {Chip}";
}
