using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Nexus.Service.Routes;

namespace Nexus.Service.Tests.Routes;

public class FirmwareReadBusyTests
{
    [Fact]
    public void A_failed_firmware_read_is_a_retryable_503_not_a_500()
    {
        var result = DevicesRoutes.FirmwareReadBusy("Failed to read firmware animation from NP50.");

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
    }
}
