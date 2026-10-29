using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Corsair.Galleon;
using Nexus.Service.Peripherals.Ibp;
using Nexus.Service.Peripherals.Nollie;
using Nexus.Service.Persistence;
using Nexus.Service.Plugins;

namespace Nexus.Service.Tests;

public class DeviceListWriteHealthTests
{
    private sealed class Handler(string id) : IDeviceHandler
    {
        public string Id => id;
        public string Name => id;
        public string Category => "test";
        public IReadOnlyList<UsbId> Identifiers { get; } = Array.Empty<UsbId>();
        public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) => false;
        public string GetFirmwareVersion() => "";
    }

    // Driver key and the handler id the driver reports it under, as the hubs do.
    public static IEnumerable<object[]> DriverKeys() => new[]
    {
        new object[] { "keeb:SN1", "keeb" },
        new object[] { "np50:SN1", "np50" },
        new object[] { "smarthub:SN1", "smarthub" },
        new object[] { "minihub:SN1", "fan-hub" },
        new object[] { "nollie-s-ABC", NollieConnectionWorker.HandlerId },
        new object[] { GalleonSdProtocol.DeviceIdPrefix + "SN1", GalleonSdProtocol.HandlerId },
        new object[] { IbpPeripheralProtocol.DeviceIdPrefix + "kb1:SN1", IbpPeripheralProtocol.KeyboardHandlerId },
        new object[] { IbpPeripheralProtocol.DeviceIdPrefix + "ms1:SN1", IbpPeripheralProtocol.MouseHandlerId },
        new object[] { "strimer", "strimer" },
        new object[] { "lianli-wireless:aabbcc", "lianli-wireless" },
    };

    private static LightingWriteHealth Failing(string key, string handlerId)
    {
        var now = TimeSpan.Zero;
        var health = new LightingWriteHealth(() => now);
        for (var i = 0; i < 17; i++)
        {
            health.NoteFailure(key, handlerId);
            now += TimeSpan.FromSeconds(1);
        }
        return health;
    }

    private static List<Nexus.Service.Models.Devices.DeviceListItem> List(LightingWriteHealth health, params string[] ids)
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        foreach (var id in ids)
        {
            gate.SetEnabled(id, true);
        }
        return new DeviceManager(
            ids.Select(i => (IDeviceHandler)new Handler(i)).ToArray(),
            new StubUsbEnumerator(),
            new PluginProviderRegistry(),
            gate,
            health).GetAll();
    }

    [Theory]
    [MemberData(nameof(DriverKeys))]
    public void A_drivers_failing_key_marks_exactly_its_handler_row(string key, string handlerId)
    {
        var ids = DriverKeys().Select(o => (string)o[1]).Distinct().ToArray();

        var items = List(Failing(key, handlerId), ids);

        Assert.Equal(LightingWriteHealth.Failing, items.Single(i => i.Id == handlerId).WriteHealth);
        Assert.All(items.Where(i => i.Id != handlerId), i => Assert.Null(i.WriteHealth));
    }

    [Fact]
    public void A_key_that_merely_starts_like_a_handler_id_does_not_mark_it()
    {
        var items = List(Failing("keeb-extra:1", "other"), "keeb", "other");

        Assert.Null(items.Single(i => i.Id == "keeb").WriteHealth);
    }
}
