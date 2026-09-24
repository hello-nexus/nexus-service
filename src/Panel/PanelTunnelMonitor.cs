using System;
using System.Threading;
using Microsoft.AspNetCore.Http;

namespace Nexus.Service.Panel;

/// <summary>
/// Liveness signal for the Q-series panel's USB reverse tunnel. The service
/// binds a second loopback-only listener on <see cref="Port"/> and
/// <c>QSeriesPortWatcher</c> points the panel's <c>adb reverse</c> at it. The
/// adb tunnel is the only intended client; another loopback-local process
/// connecting there would also count, and the signal is host-global (a second
/// attached panel shares it) - parity with the single-record legacy signal.
/// On the main port, panel traffic is indistinguishable from the desktop
/// dashboard - both arrive as 127.0.0.1 - which let an open dashboard mask a
/// stranded panel from the watcher's escalation reboot.
/// <see cref="MarkInboundActivity"/> is stamped by the tunnel listener's
/// connection middleware on every inbound read, including WebSocket keepalive
/// pongs, so an idle-but-healthy panel still registers activity.
/// </summary>
public sealed class PanelTunnelMonitor
{
    private long _lastInboundActivityUnixMs;

    public PanelTunnelMonitor(int? port) => Port = port;

    /// <summary>Tunnel listener port; null when the bind was unavailable
    /// (port taken, non-Windows, test host) - consumers then fall back to the
    /// legacy record-based liveness signal.</summary>
    public int? Port { get; }

    public bool IsActive => Port is not null;

    /// <summary>Unix ms of the last inbound byte on the tunnel listener; 0 when
    /// nothing has arrived since service start.</summary>
    public long LastInboundActivityUnixMs => Interlocked.Read(ref _lastInboundActivityUnixMs);

    public void MarkInboundActivity() =>
        Interlocked.Exchange(ref _lastInboundActivityUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private long _lastAuthorizedUnixMs;
    private int _authenticatedSockets;

    /// <summary>Unix ms of the last tunnel request that passed token or session validation; 0 when none since service start.</summary>
    public long LastAuthorizedUnixMs => Interlocked.Read(ref _lastAuthorizedUnixMs);

    /// <summary>Multiplex sockets currently open on the tunnel listener; every one of them passed validation at its upgrade.</summary>
    public int AuthenticatedSockets => Volatile.Read(ref _authenticatedSockets);

    public bool IsTunnelRequest(HttpContext ctx) => Port is int port && ctx.Connection.LocalPort == port;

    public void MarkAuthorized(HttpContext ctx)
    {
        if (IsTunnelRequest(ctx))
            Interlocked.Exchange(ref _lastAuthorizedUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void SocketOpened()
    {
        Interlocked.Increment(ref _authenticatedSockets);
        Interlocked.Exchange(ref _lastAuthorizedUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>Also re-stamps the authenticated moment: a socket that upgraded before the watcher's first-sighting anchor would otherwise read as never-authenticated the moment it drops.</summary>
    public void SocketClosed()
    {
        Interlocked.Decrement(ref _authenticatedSockets);
        Interlocked.Exchange(ref _lastAuthorizedUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}
