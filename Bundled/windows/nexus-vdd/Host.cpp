// NexusVirtualDisplayHost: creates the Nexus Virtual Display software device and keeps it
// until the parent process exits or the named stop event is set. SwDeviceCreate is called
// from native code because the same call fails with ERROR_MOD_NOT_FOUND from .NET.
//   NexusVirtualDisplayHost.exe --parent-pid <pid> --stop-event <name>

#include <windows.h>
#include <swdevice.h>
#include <stdio.h>
#include <wchar.h>

static HANDLE g_created;
static HRESULT g_result;

static VOID WINAPI OnCreated(HSWDEVICE, HRESULT hr, PVOID, PCWSTR)
{
    g_result = hr;
    SetEvent(g_created);
}

int wmain(int argc, wchar_t** argv)
{
    DWORD parentPid = 0;
    const wchar_t* stopName = nullptr;
    for (int i = 1; i + 1 < argc; i += 2)
    {
        if (wcscmp(argv[i], L"--parent-pid") == 0) parentPid = wcstoul(argv[i + 1], nullptr, 10);
        else if (wcscmp(argv[i], L"--stop-event") == 0) stopName = argv[i + 1];
    }
    HANDLE parent = parentPid ? OpenProcess(SYNCHRONIZE, FALSE, parentPid) : nullptr;
    HANDLE stop = stopName ? OpenEventW(SYNCHRONIZE, FALSE, stopName) : nullptr;
    if (!parent || !stop)
    {
        fwprintf(stderr, L"parent or stop event unavailable (%lu)\n", GetLastError());
        return 2;
    }

    g_created = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    SW_DEVICE_CREATE_INFO info = { sizeof(info) };
    info.pszInstanceId = L"NexusVirtualDisplay";
    info.pszzHardwareIds = L"NexusVirtualDisplay\0\0";
    info.CapabilityFlags = SWDeviceCapabilitiesRemovable | SWDeviceCapabilitiesSilentInstall | SWDeviceCapabilitiesDriverRequired;
    info.pszDeviceDescription = L"Nexus Virtual Display";

    HSWDEVICE device = nullptr;
    HRESULT hr = SwDeviceCreate(L"NexusVirtualDisplay", L"HTREE\\ROOT\\0", &info, 0, nullptr, OnCreated, nullptr, &device);
    if (SUCCEEDED(hr))
    {
        hr = WaitForSingleObject(g_created, 30000) == WAIT_OBJECT_0 ? g_result : HRESULT_FROM_WIN32(ERROR_TIMEOUT);
    }
    wprintf(L"create hr=0x%08lx\n", hr);
    fflush(stdout);
    if (FAILED(hr))
    {
        if (device) SwDeviceClose(device);
        return 1;
    }

    HANDLE waits[] = { parent, stop };
    WaitForMultipleObjects(2, waits, FALSE, INFINITE);
    SwDeviceClose(device);
    return 0;
}
