// Nexus Virtual Display: an IddCx indirect display driver that exposes one monitor and
// publishes its desktop frames to the Nexus service through a shared section.
// Derived from the Microsoft IddSampleDriver (Windows-driver-samples, video/IndirectDisplay),
// Copyright (c) Microsoft Corporation, used under the Microsoft Public License (MS-PL).

#pragma once

#define NOMINMAX
#include <windows.h>
#include <bugcodes.h>
#include <wudfwdm.h>
#include <wdf.h>
#include <iddcx.h>

#include <dxgi1_5.h>
#include <d3d11_2.h>
#include <avrt.h>
#include <wrl.h>

#include <memory>
#include <vector>

namespace Microsoft
{
    namespace WRL
    {
        namespace Wrappers
        {
            typedef HandleT<HandleTraits::HANDLENullTraits> Thread;
        }
    }
}

namespace Nexus
{
    namespace Vdd
    {
        struct Direct3DDevice
        {
            Direct3DDevice(LUID AdapterLuid);
            HRESULT Init();

            LUID AdapterLuid;
            Microsoft::WRL::ComPtr<IDXGIFactory5> DxgiFactory;
            Microsoft::WRL::ComPtr<IDXGIAdapter1> Adapter;
            Microsoft::WRL::ComPtr<ID3D11Device> Device;
            Microsoft::WRL::ComPtr<ID3D11DeviceContext> DeviceContext;
        };

        // Global\NexusVirtualDisplayFrame: a 64-byte header then top-down BGRA rows.
        // Header: magic "NXVD", version 1, sequence (int64 at 8, odd while writing),
        // width/height/stride/size (int32 at 16/20/24/28). Global\NexusVirtualDisplayFrameReady
        // is set after every published frame.
        class FrameExport
        {
        public:
            FrameExport();
            ~FrameExport();

            void Publish(ID3D11Device* Device, ID3D11DeviceContext* Context, ID3D11Texture2D* Surface);

        private:
            HANDLE m_Mapping = nullptr;
            HANDLE m_Ready = nullptr;
            BYTE* m_View = nullptr;
            Microsoft::WRL::ComPtr<ID3D11Texture2D> m_Staging;
            UINT m_StagingWidth = 0;
            UINT m_StagingHeight = 0;
        };

        class SwapChainProcessor
        {
        public:
            SwapChainProcessor(IDDCX_SWAPCHAIN hSwapChain, std::shared_ptr<Direct3DDevice> Device, HANDLE NewFrameEvent, FrameExport* Export);
            ~SwapChainProcessor();

        private:
            static DWORD CALLBACK RunThread(LPVOID Argument);

            void Run();
            void RunCore();

            IDDCX_SWAPCHAIN m_hSwapChain;
            std::shared_ptr<Direct3DDevice> m_Device;
            HANDLE m_hAvailableBufferEvent;
            FrameExport* m_Export;
            Microsoft::WRL::Wrappers::Thread m_hThread;
            Microsoft::WRL::Wrappers::Event m_hTerminateEvent;
        };

        class IndirectDeviceContext
        {
        public:
            IndirectDeviceContext(_In_ WDFDEVICE WdfDevice);
            virtual ~IndirectDeviceContext();

            void InitAdapter();
            void FinishInit();

        protected:
            WDFDEVICE m_WdfDevice;
            IDDCX_ADAPTER m_Adapter;
        };

        class IndirectMonitorContext
        {
        public:
            IndirectMonitorContext(_In_ IDDCX_MONITOR Monitor);
            virtual ~IndirectMonitorContext();

            void AssignSwapChain(IDDCX_SWAPCHAIN SwapChain, LUID RenderAdapter, HANDLE NewFrameEvent);
            void UnassignSwapChain();

        private:
            IDDCX_MONITOR m_Monitor;
            FrameExport m_Export;
            std::unique_ptr<SwapChainProcessor> m_ProcessingThread;
        };
    }
}
