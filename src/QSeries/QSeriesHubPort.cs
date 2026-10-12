namespace Nexus.Service.QSeries;

/// <summary>
/// One downstream hub port as the hub itself reports it, so a port whose device
/// never got a devnode still reads. <see cref="Status"/> is USB_CONNECTION_STATUS.
/// </summary>
internal readonly record struct QSeriesHubPort(uint Port, uint Status, ushort VendorId, ushort ProductId)
{
    internal const uint NoDeviceConnected = 0;
    internal const uint DeviceConnected = 1;
    internal const uint Unreadable = uint.MaxValue;

    public override string ToString() => Status switch
    {
        NoDeviceConnected => $"{Port}=empty",
        DeviceConnected => $"{Port}={VendorId:X4}:{ProductId:X4}",
        2 => $"{Port}=failed-enumeration",
        3 => $"{Port}=general-failure",
        4 => $"{Port}=overcurrent",
        5 => $"{Port}=not-enough-power",
        9 => $"{Port}=enumerating",
        10 => $"{Port}=reset",
        Unreadable => $"{Port}=unreadable",
        _ => $"{Port}=status{Status}",
    };
}
