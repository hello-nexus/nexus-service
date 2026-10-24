using System;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Cycles the USB hub port the SLV3 TX is attached to. Resolve names the target or the
/// reason it is refused; TryCycle acts on the resolved instance id only.
/// </summary>
internal interface ISlv3TxPortCycler
{
    bool TryResolve(Slv3PortInfo txPort, out string instanceId, out string target);

    bool TryCycle(string instanceId, out string detail);
}

internal static class Slv3TxPortIdentity
{
    private const string TxInstancePrefix = @"USB\VID_0416&PID_8040\";

    /// <summary>
    /// True only for the plain TX device itself: not an interface of a composite device,
    /// not the RX, not a hub, not the WCH alias.
    /// </summary>
    internal static bool IsTxInstanceId(string instanceId)
    {
        if (!instanceId.StartsWith(TxInstancePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var serial = instanceId.AsSpan(TxInstancePrefix.Length);
        return serial.Length > 0
            && serial.IndexOf('\\') < 0
            && !serial.Contains("&MI_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The parent must be a USB hub devnode (root hub or external hub).</summary>
    internal static bool IsUsbHubInstanceId(string instanceId) =>
        instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
}
