using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Nexus.Service.Activity;

/// <summary>
/// Native COM sinks for IMMNotificationClient and IAudioSessionNotification,
/// hand-built vtables because the AOT build has no ComWrappers. Callbacks arrive
/// on arbitrary Core Audio threads and only bump <see cref="Generation"/>; they
/// make no COM calls (Core Audio forbids re-entering from a notification). The
/// two objects and their vtables live for the process, so Windows can never
/// hold a dangling reference.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class AudioTopologyNotifier
{
    private const int ENoInterface = unchecked((int)0x80004002);

    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidClient = new("7991EEC9-7E89-4D85-8390-6C703CEC60C0");
    private static readonly Guid IidSession = new("641DD20B-4D41-49CC-ABA3-174B9477BB08");

    private static readonly object InitLock = new();
    private static IntPtr s_client;
    private static IntPtr s_session;
    private static int s_generation;

    /// <summary>Increments whenever an endpoint or session set may have changed.</summary>
    public static int Generation => Volatile.Read(ref s_generation);

    public static IntPtr ClientObject
    {
        get { EnsureCreated(); return s_client; }
    }

    public static IntPtr SessionObject
    {
        get { EnsureCreated(); return s_session; }
    }

    private static void EnsureCreated()
    {
        if (s_session != IntPtr.Zero) return;
        lock (InitLock)
        {
            if (s_session != IntPtr.Zero) return;
            var clientVtbl = (IntPtr*)NativeMemory.Alloc((nuint)(8 * sizeof(IntPtr)));
            clientVtbl[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QueryClient;
            clientVtbl[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef;
            clientVtbl[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef;
            clientVtbl[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)&OnDeviceStateChanged;
            clientVtbl[4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&OnDeviceChange;
            clientVtbl[5] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&OnDeviceChange;
            clientVtbl[6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr, int>)&OnDefaultDeviceChanged;
            // PROPERTYKEY is passed by value; as a pointer-sized stand-in it is
            // never read, and property changes are ignored.
            clientVtbl[7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)&OnPropertyValueChanged;

            var sessionVtbl = (IntPtr*)NativeMemory.Alloc((nuint)(4 * sizeof(IntPtr)));
            sessionVtbl[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QuerySession;
            sessionVtbl[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef;
            sessionVtbl[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef;
            sessionVtbl[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&OnSessionCreated;

            var client = (IntPtr*)NativeMemory.Alloc((nuint)sizeof(IntPtr));
            *client = (IntPtr)clientVtbl;
            var session = (IntPtr*)NativeMemory.Alloc((nuint)sizeof(IntPtr));
            *session = (IntPtr)sessionVtbl;

            s_client = (IntPtr)client;
            s_session = (IntPtr)session;
        }
    }

    private static void Bump() => Interlocked.Increment(ref s_generation);

    private static int Query(IntPtr self, Guid* riid, IntPtr* ppv, in Guid own)
    {
        if (ppv == null) return ENoInterface;
        if (riid != null && (*riid == IidUnknown || *riid == own))
        {
            *ppv = self;
            return 0;
        }
        *ppv = IntPtr.Zero;
        return ENoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryClient(IntPtr self, Guid* riid, IntPtr* ppv) => Query(self, riid, ppv, IidClient);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QuerySession(IntPtr self, Guid* riid, IntPtr* ppv) => Query(self, riid, ppv, IidSession);

    // Process-lifetime object: the count is never acted on.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint AddRef(IntPtr self) => 2;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnDeviceStateChanged(IntPtr self, IntPtr deviceId, uint newState)
    {
        Bump();
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnDeviceChange(IntPtr self, IntPtr deviceId)
    {
        Bump();
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnDefaultDeviceChanged(IntPtr self, int flow, int role, IntPtr deviceId)
    {
        Bump();
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnPropertyValueChanged(IntPtr self, IntPtr deviceId, IntPtr key) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnSessionCreated(IntPtr self, IntPtr newSession)
    {
        Bump();
        return 0;
    }
}
