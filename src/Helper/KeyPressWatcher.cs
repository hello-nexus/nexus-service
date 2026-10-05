#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Lighting.KeyReactive;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper;

/// <summary>
/// Reports key-downs for key-reactive lighting. Runs in the user session
/// because the Session 0 service sees no input, and only while the service has
/// it armed (some keyboard has reactions on).
///
/// Raw Input in sink mode on a message-only window: it observes keystrokes
/// without sitting in the input chain the way a low-level hook does, so it adds
/// no latency to typing and cannot be unhooked by the hook timeout. It never
/// sees the secure desktop. Key names go to the service and nowhere else;
/// nothing here logs them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KeyPressWatcher : IDisposable
{
    private const uint WM_INPUT = 0x00FF;
    private const uint WM_QUIT = 0x0012;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEKEYBOARD = 1;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const ushort RI_KEY_BREAK = 0x01;
    private const ushort RI_KEY_E0 = 0x02;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    /// <summary>
    /// A make for a key already down is auto-repeat, unless the last make for
    /// it is older than the slowest repeat delay Windows offers - then the key
    /// up was lost (focus moved mid-press) and this is a new press.
    /// </summary>
    private const long RepeatWindowMs = 1100;

    // Windows marks the extra half of an escape sequence (Pause's trailing 45) with this VKey.
    private const ushort VK_FAKE = 0xFF;

    private readonly HelperOutbound _outbound;
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile uint _threadId;
    private volatile bool _registered;

    public KeyPressWatcher(HelperOutbound outbound) => _outbound = outbound;

    public void SetArmed(bool armed)
    {
        lock (_gate)
        {
            if (armed)
            {
                if (_thread is not null) return;
                using var started = new ManualResetEventSlim();
                _thread = new Thread(() => Run(started)) { IsBackground = true, Name = "key-press-watcher" };
                _thread.Start();
                // Run signals only after recording its thread id and registering,
                // and does nothing that can stall before then.
                started.Wait();
                if (!_registered)
                {
                    // Registration failed and Run has returned; a later arm retries.
                    _thread.Join();
                    _thread = null;
                    _threadId = 0;
                }
                return;
            }
            StopLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        var thread = _thread;
        if (thread is null) return;
        _thread = null;
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(2));
        _threadId = 0;
    }

    private void Run(ManualResetEventSlim started)
    {
        _threadId = GetCurrentThreadId();
        // The built-in STATIC class needs no registration; its window procedure
        // ends in DefWindowProc, which frees each WM_INPUT on dispatch.
        var hwnd = CreateWindowExW(0, "STATIC", "nexus-key-press-watcher", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        var registered = hwnd != IntPtr.Zero && Register(hwnd, RIDEV_INPUTSINK);
        _registered = registered;
        started.Set();
        if (!registered)
        {
            Platform.HelperLog.Write($"[key-press] raw input registration failed err={Marshal.GetLastWin32Error()}");
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
            return;
        }

        var lastMake = new Dictionary<int, long>();
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_INPUT)
                {
                    try { Handle(msg.lParam, buffer, lastMake); }
                    catch { /* one bad event must not stop the watcher */ }
                }
                DispatchMessageW(ref msg);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Register(IntPtr.Zero, RIDEV_REMOVE);
            DestroyWindow(hwnd);
        }
    }

    private void Handle(IntPtr rawInput, IntPtr buffer, Dictionary<int, long> lastMake)
    {
        var headerSize = (uint)(8 + 2 * IntPtr.Size);
        uint size = 256;
        if (GetRawInputData(rawInput, RID_INPUT, buffer, ref size, headerSize) == unchecked((uint)-1)) return;
        if ((uint)Marshal.ReadInt32(buffer, 0) != RIM_TYPEKEYBOARD) return;
        var off = (int)headerSize;
        var makeCode = (ushort)Marshal.ReadInt16(buffer, off);
        var flags = (ushort)Marshal.ReadInt16(buffer, off + 2);
        var vkey = (ushort)Marshal.ReadInt16(buffer, off + 6);
        if (vkey == VK_FAKE) return;
        var e0 = (flags & RI_KEY_E0) != 0;
        var code = makeCode | (e0 ? 0xE000 : 0);
        if ((flags & RI_KEY_BREAK) != 0)
        {
            lastMake.Remove(code);
            return;
        }
        var now = Environment.TickCount64;
        var repeat = lastMake.TryGetValue(code, out var prev) && now - prev < RepeatWindowMs;
        lastMake[code] = now;
        if (repeat) return;
        var name = KeyNames.FromScanCode(makeCode, e0, vkey);
        if (name is null) return;
        _ = _outbound.SendAsync(
            type: KeyReactiveCommands.KeyPressedType,
            payload: new KeyPressedPayload { Keys = new List<string> { name } },
            payloadType: AppJsonContext.Default.KeyPressedPayload);
    }

    private static bool Register(IntPtr hwnd, uint flags)
    {
        var device = new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = flags, hwndTarget = hwnd };
        return RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
#endif
