using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Platform;

#if MACOS
using System.Runtime.InteropServices;
#endif

#if LINUX
using Nexus.Service.Platform.Linux.DBus;
#endif

namespace Nexus.Service.Lighting;

/// <summary>
/// Watches the desktop session lock and drives
/// <see cref="SleepBlackoutCoordinator.OnSessionLocked"/> /
/// <see cref="SleepBlackoutCoordinator.OnSessionUnlocked"/> across it.
///
/// One class, three sources, because no two of these platforms expose the lock
/// the same way:
///
/// <list type="bullet">
/// <item>Windows: the SCM control handler's SERVICE_CONTROL_SESSIONCHANGE. The
/// service lives in session 0 where there are no window messages, so
/// <c>SystemEvents.SessionSwitch</c> - which rides them - never fires; SCM is
/// the only delivery a service gets.</item>
/// <item>macOS: the <c>com.apple.screenIsLocked</c> /
/// <c>com.apple.screenIsUnlocked</c> distributed notifications.</item>
/// <item>Linux: logind's <c>LockedHint</c> property on this process's session.
/// The session's own <c>Lock</c>/<c>Unlock</c> signals are NOT it: those are
/// requests to a screen locker, so a desktop locked from its own shortcut never
/// emits one, while every locker that participates sets the hint.</item>
/// </list>
///
/// Transitions only. Nothing here reads the lock state at startup, and that is
/// deliberate: a machine sitting at the login screen after a cold boot has not
/// been locked by anyone, and blanking there would read as lighting that stays
/// off until you sign in.
/// </summary>
public sealed class SessionLockListener : IHostedService, IDisposable
{
    private readonly SleepBlackoutCoordinator _blackout;

    /// <summary>Raised after the lighting coordinator has taken the transition, on the same hop. True = locked.</summary>
    public event Action<bool>? LockChanged;

#if WINDOWS
    private bool _subscribed;
#endif

#if MACOS
    // The unmanaged callback is a bare function pointer with no state of its
    // own, so the running instance is reachable only through a static.
    private static volatile SessionLockListener? s_macInstance;
    private bool _macObserving;
#endif

#if LINUX
    private CancellationTokenSource? _cts;
    private DBusConnection? _dbus;
#endif

    public SessionLockListener(SleepBlackoutCoordinator blackout)
    {
        _blackout = blackout;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
#if WINDOWS
        Nexus.Service.Lifecycle.WindowsServiceHost.SessionLockChanged += OnLockChanged;
        _subscribed = true;
#endif
#if MACOS
        StartMacObserver();
#endif
#if LINUX
        _cts = new CancellationTokenSource();
        _ = RunLinuxAsync(_cts.Token);
#endif
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        return Task.CompletedTask;
    }

    public void Dispose() => Unsubscribe();

    /// <summary>
    /// Common entry point for all three sources. Off the caller's thread on
    /// purpose: every one of them is an OS callback - SCM is waiting on the
    /// Windows one, the macOS one runs on the main run loop - and none of this
    /// is on a deadline the way the suspend path is. A lock and its unlock are
    /// minutes apart in practice, so the hop cannot reorder them.
    /// </summary>
    private void OnLockChanged(bool locked)
    {
        _ = Task.Run(() =>
        {
            if (locked)
            {
                _blackout.OnSessionLocked();
            }
            else
            {
                _blackout.OnSessionUnlocked();
            }
            try { LockChanged?.Invoke(locked); }
            catch (Exception ex)
            {
                ServiceLog.Info($"[session-lock] subscriber failed on {(locked ? "lock" : "unlock")}: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private void Unsubscribe()
    {
#if WINDOWS
        if (_subscribed)
        {
            Nexus.Service.Lifecycle.WindowsServiceHost.SessionLockChanged -= OnLockChanged;
            _subscribed = false;
        }
#endif
#if MACOS
        StopMacObserver();
#endif
#if LINUX
        _cts?.Cancel();
        _dbus?.Dispose();
        _dbus = null;
#endif
    }

#if MACOS
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint KCFStringEncodingUtf8 = 0x08000100;
    private const int DeliverImmediately = 4;
    // Passed back to the callback as the observer token, which is what tells the
    // two notifications apart without reading the CFString name.
    private static readonly IntPtr LockToken = 1;
    private static readonly IntPtr UnlockToken = 2;

    /// <summary>
    /// Registers on the distributed centre. Delivery is the MAIN run loop's,
    /// whatever thread registered - measured, not assumed: a probe registering
    /// from a worker thread had its callback run on the main thread, and that
    /// worker's own CFRunLoopRun returned immediately, having no source
    /// attached. So this owns no thread and starts no loop; it rides the one the
    /// app bundle already runs for the status item. A host without that loop (a
    /// bare NEXUS_TEST_HOST console run) never sees a lock.
    /// </summary>
    private unsafe void StartMacObserver()
    {
        try
        {
            var center = CFNotificationCenterGetDistributedCenter();
            if (center == IntPtr.Zero)
            {
                ServiceLog.Info("[lighting-lock] no distributed notification center; lock blanking off for this run");
                return;
            }
            s_macInstance = this;
            var callback = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&MacNotificationCallback;
            // Marked observing before the first registration, not after both:
            // a throw between the two still leaves an observer installed, and
            // Stop has to take it back out.
            _macObserving = true;
            AddMacObserver(center, callback, LockToken, "com.apple.screenIsLocked");
            AddMacObserver(center, callback, UnlockToken, "com.apple.screenIsUnlocked");
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[lighting-lock] macOS lock watch failed: {ex.GetType().Name}: {ex.Message}");
            StopMacObserver();
        }
    }

    private static void AddMacObserver(IntPtr center, IntPtr callback, IntPtr token, string name)
    {
        var cfName = CFStringCreateWithCString(IntPtr.Zero, name, KCFStringEncodingUtf8);
        try
        {
            CFNotificationCenterAddObserver(center, token, callback, cfName, IntPtr.Zero, DeliverImmediately);
        }
        finally
        {
            if (cfName != IntPtr.Zero)
            {
                CFRelease(cfName);
            }
        }
    }

    private void StopMacObserver()
    {
        if (!_macObserving)
        {
            return;
        }
        _macObserving = false;
        try
        {
            var center = CFNotificationCenterGetDistributedCenter();
            if (center != IntPtr.Zero)
            {
                CFNotificationCenterRemoveEveryObserver(center, LockToken);
                CFNotificationCenterRemoveEveryObserver(center, UnlockToken);
            }
        }
        catch { }
        s_macInstance = null;
    }

    [UnmanagedCallersOnly]
    private static void MacNotificationCallback(IntPtr center, IntPtr observer, IntPtr name, IntPtr obj, IntPtr userInfo)
    {
        // Runs on the main run loop: hand off and return, never work here.
        try { s_macInstance?.OnLockChanged(observer == LockToken); }
        catch { }
    }

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFNotificationCenterGetDistributedCenter();

    [DllImport(CoreFoundation)]
    private static extern void CFNotificationCenterAddObserver(
        IntPtr center, IntPtr observer, IntPtr callback, IntPtr name, IntPtr obj, int suspensionBehavior);

    [DllImport(CoreFoundation)]
    private static extern void CFNotificationCenterRemoveEveryObserver(IntPtr center, IntPtr observer);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(
        IntPtr alloc, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
#endif

#if LINUX
    private const string LoginService = "org.freedesktop.login1";
    private const string LoginManagerPath = "/org/freedesktop/login1";
    private const string LoginManagerInterface = "org.freedesktop.login1.Manager";
    private const string LoginSessionInterface = "org.freedesktop.login1.Session";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private async Task RunLinuxAsync(CancellationToken ct)
    {
        // Local reference: Stop/Dispose null the field from another thread.
        var dbus = new DBusConnection(DBusBusKind.System);
        _dbus = dbus;
        try
        {
            await dbus.StartAsync().ConfigureAwait(false);

            // "auto" is this process's own session. A Nexus running as a system
            // daemon has none, and there is no sound way to guess which of
            // several seats' sessions its lighting belongs to - so that install
            // goes without, rather than tracking a stranger's screen.
            var reply = await dbus.CallAsync(LoginService, LoginManagerPath, LoginManagerInterface,
                "GetSession", "s", w => w.WriteString("auto")).ConfigureAwait(false);
            var sessionPath = new DBusReader(reply.Body).ReadObjectPath();
            if (string.IsNullOrEmpty(sessionPath))
            {
                ServiceLog.Info("[lighting-lock] logind reports no session for this process; lock blanking off for this run");
                return;
            }

            await dbus.AddMatchAsync(
                $"type='signal',interface='{PropertiesInterface}',member='PropertiesChanged',path='{sessionPath}'")
                .ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                DBusMessage signal;
                try
                {
                    signal = await dbus.WaitForSignalAsync(sessionPath, "PropertiesChanged", 600_000)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Re-arm. A lock landing in the gap costs one blank, not the watch.
                    continue;
                }

                var reader = new DBusReader(signal.Body);
                if (reader.ReadString() != LoginSessionInterface)
                {
                    continue;
                }
                if (reader.ReadStringVariantDict().TryGetValue("LockedHint", out var hint) && hint is bool locked)
                {
                    OnLockChanged(locked);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[lighting-lock] logind lock watch stopped: {ex.GetType().Name}: {ex.Message}");
        }
    }
#endif
}
