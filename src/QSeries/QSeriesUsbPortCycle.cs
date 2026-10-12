#if WINDOWS
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nexus.Service.Devices.Detection.Native;

namespace Nexus.Service.QSeries;

/// <summary>
/// Cycles the hub port a USB device is attached to (IOCTL_USB_HUB_CYCLE_PORT). Windows
/// handles it as a surprise removal and re-enumeration, which no open handle can veto.
/// Success means the device left or re-arrived, not only that the hub accepted the request.
/// </summary>
internal static partial class QSeriesUsbPortCycle
{
    // usbioctl.h: CTL_CODE(FILE_DEVICE_USB, USB_HUB_CYCLE_PORT, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint IoctlUsbHubCyclePort = 0x220444;
    private const uint GenericReadWrite = 0xC0000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    // usbiodef.h: GUID_DEVINTERFACE_USB_HUB
    private static readonly Guid UsbHubInterfaceClass = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    // USBD_STATUS_SUCCESS
    private const uint UsbdStatusSuccess = 0;

    // Re-arrival is fast; the bound only stops a hub that accepts the request
    // without resetting the port.
    private static readonly TimeSpan ReArrivalTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReArrivalPoll = TimeSpan.FromMilliseconds(100);

    [StructLayout(LayoutKind.Sequential)]
    private struct UsbCyclePortParams
    {
        public uint ConnectionIndex;
        public uint StatusReturned;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateFile(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(nint device, uint code, ref UsbCyclePortParams inBuffer, int inSize,
        ref UsbCyclePortParams outBuffer, int outSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(nint device, uint code, byte* inBuffer, int inSize,
        byte* outBuffer, int outSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    // usbioctl.h: IOCTL_USB_GET_NODE_INFORMATION, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX
    private const uint IoctlUsbGetNodeInformation = 0x220408;
    private const uint IoctlUsbGetNodeConnectionInformationEx = 0x220448;

    // USB_NODE_INFORMATION: NodeType (4) + USB_HUB_DESCRIPTOR (71) + HubIsBusPowered (1).
    private const int NodeInformationSize = 76;
    private const int HubPortCountOffset = 6;

    // USB_NODE_CONNECTION_INFORMATION_EX is packed: ConnectionIndex at 0, the device
    // descriptor at 4 (idVendor 12, idProduct 14), ConnectionStatus at 31, then the
    // open pipes, room for 30 of them.
    private const int ConnectionInformationSize = 512;
    private const int VendorIdOffset = 12;
    private const int ProductIdOffset = 14;
    private const int ConnectionStatusOffset = 31;

    /// <summary>Every downstream port of a hub with its connection status; <paramref name="detail"/> names a failed step.</summary>
    internal static unsafe bool TryReadHubPorts(string hubInstanceId, out List<QSeriesHubPort> ports, out string detail)
    {
        ports = [];
        var hubPath = CfgMgr32.GetInterfacePath(hubInstanceId, UsbHubInterfaceClass);
        if (hubPath.Length == 0)
        {
            detail = $"no hub interface on {hubInstanceId}";
            return false;
        }
        var handle = CreateFile(hubPath, GenericReadWrite, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle == -1 || handle == 0)
        {
            detail = $"open {hubInstanceId} failed: error {Marshal.GetLastPInvokeError()}";
            return false;
        }
        try
        {
            var node = stackalloc byte[NodeInformationSize];
            if (!DeviceIoControl(handle, IoctlUsbGetNodeInformation, node, NodeInformationSize, node, NodeInformationSize, out _, 0))
            {
                detail = $"hub information on {hubInstanceId} failed: error {Marshal.GetLastPInvokeError()}";
                return false;
            }
            var count = node[HubPortCountOffset];
            var connection = stackalloc byte[ConnectionInformationSize];
            var info = new Span<byte>(connection, ConnectionInformationSize);
            for (uint port = 1; port <= count; port++)
            {
                info.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(info, port);
                if (!DeviceIoControl(handle, IoctlUsbGetNodeConnectionInformationEx, connection, ConnectionInformationSize,
                        connection, ConnectionInformationSize, out _, 0))
                {
                    ports.Add(new QSeriesHubPort(port, QSeriesHubPort.Unreadable, 0, 0));
                    continue;
                }
                ports.Add(new QSeriesHubPort(
                    port,
                    BinaryPrimitives.ReadUInt32LittleEndian(info[ConnectionStatusOffset..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(info[VendorIdOffset..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(info[ProductIdOffset..])));
            }
        }
        finally
        {
            CloseHandle(handle);
        }
        detail = "";
        return true;
    }

    /// <summary>True once the device left or re-arrived after the cycle; <paramref name="detail"/> names the port or the failed step.</summary>
    internal static bool TryCycle(string instanceId, out string detail)
    {
        if (!CfgMgr32.TryGetUsbHubPort(instanceId, out var hubInstanceId, out var port))
        {
            detail = $"no hub port for {instanceId}";
            return false;
        }
        // Without a readable arrival time a cycle cannot be confirmed, so it is not tried.
        if (!CfgMgr32.TryGetLastArrival(instanceId, out var arrivedBefore))
        {
            detail = $"no arrival time for {instanceId}";
            return false;
        }
        var hubPath = CfgMgr32.GetInterfacePath(hubInstanceId, UsbHubInterfaceClass);
        if (hubPath.Length == 0)
        {
            detail = $"no hub interface on {hubInstanceId}";
            return false;
        }
        var handle = CreateFile(hubPath, GenericReadWrite, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle == -1 || handle == 0)
        {
            detail = $"open {hubInstanceId} failed: error {Marshal.GetLastPInvokeError()}";
            return false;
        }
        try
        {
            var p = new UsbCyclePortParams { ConnectionIndex = port };
            var size = Marshal.SizeOf<UsbCyclePortParams>();
            if (!DeviceIoControl(handle, IoctlUsbHubCyclePort, ref p, size, ref p, size, out _, 0))
            {
                detail = $"cycle port {port} on {hubInstanceId} failed: error {Marshal.GetLastPInvokeError()}";
                return false;
            }
            if (p.StatusReturned != UsbdStatusSuccess)
            {
                detail = $"cycle port {port} on {hubInstanceId} returned USBD status 0x{p.StatusReturned:X8}";
                return false;
            }
        }
        finally
        {
            CloseHandle(handle);
        }

        // A panel can re-enumerate under another product id, which retires this
        // instance id, so its devnode leaving is proof as well.
        var deadline = DateTime.UtcNow + ReArrivalTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (CfgMgr32.LocateDevNode(instanceId, out _) != CfgMgr32.CR_SUCCESS
                || (CfgMgr32.TryGetLastArrival(instanceId, out var arrivedNow) && arrivedNow > arrivedBefore))
            {
                detail = $"port {port} on {hubInstanceId}";
                return true;
            }
            Thread.Sleep(ReArrivalPoll);
        }
        detail = $"port {port} on {hubInstanceId} accepted the cycle but the device neither left nor re-arrived within {ReArrivalTimeout.TotalSeconds:F0}s";
        return false;
    }
}
#endif
