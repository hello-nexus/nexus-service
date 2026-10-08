using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>Results.Problem writes ProblemDetails through the AOT resolver; without its metadata the route throws instead of answering 500.</summary>
public class ProblemDetailsSerializationTests
{
    [Fact]
    public void ProblemDetailsSerializesThroughAppJsonContext()
    {
        var json = JsonSerializer.Serialize(
            new ProblemDetails { Status = 500, Detail = "Failed to read firmware animation from NP50." },
            AppJsonContext.Default.Options.GetTypeInfo(typeof(ProblemDetails)));

        Assert.Contains("\"detail\":\"Failed to read firmware animation from NP50.\"", json);
    }
}
