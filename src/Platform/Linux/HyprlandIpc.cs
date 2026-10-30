using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Raw client for Hyprland's request socket, one connection per request. Hyprland ignores the peer uid,
/// so the root daemon connects directly. The instance is the newest one whose socket answers.
/// </summary>
internal static class HyprlandIpc
{
    private const int TimeoutMs = 2000;
    private const int MaxReplyBytes = 4 * 1024 * 1024;

    internal readonly record struct Monitor(
        int Id, string Name, int Width, int Height, int Transform, int? WorkspaceId, string WorkspaceName);

    internal readonly record struct Client(
        string Address, string Class, string InitialClass, int MonitorId, bool Mapped, int Fullscreen, int FullscreenClient);

    private static string? _socketPath;

    /// <summary>Runtime dir of the adopted session, or the process's own when it is the session user.</summary>
    internal static string? RuntimeDir()
    {
        if (LinuxSession.SessionUid is { } uid)
            return $"/run/user/{uid}";
        var env = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return string.IsNullOrEmpty(env) ? null : env;
    }

    /// <summary>Instance sockets under <c>hypr/*</c>, newest first.</summary>
    internal static List<string> InstanceSockets(string runtimeDir)
    {
        var found = new List<(string Path, DateTime Time)>();
        try
        {
            var root = Path.Combine(runtimeDir, "hypr");
            if (!Directory.Exists(root))
                return [];
            foreach (var dir in Directory.GetDirectories(root))
            {
                var sock = Path.Combine(dir, ".socket.sock");
                if (File.Exists(sock))
                    found.Add((sock, File.GetLastWriteTimeUtc(sock)));
            }
        }
        catch (Exception)
        {
            return [];
        }
        return found.OrderByDescending(f => f.Time).Select(f => f.Path).ToList();
    }

    /// <summary>Path of the live instance's request socket, or null when no Hyprland answers.</summary>
    internal static string? FindSocket()
    {
        if (!OperatingSystem.IsLinux() || RuntimeDir() is not { } runtime)
            return null;
        var cached = _socketPath;
        if (cached is not null && Send(cached, "/version") is not null)
            return cached;
        _socketPath = null;
        foreach (var candidate in InstanceSockets(runtime))
        {
            if (Send(candidate, "/version") is null)
                continue;
            _socketPath = candidate;
            return candidate;
        }
        return null;
    }

    /// <summary>A send function bound to the live instance, or null when no Hyprland answers.</summary>
    internal static Func<string, string?>? Open()
    {
        var path = FindSocket();
        return path is null ? null : request => Send(path, request);
    }

    /// <summary>The event socket beside a request socket.</summary>
    internal static string EventSocketPath(string requestSocket)
        => Path.Combine(Path.GetDirectoryName(requestSocket) ?? "", ".socket2.sock");

    /// <summary>True when SO_PEERCRED bytes (pid, uid, gid) name the expected uid.</summary>
    internal static bool PeerUidMatches(byte[] credentials, uint expectedUid)
        => credentials.Length >= 12 && BitConverter.ToUInt32(credentials, 4) == expectedUid;

    // Linux values; .NET's SocketOptionLevel.Socket is the BSD 65535.
    internal const int LinuxSolSocket = 1;
    internal const int LinuxSoPeerCred = 17;

    /// <summary>Connects to a real socket file owned by the session user, or throws.</summary>
    internal static Socket Connect(string socketPath)
    {
        if (new FileInfo(socketPath).LinkTarget is not null)
            throw new IOException("socket path is a symlink");
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
        {
            SendTimeout = TimeoutMs,
            ReceiveTimeout = TimeoutMs,
        };
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            if (OperatingSystem.IsLinux() && LinuxSession.SessionUid is { } uid
                && !PeerUidMatches(PeerCredentials(socket), uid))
            {
                throw new IOException("socket peer is not the session user");
            }
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static byte[] PeerCredentials(Socket socket)
    {
        var buffer = new byte[12];
        socket.GetRawSocketOption(LinuxSolSocket, LinuxSoPeerCred, buffer);
        return buffer;
    }

    /// <summary>The reply, or null when the connection or the exchange failed.</summary>
    internal static string? Send(string socketPath, string request)
    {
        try
        {
            using var socket = Connect(socketPath);
            socket.Send(Encoding.UTF8.GetBytes(request));
            using var reply = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var n = socket.Receive(buffer);
                if (n <= 0)
                    break;
                reply.Write(buffer, 0, n);
                if (reply.Length > MaxReplyBytes)
                    return null;
            }
            return Encoding.UTF8.GetString(reply.GetBuffer(), 0, (int)reply.Length);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Complete lines from a chunk of the event stream; the unterminated tail stays in <paramref name="pending"/>.</summary>
    internal static List<string> TakeLines(ref string pending, string chunk)
    {
        pending += chunk;
        var lines = new List<string>();
        int nl;
        while ((nl = pending.IndexOf('\n')) >= 0)
        {
            lines.Add(pending[..nl]);
            pending = pending[(nl + 1)..];
        }
        return lines;
    }

    internal static bool IsConfigReloaded(string line) => line.StartsWith("configreloaded>>", StringComparison.Ordinal);

    /// <summary>Eval and dispatch answer exactly "ok" on success and an error text otherwise.</summary>
    internal static bool IsOk(string? reply) => reply is not null && reply.Trim().StartsWith("ok", StringComparison.Ordinal);

    internal static List<Monitor> ParseMonitors(string? json)
    {
        var result = new List<Monitor>();
        foreach (var m in Elements(json))
        {
            if (!TryString(m, "name", out var name) || !TryInt(m, "id", out var id))
                continue;
            int? workspaceId = null;
            var workspaceName = "";
            if (m.TryGetProperty("activeWorkspace", out var ws) && ws.ValueKind == JsonValueKind.Object)
            {
                if (TryInt(ws, "id", out var wid))
                    workspaceId = wid;
                TryString(ws, "name", out workspaceName);
            }
            TryInt(m, "width", out var width);
            TryInt(m, "height", out var height);
            TryInt(m, "transform", out var transform);
            result.Add(new Monitor(id, name, width, height, transform, workspaceId, workspaceName));
        }
        return result;
    }

    internal static List<Client> ParseClients(string? json)
    {
        var result = new List<Client>();
        foreach (var c in Elements(json))
        {
            if (!TryString(c, "address", out var address))
                continue;
            TryString(c, "class", out var cls);
            TryString(c, "initialClass", out var initialClass);
            TryInt(c, "monitor", out var monitor);
            var mapped = !c.TryGetProperty("mapped", out var m) || m.ValueKind != JsonValueKind.False;
            var fullscreen = FullscreenLevel(c);
            TryInt(c, "fullscreenClient", out var fullscreenClient);
            result.Add(new Client(address, cls, initialClass, monitor, mapped, fullscreen, fullscreenClient));
        }
        return result;
    }

    /// <summary>Touch device names from <c>j/devices</c>, already in Hyprland's internal form.</summary>
    internal static List<string> ParseTouchDevices(string? json)
    {
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("touch", out var touch)
                && touch.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in touch.EnumerateArray())
                {
                    if (d.ValueKind == JsonValueKind.Object && TryString(d, "name", out var name) && name.Length > 0)
                        result.Add(name);
                }
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }

    /// <summary>Address of the focused window from <c>j/activewindow</c>, null when none (Hyprland replies with an empty object).</summary>
    internal static string? ParseActiveAddress(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            return doc.RootElement.ValueKind == JsonValueKind.Object && TryString(doc.RootElement, "address", out var a) && a.Length > 0
                ? a
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Position from <c>j/cursorpos</c>, null when unparseable.</summary>
    internal static (int X, int Y)? ParseCursor(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            return doc.RootElement.ValueKind == JsonValueKind.Object && TryInt(doc.RootElement, "x", out var x) && TryInt(doc.RootElement, "y", out var y)
                ? (x, y)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>String value of a <c>j/getoption</c> reply when the user set it, else null.</summary>
    internal static string? ParseSetStringOption(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("set", out var set) && set.ValueKind == JsonValueKind.True
                && TryString(root, "str", out var value) && value.Length > 0)
            {
                return value;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    /// <summary>Hyprland's internal device name: lowercase, with space, newline and comma as a dash.</summary>
    internal static string NormalizeDeviceName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(ch is ' ' or '\n' or ',' ? '-' : ch is >= 'A' and <= 'Z' ? (char)(ch + 32) : ch);
        return sb.ToString();
    }

    /// <summary>A Lua string literal; null when the text holds a control character that has no safe form.</summary>
    internal static string? LuaString(string text)
    {
        var sb = new StringBuilder(text.Length + 2).Append('"');
        foreach (var ch in text)
        {
            if (ch is '\\' or '"')
                sb.Append('\\').Append(ch);
            else if (char.IsControl(ch))
                return null;
            else
                sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }

    private static IEnumerable<JsonElement> Elements(string? json)
    {
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(json ?? "");
        }
        catch (JsonException)
        {
        }
        if (doc is null)
            yield break;
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                yield break;
            // Cloned: the document is disposed when the enumeration ends.
            foreach (var e in doc.RootElement.EnumerateArray().Select(e => e.Clone()))
            {
                if (e.ValueKind == JsonValueKind.Object)
                    yield return e;
            }
        }
    }

    /// <summary>Older Hyprland reports fullscreen as a bool, newer as the mode number.</summary>
    private static int FullscreenLevel(JsonElement client)
    {
        if (!client.TryGetProperty("fullscreen", out var p))
            return 0;
        return p.ValueKind switch
        {
            JsonValueKind.True => 2,
            JsonValueKind.Number when p.TryGetInt32(out var n) => n,
            _ => 0,
        };
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
        {
            value = p.GetString() ?? "";
            return true;
        }
        value = "";
        return false;
    }

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        if (element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out value))
            return true;
        value = 0;
        return false;
    }
}
