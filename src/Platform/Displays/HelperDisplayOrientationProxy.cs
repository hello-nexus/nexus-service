#if WINDOWS
using System.Runtime.Versioning;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Service-side <see cref="IDisplayOrientationProvider"/> that proxies the
/// rotation call to the user-session helper. The Win32 implementation lives
/// in <see cref="WindowsDisplayOrientationProvider"/> and runs inside the
/// helper, where <c>ChangeDisplaySettingsEx</c> can see the user's monitors.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperDisplayOrientationProxy : IDisplayOrientationProvider
{
    private readonly HelperRegistry _registry;

    public HelperDisplayOrientationProxy(HelperRegistry registry)
    {
        _registry = registry;
    }

    public (bool Ok, string Error) SetY70Orientation(string orientation)
    {
        var result = OrientationCommands.SetAsync(_registry, orientation).GetAwaiter().GetResult();
        return (result.Ok, result.Error);
    }

    public (bool Ok, string Error) SetDisplayOrientation(string displayId, string orientation, string coverColorHex)
    {
        var result = OrientationCommands.SetForDisplayAsync(_registry, displayId, orientation, coverColorHex).GetAwaiter().GetResult();
        return (result.Ok, result.Error);
    }
}
#endif
