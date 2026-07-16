#if WINDOWS
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Writes HKLM\SOFTWARE\Microsoft\Wisp\Pen\Digimon the same way
/// MultiDigiMon.exe does: delete the value, then create/set it. Both steps
/// run fine from LocalSystem (bench-verified).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDigimonRegistryWriter : IDigimonRegistryWriter
{
    public void Write(string digitizerInterfacePath, string monitorInterfacePath)
    {
        using var key = Registry.LocalMachine.CreateSubKey(DigimonRegistryFormat.KeyPath, writable: true);
        if (key is null) return;
        var name = DigimonRegistryFormat.ValueName(digitizerInterfacePath);
        key.DeleteValue(name, throwOnMissingValue: false);
        key.SetValue(name, monitorInterfacePath, RegistryValueKind.String);
    }
}
#endif
