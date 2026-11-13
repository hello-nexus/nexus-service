using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Service.Models.Peripherals.Keeb;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Keeb;

/// <summary>
/// Linux keyboard input injection via the kernel uinput subsystem
/// (<c>/dev/uinput</c>). Creates a single virtual keyboard on first use and
/// keeps it open for the provider's lifetime, then writes <c>input_event</c>
/// records to play back macro strokes - the Linux counterpart to
/// <see cref="WindowsInputter"/>'s SendInput path. AOT-safe: blittable
/// <c>[LibraryImport]</c> P/Invoke into libc (open/close/ioctl/write), no
/// reflection. Needs write access to <c>/dev/uinput</c> (see the bundled udev
/// rule putting it in the <c>input</c> group).
/// </summary>
public sealed partial class LinuxInputter : IInputterProvider, IDisposable
{
    private readonly object _lock = new();
    private int _fd = -1;
    private bool _failed;
    private bool _emitWarned;

    public void Send(InputterBody body)
    {
        if (!OperatingSystem.IsLinux()) return;
        if (body.Strokes is null || body.Strokes.Count == 0) return;

        lock (_lock)
        {
            if (!EnsureDevice())
                return;

            foreach (var stroke in body.Strokes)
            {
                var code = ParseKey(stroke.Key);
                if (code == 0) continue;

                var down = string.Equals(stroke.Type, "keydown", StringComparison.OrdinalIgnoreCase);

                // Press modifiers
                if (stroke.Ctrl) Key(KEY_LEFTCTRL, true);
                if (stroke.Shift) Key(KEY_LEFTSHIFT, true);
                if (stroke.Alt) Key(KEY_LEFTALT, true);
                if (stroke.Meta) Key(KEY_LEFTMETA, true);

                // Key event
                Key(code, down);

                if (stroke.Duration > 0)
                    Thread.Sleep(stroke.Duration);

                // Release modifiers (reverse order)
                if (stroke.Meta) Key(KEY_LEFTMETA, false);
                if (stroke.Alt) Key(KEY_LEFTALT, false);
                if (stroke.Shift) Key(KEY_LEFTSHIFT, false);
                if (stroke.Ctrl) Key(KEY_LEFTCTRL, false);
            }
        }
    }

    private bool EnsureDevice()
    {
        if (_fd >= 0) return true;
        if (_failed) return false;

        var fd = open("/dev/uinput", O_WRONLY | O_CLOEXEC);
        if (fd < 0)
        {
            _failed = true;
            ServiceLog.Warn("[keeb] /dev/uinput open failed - keyboard macros unavailable (check udev rule / 'input' group membership).");
            return false;
        }

        try
        {
            ioctl(fd, UI_SET_EVBIT, EV_KEY);
            ioctl(fd, UI_SET_EVBIT, EV_SYN);
            foreach (var code in AllKeyCodes())
                ioctl(fd, UI_SET_KEYBIT, code);

            var setup = new uinput_setup { ff_effects_max = 0 };
            setup.id.bustype = BUS_VIRTUAL;
            setup.id.vendor = 0x3402;  // Nexus
            setup.id.product = 0x4E58; // "NX"
            setup.id.version = 1;
            SetName(ref setup, "Nexus Virtual Keyboard");

            if (ioctlSetup(fd, UI_DEV_SETUP, in setup) < 0 || ioctl(fd, UI_DEV_CREATE, 0) < 0)
            {
                close(fd);
                _failed = true;
                ServiceLog.Warn("[keeb] uinput device setup failed - keyboard macros unavailable.");
                return false;
            }

            // The input stack (libinput / X / Wayland) needs a beat to notice the
            // freshly created device and start reading it; events written before
            // it is bound are silently dropped. One-time settle on first use only.
            Thread.Sleep(200);
            _fd = fd;
            return true;
        }
        catch (Exception ex)
        {
            try { close(fd); } catch { }
            _failed = true;
            ServiceLog.Error($"[keeb] uinput init error: {ex.Message}");
            return false;
        }
    }

    private void Key(int code, bool down)
    {
        Emit(EV_KEY, (ushort)code, down ? 1 : 0);
        Emit(EV_SYN, SYN_REPORT, 0);
    }

    private void Emit(ushort type, ushort code, int value)
    {
        var ev = new input_event { type = type, code = code, value = value };
        if (write(_fd, in ev, (nuint)Marshal.SizeOf<input_event>()) < 0 && !_emitWarned)
        {
            _emitWarned = true;
            ServiceLog.Warn("[keeb] uinput write failed (device unbound?) - macro events dropped.");
        }
    }

    private static IEnumerable<int> AllKeyCodes()
    {
        foreach (var c in Letters) yield return c;
        foreach (var c in Digits) yield return c;
        for (var f = 1; f <= 24; f++) yield return FKey(f);
        foreach (var c in Named.Values) yield return c;
        yield return KEY_LEFTCTRL;
        yield return KEY_LEFTSHIFT;
        yield return KEY_LEFTALT;
        yield return KEY_LEFTMETA;
    }

    internal static int ParseKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;

        // Letters: KeyA..KeyZ
        if (key.Length == 4 && key.StartsWith("Key", StringComparison.Ordinal))
        {
            var c = char.ToUpperInvariant(key[3]);
            if (c is >= 'A' and <= 'Z') return Letters[c - 'A'];
            return 0;
        }

        // Digits: Digit0..Digit9
        if (key.Length == 6 && key.StartsWith("Digit", StringComparison.Ordinal))
        {
            var d = key[5];
            if (d is >= '0' and <= '9') return Digits[d - '0'];
            return 0;
        }

        // Function keys: F1..F24
        if (key.Length is 2 or 3 && key[0] == 'F'
            && int.TryParse(key.AsSpan(1), out var fn) && fn is >= 1 and <= 24)
        {
            return FKey(fn);
        }

        return Named.TryGetValue(key, out var code) ? code : 0;
    }

    // KEY_* codes from linux/input-event-codes.h
    private static readonly int[] Letters =
    {
        30, 48, 46, 32, 18, 33, 34, 35, 23, 36, 37, 38, 50, // A-M
        49, 24, 25, 16, 19, 31, 20, 22, 47, 17, 45, 21, 44, // N-Z
    };

    private static readonly int[] Digits = { 11, 2, 3, 4, 5, 6, 7, 8, 9, 10 }; // 0-9

    private static int FKey(int n) => n switch
    {
        <= 10 => 58 + n,        // F1=59 .. F10=68
        11 => 87,               // F11
        12 => 88,               // F12
        _ => 183 + (n - 13),    // F13=183 .. F24=194
    };

    private static readonly Dictionary<string, int> Named = new()
    {
        ["Space"] = 57,
        ["Enter"] = 28,
        ["Tab"] = 15,
        ["Escape"] = 1,
        ["Backspace"] = 14,
        ["Delete"] = 111,
        ["Insert"] = 110,
        ["Home"] = 102,
        ["End"] = 107,
        ["PageUp"] = 104,
        ["PageDown"] = 109,
        ["ArrowUp"] = 103,
        ["ArrowDown"] = 108,
        ["ArrowLeft"] = 105,
        ["ArrowRight"] = 106,
        ["CapsLock"] = 58,
        ["NumLock"] = 69,
        ["Numpad0"] = 82, ["Numpad1"] = 79, ["Numpad2"] = 80, ["Numpad3"] = 81, ["Numpad4"] = 75,
        ["Numpad5"] = 76, ["Numpad6"] = 77, ["Numpad7"] = 71, ["Numpad8"] = 72, ["Numpad9"] = 73, // KEY_KP0..9
        ["NumpadMultiply"] = 55,
        ["NumpadSubtract"] = 74,
        ["NumpadAdd"] = 78,
        ["NumpadDecimal"] = 83,
        ["NumpadEnter"] = 96,
        ["NumpadDivide"] = 98,
        ["NumpadEqual"] = 117,
        ["ScrollLock"] = 70,
        ["PrintScreen"] = 99,
        ["Pause"] = 119,
        ["ContextMenu"] = 127,
        ["MediaPlayPause"] = 164,
        ["MediaStop"] = 166,
        ["MediaTrackNext"] = 163,
        ["MediaTrackPrevious"] = 165,
        ["AudioVolumeMute"] = 113,
        ["AudioVolumeDown"] = 114,
        ["AudioVolumeUp"] = 115,
    };

    public void Dispose()
    {
        lock (_lock)
        {
            if (_fd < 0) return;
            try { ioctl(_fd, UI_DEV_DESTROY, 0); } catch { }
            try { close(_fd); } catch { }
            _fd = -1;
        }
    }

    private static unsafe void SetName(ref uinput_setup setup, string name)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(name);
        var n = Math.Min(bytes.Length, 79);
        fixed (byte* dst = setup.name)
        {
            for (var i = 0; i < n; i++) dst[i] = bytes[i];
            dst[n] = 0;
        }
    }

    // ── uinput / input-event-codes constants (x86-64) ──
    private const int O_WRONLY = 1;
    private const int O_CLOEXEC = 0x80000;
    private const ushort EV_SYN = 0x00;
    private const ushort EV_KEY = 0x01;
    private const ushort SYN_REPORT = 0x00;
    private const ushort BUS_VIRTUAL = 0x06;
    private const int KEY_LEFTCTRL = 29;
    private const int KEY_LEFTSHIFT = 42;
    private const int KEY_LEFTALT = 56;
    private const int KEY_LEFTMETA = 125;

    // ioctl request codes: _IO('U',1)=0x5501, _IO('U',2)=0x5502,
    // _IOW('U',3,sizeof(uinput_setup)=92)=0x405C5503,
    // _IOW('U',100,sizeof(int))=0x40045564, _IOW('U',101,sizeof(int))=0x40045565.
    private const nuint UI_DEV_CREATE = 0x5501;
    private const nuint UI_DEV_DESTROY = 0x5502;
    private const nuint UI_DEV_SETUP = 0x405C5503;
    private const nuint UI_SET_EVBIT = 0x40045564;
    private const nuint UI_SET_KEYBIT = 0x40045565;

    [StructLayout(LayoutKind.Sequential)]
    private struct input_id
    {
        public ushort bustype;
        public ushort vendor;
        public ushort product;
        public ushort version;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct uinput_setup
    {
        public input_id id;
        public fixed byte name[80];
        public uint ff_effects_max;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct input_event
    {
        public long tv_sec;
        public long tv_usec;
        public ushort type;
        public ushort code;
        public int value;
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string pathname, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static partial int ioctl(int fd, nuint request, int arg);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static partial int ioctlSetup(int fd, nuint request, in uinput_setup arg);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint write(int fd, in input_event buf, nuint count);
}
