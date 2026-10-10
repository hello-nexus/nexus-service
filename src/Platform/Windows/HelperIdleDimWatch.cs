#if WINDOWS
using System.Runtime.Versioning;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Lighting.IdleDim;

namespace Nexus.Service.Platform.Windows;

/// <summary>
/// Service-side idle-dim watch on Windows: the Session 0 service can read
/// neither session idle time nor the user's display state, so it arms the
/// session helper and listens for its transitions. Remembers what it armed so
/// a helper that reconnects can be re-asserted.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HelperIdleDimWatch : IIdleDimWatch
{
    private readonly HelperRegistry _registry;
    private readonly object _gate = new();
    private int _threshold;
    private bool _display;

    public HelperIdleDimWatch(HelperRegistry registry) => _registry = registry;

    public void SetInputWatch(int thresholdSeconds)
    {
        lock (_gate) { _threshold = thresholdSeconds; }
        Push();
    }

    public void SetDisplayWatch(bool armed)
    {
        lock (_gate) { _display = armed; }
        Push();
    }

    /// <summary>Send the current arming; also called when a helper (re)connects.</summary>
    public void Push()
    {
        int threshold;
        bool display;
        lock (_gate)
        {
            threshold = _threshold;
            display = _display;
        }
        _ = IdleDimCommands.SetWatchAsync(_registry, threshold, display);
    }

    /// <summary>True when something is armed, so a reconnect has anything to re-assert.</summary>
    public bool Armed
    {
        get
        {
            lock (_gate) { return _threshold != 0 || _display; }
        }
    }
}
#endif
