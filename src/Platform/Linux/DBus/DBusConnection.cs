using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Platform.Linux.DBus;

/// <summary>Which bus a <see cref="DBusConnection"/> targets.</summary>
public enum DBusBusKind
{
    /// <summary>The per-user session bus (tray, media, portals). Rejects root.</summary>
    Session,

    /// <summary>The machine-wide system bus (logind, udisks, ...). Accepts root directly.</summary>
    System,
}

/// <summary>
/// A pure-C# D-Bus client. Handles SASL EXTERNAL auth, message marshalling,
/// method calls with reply matching, and dispatch of incoming method calls to
/// registered path handlers.
///
/// Defaults to the session bus, shared across subsystems (one connection per
/// process, multiple handlers for different object paths: tray SNI,
/// screen-time receiver, etc.). A <see cref="DBusBusKind.System"/> instance is
/// a private, single-purpose connection (e.g. logind PrepareForSleep) - the
/// system bus authenticates a root daemon's own euid directly, so it skips
/// the session bus's user-impersonation dance.
/// </summary>
public sealed class DBusConnection : IDisposable
{
    private static int _instanceCounter;
    private readonly int _instanceId = Interlocked.Increment(ref _instanceCounter);
    private readonly DBusBusKind _bus;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Socket? _socket;
    private NetworkStream? _stream;
    private uint _serial;
    private readonly object _sendLock = new();
    private readonly Dictionary<uint, TaskCompletionSource<DBusMessage>> _pending = new();
    private readonly Dictionary<string, Func<DBusMessage, DBusMessage?>> _handlers = new();
    private readonly Dictionary<string, TaskCompletionSource<DBusMessage>> _signalWaiters = new();
    private Task? _readerTask;
    private bool _started;
    private bool _hasConnectedBefore;
    private readonly object _connLock = new();

    public DBusConnection(DBusBusKind bus = DBusBusKind.Session)
    {
        _bus = bus;
    }

    public string UniqueName { get; private set; } = "";
    public bool Connected { get; private set; }

    /// <summary>
    /// Raised after the connection is re-established following a drop (logout,
    /// bus restart) - not on the first connect. Subsystems holding bus-side
    /// registrations (tray StatusNotifierItem, signal matches) re-establish
    /// them here. Consumers that only issue calls don't need this: every call
    /// is preceded by <see cref="StartAsync"/>, which now transparently
    /// reconnects.
    /// </summary>
    public event Action? Reconnected;

    /// <summary>Connect + authenticate + Hello. Idempotent; safe to call from multiple subsystems.</summary>
    public async Task StartAsync()
    {
        if (_started)
            return;
        var reconnected = false;
        await _startLock.WaitAsync();
        try
        {
            if (_started)
                return;
            var socketPath = ResolveSocketPath();
            if (!File.Exists(socketPath))
            {
                throw new InvalidOperationException(
                    $"D-Bus {(_bus == DBusBusKind.System ? "system" : "session")} bus socket not found at {socketPath}");
            }
            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            if (_bus == DBusBusKind.System)
            {
                // The system bus authenticates the connecting process's own euid
                // and accepts root directly, but a concurrent session-bus connect
                // dropping euid process-wide would poison SO_PEERCRED for this
                // socket too - serialize against that critical section.
                LinuxSession.ConnectSerialized(
                    () => _socket.Connect(new UnixDomainSocketEndPoint(socketPath)));
            }
            else
            {
                // The session bus authenticates by the peer's effective uid at
                // connect time and rejects root, so a root daemon must connect as
                // the session user. Synchronous connect (no await) keeps the
                // process-wide euid drop tight around just this call. No-op as --user.
                LinuxSession.ConnectAsSessionUser(
                    () => _socket.Connect(new UnixDomainSocketEndPoint(socketPath)));
            }
            _stream = new NetworkStream(_socket, ownsSocket: false);

            await AuthAsync();
            _readerTask = Task.Run(ReadLoopAsync);
            UniqueName = await HelloAsync();
            Connected = true;
            _started = true;
            reconnected = _hasConnectedBefore;
            _hasConnectedBefore = true;
            Console.Error.WriteLine($"[dbus] connection {(reconnected ? "re-" : "")}started, unique={UniqueName} instance#{_instanceId}");
        }
        finally
        {
            _startLock.Release();
        }
        // Fire outside the lock so a handler that re-registers (and may call
        // back into the connection) can't deadlock on _startLock.
        if (reconnected)
        {
            try { Reconnected?.Invoke(); }
            catch (Exception ex) { Console.Error.WriteLine($"[dbus] reconnect handler failed: {ex.Message}"); }
        }
    }

    public async Task<DBusMessage> CallAsync(
        string destination,
        string path,
        string iface,
        string member,
        string signature,
        Action<DBusWriter>? body,
        int timeoutMs = 5000)
    {
        var serial = NextSerial();
        var msg = new DBusMessage
        {
            Type = DBusMessageType.MethodCall,
            Flags = 0,
            Serial = serial,
            Destination = destination,
            Path = path,
            Interface = iface,
            Member = member,
            Signature = signature,
        };
        if (body is not null)
        {
            var w = new DBusWriter();
            body(w);
            msg.Body = w.ToArray();
        }
        var tcs = new TaskCompletionSource<DBusMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            _pending[serial] = tcs;
        }
        try
        {
            SendMessage(msg);
        }
        catch
        {
            // Never awaited: left registered, the next disconnect would fault this waiter with nobody to observe it.
            lock (_pending)
            {
                _pending.Remove(serial);
            }
            // The disconnect handler may have faulted this waiter first, in the window before it was removed.
            _ = tcs.Task.Exception;
            throw;
        }
        using var timeout = new CancellationTokenSource(timeoutMs);
        using var reg = timeout.Token.Register(() => tcs.TrySetException(new TimeoutException($"{iface}.{member} timed out")));
        return await tcs.Task;
    }

    public async Task<uint> RequestNameAsync(string name, uint flags = 0)
    {
        var reply = await CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus",
            "org.freedesktop.DBus", "RequestName", "su", w =>
            {
                w.WriteString(name);
                w.WriteUInt32(flags);
            });
        return new DBusReader(reply.Body).ReadUInt32();
    }

    /// <summary>Subscribe to a class of signals on the bus (org.freedesktop.DBus.AddMatch).</summary>
    public Task AddMatchAsync(string rule)
        => CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus",
            "org.freedesktop.DBus", "AddMatch", "s", w => w.WriteString(rule));

    /// <summary>
    /// Await a single signal with <paramref name="member"/> on object
    /// <paramref name="path"/>. Register this BEFORE issuing the call that
    /// triggers it (e.g. an xdg-desktop-portal request) so a fast reply can't
    /// race ahead of the waiter. Times out so a dropped signal can't hang the
    /// caller forever. Requires a matching <see cref="AddMatchAsync"/> first.
    /// </summary>
    public Task<DBusMessage> WaitForSignalAsync(string path, string member, int timeoutMs = 90000)
    {
        var key = $"{path}|{member}";
        var tcs = new TaskCompletionSource<DBusMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_signalWaiters)
        {
            _signalWaiters[key] = tcs;
        }
        var cts = new CancellationTokenSource(timeoutMs);
        cts.Token.Register(() =>
        {
            lock (_signalWaiters)
            {
                _signalWaiters.Remove(key);
            }
            tcs.TrySetException(new TimeoutException($"signal {member} on {path} timed out"));
        });
        // Also reads the waiter's fault: a caller that stopped awaiting (cancelled, or superseded under the same key)
        // would otherwise leave the disconnect or timeout exception unobserved.
        _ = tcs.Task.ContinueWith(t =>
        {
            _ = t.Exception;
            cts.Dispose();
        }, TaskScheduler.Default);
        return tcs.Task;
    }

    /// <summary>Register a handler for incoming method calls on a given object path.</summary>
    public void RegisterHandler(string path, Func<DBusMessage, DBusMessage?> handler)
    {
        lock (_handlers)
        {
            _handlers[path] = handler;
        }
    }

    public void UnregisterHandler(string path)
    {
        lock (_handlers)
        {
            _handlers.Remove(path);
        }
    }

    internal void SendMessage(DBusMessage msg)
    {
        var bytes = msg.Encode();
        var stream = _stream ?? throw new IOException("D-Bus not connected");
        lock (_sendLock)
        {
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    internal int PendingCallCount
    {
        get
        {
            lock (_pending)
            {
                return _pending.Count;
            }
        }
    }

    internal uint NextSerial()
    {
        var s = Interlocked.Increment(ref _serial);
        return s == 0 ? Interlocked.Increment(ref _serial) : s;
    }

    public void Dispose()
    {
        _cts.Cancel();
        Connected = false;
        try
        { _stream?.Dispose(); }
        catch { }
        try
        { _socket?.Dispose(); }
        catch { }
    }

    private string ResolveSocketPath() => ResolveSocketPath(
        _bus,
        Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"),
        Environment.GetEnvironmentVariable("DBUS_SYSTEM_BUS_ADDRESS"),
        GetUid());

    /// <summary>
    /// Pure socket-path resolution: env override first, per-bus well-known
    /// default otherwise. Session default is the per-uid XDG runtime socket;
    /// system default is the D-Bus spec's well-known path (unaffected by uid).
    /// </summary>
    internal static string ResolveSocketPath(DBusBusKind bus, string? sessionAddress, string? systemAddress, int uid)
    {
        if (bus == DBusBusKind.System)
        {
            var fromEnv = ExtractUnixPath(systemAddress);
            return fromEnv ?? "/run/dbus/system_bus_socket";
        }
        return ExtractUnixPath(sessionAddress) ?? $"/run/user/{uid}/bus";
    }

    private static string? ExtractUnixPath(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return null;
        }
        const string prefix = "unix:path=";
        var idx = address.IndexOf(prefix, StringComparison.Ordinal);
        return idx >= 0 ? address[(idx + prefix.Length)..].Split(',')[0] : null;
    }

    private static int GetUid()
    {
        try
        {
            foreach (var line in File.ReadAllLines("/proc/self/status"))
            {
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var uid))
                    {
                        return uid;
                    }
                }
            }
        }
        catch { }
        return 1000;
    }

    private async Task AuthAsync()
    {
        _stream!.WriteByte(0);
        // EXTERNAL auth must claim the uid the bus saw via SO_PEERCRED at
        // connect. A root daemon connected as the session user on the session
        // bus (euid drop), so it must authenticate as that uid, not its real
        // uid (0); the system bus connects (and so authenticates) as our own
        // real uid, session-impersonation or not.
        var uid = _bus == DBusBusKind.System
            ? (uint)GetUid()
            : LinuxSession.SessionUid ?? (uint)GetUid();
        var uidHex = ToHex(uid.ToString());
        await WriteLineAsync($"AUTH EXTERNAL {uidHex}");
        var line = await ReadLineAsync();
        if (!line.StartsWith("OK ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"D-Bus auth failed: {line}");
        }
        await WriteLineAsync("BEGIN");
    }

    private static string ToHex(string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    private async Task WriteLineAsync(string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        await _stream!.WriteAsync(bytes);
    }

    private async Task<string> ReadLineAsync()
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            var n = await _stream!.ReadAsync(buf.AsMemory(0, 1));
            if (n == 0)
                break;
            var c = (char)buf[0];
            if (c == '\r')
                continue;
            if (c == '\n')
                break;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private async Task<string> HelloAsync()
    {
        var reply = await CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus",
            "org.freedesktop.DBus", "Hello", "", null);
        return new DBusReader(reply.Body).ReadString();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var msg = await DBusMessage.ReadAsync(_stream!, _cts.Token);
                if (msg is null)
                {
                    if (!_cts.IsCancellationRequested)
                        HandleDisconnect("bus connection closed (EOF)");
                    break;
                }
                HandleIncoming(msg);
            }
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
                HandleDisconnect($"reader loop error: {ex.Message}");
        }
    }

    /// <summary>
    /// Tear down a dropped connection so the next <see cref="StartAsync"/>
    /// rebuilds it. Previously <c>_started</c> stayed true after the bus went
    /// away (logout, bus restart) and every later call wrote to a dead socket
    /// forever. Faults in-flight calls/waiters so callers fail fast and retry
    /// on their next poll instead of hanging until their per-call timeout.
    /// </summary>
    private void HandleDisconnect(string reason)
    {
        Socket? socket;
        NetworkStream? stream;
        bool wasStarted;
        lock (_connLock)
        {
            wasStarted = _started;
            _started = false;
            Connected = false;
            socket = _socket;
            stream = _stream;
            _socket = null;
            _stream = null;
        }
        try { stream?.Dispose(); } catch { }
        try { socket?.Dispose(); } catch { }

        var ex = new IOException($"D-Bus connection lost: {reason}");
        lock (_pending)
        {
            foreach (var tcs in _pending.Values) tcs.TrySetException(ex);
            _pending.Clear();
        }
        lock (_signalWaiters)
        {
            foreach (var tcs in _signalWaiters.Values) tcs.TrySetException(ex);
            _signalWaiters.Clear();
        }
        if (wasStarted)
            Console.Error.WriteLine($"[dbus] {reason}; will reconnect on next use (instance#{_instanceId})");
    }

    private void HandleIncoming(DBusMessage msg)
    {
        if (msg.Type == DBusMessageType.MethodReturn || msg.Type == DBusMessageType.Error)
        {
            TaskCompletionSource<DBusMessage>? tcs = null;
            lock (_pending)
            {
                if (msg.ReplySerial != 0 && _pending.TryGetValue(msg.ReplySerial, out tcs))
                {
                    _pending.Remove(msg.ReplySerial);
                }
            }
            if (tcs is not null)
            {
                if (msg.Type == DBusMessageType.Error)
                {
                    tcs.TrySetException(new InvalidOperationException(
                        $"D-Bus error {msg.ErrorName}: {TryReadErrorMessage(msg.Body)}"));
                }
                else
                {
                    tcs.TrySetResult(msg);
                }
            }
            return;
        }

        if (msg.Type == DBusMessageType.Signal)
        {
            TaskCompletionSource<DBusMessage>? waiter = null;
            var key = $"{msg.Path}|{msg.Member}";
            lock (_signalWaiters)
            {
                if (_signalWaiters.TryGetValue(key, out waiter))
                    _signalWaiters.Remove(key);
            }
            waiter?.TrySetResult(msg);
            return;
        }

        if (msg.Type == DBusMessageType.MethodCall)
        {
            _ = Task.Run(() => DispatchMethodCall(msg));
        }
    }

    private void DispatchMethodCall(DBusMessage msg)
    {
        try
        {
            Func<DBusMessage, DBusMessage?>? handler = null;
            lock (_handlers)
            {
                if (msg.Path is not null)
                {
                    _handlers.TryGetValue(msg.Path, out handler);
                }
            }

            Console.Error.WriteLine($"[dbus] call {msg.Path} {msg.Interface}.{msg.Member} from {msg.Sender} handler={(handler != null)}");

            DBusMessage? reply;
            if (handler is null)
            {
                reply = MakeErrorReply(msg, "org.freedesktop.DBus.Error.UnknownObject",
                    $"no handler for {msg.Path}");
            }
            else
            {
                reply = handler(msg);
            }

            if (reply is not null)
            {
                SendMessage(reply);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[dbus] handler {msg.Interface}.{msg.Member} failed: {ex.Message}");
            SendMessage(MakeErrorReply(msg, "org.freedesktop.DBus.Error.Failed", ex.Message));
        }
    }

    private static string TryReadErrorMessage(byte[] body)
    {
        if (body.Length == 0)
            return "";
        try
        { return new DBusReader(body).ReadString(); }
        catch { return ""; }
    }

    public DBusMessage MakeReply(DBusMessage call, string signature, Action<DBusWriter>? body)
    {
        var reply = new DBusMessage
        {
            Type = DBusMessageType.MethodReturn,
            Flags = 1,
            Serial = NextSerial(),
            ReplySerial = call.Serial,
            Destination = call.Sender,
            Signature = signature,
        };
        if (body is not null && signature.Length > 0)
        {
            var w = new DBusWriter();
            body(w);
            reply.Body = w.ToArray();
        }
        return reply;
    }

    public DBusMessage MakeErrorReply(DBusMessage call, string errorName, string message)
    {
        var reply = new DBusMessage
        {
            Type = DBusMessageType.Error,
            Flags = 1,
            Serial = NextSerial(),
            ReplySerial = call.Serial,
            Destination = call.Sender,
            ErrorName = errorName,
            Signature = "s",
        };
        var w = new DBusWriter();
        w.WriteString(message);
        reply.Body = w.ToArray();
        return reply;
    }
}
