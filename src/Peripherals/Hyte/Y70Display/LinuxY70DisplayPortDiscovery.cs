using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Peripherals.Hyte.Y70Display;

/// <summary>
/// Linux Y70 display discovery: finds <c>/dev/ttyACM*</c> ports whose USB parent
/// is VID 0x3402 / PID 0x0C00 (Touch), 0x0C01 (Infinite), 0x0C02 (Truly) or 0x0C06 (GT),
/// tagging each with its variant. Counterpart to
/// <see cref="WindowsY70DisplayPortDiscovery"/>.
/// </summary>
public sealed class LinuxY70DisplayPortDiscovery : IY70DisplayPortDiscovery
{
    public IReadOnlyList<Y70DisplayPort> Discover() =>
        LinuxSerialDiscovery.Find(
                Y70DisplayProtocol.VendorId,
                Y70DisplayProtocol.Y70TouchProductId,
                Y70DisplayProtocol.Y70InfiniteProductId,
                Y70DisplayProtocol.Y70TrulyProductId,
                Y70DisplayProtocol.Y70GtProductId)
            .Select(m => new Y70DisplayPort
            {
                PortName = m.PortName,
                Serial = m.Serial,
                Variant = Y70DisplayProtocol.VariantForProductId(m.ProductId),
            })
            .ToList();
}
