using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform.Linux.DBus;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Decides whether OS key presses may reach the key-reactive overlay: only while
/// the active seat0 session is an unlocked user session and no screen locker is
/// up, so nothing typed at a login prompt, a lock screen or another user's
/// session is ever read. Fails closed: until a first successful logind read,
/// and whenever logind is unreachable, nothing is allowed.
/// </summary>
public sealed class LinuxKeyInputGate : IDisposable
{
    internal const int IntervalMs = 2000;
    private const int CallTimeoutMs = 1000;
    private const long LockerLookupMs = 60_000;

    private const string Login = "org.freedesktop.login1";
    private const string Properties = "org.freedesktop.DBus.Properties";

    private static readonly (string Name, string Path)[] Lockers =
    {
        ("org.cinnamon.ScreenSaver", "/org/cinnamon/ScreenSaver"),
        ("org.mate.ScreenSaver", "/org/mate/ScreenSaver"),
        ("org.xfce.ScreenSaver", "/org/xfce/ScreenSaver"),
        ("org.gnome.ScreenSaver", "/org/gnome/ScreenSaver"),
        ("org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver"),
    };

    private readonly Func<Task<SeatState?>> _readSeat;
    private readonly Func<Task<bool?>> _readLocker;
    private readonly int _intervalMs;
    private readonly DBusConnection? _session;
    private readonly object _lock = new();
    private DBusConnection? _system;
    private CancellationTokenSource? _cts;
    private volatile bool _allowed;
    private string _lastReason = "";
    private (string Name, string Path)? _locker;
    private long _lockerLookedUpAt = long.MinValue;

    /// <param name="session">The session-bus connection used to ask a screen locker whether it is active; null skips that check.</param>
    public LinuxKeyInputGate(DBusConnection? session)
    {
        _session = session;
        _readSeat = ReadSeatAsync;
        _readLocker = ReadLockerAsync;
        _intervalMs = IntervalMs;
    }

    internal LinuxKeyInputGate(Func<Task<SeatState?>> readSeat, Func<Task<bool?>> readLocker, int intervalMs)
    {
        _readSeat = readSeat;
        _readLocker = readLocker;
        _intervalMs = intervalMs;
    }

    public bool Allowed => _allowed;

    /// <summary>What logind reports about seat0's active session; <c>HasActiveSession</c> false at a greeter or on a bare console.</summary>
    internal readonly record struct SeatState(bool HasActiveSession, string? Class, bool? LockedHint);

    /// <summary>Pure decision. A null seat means logind could not be read; a null locker means none was found or it did not answer.</summary>
    internal static (bool Allowed, string Reason) Decide(SeatState? seat, bool? lockerActive)
    {
        if (seat is not { } s) return (false, "login manager unreachable");
        if (!s.HasActiveSession) return (false, "no active session on seat0 (login prompt or console)");
        if (!string.Equals(s.Class, "user", StringComparison.Ordinal))
        {
            return (false, $"active session is a {s.Class ?? "unknown"} session");
        }
        if (s.LockedHint != false) return (false, "session is locked");
        if (lockerActive == true) return (false, "screen locker is up");
        return (true, "");
    }

    /// <summary>Starts or stops polling. Stopping also blocks, so the next start begins closed.</summary>
    public void SetActive(bool active)
    {
        lock (_lock)
        {
            if (active)
            {
                if (_cts is not null) return;
                var cts = new CancellationTokenSource();
                _cts = cts;
                _ = Task.Run(() => LoopAsync(cts.Token));
                return;
            }
            _cts?.Cancel();
            _cts = null;
            _allowed = false;
            _lastReason = "";
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var seat = await _readSeat().ConfigureAwait(false);
                var locker = seat is null ? null : await _readLocker().ConfigureAwait(false);
                var (allowed, reason) = Decide(seat, locker);
                if (!ct.IsCancellationRequested)
                {
                    _allowed = allowed;
                    if (reason != _lastReason)
                    {
                        _lastReason = reason;
                        ServiceLog.Info(allowed
                            ? "[key-press] key reactions resumed"
                            : $"[key-press] key reactions paused: {reason}");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _allowed = false;
                Console.Error.WriteLine($"[key-press] input gate check failed: {ex.GetType().Name}: {ex.Message}");
            }
            try { await Task.Delay(_intervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<SeatState?> ReadSeatAsync()
    {
        DBusConnection bus;
        lock (_lock)
        {
            bus = _system ??= new DBusConnection(DBusBusKind.System);
        }
        try
        {
            await bus.StartAsync().ConfigureAwait(false);
            var active = await GetAsync(bus, "/org/freedesktop/login1/seat/seat0",
                "org.freedesktop.login1.Seat", "ActiveSession").ConfigureAwait(false);
            if (active.Signature != "(so)") return null;
            active.Reader.AlignTo(8);
            var id = active.Reader.ReadString();
            var path = active.Reader.ReadObjectPath();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(path) || path == "/")
            {
                return new SeatState(false, null, null);
            }

            var cls = await GetAsync(bus, path, "org.freedesktop.login1.Session", "Class").ConfigureAwait(false);
            var locked = await GetAsync(bus, path, "org.freedesktop.login1.Session", "LockedHint").ConfigureAwait(false);
            return new SeatState(
                true,
                cls.Signature == "s" ? cls.Reader.ReadString() : null,
                locked.Signature == "b" ? locked.Reader.ReadBool() : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Drop the connection so the next tick reconnects.
            lock (_lock)
            {
                if (ReferenceEquals(_system, bus))
                {
                    _system = null;
                }
            }
            bus.Dispose();
            return null;
        }
    }

    private async Task<bool?> ReadLockerAsync()
    {
        if (_session is null) return null;
        try
        {
            await _session.StartAsync().ConfigureAwait(false);
            var now = Environment.TickCount64;
            if (_locker is null || now - _lockerLookedUpAt > LockerLookupMs)
            {
                _lockerLookedUpAt = now;
                _locker = null;
                foreach (var candidate in Lockers)
                {
                    var reply = await _session.CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus",
                        "org.freedesktop.DBus", "NameHasOwner", "s", w => w.WriteString(candidate.Name), CallTimeoutMs)
                        .ConfigureAwait(false);
                    if (new DBusReader(reply.Body).ReadBool())
                    {
                        _locker = candidate;
                        break;
                    }
                }
            }
            if (_locker is not { } locker) return null;
            var active = await _session.CallAsync(locker.Name, locker.Path, locker.Name, "GetActive", "", null, CallTimeoutMs)
                .ConfigureAwait(false);
            return new DBusReader(active.Body).ReadBool();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _locker = null;
            return null;
        }
    }

    private static async Task<(string Signature, DBusReader Reader)> GetAsync(
        DBusConnection bus, string path, string iface, string property)
    {
        var reply = await bus.CallAsync(Login, path, Properties, "Get", "ss", w =>
        {
            w.WriteString(iface);
            w.WriteString(property);
        }, CallTimeoutMs).ConfigureAwait(false);
        var reader = new DBusReader(reply.Body);
        var signature = reader.ReadSignature();
        return (signature, reader);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
            _allowed = false;
            _system?.Dispose();
            _system = null;
        }
    }
}
