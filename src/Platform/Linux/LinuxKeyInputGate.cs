using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform.Linux.DBus;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Decides whether OS key presses may reach the key-reactive overlay: only while
/// the active seat0 session is an unlocked user session of the session user and
/// no screen locker is up, so nothing typed at a login prompt, a lock screen or
/// another user's session is ever read. Fails closed: until a first successful
/// logind read, and whenever logind is unreachable, nothing is allowed.
/// Lockers that expose neither logind's LockedHint nor a ScreenSaver D-Bus
/// interface (swaylock, i3lock, xsecurelock) are not detected. State is polled,
/// and <see cref="ForceBlocked"/> closes the gate at once on a
/// session lock or suspend (wired from the existing listeners); the repo's D-Bus
/// client only has one-shot signal waiters, too fragile for PropertiesChanged
/// subscriptions.
/// </summary>
public sealed partial class LinuxKeyInputGate : IDisposable
{
    internal const int IntervalMs = 1000;
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
    private readonly Func<uint?> _expectedUid;
    private readonly int _intervalMs;
    private readonly DBusConnection? _session;
    private readonly object _lock = new();
    private DBusConnection? _system;
    private CancellationTokenSource? _cts;
    // Bumped on every start and stop; a loop publishes only while its generation is current.
    private int _generation;
    // Bumped by ForceBlocked; a read that began before it is stale and is not published.
    private int _epoch;
    private volatile bool _allowed;
    private string _lastReason = "";
    private (string Name, string Path)[]? _lockers;
    private long _lockersLookedUpAt;

    /// <param name="session">The session-bus connection used to ask a screen locker whether it is active; null skips that check.</param>
    public LinuxKeyInputGate(DBusConnection? session)
    {
        _session = session;
        _readSeat = ReadSeatAsync;
        _readLocker = ReadLockerAsync;
        _expectedUid = () => LinuxSession.SessionUid ?? getuid();
        _intervalMs = IntervalMs;
    }

    internal LinuxKeyInputGate(Func<Task<SeatState?>> readSeat, Func<Task<bool?>> readLocker, Func<uint?> expectedUid, int intervalMs)
    {
        _readSeat = readSeat;
        _readLocker = readLocker;
        _expectedUid = expectedUid;
        _intervalMs = intervalMs;
    }

    public bool Allowed => _allowed;

    /// <summary>Test seam: runs at the end of every publish attempt, current or stale.</summary>
    internal Action? AfterPublish { get; set; }

    /// <summary>What logind reports about seat0's active session; <c>HasActiveSession</c> false at a greeter or on a bare console.</summary>
    internal readonly record struct SeatState(bool HasActiveSession, string? Class, bool? LockedHint, uint? Uid);

    /// <summary>
    /// Pure decision. A null seat means logind could not be read; a null locker means none exists
    /// or the session bus is unreachable; <paramref name="expectedUid"/> is the user whose session may be read.
    /// </summary>
    internal static (bool Allowed, string Reason) Decide(SeatState? seat, bool? lockerActive, uint? expectedUid)
    {
        if (seat is not { } s) return (false, "login manager unreachable");
        if (!s.HasActiveSession) return (false, "no active session on seat0 (login prompt or console)");
        if (!string.Equals(s.Class, "user", StringComparison.Ordinal))
        {
            return (false, $"active session is a {s.Class ?? "unknown"} session");
        }
        if (expectedUid is null || s.Uid != expectedUid) return (false, "active session belongs to another user");
        if (s.LockedHint != false) return (false, "session is locked");
        if (lockerActive == true) return (false, "screen locker is up");
        return (true, "");
    }

    /// <summary>Starts or stops polling. Every start and stop leaves the gate blocked until a fresh read allows it.</summary>
    public void SetActive(bool active)
    {
        lock (_lock)
        {
            if (active && _cts is not null) return;
            _generation++;
            _allowed = false;
            _lastReason = "";
            if (active)
            {
                var cts = new CancellationTokenSource();
                _cts = cts;
                var generation = _generation;
                _ = Task.Run(() => LoopAsync(cts.Token, generation));
                return;
            }
            _cts?.Cancel();
            _cts = null;
        }
    }

    /// <summary>Closes the gate now and discards any read already in flight; the next poll reopens it if the session is usable.</summary>
    public void ForceBlocked(string reason)
    {
        lock (_lock)
        {
            _epoch++;
            _allowed = false;
            _lastReason = reason;
        }
        ServiceLog.Info($"[key-press] key reactions paused: {reason}");
    }

    private async Task LoopAsync(CancellationToken ct, int generation)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int epoch;
                lock (_lock)
                {
                    epoch = _epoch;
                }
                try
                {
                    var seat = await _readSeat().ConfigureAwait(false);
                    var locker = seat is null ? null : await _readLocker().ConfigureAwait(false);
                    var (allowed, reason) = Decide(seat, locker, _expectedUid());
                    Publish(generation, epoch, allowed, reason);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Publish(generation, epoch, false, "gate check failed");
                    ServiceLog.Warn($"[key-press] input gate check failed: {ex.GetType().Name}");
                }
                try { await Task.Delay(_intervalMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            // Any exit, including a stray cancellation, leaves the gate closed for this generation.
            Publish(generation, null, false, "gate stopped");
        }
    }

    private void Publish(int generation, int? epoch, bool allowed, string reason)
    {
        try
        {
            lock (_lock)
            {
                if (generation != _generation || (epoch is { } e && e != _epoch)) return;
                _allowed = allowed;
                if (reason == _lastReason) return;
                _lastReason = reason;
            }
            ServiceLog.Info(allowed
                ? "[key-press] key reactions resumed"
                : $"[key-press] key reactions paused: {reason}");
        }
        finally
        {
            AfterPublish?.Invoke();
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
                return new SeatState(false, null, null, null);
            }

            const string session = "org.freedesktop.login1.Session";
            var cls = await GetAsync(bus, path, session, "Class").ConfigureAwait(false);
            var locked = await GetAsync(bus, path, session, "LockedHint").ConfigureAwait(false);
            var user = await GetAsync(bus, path, session, "User").ConfigureAwait(false);
            uint? uid = null;
            if (user.Signature == "(uo)")
            {
                user.Reader.AlignTo(8);
                uid = user.Reader.ReadUInt32();
            }
            return new SeatState(
                true,
                cls.Signature == "s" ? cls.Reader.ReadString() : null,
                locked.Signature == "b" ? locked.Reader.ReadBool() : null,
                uid);
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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No session bus to ask: logind's LockedHint is the only signal.
            _lockers = null;
            return null;
        }
        return await CheckLockerAsync(
            async name =>
            {
                var reply = await _session.CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus",
                    "org.freedesktop.DBus", "NameHasOwner", "s", w => w.WriteString(name), CallTimeoutMs).ConfigureAwait(false);
                return new DBusReader(reply.Body).ReadBool();
            },
            async locker =>
            {
                var reply = await _session.CallAsync(locker.Name, locker.Path, locker.Name, "GetActive", "", null, CallTimeoutMs)
                    .ConfigureAwait(false);
                return new DBusReader(reply.Body).ReadBool();
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Null when no locker owns a known name or none answers GetActive. A failed or unanswered query
    /// blocks (true): a locker that cannot be asked is treated as up, never as absent. An owner that
    /// lacks the method (an inhibit-only ScreenSaver service) is skipped. The owner list, including an
    /// empty one, is cached so the name lookups do not run every poll.
    /// </summary>
    internal async Task<bool?> CheckLockerAsync(
        Func<string, Task<bool>> nameHasOwner, Func<(string Name, string Path), Task<bool>> getActive)
    {
        try
        {
            var now = Environment.TickCount64;
            var lockers = _lockers;
            if (lockers is null || now - _lockersLookedUpAt > LockerLookupMs)
            {
                var owners = new System.Collections.Generic.List<(string Name, string Path)>();
                foreach (var candidate in Lockers)
                {
                    if (await nameHasOwner(candidate.Name).ConfigureAwait(false))
                    {
                        owners.Add(candidate);
                    }
                }
                lockers = owners.ToArray();
                _lockers = lockers;
                _lockersLookedUpAt = now;
            }
            bool? answer = null;
            foreach (var locker in lockers)
            {
                try
                {
                    if (await getActive(locker).ConfigureAwait(false)) return true;
                    answer = false;
                }
                catch (Exception ex) when (IsMissingMethod(ex))
                {
                }
            }
            return answer;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lockers = null;
            return true;
        }
    }

    // CallAsync surfaces a D-Bus error reply as InvalidOperationException whose message starts with the error name.
    // NotSupported is what Cinnamon's csd-screensaver-proxy answers for GetActive on org.freedesktop.ScreenSaver.
    private static bool IsMissingMethod(Exception ex)
        => ex is InvalidOperationException
            && (ex.Message.StartsWith("D-Bus error org.freedesktop.DBus.Error.UnknownMethod:", StringComparison.Ordinal)
                || ex.Message.StartsWith("D-Bus error org.freedesktop.DBus.Error.UnknownInterface:", StringComparison.Ordinal)
                || ex.Message.StartsWith("D-Bus error org.freedesktop.DBus.Error.UnknownObject:", StringComparison.Ordinal)
                || ex.Message.StartsWith("D-Bus error org.freedesktop.DBus.Error.NotSupported:", StringComparison.Ordinal));

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
            _generation++;
            _cts?.Cancel();
            _cts = null;
            _allowed = false;
            _system?.Dispose();
            _system = null;
        }
    }

    [LibraryImport("libc")]
    private static partial uint getuid();
}
