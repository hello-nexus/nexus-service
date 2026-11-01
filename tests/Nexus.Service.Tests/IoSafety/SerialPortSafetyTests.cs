using System.IO.Ports;
using Nexus.Service.Peripherals.IoSafety;
using Xunit;

namespace Nexus.Service.Tests.IoSafety;

public class SerialPortSafetyTests
{
    [Fact]
    public void Closing_a_port_that_was_never_opened_does_not_throw()
    {
        var port = new SerialPort("NEXUS-TEST-NO-SUCH-PORT");

        SerialPortSafety.Close(port);
        SerialPortSafety.Close(port);

        Assert.False(port.IsOpen);
    }

    [Fact]
    public void Opening_a_port_that_does_not_exist_throws_and_leaves_nothing_open()
    {
        var port = new SerialPort("NEXUS-TEST-NO-SUCH-PORT");

        Assert.ThrowsAny<System.Exception>(() => SerialPortSafety.OpenAndClear(port));

        Assert.False(port.IsOpen);
    }
}
