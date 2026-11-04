#if WINDOWS
using System.Runtime.InteropServices;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>GUID_CONSOLE_DISPLAY_STATE through a callback registration, which needs no window and works in a service.</summary>
internal sealed unsafe partial class WindowsDisplayStateWatch : IDisposable
{
    // winuser.h DEVICE_NOTIFY_CALLBACK
    private const uint DeviceNotifyCallback = 2;
    // winuser.h PBT_POWERSETTINGCHANGE
    private const uint PbtPowerSettingChange = 0x8013;
    // POWERBROADCAST_SETTING: the setting GUID, then DataLength, then Data
    private const int DataLengthOffset = 16;
    private const int DataOffset = 20;

    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    // DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS { PDEVICE_NOTIFY_CALLBACK_ROUTINE Callback; PVOID Context; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SubscribeParameters
    {
        public nint Callback;
        public nint Context;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSettingRegisterNotification(in Guid settingGuid, uint flags, nint recipient, out nint registrationHandle);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSettingUnregisterNotification(nint registrationHandle);

    private readonly Action<int> _onState;
    private GCHandle _self;
    private SubscribeParameters* _parameters;
    private nint _registration;

    internal WindowsDisplayStateWatch(Action<int> onState)
    {
        _onState = onState;
        _self = GCHandle.Alloc(this);
        _parameters = (SubscribeParameters*)NativeMemory.Alloc((nuint)sizeof(SubscribeParameters));
        _parameters->Callback = (nint)(delegate* unmanaged<nint, uint, nint, uint>)&OnPowerSetting;
        _parameters->Context = GCHandle.ToIntPtr(_self);
        var result = PowerSettingRegisterNotification(in ConsoleDisplayState, DeviceNotifyCallback, (nint)_parameters, out _registration);
        if (result != 0 || _registration == 0)
        {
            ServiceLog.Warn($"[lianli-wireless] display state registration failed (error {result}); TX port cycling stays off");
            _registration = 0;
            Release();
        }
    }

    [UnmanagedCallersOnly]
    private static uint OnPowerSetting(nint context, uint type, nint setting)
    {
        try
        {
            if (type == PbtPowerSettingChange && context != 0 && setting != 0
                && GCHandle.FromIntPtr(context).Target is WindowsDisplayStateWatch watch
                && *(Guid*)setting == ConsoleDisplayState
                && *(uint*)(setting + DataLengthOffset) >= sizeof(int))
            {
                watch._onState(*(int*)(setting + DataOffset));
            }
        }
        catch
        {
            // Never let an exception cross the native boundary.
        }
        return 0;
    }

    public void Dispose()
    {
        if (_registration != 0)
        {
            PowerSettingUnregisterNotification(_registration);
            _registration = 0;
        }
        // The handle and parameters stay allocated: unregister is not documented to wait for an in-flight callback.
    }

    private void Release()
    {
        if (_parameters is not null)
        {
            NativeMemory.Free(_parameters);
            _parameters = null;
        }
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}
#endif
