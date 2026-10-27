using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nexus.Service.Mcp;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// McpServerHost lifecycle: the master toggle gates whether anything binds at
/// all, runtime enable/disable starts and stops the real port, and the
/// NEXUS_TEST_HOST=1 boot gate skips autostart. Each test builds its own
/// host/store rather than sharing the always-on fixture the protocol suite uses.
/// </summary>
public sealed class McpServerHostLifecycleTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private McpServerHost? _host;

    public void Dispose()
    {
        try { _host?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        _tempDir.Dispose();
    }

    private (TestableConfigStore Store, McpServerHost Host) NewHost()
    {
        var store = new TestableConfigStore(Path.Combine(_tempDir.Root, "settings.json"));
        var tools = McpTestHarness.BuildRealTools(store, out _, out _);
        var registry = new McpToolRegistry(tools, store, new LoggingMcpAuditSink());
        var host = new McpServerHost(store, registry);
        _host = host;
        return (store, host);
    }

    [Fact]
    public async Task Master_off_means_nothing_is_listening()
    {
        var (store, host) = NewHost();
        store.Update(s => s.AiIntegration.Enabled = false);

        await host.ApplyConfiguredStateAsync();

        Assert.False(host.Running);
        Assert.Null(host.BoundPort);
    }

    [Fact]
    public async Task Runtime_enable_then_disable_starts_then_stops_the_port()
    {
        var (store, host) = NewHost();
        store.Update(s =>
        {
            s.AiIntegration.Enabled = true;
            s.AiIntegration.Token = "some-token";
            s.AiIntegration.Port = 0;
        });

        await host.ApplyConfiguredStateAsync();
        Assert.True(host.Running);
        var boundPort = host.BoundPort;
        Assert.NotNull(boundPort);

        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
        {
            var probe = await client.GetAsync($"http://127.0.0.1:{boundPort}/mcp");
            Assert.Equal(HttpStatusCode.MethodNotAllowed, probe.StatusCode);
        }

        store.Update(s => s.AiIntegration.Enabled = false);
        await host.ApplyConfiguredStateAsync();

        Assert.False(host.Running);
        Assert.Null(host.BoundPort);

        using var clientAfterStop = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        await Assert.ThrowsAsync<HttpRequestException>(() => clientAfterStop.GetAsync($"http://127.0.0.1:{boundPort}/mcp"));
    }

    // Physical Ethernet or Wi-Fi only: tunnel, VPN and virtual-bridge adapters
    // may not route back to this host.
    private static IPAddress? NonLoopbackLocalIPv4() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && (n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
                    || n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(a)
                && !(a.GetAddressBytes()[0] == 169 && a.GetAddressBytes()[1] == 254));

    private static async Task<HttpResponseMessage?> TryGetAsync(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            return await client.GetAsync(url);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    [Fact]
    public async Task Default_binds_loopback_only()
    {
        var (store, host) = NewHost();
        store.Update(s =>
        {
            s.AiIntegration.Enabled = true;
            s.AiIntegration.Token = "some-token";
            s.AiIntegration.Port = 0;
        });

        await host.ApplyConfiguredStateAsync();

        Assert.True(host.Running);
        Assert.False(host.BoundRemote);
        var lan = NonLoopbackLocalIPv4();
        if (lan is not null)
        {
            Assert.Null(await TryGetAsync($"http://{lan}:{host.BoundPort}/mcp"));
        }
    }

    [Fact]
    public async Task Allow_remote_binds_all_interfaces_and_still_requires_the_token()
    {
        var (store, host) = NewHost();
        store.Update(s =>
        {
            s.AiIntegration.Enabled = true;
            s.AiIntegration.Token = "some-token";
            s.AiIntegration.Port = 0;
            s.AiIntegration.AllowRemote = true;
        });

        await host.ApplyConfiguredStateAsync();

        Assert.True(host.Running);
        Assert.True(host.BoundRemote);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var loopbackUrl = $"http://127.0.0.1:{host.BoundPort}/mcp";
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync(loopbackUrl)).StatusCode);
        var unauth = await client.PostAsync(loopbackUrl, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        // The LAN address is only asserted when this machine can reach itself on it.
        var lan = NonLoopbackLocalIPv4();
        if (lan is not null && await TryGetAsync($"http://{lan}:{host.BoundPort}/mcp") is { } lanRes)
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, lanRes.StatusCode);
        }
    }

    [Fact]
    public async Task Allow_remote_fails_loudly_when_another_process_holds_loopback_on_the_port()
    {
        using var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var takenPort = ((IPEndPoint)squatter.LocalEndpoint).Port;

        var (store, host) = NewHost();
        store.Update(s =>
        {
            s.AiIntegration.Enabled = true;
            s.AiIntegration.Token = "some-token";
            s.AiIntegration.Port = takenPort;
            s.AiIntegration.AllowRemote = true;
        });

        await host.ApplyConfiguredStateAsync();

        Assert.False(host.Running);
        Assert.False(host.BoundRemote);
        Assert.Contains("already in use", host.LastError);
    }

    [Fact]
    public async Task Re_enabling_after_disable_binds_a_fresh_listening_port()
    {
        var (store, host) = NewHost();
        store.Update(s =>
        {
            s.AiIntegration.Enabled = true;
            s.AiIntegration.Token = "some-token";
            s.AiIntegration.Port = 0;
        });
        await host.ApplyConfiguredStateAsync();
        Assert.True(host.Running);

        store.Update(s => s.AiIntegration.Enabled = false);
        await host.ApplyConfiguredStateAsync();
        Assert.False(host.Running);

        store.Update(s => s.AiIntegration.Enabled = true);
        await host.ApplyConfiguredStateAsync();

        Assert.True(host.Running);
        Assert.NotNull(host.BoundPort);
    }

    [Fact]
    public async Task Bind_failure_when_the_port_is_taken_leaves_running_false_with_last_error_set_and_recovers_after_the_port_frees()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var takenPort = ((IPEndPoint)blocker.LocalEndpoint).Port;

        var (store, host) = NewHost();
        store.Update(s =>
        {
            s.AiIntegration.Enabled = true;
            s.AiIntegration.Token = "some-token";
            s.AiIntegration.Port = takenPort;
        });

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await host.ApplyConfiguredStateAsync();
            Assert.False(host.Running);
            Assert.NotNull(host.LastError);
            Assert.Null(host.BoundPort);
        }

        blocker.Stop();
        store.Update(s => s.AiIntegration.Port = 0);
        await host.ApplyConfiguredStateAsync();
        Assert.True(host.Running);
        Assert.NotNull(host.BoundPort);

        store.Update(s => s.AiIntegration.Enabled = false);
        await host.ApplyConfiguredStateAsync();
        Assert.False(host.Running);
        Assert.Null(host.BoundPort);
    }

    [Fact]
    public async Task StartAsync_does_not_bind_under_NEXUS_TEST_HOST()
    {
        var original = Environment.GetEnvironmentVariable("NEXUS_TEST_HOST");
        Environment.SetEnvironmentVariable("NEXUS_TEST_HOST", "1");
        try
        {
            var store = new TestableConfigStore(Path.Combine(_tempDir.Root, "settings-testhost.json"));
            store.Update(s =>
            {
                s.AiIntegration.Enabled = true;
                s.AiIntegration.Token = "some-token";
                s.AiIntegration.Port = 0;
            });
            var tools = McpTestHarness.BuildRealTools(store, out _, out _);
            var registry = new McpToolRegistry(tools, store, new LoggingMcpAuditSink());
            var host = new McpServerHost(store, registry);
            _host = host;

            await host.StartAsync(default);

            Assert.False(host.Running);
            Assert.Null(host.BoundPort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEXUS_TEST_HOST", original);
        }
    }
}
