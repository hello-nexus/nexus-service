// Nexus Virtual Display. Derived from the Microsoft IddSampleDriver (Windows-driver-samples,
// video/IndirectDisplay), Copyright (c) Microsoft Corporation, used under the Microsoft
// Public License (MS-PL).

#include "Driver.h"
#include <sddl.h>

using namespace std;
using namespace Nexus::Vdd;
using namespace Microsoft::WRL;

namespace
{
    constexpr DWORD FrameMagic = 0x4456584E; // "NXVD"
    constexpr DWORD FrameVersion = 1;
    constexpr DWORD HeaderBytes = 64;
    constexpr DWORD MaxWidth = 3840;
    constexpr DWORD MaxHeight = 2160;
    constexpr DWORD RefreshHz = 60;

    // The service writes the size it wants before it creates the device.
    DWORD g_Width = 1120;
    DWORD g_Height = 540;

    void ReadRequestedSize()
    {
        DWORD value = 0;
        DWORD size = sizeof(value);
        if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Nexus\\VirtualDisplay", L"Width", RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS
            && value >= 320 && value <= MaxWidth)
        {
            g_Width = value;
        }
        size = sizeof(value);
        if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Nexus\\VirtualDisplay", L"Height", RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS
            && value >= 240 && value <= MaxHeight)
        {
            g_Height = value;
        }
    }

    void FillSignalInfo(DISPLAYCONFIG_VIDEO_SIGNAL_INFO& Mode, DWORD Width, DWORD Height, DWORD VSync, bool bMonitorMode)
    {
        Mode.totalSize.cx = Mode.activeSize.cx = Width;
        Mode.totalSize.cy = Mode.activeSize.cy = Height;
        Mode.AdditionalSignalInfo.vSyncFreqDivider = bMonitorMode ? 0 : 1;
        Mode.AdditionalSignalInfo.videoStandard = 255;
        Mode.vSyncFreq.Numerator = VSync;
        Mode.vSyncFreq.Denominator = 1;
        Mode.hSyncFreq.Numerator = VSync * Height;
        Mode.hSyncFreq.Denominator = 1;
        Mode.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
        Mode.pixelRate = ((UINT64)VSync) * ((UINT64)Width) * ((UINT64)Height);
    }

    IDDCX_MONITOR_MODE CreateMonitorMode(DWORD Width, DWORD Height)
    {
        IDDCX_MONITOR_MODE Mode = {};
        Mode.Size = sizeof(Mode);
        Mode.Origin = IDDCX_MONITOR_MODE_ORIGIN_DRIVER;
        FillSignalInfo(Mode.MonitorVideoSignalInfo, Width, Height, RefreshHz, true);
        return Mode;
    }

    IDDCX_TARGET_MODE CreateTargetMode(DWORD Width, DWORD Height)
    {
        IDDCX_TARGET_MODE Mode = {};
        Mode.Size = sizeof(Mode);
        FillSignalInfo(Mode.TargetVideoSignalInfo.targetVideoSignalInfo, Width, Height, RefreshHz, false);
        return Mode;
    }

    vector<pair<DWORD, DWORD>> Modes()
    {
        return { { g_Width, g_Height } };
    }

    // EDID 1.4 describing the requested size as the native timing. An EDID-less monitor is a
    // generic one whose saved desktop mode wins over the offered mode (1024x768 was kept and
    // letterboxed into 1120x540); with this the monitor is "NXS0001" and starts at native size.
    // The physical size is the one that makes the pixel density 96 dpi, so Windows scales 100%.
    BYTE g_Edid[128];

    void BuildEdid()
    {
        BYTE* e = g_Edid;
        memset(e, 0, sizeof(g_Edid));
        const BYTE header[] = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
        memcpy(e, header, sizeof(header));
        e[8] = 0x3B; e[9] = 0x13;             // manufacturer "NXS"
        e[10] = 0x01; e[11] = 0x00;           // product 0x0001
        e[12] = 0x01;                         // serial 1
        e[16] = 1; e[17] = 36;                // week 1 of 2026
        e[18] = 1; e[19] = 4;                 // EDID 1.4
        e[20] = 0xA5;                         // digital, 8 bits per colour, DisplayPort
        const DWORD widthMm = g_Width * 254 / 960;
        const DWORD heightMm = g_Height * 254 / 960;
        e[21] = (BYTE)((widthMm + 5) / 10);
        e[22] = (BYTE)((heightMm + 5) / 10);
        e[23] = 120;                          // gamma 2.2
        e[24] = 0x06;                         // RGB 4:4:4, preferred timing is native
        const BYTE chroma[] = { 0xEE, 0x91, 0xA3, 0x54, 0x4C, 0x99, 0x26, 0x0F, 0x50, 0x54 };
        memcpy(e + 25, chroma, sizeof(chroma));
        for (int i = 38; i < 54; i += 2)
        {
            e[i] = 0x01; e[i + 1] = 0x01;     // no standard timings
        }

        const DWORD hBlank = 160, hFront = 48, hSync = 32;
        const DWORD vBlank = 30, vFront = 3, vSync = 5;
        const DWORD clock10k = ((g_Width + hBlank) * (g_Height + vBlank) * RefreshHz + 5000) / 10000;
        BYTE* d = e + 54;
        d[0] = (BYTE)clock10k; d[1] = (BYTE)(clock10k >> 8);
        d[2] = (BYTE)g_Width; d[3] = (BYTE)hBlank; d[4] = (BYTE)(((g_Width >> 8) << 4) | (hBlank >> 8));
        d[5] = (BYTE)g_Height; d[6] = (BYTE)vBlank; d[7] = (BYTE)(((g_Height >> 8) << 4) | (vBlank >> 8));
        d[8] = (BYTE)hFront; d[9] = (BYTE)hSync;
        d[10] = (BYTE)(((vFront & 0xF) << 4) | (vSync & 0xF));
        d[11] = (BYTE)(((hFront >> 8) << 6) | ((hSync >> 8) << 4) | ((vFront >> 4) << 2) | (vSync >> 4));
        d[12] = (BYTE)widthMm; d[13] = (BYTE)heightMm; d[14] = (BYTE)(((widthMm >> 8) << 4) | (heightMm >> 8));
        d[17] = 0x1E;                         // digital separate sync, both polarities positive

        BYTE* name = e + 72;                  // monitor name descriptor
        name[3] = 0xFC;
        // 13 bytes of text; a shorter name ends in a line feed and is padded with spaces.
        const char text[] = "Nexus Display";
        static_assert(sizeof(text) - 1 <= 13, "EDID names hold 13 bytes");
        memcpy(name + 5, text, sizeof(text) - 1);
        for (size_t i = 5 + sizeof(text) - 1; i < 18; i++)
        {
            name[i] = i == 5 + sizeof(text) - 1 ? 0x0A : 0x20;
        }
        e[90 + 3] = 0x10;                     // two unused descriptors
        e[108 + 3] = 0x10;

        BYTE sum = 0;
        for (int i = 0; i < 127; i++)
        {
            sum += e[i];
        }
        e[127] = (BYTE)(0x100 - sum);
    }
}

extern "C" DRIVER_INITIALIZE DriverEntry;

EVT_WDF_DRIVER_DEVICE_ADD NexusVddDeviceAdd;
EVT_WDF_DEVICE_D0_ENTRY NexusVddDeviceD0Entry;
EVT_IDD_CX_ADAPTER_INIT_FINISHED NexusVddAdapterInitFinished;
EVT_IDD_CX_ADAPTER_COMMIT_MODES NexusVddAdapterCommitModes;
EVT_IDD_CX_PARSE_MONITOR_DESCRIPTION NexusVddParseMonitorDescription;
EVT_IDD_CX_MONITOR_GET_DEFAULT_DESCRIPTION_MODES NexusVddMonitorGetDefaultModes;
EVT_IDD_CX_MONITOR_QUERY_TARGET_MODES NexusVddMonitorQueryModes;
EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN NexusVddMonitorAssignSwapChain;
EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN NexusVddMonitorUnassignSwapChain;

struct IndirectDeviceContextWrapper
{
    IndirectDeviceContext* pContext;

    void Cleanup()
    {
        delete pContext;
        pContext = nullptr;
    }
};

struct IndirectMonitorContextWrapper
{
    IndirectMonitorContext* pContext;

    void Cleanup()
    {
        delete pContext;
        pContext = nullptr;
    }
};

WDF_DECLARE_CONTEXT_TYPE(IndirectDeviceContextWrapper);
WDF_DECLARE_CONTEXT_TYPE(IndirectMonitorContextWrapper);

extern "C" BOOL WINAPI DllMain(_In_ HINSTANCE hInstance, _In_ UINT dwReason, _In_opt_ LPVOID lpReserved)
{
    UNREFERENCED_PARAMETER(hInstance);
    UNREFERENCED_PARAMETER(lpReserved);
    UNREFERENCED_PARAMETER(dwReason);
    return TRUE;
}

_Use_decl_annotations_
extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT pDriverObject, PUNICODE_STRING pRegistryPath)
{
    WDF_DRIVER_CONFIG Config;
    WDF_OBJECT_ATTRIBUTES Attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&Attributes);
    WDF_DRIVER_CONFIG_INIT(&Config, NexusVddDeviceAdd);
    return WdfDriverCreate(pDriverObject, pRegistryPath, &Attributes, &Config, WDF_NO_HANDLE);
}

_Use_decl_annotations_
NTSTATUS NexusVddDeviceAdd(WDFDRIVER Driver, PWDFDEVICE_INIT pDeviceInit)
{
    UNREFERENCED_PARAMETER(Driver);
    ReadRequestedSize();
    BuildEdid();

    WDF_PNPPOWER_EVENT_CALLBACKS PnpPowerCallbacks;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&PnpPowerCallbacks);
    PnpPowerCallbacks.EvtDeviceD0Entry = NexusVddDeviceD0Entry;
    WdfDeviceInitSetPnpPowerEventCallbacks(pDeviceInit, &PnpPowerCallbacks);

    IDD_CX_CLIENT_CONFIG IddConfig;
    IDD_CX_CLIENT_CONFIG_INIT(&IddConfig);
    IddConfig.EvtIddCxAdapterInitFinished = NexusVddAdapterInitFinished;
    IddConfig.EvtIddCxParseMonitorDescription = NexusVddParseMonitorDescription;
    IddConfig.EvtIddCxMonitorGetDefaultDescriptionModes = NexusVddMonitorGetDefaultModes;
    IddConfig.EvtIddCxMonitorQueryTargetModes = NexusVddMonitorQueryModes;
    IddConfig.EvtIddCxAdapterCommitModes = NexusVddAdapterCommitModes;
    IddConfig.EvtIddCxMonitorAssignSwapChain = NexusVddMonitorAssignSwapChain;
    IddConfig.EvtIddCxMonitorUnassignSwapChain = NexusVddMonitorUnassignSwapChain;

    NTSTATUS Status = IddCxDeviceInitConfig(pDeviceInit, &IddConfig);
    if (!NT_SUCCESS(Status))
    {
        return Status;
    }

    WDF_OBJECT_ATTRIBUTES Attr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&Attr, IndirectDeviceContextWrapper);
    Attr.EvtCleanupCallback = [](WDFOBJECT Object)
    {
        auto* pContext = WdfObjectGet_IndirectDeviceContextWrapper(Object);
        if (pContext)
        {
            pContext->Cleanup();
        }
    };

    WDFDEVICE Device = nullptr;
    Status = WdfDeviceCreate(&pDeviceInit, &Attr, &Device);
    if (!NT_SUCCESS(Status))
    {
        return Status;
    }

    Status = IddCxDeviceInitialize(Device);

    auto* pContext = WdfObjectGet_IndirectDeviceContextWrapper(Device);
    pContext->pContext = new IndirectDeviceContext(Device);
    return Status;
}

_Use_decl_annotations_
NTSTATUS NexusVddDeviceD0Entry(WDFDEVICE Device, WDF_POWER_DEVICE_STATE PreviousState)
{
    UNREFERENCED_PARAMETER(PreviousState);
    auto* pContext = WdfObjectGet_IndirectDeviceContextWrapper(Device);
    pContext->pContext->InitAdapter();
    return STATUS_SUCCESS;
}

#pragma region Direct3DDevice

Direct3DDevice::Direct3DDevice(LUID AdapterLuid) : AdapterLuid(AdapterLuid)
{
}

HRESULT Direct3DDevice::Init()
{
    HRESULT hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&DxgiFactory));
    if (FAILED(hr))
    {
        return hr;
    }
    hr = DxgiFactory->EnumAdapterByLuid(AdapterLuid, IID_PPV_ARGS(&Adapter));
    if (FAILED(hr))
    {
        return hr;
    }
    return D3D11CreateDevice(Adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &Device, nullptr, &DeviceContext);
}

#pragma endregion

#pragma region FrameExport

FrameExport::FrameExport()
{
    // The driver host runs as LocalService; the service that reads frames runs as LocalSystem.
    SECURITY_ATTRIBUTES sa = { sizeof(sa) };
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;LS)", SDDL_REVISION_1, &sd, nullptr))
    {
        return;
    }
    sa.lpSecurityDescriptor = sd;

    const ULONGLONG bytes = HeaderBytes + (ULONGLONG)MaxWidth * MaxHeight * 4;
    m_Mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE, (DWORD)(bytes >> 32), (DWORD)bytes, L"Global\\NexusVirtualDisplayFrame");
    m_Ready = CreateEventW(&sa, FALSE, FALSE, L"Global\\NexusVirtualDisplayFrameReady");
    LocalFree(sd);
    if (m_Mapping)
    {
        m_View = static_cast<BYTE*>(MapViewOfFile(m_Mapping, FILE_MAP_WRITE, 0, 0, 0));
    }
    if (m_View)
    {
        *reinterpret_cast<DWORD*>(m_View + 4) = FrameVersion;
        *reinterpret_cast<DWORD*>(m_View) = FrameMagic;
    }
}

FrameExport::~FrameExport()
{
    if (m_View)
    {
        UnmapViewOfFile(m_View);
    }
    if (m_Mapping)
    {
        CloseHandle(m_Mapping);
    }
    if (m_Ready)
    {
        CloseHandle(m_Ready);
    }
}

void FrameExport::Publish(ID3D11Device* Device, ID3D11DeviceContext* Context, ID3D11Texture2D* Surface)
{
    if (!m_View || !Surface)
    {
        return;
    }
    D3D11_TEXTURE2D_DESC desc;
    Surface->GetDesc(&desc);
    if (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM || desc.Width > MaxWidth || desc.Height > MaxHeight)
    {
        return;
    }
    if (!m_Staging || m_StagingWidth != desc.Width || m_StagingHeight != desc.Height)
    {
        D3D11_TEXTURE2D_DESC staging = desc;
        staging.Usage = D3D11_USAGE_STAGING;
        staging.BindFlags = 0;
        staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        staging.MiscFlags = 0;
        staging.MipLevels = 1;
        staging.ArraySize = 1;
        staging.SampleDesc.Count = 1;
        staging.SampleDesc.Quality = 0;
        m_Staging.Reset();
        if (FAILED(Device->CreateTexture2D(&staging, nullptr, &m_Staging)))
        {
            return;
        }
        m_StagingWidth = desc.Width;
        m_StagingHeight = desc.Height;
    }

    Context->CopyResource(m_Staging.Get(), Surface);
    D3D11_MAPPED_SUBRESOURCE mapped;
    if (FAILED(Context->Map(m_Staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)))
    {
        return;
    }

    const DWORD stride = desc.Width * 4;
    volatile LONG64* sequence = reinterpret_cast<volatile LONG64*>(m_View + 8);
    InterlockedIncrement64(sequence);
    *reinterpret_cast<DWORD*>(m_View + 16) = desc.Width;
    *reinterpret_cast<DWORD*>(m_View + 20) = desc.Height;
    *reinterpret_cast<DWORD*>(m_View + 24) = stride;
    *reinterpret_cast<DWORD*>(m_View + 28) = stride * desc.Height;
    const BYTE* source = static_cast<const BYTE*>(mapped.pData);
    BYTE* target = m_View + HeaderBytes;
    for (UINT row = 0; row < desc.Height; row++)
    {
        memcpy(target + (size_t)row * stride, source + (size_t)row * mapped.RowPitch, stride);
    }
    InterlockedIncrement64(sequence);
    Context->Unmap(m_Staging.Get(), 0);
    SetEvent(m_Ready);
}

#pragma endregion

#pragma region SwapChainProcessor

SwapChainProcessor::SwapChainProcessor(IDDCX_SWAPCHAIN hSwapChain, shared_ptr<Direct3DDevice> Device, HANDLE NewFrameEvent, FrameExport* Export)
    : m_hSwapChain(hSwapChain), m_Device(Device), m_hAvailableBufferEvent(NewFrameEvent), m_Export(Export)
{
    m_hTerminateEvent.Attach(CreateEvent(nullptr, FALSE, FALSE, nullptr));
    m_hThread.Attach(CreateThread(nullptr, 0, RunThread, this, 0, nullptr));
}

SwapChainProcessor::~SwapChainProcessor()
{
    SetEvent(m_hTerminateEvent.Get());
    if (m_hThread.Get())
    {
        WaitForSingleObject(m_hThread.Get(), INFINITE);
    }
}

DWORD CALLBACK SwapChainProcessor::RunThread(LPVOID Argument)
{
    reinterpret_cast<SwapChainProcessor*>(Argument)->Run();
    return 0;
}

void SwapChainProcessor::Run()
{
    DWORD AvTask = 0;
    HANDLE AvTaskHandle = AvSetMmThreadCharacteristicsW(L"Distribution", &AvTask);

    RunCore();

    // Deleting the swap-chain makes the OS hand out a new one if it still needs this monitor.
    WdfObjectDelete((WDFOBJECT)m_hSwapChain);
    m_hSwapChain = nullptr;

    AvRevertMmThreadCharacteristics(AvTaskHandle);
}

void SwapChainProcessor::RunCore()
{
    ComPtr<IDXGIDevice> DxgiDevice;
    HRESULT hr = m_Device->Device.As(&DxgiDevice);
    if (FAILED(hr))
    {
        return;
    }

    IDARG_IN_SWAPCHAINSETDEVICE SetDevice = {};
    SetDevice.pDevice = DxgiDevice.Get();
    hr = IddCxSwapChainSetDevice(m_hSwapChain, &SetDevice);
    if (FAILED(hr))
    {
        return;
    }

    for (;;)
    {
        ComPtr<IDXGIResource> AcquiredBuffer;
        IDARG_OUT_RELEASEANDACQUIREBUFFER Buffer = {};
        hr = IddCxSwapChainReleaseAndAcquireBuffer(m_hSwapChain, &Buffer);

        if (hr == E_PENDING)
        {
            HANDLE WaitHandles[] = { m_hAvailableBufferEvent, m_hTerminateEvent.Get() };
            DWORD WaitResult = WaitForMultipleObjects(ARRAYSIZE(WaitHandles), WaitHandles, FALSE, 16);
            if (WaitResult == WAIT_OBJECT_0 || WaitResult == WAIT_TIMEOUT)
            {
                continue;
            }
            break;
        }
        if (FAILED(hr))
        {
            // The swap-chain was abandoned (e.g. DXGI_ERROR_ACCESS_LOST).
            break;
        }

        AcquiredBuffer.Attach(Buffer.MetaData.pSurface);
        ComPtr<ID3D11Texture2D> Surface;
        if (SUCCEEDED(AcquiredBuffer.As(&Surface)))
        {
            m_Export->Publish(m_Device->Device.Get(), m_Device->DeviceContext.Get(), Surface.Get());
        }
        AcquiredBuffer.Reset();

        hr = IddCxSwapChainFinishedProcessingFrame(m_hSwapChain);
        if (FAILED(hr))
        {
            break;
        }
    }
}

#pragma endregion

#pragma region IndirectDeviceContext

IndirectDeviceContext::IndirectDeviceContext(_In_ WDFDEVICE WdfDevice) : m_WdfDevice(WdfDevice)
{
    m_Adapter = {};
}

IndirectDeviceContext::~IndirectDeviceContext()
{
}

void IndirectDeviceContext::InitAdapter()
{
    IDDCX_ADAPTER_CAPS AdapterCaps = {};
    AdapterCaps.Size = sizeof(AdapterCaps);
    AdapterCaps.MaxMonitorsSupported = 1;
    AdapterCaps.EndPointDiagnostics.Size = sizeof(AdapterCaps.EndPointDiagnostics);
    AdapterCaps.EndPointDiagnostics.GammaSupport = IDDCX_FEATURE_IMPLEMENTATION_NONE;
    AdapterCaps.EndPointDiagnostics.TransmissionType = IDDCX_TRANSMISSION_TYPE_WIRED_OTHER;
    AdapterCaps.EndPointDiagnostics.pEndPointFriendlyName = L"Nexus Virtual Display";
    AdapterCaps.EndPointDiagnostics.pEndPointManufacturerName = L"Nexus";
    AdapterCaps.EndPointDiagnostics.pEndPointModelName = L"Nexus Virtual Display";

    IDDCX_ENDPOINT_VERSION Version = {};
    Version.Size = sizeof(Version);
    Version.MajorVer = 1;
    AdapterCaps.EndPointDiagnostics.pFirmwareVersion = &Version;
    AdapterCaps.EndPointDiagnostics.pHardwareVersion = &Version;

    WDF_OBJECT_ATTRIBUTES Attr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&Attr, IndirectDeviceContextWrapper);

    IDARG_IN_ADAPTER_INIT AdapterInit = {};
    AdapterInit.WdfDevice = m_WdfDevice;
    AdapterInit.pCaps = &AdapterCaps;
    AdapterInit.ObjectAttributes = &Attr;

    IDARG_OUT_ADAPTER_INIT AdapterInitOut;
    NTSTATUS Status = IddCxAdapterInitAsync(&AdapterInit, &AdapterInitOut);
    if (NT_SUCCESS(Status))
    {
        m_Adapter = AdapterInitOut.AdapterObject;
        auto* pContext = WdfObjectGet_IndirectDeviceContextWrapper(AdapterInitOut.AdapterObject);
        pContext->pContext = this;
    }
}

void IndirectDeviceContext::FinishInit()
{
    WDF_OBJECT_ATTRIBUTES Attr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&Attr, IndirectMonitorContextWrapper);

    IDDCX_MONITOR_INFO MonitorInfo = {};
    MonitorInfo.Size = sizeof(MonitorInfo);
    MonitorInfo.MonitorType = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_OTHER;
    MonitorInfo.ConnectorIndex = 0;
    MonitorInfo.MonitorDescription.Size = sizeof(MonitorInfo.MonitorDescription);
    MonitorInfo.MonitorDescription.Type = IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
    MonitorInfo.MonitorDescription.DataSize = sizeof(g_Edid);
    MonitorInfo.MonitorDescription.pData = g_Edid;

    // A fixed container id keeps Windows' saved arrangement for this monitor across sessions.
    MonitorInfo.MonitorContainerId = { 0x6e78766d, 0x6f6e, 0x4e58, { 0x9a, 0x1e, 0x4e, 0x45, 0x58, 0x55, 0x53, 0x01 } };

    IDARG_IN_MONITORCREATE MonitorCreate = {};
    MonitorCreate.ObjectAttributes = &Attr;
    MonitorCreate.pMonitorInfo = &MonitorInfo;

    IDARG_OUT_MONITORCREATE MonitorCreateOut;
    NTSTATUS Status = IddCxMonitorCreate(m_Adapter, &MonitorCreate, &MonitorCreateOut);
    if (NT_SUCCESS(Status))
    {
        auto* pMonitorContextWrapper = WdfObjectGet_IndirectMonitorContextWrapper(MonitorCreateOut.MonitorObject);
        pMonitorContextWrapper->pContext = new IndirectMonitorContext(MonitorCreateOut.MonitorObject);

        IDARG_OUT_MONITORARRIVAL ArrivalOut;
        IddCxMonitorArrival(MonitorCreateOut.MonitorObject, &ArrivalOut);
    }
}

IndirectMonitorContext::IndirectMonitorContext(_In_ IDDCX_MONITOR Monitor) : m_Monitor(Monitor)
{
}

IndirectMonitorContext::~IndirectMonitorContext()
{
    m_ProcessingThread.reset();
}

void IndirectMonitorContext::AssignSwapChain(IDDCX_SWAPCHAIN SwapChain, LUID RenderAdapter, HANDLE NewFrameEvent)
{
    m_ProcessingThread.reset();

    auto Device = make_shared<Direct3DDevice>(RenderAdapter);
    if (FAILED(Device->Init()))
    {
        // Deleting the swap-chain makes the OS retry with a new one.
        WdfObjectDelete(SwapChain);
    }
    else
    {
        m_ProcessingThread.reset(new SwapChainProcessor(SwapChain, Device, NewFrameEvent, &m_Export));
    }
}

void IndirectMonitorContext::UnassignSwapChain()
{
    m_ProcessingThread.reset();
}

#pragma endregion

#pragma region DDI Callbacks

_Use_decl_annotations_
NTSTATUS NexusVddAdapterInitFinished(IDDCX_ADAPTER AdapterObject, const IDARG_IN_ADAPTER_INIT_FINISHED* pInArgs)
{
    auto* pDeviceContextWrapper = WdfObjectGet_IndirectDeviceContextWrapper(AdapterObject);
    if (NT_SUCCESS(pInArgs->AdapterInitStatus))
    {
        pDeviceContextWrapper->pContext->FinishInit();
    }
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS NexusVddAdapterCommitModes(IDDCX_ADAPTER AdapterObject, const IDARG_IN_COMMITMODES* pInArgs)
{
    UNREFERENCED_PARAMETER(AdapterObject);
    UNREFERENCED_PARAMETER(pInArgs);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS NexusVddParseMonitorDescription(const IDARG_IN_PARSEMONITORDESCRIPTION* pInArgs, IDARG_OUT_PARSEMONITORDESCRIPTION* pOutArgs)
{
    // The only EDID ever reported is g_Edid, whose single timing is the requested size.
    if (pInArgs->MonitorDescription.DataSize != sizeof(g_Edid))
    {
        return STATUS_INVALID_PARAMETER;
    }
    auto modes = Modes();
    pOutArgs->MonitorModeBufferOutputCount = (UINT)modes.size();
    if (pInArgs->MonitorModeBufferInputCount < modes.size())
    {
        return pInArgs->MonitorModeBufferInputCount > 0 ? STATUS_BUFFER_TOO_SMALL : STATUS_SUCCESS;
    }
    for (size_t i = 0; i < modes.size(); i++)
    {
        pInArgs->pMonitorModes[i] = CreateMonitorMode(modes[i].first, modes[i].second);
        pInArgs->pMonitorModes[i].Origin = IDDCX_MONITOR_MODE_ORIGIN_MONITORDESCRIPTOR;
    }
    pOutArgs->PreferredMonitorModeIdx = 0;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS NexusVddMonitorGetDefaultModes(IDDCX_MONITOR MonitorObject, const IDARG_IN_GETDEFAULTDESCRIPTIONMODES* pInArgs, IDARG_OUT_GETDEFAULTDESCRIPTIONMODES* pOutArgs)
{
    UNREFERENCED_PARAMETER(MonitorObject);
    auto modes = Modes();
    pOutArgs->DefaultMonitorModeBufferOutputCount = (UINT)modes.size();
    if (pInArgs->DefaultMonitorModeBufferInputCount == 0)
    {
        return STATUS_SUCCESS;
    }
    if (pInArgs->DefaultMonitorModeBufferInputCount < modes.size())
    {
        return STATUS_BUFFER_TOO_SMALL;
    }
    for (size_t i = 0; i < modes.size(); i++)
    {
        pInArgs->pDefaultMonitorModes[i] = CreateMonitorMode(modes[i].first, modes[i].second);
    }
    pOutArgs->PreferredMonitorModeIdx = 0;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS NexusVddMonitorQueryModes(IDDCX_MONITOR MonitorObject, const IDARG_IN_QUERYTARGETMODES* pInArgs, IDARG_OUT_QUERYTARGETMODES* pOutArgs)
{
    UNREFERENCED_PARAMETER(MonitorObject);
    vector<IDDCX_TARGET_MODE> TargetModes;
    for (auto& mode : Modes())
    {
        TargetModes.push_back(CreateTargetMode(mode.first, mode.second));
    }
    pOutArgs->TargetModeBufferOutputCount = (UINT)TargetModes.size();
    if (pInArgs->TargetModeBufferInputCount >= TargetModes.size())
    {
        copy(TargetModes.begin(), TargetModes.end(), pInArgs->pTargetModes);
    }
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS NexusVddMonitorAssignSwapChain(IDDCX_MONITOR MonitorObject, const IDARG_IN_SETSWAPCHAIN* pInArgs)
{
    auto* pMonitorContextWrapper = WdfObjectGet_IndirectMonitorContextWrapper(MonitorObject);
    pMonitorContextWrapper->pContext->AssignSwapChain(pInArgs->hSwapChain, pInArgs->RenderAdapterLuid, pInArgs->hNextSurfaceAvailable);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS NexusVddMonitorUnassignSwapChain(IDDCX_MONITOR MonitorObject)
{
    auto* pMonitorContextWrapper = WdfObjectGet_IndirectMonitorContextWrapper(MonitorObject);
    pMonitorContextWrapper->pContext->UnassignSwapChain();
    return STATUS_SUCCESS;
}

#pragma endregion
