#if WINDOWS
using Nexus.Service.Devices.Detection.Native;
using Nexus.Service.QSeries;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// IOCTL_USB_HUB_CYCLE_PORT on the TX's own port, no pnputil fallback. The instance id is
/// read back from the discovered interface, its VID/PID must be the TX, and its parent must
/// be a hub; anything else is refused. <see cref="QSeriesUsbPortCycle"/> then resolves the
/// hub and port from that same instance id, so it cannot reach a different device.
/// </summary>
internal sealed class WindowsSlv3TxPortCycler : ISlv3TxPortCycler
{
    public bool TryResolve(Slv3PortInfo txPort, out string instanceId, out string target)
    {
        instanceId = "";
        if (txPort.Role != Slv3DongleRole.Tx)
        {
            target = $"{txPort.PortName} is not the TX";
            return false;
        }
        var id = CfgMgr32.GetInterfaceInstanceId(txPort.PortName);
        if (!Slv3TxPortIdentity.IsTxInstanceId(id))
        {
            target = id.Length == 0 ? $"no devnode for {txPort.PortName}" : $"{id} is not a 0416:8040 device";
            return false;
        }
        if (!CfgMgr32.TryGetUsbHubPort(id, out var hub, out var port))
        {
            target = $"no hub port for {id}";
            return false;
        }
        if (!Slv3TxPortIdentity.IsUsbHubInstanceId(hub))
        {
            target = $"{id} sits on {hub}, not a USB hub";
            return false;
        }
        instanceId = id;
        target = $"{hub} port {port} ({id})";
        return true;
    }

    public bool TryCycle(string instanceId, out string detail)
    {
        if (!Slv3TxPortIdentity.IsTxInstanceId(instanceId))
        {
            detail = $"{instanceId} is not a 0416:8040 device";
            return false;
        }
        return QSeriesUsbPortCycle.TryCycle(instanceId, out detail);
    }
}
#endif
