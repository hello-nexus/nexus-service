namespace Nexus.Service.Platform.Displays;

/// <summary>
/// The on-disk shape of HKLM\SOFTWARE\Microsoft\Wisp\Pen\Digimon, reverse
/// engineered from MultiDigiMon.exe and bench-confirmed against a live Y70
/// box (plans/touch-mapping-auto-repair.md section 4a). The "20-" prefix is a
/// hardcoded constant in the binary, not derived from anything; the value
/// name and data must be the digitizer/monitor interface paths verbatim, not
/// re-cased or normalized.
/// </summary>
public static class DigimonRegistryFormat
{
    public const string KeyPath = @"SOFTWARE\Microsoft\Wisp\Pen\Digimon";
    private const string ValueNamePrefix = "20-";

    public static string ValueName(string digitizerInterfacePath) => ValueNamePrefix + digitizerInterfacePath;
}
