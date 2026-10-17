#if WINDOWS
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Nexus.Service.Helper;

/// <summary>
/// Tracks the active helper connection per WTS session id. One connection
/// per session is the invariant; a new connection from the same session
/// displaces the prior one (closes the older pipe). Service-side code
/// (settings hooks, command clients, telemetry subscribers) talks to the
/// registry rather than holding pipe references directly.
/// </summary>
public sealed class HelperRegistry
{
    private readonly ConcurrentDictionary<int, HelperConnection> _bySession = new();

    /// <summary>Raised whenever a helper completes its hello handshake.</summary>
    public event Action<HelperConnection>? Connected;

    /// <summary>Raised when a helper drops or is displaced.</summary>
    public event Action<HelperConnection>? Disconnected;

    /// <summary>
    /// Every non-result envelope received from any helper. Result envelopes
    /// are routed internally by HelperConnection to the originating TCS.
    /// </summary>
    public event Action<HelperConnection, HelperEnvelope>? InboundEnvelope;

    public System.Collections.Generic.ICollection<HelperConnection> GetAll() => _bySession.Values;

    public bool IsAnyConnected => !_bySession.IsEmpty;

    public HelperConnection? GetForSession(int sessionId)
        => _bySession.TryGetValue(sessionId, out var c) ? c : null;

    /// <summary>
    /// Returns whichever connection exists. Single-session only: picks the
    /// only active helper, does not address multi-user.
    /// </summary>
    public HelperConnection? GetAny()
    {
        foreach (var kv in _bySession) return kv.Value;
        return null;
    }

    internal void Register(HelperConnection conn)
    {
        if (_bySession.TryGetValue(conn.SessionId, out var existing) && !ReferenceEquals(existing, conn))
        {
            _ = Task.Run(existing.DisposeAsync);
        }
        _bySession[conn.SessionId] = conn;
        Connected?.Invoke(conn);
    }

    internal void Unregister(HelperConnection conn)
    {
        if (_bySession.TryGetValue(conn.SessionId, out var existing) && ReferenceEquals(existing, conn))
        {
            _bySession.TryRemove(conn.SessionId, out _);
        }
        Disconnected?.Invoke(conn);
    }

    internal void RaiseInbound(HelperConnection conn, HelperEnvelope env)
        => InboundEnvelope?.Invoke(conn, env);
}
#endif
