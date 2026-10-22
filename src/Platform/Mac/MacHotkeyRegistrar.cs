using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Service.Hotkeys;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// Global hotkeys through Carbon RegisterEventHotKey. That API registers a combo
/// with the window server and delivers a pressed event to the app's event target;
/// it needs neither Accessibility nor Input Monitoring permission and observes no
/// other keystroke. Registration runs on the main thread (the AppKit run loop
/// drains the main dispatch queue and delivers the Carbon events); the fired
/// callback hands the binding id off the main thread immediately.
/// </summary>
internal sealed class MacHotkeyRegistrar : IHotkeyRegistrar
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    // Carbon four-char codes: keyboard event class, hotkey id type, direct-object parameter, and this app's own signature.
    private const uint EventClassKeyboard = 0x6B657962;
    private const uint EventHotKeyPressed = 5;
    private const uint TypeEventHotKeyId = 0x686B6964;
    private const uint ParamDirectObject = 0x2D2D2D2D;
    private const uint Signature = 0x4E58484B;

    private static MacHotkeyRegistrar? s_instance;

    // Touched on the main thread only.
    private readonly Dictionary<uint, (string Id, string Combo, IntPtr Ref)> _registered = new();
    private uint _nextId = 1;
    private bool _handlerInstalled;
    private IntPtr _mainQueue;

    public MacHotkeyRegistrar()
    {
        s_instance = this;
    }

    public string Platform => "macos";
    public bool Supported => true;
    public bool ReleaseOnStop => false;

    public event Action<string, string>? Fired;

#pragma warning disable CS0067 // macOS registrations live in-process, so nothing ever needs a re-push
    public event Action? NeedsReapply;
#pragma warning restore CS0067

    // Registration waits for the AppKit run loop to drain the main queue. A process that never runs it (a bare test host, or a quit in progress) must not hang a caller.
    private static readonly TimeSpan MainQueueTimeout = TimeSpan.FromSeconds(10);

    private const int WorkPending = 0;
    private const int WorkRunning = 1;
    private const int WorkAbandoned = 2;

    public async Task<IReadOnlyDictionary<string, bool>> ApplyAsync(IReadOnlyList<HotkeyRegistration> set, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<IReadOnlyDictionary<string, bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = WorkPending;
        try
        {
            RunOnMain(() =>
            {
                // A caller that timed out has already reported these ids as failed; registering now would leave them live.
                if (Interlocked.CompareExchange(ref state, WorkRunning, WorkPending) != WorkPending)
                {
                    return;
                }
                try
                {
                    tcs.TrySetResult(ApplyOnMain(set));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            try
            {
                return await tcs.Task.WaitAsync(MainQueueTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (Interlocked.CompareExchange(ref state, WorkAbandoned, WorkPending) == WorkPending)
                {
                    ServiceLog.Warn("[hotkeys] mac main queue did not run the registration in time");
                    return set.ToDictionary(r => r.Id, _ => false);
                }
                // The work started just as the wait ended and finishes in moments on the main thread.
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceLog.Warn($"[hotkeys] mac apply failed: {ex.Message}");
            return set.ToDictionary(r => r.Id, _ => false);
        }
    }

    private Dictionary<string, bool> ApplyOnMain(IReadOnlyList<HotkeyRegistration> set)
    {
        foreach (var (_, entry) in _registered)
        {
            UnregisterEventHotKey(entry.Ref);
        }
        _registered.Clear();

        var results = new Dictionary<string, bool>(set.Count);
        if (set.Count == 0)
        {
            return results;
        }
        var target = GetApplicationEventTarget();
        if (target == IntPtr.Zero || !EnsureHandler(target))
        {
            foreach (var reg in set)
            {
                results[reg.Id] = false;
            }
            return results;
        }
        foreach (var reg in set)
        {
            results[reg.Id] = TryRegister(reg, target);
        }
        return results;
    }

    private bool TryRegister(HotkeyRegistration reg, IntPtr target)
    {
        if (HotkeyCombo.MacKeyCode(reg.Combo.Key) is not { } keyCode)
        {
            return false;
        }
        var id = _nextId++;
        var hotKeyId = new EventHotKeyId { Signature = Signature, Id = id };
        var status = RegisterEventHotKey((uint)keyCode, (uint)reg.Combo.MacModifiers, hotKeyId, target, 0, out var hotKeyRef);
        if (status != 0 || hotKeyRef == IntPtr.Zero)
        {
            ServiceLog.Info($"[hotkeys] mac register failed id={reg.Id} combo={reg.Combo} status={status}");
            return false;
        }
        _registered[id] = (reg.Id, reg.Combo.ToString(), hotKeyRef);
        return true;
    }

    private unsafe bool EnsureHandler(IntPtr target)
    {
        if (_handlerInstalled)
        {
            return true;
        }
        var spec = new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed };
        var status = InstallEventHandler(target, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)&OnHotKeyEvent, 1, &spec, IntPtr.Zero, out _);
        if (status != 0)
        {
            ServiceLog.Warn($"[hotkeys] mac InstallEventHandler failed status={status}");
            return false;
        }
        _handlerInstalled = true;
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int OnHotKeyEvent(IntPtr nextHandler, IntPtr theEvent, IntPtr userData)
    {
        try
        {
            EventHotKeyId hotKeyId;
            var status = GetEventParameter(theEvent, ParamDirectObject, TypeEventHotKeyId, IntPtr.Zero, (nuint)sizeof(EventHotKeyId), IntPtr.Zero, &hotKeyId);
            if (status == 0 && hotKeyId.Signature == Signature)
            {
                s_instance?.OnPressedOnMain(hotKeyId.Id);
            }
        }
        catch
        {
            // Never unwind a managed exception into the Carbon dispatcher.
        }
        // noErr tells Carbon the event was handled; an unhandled hotkey event would otherwise fall through to the app.
        return 0;
    }

    private void OnPressedOnMain(uint id)
    {
        if (_registered.TryGetValue(id, out var entry))
        {
            var (bindingId, combo, _) = entry;
            _ = Task.Run(() => Fired?.Invoke(bindingId, combo));
        }
    }

    // ---- Main-thread dispatch ---------------------------------------------

    private unsafe void RunOnMain(Action work)
    {
        if (_mainQueue == IntPtr.Zero)
        {
            // dispatch_get_main_queue is an inline macro; the queue object is this exported symbol's address.
            var lib = NativeLibrary.Load(LibSystem);
            _mainQueue = NativeLibrary.GetExport(lib, "_dispatch_main_q");
        }
        var handle = GCHandle.Alloc(work);
        dispatch_async_f(_mainQueue, GCHandle.ToIntPtr(handle), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&RunWork);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void RunWork(IntPtr context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var work = (Action)handle.Target!;
        handle.Free();
        try
        {
            work();
        }
        catch
        {
            // The work item reports its own failure through its task.
        }
    }

    // ---- Native -------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyId
    {
        public uint Signature;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [DllImport(Carbon)]
    private static extern IntPtr GetApplicationEventTarget();

    [DllImport(Carbon)]
    private static extern unsafe int InstallEventHandler(IntPtr target, IntPtr handler, nuint numTypes, EventTypeSpec* typeList, IntPtr userData, out IntPtr handlerRef);

    [DllImport(Carbon)]
    private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyId hotKeyId, IntPtr target, uint options, out IntPtr hotKeyRef);

    [DllImport(Carbon)]
    private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    [DllImport(Carbon)]
    private static extern unsafe int GetEventParameter(IntPtr theEvent, uint name, uint desiredType, IntPtr actualType, nuint bufferSize, IntPtr actualSize, EventHotKeyId* data);

    [DllImport(LibSystem)]
    private static extern void dispatch_async_f(IntPtr queue, IntPtr context, IntPtr work);
}
