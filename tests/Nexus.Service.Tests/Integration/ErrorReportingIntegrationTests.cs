using System.IO;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;
using Nexus.Service.Telemetry;

namespace Nexus.Service.Tests.Integration;

public sealed class ErrorReportingIntegrationTests : IClassFixture<NexusAppFactory>
{
    private static readonly string CrashFile =
        Path.Combine(Path.GetTempPath(), "nexus-err-itest-" + Guid.NewGuid().ToString("N"), "error-crash.json");

    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;

    // The shared host reports nothing in a non-official build, so the reporter is swapped for a capturing one on a temp crash file.
    public ErrorReportingIntegrationTests(NexusAppFactory factory) =>
        _factory = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ErrorReporter>();
            s.AddSingleton(sp => new ErrorReporter(sp.GetRequiredService<IConfigStore>(), new NullErrorTransport(), TimeProvider.System, CrashFile));
        }));

    private string Token => _factory.Services.GetRequiredService<TokenService>().Token;

    private async Task<int> PostClientErrors(string json, bool withToken = true, int? declaredLength = null)
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            c.Request.Method = "POST";
            c.Request.Path = "/telemetry/client-errors";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (withToken)
                c.Request.Headers.Authorization = "Bearer " + Token;
            c.Request.ContentType = "application/json";
            c.Request.Body = new MemoryStream(bytes);
            c.Request.ContentLength = declaredLength ?? bytes.Length;
        });
        return ctx.Response.StatusCode;
    }

    private const string OneError =
        "{\"errors\":[{\"kind\":\"render\",\"fingerprint\":\"abc\",\"type\":\"TypeError\",\"message\":\"m\",\"stack\":\"s\",\"context\":\"c\",\"count\":2}]}";

    [Fact]
    public async Task Client_errors_returns_204_and_feeds_the_reporter()
    {
        var reporter = _factory.Services.GetRequiredService<ErrorReporter>();
        var before = reporter.PendingCount;
        Assert.Equal(StatusCodes.Status204NoContent, await PostClientErrors(OneError.Replace("abc", "fp-" + Guid.NewGuid().ToString("N"))));
        Assert.Equal(before + 1, reporter.PendingCount);
    }

    [Fact]
    public async Task Client_errors_skips_null_elements_instead_of_failing()
        => Assert.Equal(StatusCodes.Status204NoContent, await PostClientErrors("{\"errors\":[null]}"));

    [Fact]
    public async Task Client_errors_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await PostClientErrors(OneError, withToken: false));

    [Fact]
    public async Task Client_errors_rejects_more_than_ten_items()
    {
        var item = "{\"kind\":\"render\",\"fingerprint\":\"x\",\"count\":1}";
        var json = "{\"errors\":[" + string.Join(",", Enumerable.Repeat(item, 11)) + "]}";
        Assert.Equal(StatusCodes.Status400BadRequest, await PostClientErrors(json));
    }

    [Fact]
    public async Task Client_errors_rejects_an_oversize_body()
    {
        var json = "{\"errors\":[{\"kind\":\"render\",\"fingerprint\":\"x\",\"message\":\"" + new string('a', 200 * 1024) + "\",\"count\":1}]}";
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, await PostClientErrors(json));
    }

    [Fact]
    public async Task Paired_phone_can_post_client_errors()
    {
        var client = TestPhoneSession.CreateClient(_factory);
        var res = await client.PostAsync("/telemetry/client-errors",
            new StringContent(OneError.Replace("abc", "fp-" + Guid.NewGuid().ToString("N")), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    [Fact]
    public async Task A_route_that_throws_is_captured_and_still_propagates()
    {
        var reporter = _factory.Services.GetRequiredService<ErrorReporter>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseErrorCapture(reporter);
        app.MapGet("/boom/{id}", (string id) =>
        {
            throw new InvalidOperationException("probe " + id);
        });
        await app.StartAsync();

        var before = reporter.PendingCount;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => app.GetTestClient().GetAsync("/boom/42?secret=1"));
        Assert.Equal(before + 1, reporter.PendingCount);
        Assert.True(ex.Data.Contains(ErrorKinds.ReportedMarker));
    }

    private static (ErrorReporter Reporter, ServiceProvider Services) ReporterServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigStore>(new JsonConfigStore(Path.Combine(Path.GetTempPath(), "nexus-err-" + Guid.NewGuid().ToString("N"), "settings.json")));
        services.AddSingleton<IErrorTransport, NullErrorTransport>();
        services.AddSingleton(sp => new ErrorReporter(sp.GetRequiredService<IConfigStore>(), new NullErrorTransport(), TimeProvider.System, CrashFile));
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<ErrorReporter>(), sp);
    }

    [Fact]
    public async Task An_exception_on_an_aborted_request_is_marked_but_not_reported()
    {
        var reporter = _factory.Services.GetRequiredService<ErrorReporter>();
        var app = new ApplicationBuilder(_factory.Services);
        app.UseErrorCapture(reporter);
        app.Run(_ => throw new InvalidOperationException("aborted"));
        var pipeline = app.Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ctx = new DefaultHttpContext { RequestAborted = cts.Token };

        var before = reporter.PendingCount;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline(ctx));
        Assert.Equal(before, reporter.PendingCount);
        Assert.True(ex.Data.Contains(ErrorKinds.ReportedMarker));
    }

    [Fact]
    public void Relay_allows_only_the_client_errors_telemetry_route()
    {
        Assert.True(Nexus.Service.Relay.RelayHttpAllowlist.IsAllowed("POST", "/telemetry/client-errors"));
        Assert.False(Nexus.Service.Relay.RelayHttpAllowlist.IsAllowed("POST", "/telemetry/consent"));
    }

    [Fact]
    public void Logger_provider_skips_exceptions_the_middleware_already_reported()
    {
        var (reporter, sp) = ReporterServices();
        var logger = new ErrorLoggerProvider(sp).CreateLogger("Microsoft.AspNetCore.Server.Kestrel");

        var marked = new InvalidOperationException("a");
        marked.Data[ErrorKinds.ReportedMarker] = true;
        logger.LogError(marked, "unhandled");
        Assert.Equal(0, reporter.PendingCount);

        logger.LogError(new InvalidOperationException("b"), "unhandled");
        Assert.Equal(1, reporter.PendingCount);
    }

    private sealed class CrashingWorker : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => throw new InvalidOperationException("worker down");
    }

    [Fact]
    public async Task Logger_provider_captures_a_crashing_background_service()
    {
        var (reporter, _) = ReporterServices();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(reporter);
        builder.Services.AddSingleton<ILoggerProvider, ErrorLoggerProvider>();
        builder.Services.AddHostedService<CrashingWorker>();
        using var host = builder.Build();

        await host.StartAsync();
        for (var i = 0; i < 100 && reporter.PendingCount == 0; i++)
            await Task.Delay(50);
        await host.StopAsync();

        Assert.Equal(1, reporter.PendingCount);
    }
}
