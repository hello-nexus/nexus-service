namespace Nexus.Service.Common.ExternalTools;

/// <summary>
/// Lets a device clean up after its vendor driver is stopped by the Nexus Control
/// gate. Fired once on the off transition, after the process is gone, so the hook
/// is the only writer on the device.
///
/// Not fired when the device leaves the bus: there is nothing left to clean up on
/// hardware that is no longer there.
/// </summary>
public interface IDriverGateStopHook
{
    /// <summary><see cref="Nexus.Service.Devices.IDeviceHandler.Id"/> this hook belongs to.</summary>
    string DeviceId { get; }

    /// <summary>Runs on the worker's tick; must not block it for long.</summary>
    void OnGatedOff();
}
