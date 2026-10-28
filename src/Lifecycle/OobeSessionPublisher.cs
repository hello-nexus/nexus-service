using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Lifecycle;

public sealed class OobeSessionState
{
    public bool Oobe { get; set; }
}

/// <summary>
/// Publishes whether Windows is still in first-boot setup on the
/// <c>system/session</c> topic so the panel UI can restrict itself. The kiosk
/// launches under the temporary OOBE account, so the service only reports
/// state; nothing server-side is enforced. Re-evaluated on session events and
/// on a slow poll while a client is subscribed.
/// </summary>
public sealed class OobeSessionPublisher : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

#if DEV_TOOLS
    /// <summary>Dev-tools stand-in that forces <c>oobe: true</c>. In-memory: resets with the service.</summary>
    public static volatile bool DevForce;
#endif

    private readonly MultiplexHub _hub;
    private readonly Func<bool> _detect;
    private readonly object _lock = new();
    private bool? _last;

    public OobeSessionPublisher(MultiplexHub hub)
        : this(hub, OobeDetector.IsInOobe)
    {
    }

    internal OobeSessionPublisher(MultiplexHub hub, Func<bool> detect)
    {
        _hub = hub;
        _detect = detect;
        _hub.RegisterSnapshotProvider(PanelTopics.SystemSession, GetSnapshot);
#if WINDOWS
        WindowsServiceHost.SessionLogon += () => Publish();
        WindowsServiceHost.ConsoleSessionChanged += () => Publish();
#endif
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_hub.TopicHasSubscribers(PanelTopics.SystemSession))
                Publish();

            try
            { await Task.Delay(PollInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    internal bool Current()
    {
#if DEV_TOOLS
        if (DevForce) return true;
#endif
        try { return _detect(); }
        catch { return false; }
    }

    private ReadOnlyMemory<byte>? GetSnapshot()
    {
        lock (_lock)
        {
            var oobe = Current();
            _last = oobe;
            return Build(oobe);
        }
    }

    /// <summary>Broadcasts only when the value differs from the last one seen.</summary>
    internal void Publish()
    {
        lock (_lock)
        {
            var oobe = Current();
            if (_last == oobe) return;
            _last = oobe;
            if (!_hub.TopicHasSubscribers(PanelTopics.SystemSession)) return;
            _ = _hub.BroadcastTopicAsync(PanelTopics.SystemSession, Build(oobe));
        }
    }

    private static ReadOnlyMemory<byte> Build(bool oobe)
        => WsEnvelope.Build(PanelTopics.SystemSession, new OobeSessionState { Oobe = oobe }, AppJsonContext.Default.OobeSessionState);
}

internal static partial class OobeDetector
{
    /// <summary>
    /// Windows: kernel32 OOBEComplete. If the export is missing on this build,
    /// falls back to the SystemSetupInProgress registry flag. Other platforms: false.
    /// </summary>
    internal static bool IsInOobe()
    {
#if WINDOWS
        try
        {
            return OOBEComplete(out var complete) ? !complete : false;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\Setup");
            return key?.GetValue("SystemSetupInProgress") is int v && v != 0;
        }
#else
        return false;
#endif
    }

#if WINDOWS
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OOBEComplete([MarshalAs(UnmanagedType.Bool)] out bool isOobeComplete);
#endif
}
