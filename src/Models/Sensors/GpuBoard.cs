namespace Nexus.Service.Models.Sensors;

/// <summary>
/// The PCI subsystem identity of one discrete GPU, as the board partner programmed it. Name is the same string
/// the platform reports in <c>GetGpuModels</c>, which is how a board is matched back to its adapter.
/// </summary>
public readonly record struct GpuBoard(string Name, int SubsystemVendorId, int SubsystemDeviceId);

/// <summary>One GPU as the specs surfaces describe it: the chip plus, where the board could be read, its maker.</summary>
public sealed record GpuCard(string Chip, string? Maker = null, int SubsystemVendorId = 0, int SubsystemDeviceId = 0)
{
    /// <summary>Partner boards read "Maker Chip"; a GeForce reference board reads "NVIDIA Founders Edition GeForce ..." (the form the catalog matcher resolves); any other reference or unclean name stays the chip.</summary>
    public string Display => SubsystemVendorId switch
    {
        0x10DE => FoundersEdition(Chip) ?? Chip,
        0x1002 => Chip,
        _ => Maker is null || Chip.StartsWith(Maker, System.StringComparison.OrdinalIgnoreCase) ? Chip : $"{Maker} {Chip}",
    };

    // Only a clean "[NVIDIA ]GeForce ..." name qualifies; workstation, datacenter and raw lspci names such as "NVIDIA Corporation AD103 [...]" do not.
    private static string? FoundersEdition(string chip)
    {
        const string vendorPrefix = "NVIDIA ";
        var model = chip.StartsWith(vendorPrefix, System.StringComparison.OrdinalIgnoreCase) ? chip[vendorPrefix.Length..] : chip;
        return model.StartsWith("GeForce ", System.StringComparison.OrdinalIgnoreCase) ? $"NVIDIA Founders Edition {model}" : null;
    }
}
