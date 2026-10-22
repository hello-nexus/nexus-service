#if WINDOWS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Hotkeys;

namespace Nexus.Service.Helper;

/// <summary>
/// Registers global hotkeys for the service with RegisterHotKey. Runs in the
/// user session because hotkeys are per desktop and the Session 0 service has
/// none. RegisterHotKey binds a hotkey to the thread that called it, so one
/// dedicated thread owns a message-only window, performs every registration and
/// receives WM_HOTKEY. It registers specific combos only and never sees any other
/// keystroke: no hook, no Raw Input.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GlobalHotkeys : IDisposable
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_APP = 0x8000;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_APPLY = WM_APP + 1;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private sealed record Work(IReadOnlyList<HotkeyRegistration> Set, TaskCompletionSource<IReadOnlyDictionary<string, bool>> Result);

    private readonly object _gate = new();
    private readonly ConcurrentQueue<Work> _queue = new();
    private Thread? _thread;
    private volatile uint _threadId;
    private volatile bool _ready;

    // Owned by the hotkey thread.
    private readonly Dictionary<int, HotkeyRegistration> _byAtom = new();
    private IntPtr _hwnd;
    private int _nextAtom = 1;

    /// <summary>A registered combo was pressed: the binding id and its canonical combo. Raised on the hotkey thread.</summary>
    public event Action<string, string>? Fired;

    /// <summary>Makes exactly <paramref name="set"/> the registered set and reports per id whether Windows accepted it.</summary>
    public Task<IReadOnlyDictionary<string, bool>> ApplyAsync(IReadOnlyList<HotkeyRegistration> set)
    {
        var work = new Work(set, new TaskCompletionSource<IReadOnlyDictionary<string, bool>>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_gate)
        {
            if (!EnsureThreadLocked())
            {
                return Task.FromResult<IReadOnlyDictionary<string, bool>>(FailAll(set));
            }
            _queue.Enqueue(work);
            PostThreadMessageW(_threadId, WM_APPLY, IntPtr.Zero, IntPtr.Zero);
        }
        return work.Result.Task;
    }

    /// <summary>Unregisters everything, for when the service is gone and nothing could act on a press.</summary>
    public void Clear()
    {
        if (_thread is null)
        {
            return;
        }
        _ = ApplyAsync(Array.Empty<HotkeyRegistration>());
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
            if (thread is not null && _threadId != 0)
            {
                PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
        }
        thread?.Join(TimeSpan.FromSeconds(2));
    }

    private bool EnsureThreadLocked()
    {
        if (_thread is not null)
        {
            return _ready;
        }
        using var started = new ManualResetEventSlim();
        _ready = false;
        _thread = new Thread(() => Run(started)) { IsBackground = true, Name = "global-hotkeys" };
        _thread.Start();
        // Run signals only after recording its thread id and creating the window.
        started.Wait();
        if (!_ready)
        {
            _thread.Join();
            _thread = null;
            _threadId = 0;
            return false;
        }
        return true;
    }

    private void Run(ManualResetEventSlim started)
    {
        _threadId = GetCurrentThreadId();
        // The built-in STATIC class needs no registration; the window only exists to own the hotkeys.
        _hwnd = CreateWindowExW(0, "STATIC", "nexus-global-hotkeys", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _ready = _hwnd != IntPtr.Zero;
        started.Set();
        if (!_ready)
        {
            Platform.HelperLog.Write($"[hotkeys] message window failed err={Marshal.GetLastWin32Error()}");
            return;
        }
        try
        {
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_APPLY)
                {
                    DrainQueue();
                    continue;
                }
                if (msg.message == WM_HOTKEY)
                {
                    if (_byAtom.TryGetValue((int)msg.wParam, out var reg))
                    {
                        try { Fired?.Invoke(reg.Id, reg.Combo.ToString()); }
                        catch { /* a bad subscriber must not stop the pump */ }
                    }
                    continue;
                }
                DispatchMessageW(ref msg);
            }
        }
        finally
        {
            UnregisterAll();
            DestroyWindow(_hwnd);
            while (_queue.TryDequeue(out var pending))
            {
                pending.Result.TrySetResult(FailAll(pending.Set));
            }
        }
    }

    private void DrainQueue()
    {
        while (_queue.TryDequeue(out var work))
        {
            try
            {
                work.Result.TrySetResult(Register(work.Set));
            }
            catch (Exception ex)
            {
                work.Result.TrySetException(ex);
            }
        }
    }

    private Dictionary<string, bool> Register(IReadOnlyList<HotkeyRegistration> set)
    {
        UnregisterAll();
        // Ids stay within the app-defined range RegisterHotKey allows (0x0000 to 0xBFFF); a full re-apply restarts them.
        _nextAtom = 1;
        var results = new Dictionary<string, bool>(set.Count, StringComparer.Ordinal);
        foreach (var reg in set)
        {
            var vk = HotkeyCombo.WindowsVirtualKey(reg.Combo.Key);
            if (vk is null)
            {
                results[reg.Id] = false;
                continue;
            }
            var atom = _nextAtom++;
            if (atom > 0xBFFF)
            {
                results[reg.Id] = false;
                continue;
            }
            if (RegisterHotKey(_hwnd, atom, (uint)reg.Combo.WindowsModifiers, (uint)vk.Value))
            {
                _byAtom[atom] = reg;
                results[reg.Id] = true;
            }
            else
            {
                Platform.HelperLog.Write($"[hotkeys] RegisterHotKey failed id={reg.Id} combo={reg.Combo} err={Marshal.GetLastWin32Error()}");
                results[reg.Id] = false;
            }
        }
        return results;
    }

    private void UnregisterAll()
    {
        foreach (var atom in _byAtom.Keys)
        {
            UnregisterHotKey(_hwnd, atom);
        }
        _byAtom.Clear();
    }

    private static Dictionary<string, bool> FailAll(IReadOnlyList<HotkeyRegistration> set)
    {
        var map = new Dictionary<string, bool>(set.Count, StringComparer.Ordinal);
        foreach (var reg in set)
        {
            map[reg.Id] = false;
        }
        return map;
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
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

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
