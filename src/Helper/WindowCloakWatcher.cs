#if WINDOWS
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Nexus.Service.Activity;

namespace Nexus.Service.Helper;

/// <summary>
/// Feeds DWM cloak/uncloak and destroy WinEvents into a WindowCloakCache.
/// Out-of-context hooks are delivered through the installing thread's message
/// loop, so this owns a dedicated thread that pumps one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowCloakWatcher : IDisposable
{
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectCloaked = 0x8017;
    private const uint EventObjectUncloaked = 0x8018;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipownprocess = 0x0002;
    private const int ObjidWindow = 0;
    private const int ChildidSelf = 0;
    private const uint WmQuit = 0x0012;

    private delegate void WinEventProc(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private readonly WindowCloakCache _cache;
    private readonly WinEventProc _proc;
    private readonly Thread _thread;
    private volatile uint _threadId;

    public WindowCloakWatcher(WindowCloakCache cache)
    {
        _cache = cache;
        _proc = OnEvent;
        using var started = new ManualResetEventSlim();
        _thread = new Thread(() => Run(started)) { IsBackground = true, Name = "window-cloak-watcher" };
        _thread.Start();
        started.Wait();
    }

    private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != ObjidWindow || idChild != ChildidSelf || hwnd == IntPtr.Zero) return;
        if (eventType == EventObjectDestroy) _cache.Evict(hwnd.ToInt64());
        else _cache.MarkDirty(hwnd.ToInt64());
    }

    private void Run(ManualResetEventSlim started)
    {
        _threadId = GetCurrentThreadId();
        var destroy = SetWinEventHook(
            EventObjectDestroy, EventObjectDestroy, IntPtr.Zero, _proc, 0, 0,
            WineventOutofcontext | WineventSkipownprocess);
        var cloak = SetWinEventHook(
            EventObjectCloaked, EventObjectUncloaked, IntPtr.Zero, _proc, 0, 0,
            WineventOutofcontext | WineventSkipownprocess);
        started.Set();
        if (destroy == IntPtr.Zero && cloak == IntPtr.Zero)
        {
            Platform.HelperLog.Write($"[window-cloak] SetWinEventHook failed err={Marshal.GetLastWin32Error()}");
            return;
        }
        try
        {
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                DispatchMessageW(ref msg);
            }
        }
        finally
        {
            if (destroy != IntPtr.Zero) UnhookWinEvent(destroy);
            if (cloak != IntPtr.Zero) UnhookWinEvent(cloak);
        }
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
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
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc proc,
        uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

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
