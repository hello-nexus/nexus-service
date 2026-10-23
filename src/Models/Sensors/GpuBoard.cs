namespace Nexus.Service.Models.Sensors;

/// <summary>
/// The PCI subsystem identity of one discrete GPU, as the board partner programmed it. Name is the same string
/// the platform reports in <c>GetGpuModels</c>, which is how a board is matched back to its adapter.
/// </summary>
public readonly record struct GpuBoard(string Name, int SubsystemVendorId, int SubsystemDeviceId);

/// <summary>One GPU as the specs surfaces describe it: the chip plus, where the board could be read, its maker.</summary>
public sealed record GpuCard(string Chip, string? Maker = null, int SubsystemVendorId = 0, int SubsystemDeviceId = 0)
{
    /// <summary>Partner boards read "Maker Chip"; an NVIDIA reference board reads "NVIDIA Founders Edition Chip" (the form the catalog matcher resolves); an AMD reference board stays the bare chip.</summary>
    public string Display => SubsystemVendorId switch
    {
        0x10DE => $"NVIDIA Founders Edition {StripLeading(Chip, "NVIDIA")}",
        0x1002 => Chip,
        _ => Maker is null || Chip.StartsWith(Maker, System.StringComparison.OrdinalIgnoreCase) ? Chip : $"{Maker} {Chip}",
    };

    private static string StripLeading(string chip, string word) =>
        chip.StartsWith(word + " ", System.StringComparison.OrdinalIgnoreCase) ? chip[(word.Length + 1)..] : chip;
}
