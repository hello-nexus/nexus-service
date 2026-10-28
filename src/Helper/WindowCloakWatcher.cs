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
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile uint _threadId;
    private volatile bool _cloakHookLive;

    public WindowCloakWatcher(WindowCloakCache cache)
    {
        _cache = cache;
        _proc = OnEvent;
    }

    /// <summary>True while the cloak/uncloak hook is installed and pumping.</summary>
    public bool CloakHookLive => _cloakHookLive;

    /// <summary>Installs the hooks on a fresh message-loop thread. A no-op when
    /// already running. The cache is only told events are live when the cloak
    /// hook itself installed.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null) return;
            using var started = new ManualResetEventSlim();
            _thread = new Thread(() => Run(started)) { IsBackground = true, Name = "window-cloak-watcher" };
            _thread.Start();
            started.Wait();
            _cache.EventsLive = _cloakHookLive;
            if (_threadId == 0)
            {
                _thread.Join();
                _thread = null;
            }
        }
    }

    /// <summary>Removes the hooks and ends the thread.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _cache.EventsLive = false;
            var thread = _thread;
            if (thread is null) return;
            _thread = null;
            if (_threadId != 0) PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            thread.Join(TimeSpan.FromSeconds(2));
            _threadId = 0;
            _cloakHookLive = false;
            Platform.HelperLog.Write("[window-cloak] hooks removed");
        }
    }

    private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != ObjidWindow || idChild != ChildidSelf || hwnd == IntPtr.Zero) return;
        if (eventType == EventObjectDestroy) _cache.Evict(hwnd.ToInt64());
        else _cache.MarkDirty(hwnd.ToInt64());
    }

    private void Run(ManualResetEventSlim started)
    {
        var destroy = SetWinEventHook(
            EventObjectDestroy, EventObjectDestroy, IntPtr.Zero, _proc, 0, 0,
            WineventOutofcontext | WineventSkipownprocess);
        var destroyErr = destroy == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        var cloak = SetWinEventHook(
            EventObjectCloaked, EventObjectUncloaked, IntPtr.Zero, _proc, 0, 0,
            WineventOutofcontext | WineventSkipownprocess);
        var cloakErr = cloak == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        _cloakHookLive = cloak != IntPtr.Zero;
        if (destroy == IntPtr.Zero)
        {
            Platform.HelperLog.Write($"[window-cloak] destroy hook failed err={destroyErr}");
        }
        if (cloak == IntPtr.Zero)
        {
            Platform.HelperLog.Write($"[window-cloak] cloak hook failed err={cloakErr}; reading DWM every pass");
        }
        if (destroy != IntPtr.Zero || cloak != IntPtr.Zero)
        {
            _threadId = GetCurrentThreadId();
            Platform.HelperLog.Write($"[window-cloak] hooks installed destroy={destroy != IntPtr.Zero} cloak={cloak != IntPtr.Zero}");
        }
        started.Set();
        if (_threadId == 0) return;
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

    public void Dispose() => Stop();

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
