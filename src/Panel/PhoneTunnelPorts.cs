using System.Net;
using Microsoft.AspNetCore.Http;

namespace Nexus.Service.Panel;

/// <summary>
/// Dedicated loopback listeners for a wired Android phone's <c>adb reverse</c>.
/// The reverse lands on the host as a 127.0.0.1 connection, which every
/// loopback-trusted surface (the <c>/pair</c> desktop token, the desktop-token
/// lane) would grant to any app on the phone. Phone traffic arrives on these
/// ports instead, and <see cref="Reclassify"/> gives each request a non-loopback
/// remote address so the phone is treated as the remote client it is. The
/// Q-series tunnel keeps its own port and stays desktop-trusted.
/// </summary>
public sealed class PhoneTunnelPorts
{
    /// <summary>Stand-in remote address for phone tunnel requests: stable (so the HTTP session bind holds) and never loopback.</summary>
    public static readonly IPAddress PhoneRemote = IPAddress.Parse("169.254.77.77");

    public PhoneTunnelPorts(int? httpPort, int? httpsPort)
    {
        HttpPort = httpPort;
        HttpsPort = httpsPort;
    }

    /// <summary>Listener the phone's plain-HTTP port is reversed onto; null when unavailable.</summary>
    public int? HttpPort { get; }

    /// <summary>Listener the phone's HTTPS port is reversed onto; null when unavailable.</summary>
    public int? HttpsPort { get; }

    /// <summary>Phone listener ports for a service port: two above the main HTTP and HTTPS ports.</summary>
    public static (int Http, int Https) Derive(int servicePort)
        => (servicePort + 2, (servicePort == 9400 ? 9443 : servicePort + 443) + 2);

    /// <summary><see cref="HttpContext.Items"/> key set on every phone tunnel request; the server creates Items per request, so a client cannot set it.</summary>
    public const string ItemKey = "nexus.phone-tunnel";

    /// <summary>True only for a request <see cref="Reclassify"/> marked as arriving on a phone tunnel listener.</summary>
    public static bool IsTunnelRequest(HttpContext ctx)
        => ctx.Items.TryGetValue(ItemKey, out var marked) && marked is true;

    public bool IsPhoneTunnelRequest(HttpContext ctx)
    {
        var port = ctx.Connection.LocalPort;
        return port != 0 && (port == HttpPort || port == HttpsPort);
    }

    /// <summary>
    /// Re-label a phone tunnel request as the non-loopback client it is and mark
    /// it. Call first in the pipeline. Returns false for a request that is NOT on
    /// a tunnel listener yet claims the stand-in address (a LAN host borrowing
    /// it): the caller must refuse it, so that address only ever means the tunnel.
    /// </summary>
    public bool Reclassify(HttpContext ctx)
    {
        if (IsPhoneTunnelRequest(ctx))
        {
            ctx.Items[ItemKey] = true;
            ctx.Connection.RemoteIpAddress = PhoneRemote;
            return true;
        }
        return !PhoneRemote.Equals(ctx.Connection.RemoteIpAddress);
    }
}
