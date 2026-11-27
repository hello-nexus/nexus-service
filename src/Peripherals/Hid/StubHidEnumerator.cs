using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hid;

/// <summary>No-op HID enumerator for platforms without a real implementation yet.</summary>
public sealed class StubHidEnumerator : IHidEnumerator, IHidWholeBusEnumerator
{
    public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => System.Array.Empty<HidDeviceInfo>();
    public IReadOnlyList<HidDeviceInfo> FindAllForDiagnostics() => System.Array.Empty<HidDeviceInfo>();
    public IHidDevice? Open(string path, bool forInput = false) => null;
}
