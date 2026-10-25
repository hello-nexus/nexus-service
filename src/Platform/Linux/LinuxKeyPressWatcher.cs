using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.KeyReactive;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Feeds <see cref="KeyReactiveOverlay.PressKey"/> from the keyboards' evdev
/// nodes, the Linux counterpart of the Windows tray helper's Raw Input watcher.
/// Privacy: the nodes are open only while the overlay is armed (some keyboard has
/// reactions on) AND <see cref="LinuxKeyInputGate"/> allows input, so nothing is
/// read at a login prompt, a lock screen or with every effect off. Key codes are
/// mapped to key names and handed to the overlay; they are never logged, stored
/// or sent anywhere else.
/// </summary>
public sealed partial class LinuxKeyPressWatcher : IHostedService, IDisposable
{
    internal const int PollMs = 500;
    internal const long RescanMs = 2000;
    // One physical key can arrive on two nodes of the same board.
    internal const long DuplicateMs = 30;
    private const long ProbeEveryMs = 10_000;

    internal const ushort EV_KEY = 1;
    // struct input_event on 64-bit: 16-byte timeval, u16 type, u16 code, s32 value.
    internal const int EventSize = 24;

    private const int O_RDONLY = 0;
    private const int O_NONBLOCK = 0x800;
    private const int O_CLOEXEC = 0x80000;
    private const int R_OK = 4;
    private const short POLLIN = 0x001;
    private const short POLLERR = 0x008;
    private const short POLLHUP = 0x010;
    private const short POLLNVAL = 0x020;
    private const int EINTR = 4;
    private const int EAGAIN = 11;
    private const int EACCES = 13;
    private const int EPERM = 1;

    private const int KEY_ENTER = 28;
    private const int KEY_A = 30;
    private const int KEY_Z = 44;
    private const int KEY_SPACE = 57;
    private const int KEY_MUTE = 113;
    private const int KEY_VOLUMEUP = 115;
    private const int KEY_PLAYPAUSE = 164;

    private readonly KeyReactiveOverlay? _overlay;
    private readonly LinuxKeyInputGate? _gate;
    private readonly Action<string> _press;
    private readonly Func<bool> _allowed;
    private readonly Func<string, int> _openNode;
    private readonly string _sysInput;
    private readonly string _devInput;
    private readonly object _lock = new();
    private CancellationTokenSource? _run;
    private int _openCount;
    private long _probedAt = long.MinValue;
    private volatile bool _probeResult;

    public LinuxKeyPressWatcher(KeyReactiveOverlay overlay, LinuxKeyInputGate gate)
        : this(overlay, gate, overlay.PressKey, () => gate.Allowed, "/sys/class/input", "/dev/input", OpenReadOnly)
    {
    }

    internal LinuxKeyPressWatcher(
        KeyReactiveOverlay? overlay, LinuxKeyInputGate? gate, Action<string> press, Func<bool> allowed,
        string sysInput, string devInput, Func<string, int> openNode)
    {
        _overlay = overlay;
        _gate = gate;
        _press = press;
        _allowed = allowed;
        _sysInput = sysInput;
        _devInput = devInput;
        _openNode = openNode;
    }

    internal bool Watching => Volatile.Read(ref _run) is not null;

    /// <summary>Evdev nodes currently held open.</summary>
    internal int OpenCount => Volatile.Read(ref _openCount);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_overlay is not null)
        {
            _overlay.InputAvailable = InputAvailable;
            _overlay.ArmedChanged += SetArmed;
            if (_overlay.Armed)
            {
                SetArmed(true);
            }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _overlay?.ArmedChanged -= SetArmed;
        SetArmed(false);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        SetArmed(false);
        _gate?.Dispose();
    }

    /// <summary>Starts or stops the reader. Returns at once; the reader thread closes the nodes within one poll interval.</summary>
    internal void SetArmed(bool armed)
    {
        lock (_lock)
        {
            if (armed)
            {
                if (_run is not null) return;
                var cts = new CancellationTokenSource();
                _run = cts;
                _gate?.SetActive(true);
                new Thread(() => Run(cts.Token)) { IsBackground = true, Name = "key-press-watcher" }.Start();
                ServiceLog.Info("[key-press] key reactions on");
                return;
            }
            var run = _run;
            if (run is null) return;
            _run = null;
            run.Cancel();
            _gate?.SetActive(false);
            ServiceLog.Info("[key-press] key reactions off");
        }
    }

    /// <summary>True when at least one keyboard node is readable; probed at most every few seconds and never opens a node.</summary>
    internal bool InputAvailable()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _probedAt);
        if (last == long.MinValue || now - last > ProbeEveryMs)
        {
            Interlocked.Exchange(ref _probedAt, now);
            var any = false;
            foreach (var kb in FindKeyboards(_sysInput, _devInput))
            {
                if (access(kb.Node, R_OK) == 0) { any = true; break; }
            }
            _probeResult = any;
        }
        return _probeResult;
    }

    private sealed class Device
    {
        public required string Node;
        public required string Name;
        public required int Fd;
    }

    private void Run(CancellationToken token)
    {
        var open = new Dictionary<string, Device>(StringComparer.Ordinal);
        // Nodes that refused to open; not retried until they leave /sys/class/input or the watcher restarts.
        var denied = new HashSet<string>(StringComparer.Ordinal);
        var recent = new Dictionary<string, long>(StringComparer.Ordinal);
        var buffer = new byte[EventSize * 64];
        var codes = new List<int>(16);
        Device[] devices = Array.Empty<Device>();
        PollFd[] fds = Array.Empty<PollFd>();
        long nextScan = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!_allowed())
                {
                    if (open.Count > 0)
                    {
                        CloseAll(open);
                        devices = Array.Empty<Device>();
                        fds = Array.Empty<PollFd>();
                    }
                    nextScan = 0;
                    token.WaitHandle.WaitOne(PollMs);
                    continue;
                }

                var now = Environment.TickCount64;
                if (now >= nextScan)
                {
                    nextScan = now + RescanMs;
                    if (Rescan(open, denied))
                    {
                        devices = Snapshot(open);
                        fds = new PollFd[devices.Length];
                    }
                }
                if (devices.Length == 0)
                {
                    token.WaitHandle.WaitOne(PollMs);
                    continue;
                }

                for (var i = 0; i < devices.Length; i++)
                {
                    fds[i] = new PollFd { Fd = devices[i].Fd, Events = POLLIN };
                }
                int ready;
                unsafe
                {
                    fixed (PollFd* p = fds)
                    {
                        ready = poll(p, (nuint)fds.Length, PollMs);
                    }
                }
                if (ready < 0)
                {
                    if (Marshal.GetLastPInvokeError() != EINTR) token.WaitHandle.WaitOne(PollMs);
                    continue;
                }
                if (ready == 0) continue;

                var lost = false;
                for (var i = 0; i < devices.Length; i++)
                {
                    var revents = fds[i].Revents;
                    if (revents == 0) continue;
                    var gone = (revents & (POLLERR | POLLHUP | POLLNVAL)) != 0;
                    if ((revents & POLLIN) != 0 && !ReadEvents(devices[i].Fd, buffer, codes))
                    {
                        gone = true;
                    }
                    if (gone)
                    {
                        ServiceLog.Info($"[key-press] keyboard gone: {devices[i].Name}");
                        close(devices[i].Fd);
                        open.Remove(devices[i].Node);
                        Interlocked.Decrement(ref _openCount);
                        lost = true;
                    }
                }
                if (codes.Count > 0)
                {
                    Deliver(codes, recent, token);
                    codes.Clear();
                }
                if (lost)
                {
                    devices = Snapshot(open);
                    fds = new PollFd[devices.Length];
                }
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[key-press] keyboard watch stopped: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            CloseAll(open);
        }
    }

    private static Device[] Snapshot(Dictionary<string, Device> open)
    {
        var list = new Device[open.Count];
        open.Values.CopyTo(list, 0);
        return list;
    }

    private void CloseAll(Dictionary<string, Device> open)
    {
        foreach (var d in open.Values)
        {
            close(d.Fd);
            Interlocked.Decrement(ref _openCount);
        }
        open.Clear();
    }

    private bool Rescan(Dictionary<string, Device> open, HashSet<string> denied)
    {
        var changed = false;
        var listed = FindKeyboards(_sysInput, _devInput);
        denied.RemoveWhere(n => !listed.Exists(k => k.Node == n));
        foreach (var kb in listed)
        {
            if (open.ContainsKey(kb.Node) || denied.Contains(kb.Node)) continue;
            var fd = _openNode(kb.Node);
            if (fd < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                denied.Add(kb.Node);
                ServiceLog.Warn($"[key-press] cannot read {kb.Name}: "
                    + (errno is EACCES or EPERM ? "permission denied" : $"errno {errno}"));
                continue;
            }
            open[kb.Node] = new Device { Node = kb.Node, Name = kb.Name, Fd = fd };
            Interlocked.Increment(ref _openCount);
            ServiceLog.Info($"[key-press] reading keyboard: {kb.Name}");
            changed = true;
        }
        return changed;
    }

    /// <summary>One read after a ready poll. False when the node is gone (EOF or a hard error), so it is dropped rather than polled in a loop.</summary>
    private static unsafe bool ReadEvents(int fd, byte[] buffer, List<int> codes)
    {
        nint n;
        fixed (byte* p = buffer)
        {
            n = read(fd, p, (nuint)buffer.Length);
        }
        if (n > 0)
        {
            ParseKeyDowns(buffer.AsSpan(0, (int)n), codes);
            return true;
        }
        if (n == 0) return false;
        var errno = Marshal.GetLastPInvokeError();
        return errno is EAGAIN or EINTR;
    }

    private void Deliver(List<int> codes, Dictionary<string, long> recent, CancellationToken token)
    {
        var now = Environment.TickCount64;
        foreach (var code in codes)
        {
            var name = KeyNames.FromLinuxKeyCode(code);
            if (name is null) continue;
            if (recent.TryGetValue(name, out var at) && now - at < DuplicateMs) continue;
            recent[name] = now;
            if (token.IsCancellationRequested || !_allowed()) continue;
            _press(name);
        }
        if (recent.Count > 256)
        {
            recent.Clear();
        }
    }

    /// <summary>Collects the codes of key-down events (value 1); releases and autorepeat are skipped.</summary>
    internal static void ParseKeyDowns(ReadOnlySpan<byte> data, List<int> codes)
    {
        for (var off = 0; off + EventSize <= data.Length; off += EventSize)
        {
            var ev = data.Slice(off, EventSize);
            if (BinaryPrimitives.ReadUInt16LittleEndian(ev.Slice(16)) != EV_KEY) continue;
            if (BinaryPrimitives.ReadInt32LittleEndian(ev.Slice(20)) != 1) continue;
            codes.Add(BinaryPrimitives.ReadUInt16LittleEndian(ev.Slice(18)));
        }
    }

    internal readonly record struct InputKeyboard(string Node, string Name);

    /// <summary>
    /// Keyboard nodes under <paramref name="sysInput"/> (normally /sys/class/input):
    /// event devices that report EV_KEY with the letter keys, space and enter, or
    /// media keys (consumer-control interfaces). Sorted by node.
    /// </summary>
    internal static List<InputKeyboard> FindKeyboards(string sysInput, string devInput)
    {
        var found = new List<InputKeyboard>();
        IEnumerable<string> entries;
        try
        {
            if (!Directory.Exists(sysInput)) return found;
            entries = Directory.EnumerateDirectories(sysInput, "event*");
        }
        catch (IOException) { return found; }
        catch (UnauthorizedAccessException) { return found; }

        foreach (var dir in entries)
        {
            var name = Path.GetFileName(dir);
            if (name.Length <= 5 || !int.TryParse(name.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }
            var caps = Path.Combine(dir, "device", "capabilities");
            if (!HasBit(ReadText(Path.Combine(caps, "ev")), EV_KEY)) continue;
            var keys = ReadText(Path.Combine(caps, "key"));
            var letters = HasBit(keys, KEY_A) && HasBit(keys, KEY_Z) && HasBit(keys, KEY_SPACE) && HasBit(keys, KEY_ENTER);
            var media = HasBit(keys, KEY_PLAYPAUSE) || HasBit(keys, KEY_MUTE) || HasBit(keys, KEY_VOLUMEUP);
            if (!letters && !media) continue;
            var label = ReadText(Path.Combine(dir, "device", "name"));
            found.Add(new InputKeyboard(Path.Combine(devInput, name), string.IsNullOrEmpty(label) ? name : label));
        }
        found.Sort((a, b) => string.CompareOrdinal(a.Node, b.Node));
        return found;
    }

    /// <summary>Tests <paramref name="bit"/> in a sysfs capability bitmap: space-separated hex words, most significant first.</summary>
    internal static bool HasBit(string? bitmap, int bit)
    {
        if (string.IsNullOrWhiteSpace(bitmap) || bit < 0) return false;
        var words = bitmap.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var index = words.Length - 1 - bit / 64;
        if (index < 0) return false;
        return ulong.TryParse(words[index], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var word)
            && ((word >> (bit % 64)) & 1UL) != 0;
    }

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    private static int OpenReadOnly(string path) => open(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int access(string path, int mode);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial nint read(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int poll(PollFd* fds, nuint count, int timeoutMs);
}
