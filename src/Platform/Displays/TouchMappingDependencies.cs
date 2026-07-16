using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Reports the current digitizer/display association. Raw input and
/// EnumDisplayMonitors are user-session APIs, so only the Windows helper can
/// answer this; null means the platform or connection can't (the guard reads
/// that as "no helper", not "nothing to repair").
/// </summary>
public interface ITouchMapSnapshotSource
{
    Task<TouchMapSnapshot?> GetSnapshotAsync(CancellationToken ct = default);
}

/// <summary>Fallback for platforms with no touch-mapping mechanism.</summary>
public sealed class StubTouchMapSnapshotSource : ITouchMapSnapshotSource
{
    public Task<TouchMapSnapshot?> GetSnapshotAsync(CancellationToken ct = default) =>
        Task.FromResult<TouchMapSnapshot?>(null);
}

/// <summary>
/// Writes the Windows digitizer-to-monitor association
/// (HKLM\SOFTWARE\Microsoft\Wisp\Pen\Digimon). See DigimonRegistryFormat for
/// the value name/data shape.
/// </summary>
public interface IDigimonRegistryWriter
{
    void Write(string digitizerInterfacePath, string monitorInterfacePath);
}

/// <summary>Never invoked off Windows: the stub snapshot source always
/// returns null, so TouchMappingGuard never reaches a repair step.</summary>
public sealed class NullDigimonRegistryWriter : IDigimonRegistryWriter
{
    public void Write(string digitizerInterfacePath, string monitorInterfacePath) { }
}

/// <summary>
/// Restarts a digitizer's HID devnode so Windows re-reads the Digimon
/// mapping - a registry write alone is not picked up live. Returns false
/// when the restart could not be issued.
/// </summary>
public interface ITouchDigitizerDevnodeRestarter
{
    bool Restart(string digitizerInterfacePath);
}

/// <summary>Never invoked off Windows: see NullDigimonRegistryWriter.</summary>
public sealed class NullTouchDigitizerDevnodeRestarter : ITouchDigitizerDevnodeRestarter
{
    public bool Restart(string digitizerInterfacePath) => false;
}
