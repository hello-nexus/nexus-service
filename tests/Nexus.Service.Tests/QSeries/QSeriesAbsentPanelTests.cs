using Nexus.Service.QSeries;
using Xunit;

namespace Nexus.Service.Tests.QSeries;

public class HubServesOnlyQSeriesTests
{
    private static QSeriesHubPort Empty(uint port) => new(port, QSeriesHubPort.NoDeviceConnected, 0, 0);
    private static QSeriesHubPort Device(uint port, ushort vid, ushort pid) => new(port, QSeriesHubPort.DeviceConnected, vid, pid);

    [Fact]
    public void Unit_hub_with_the_panel_missing_is_resettable()
    {
        // The Y70 Q60 hub with the panel's port 3 empty.
        Assert.True(QSeriesPortWatcher.HubServesOnlyQSeries([Empty(1), Device(2, 0x3402, 0x0400), Empty(3), Empty(4)], 2));
    }

    [Fact]
    public void A_panel_port_that_failed_enumeration_still_counts_as_the_unit()
    {
        Assert.True(QSeriesPortWatcher.HubServesOnlyQSeries(
            [Empty(1), Device(2, 0x3402, 0x0400), new QSeriesHubPort(3, 2, 0, 0), Empty(4)], 2));
    }

    [Fact]
    public void Hub_with_another_device_is_not_reset()
    {
        Assert.False(QSeriesPortWatcher.HubServesOnlyQSeries([Device(1, 0x046D, 0xC54D), Device(2, 0x3402, 0x0400), Empty(3)], 2));
    }

    [Fact]
    public void Unreadable_port_is_not_assumed_empty()
    {
        Assert.False(QSeriesPortWatcher.HubServesOnlyQSeries(
            [new QSeriesHubPort(1, QSeriesHubPort.Unreadable, 0, 0), Device(2, 0x3402, 0x0400)], 2));
    }

    [Fact]
    public void Hub_that_does_not_list_the_cooler_is_not_reset()
    {
        Assert.False(QSeriesPortWatcher.HubServesOnlyQSeries([Empty(1), Empty(3)], 2));
    }

    [Fact]
    public void Port_table_reads_as_one_line()
    {
        Assert.Equal(
            "1=empty 2=3402:0400 3=failed-enumeration 4=status7",
            string.Join(' ', new[] { Empty(1), Device(2, 0x3402, 0x0400), new QSeriesHubPort(3, 2, 0, 0), new QSeriesHubPort(4, 7, 0, 0) }));
    }
}
