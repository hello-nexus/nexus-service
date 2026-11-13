using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Models.Activity;
using Nexus.Service.Panel;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// POST /system/pick-path through the real pipeline: mode plumbing (folder vs
/// any-file), the cancel-to-null contract, and the auth tier - the native
/// dialog opens on the host, so it must reject a paired panel session the
/// same way the gallery picker does.
/// </summary>
public sealed class SystemPickPathRoutesTests : IDisposable
{
    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly StubPicker _picker = new();

    public SystemPickPathRoutesTests()
    {
        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // The real picker opens an OS dialog; tests stub it and only
                // exercise the route plumbing + auth tier.
                services.RemoveAll<IFileDialogPicker>();
                services.AddSingleton<IFileDialogPicker>(_picker);
            }));
    }

    /// <summary>Records the mode it was invoked with and returns a canned result.</summary>
    private sealed class StubPicker : IFileDialogPicker
    {
        public FileDialogPickMode? LastMode { get; private set; }
        public FileDialogPickResult Next { get; set; } = new() { Paths = { "/stub/picked.txt" } };

        public Task<FileDialogPickResult> PickAsync(FileDialogPickMode mode, CancellationToken ct)
        {
            LastMode = mode;
            return Task.FromResult(Next);
        }
    }

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private string Token => _factory.Services.GetRequiredService<TokenService>().Token;

    private HttpClient DesktopClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    /// <summary>Mint a real paired-phone session and return a client bearing it.</summary>
    private HttpClient PanelClient()
    {
        var pairing = _factory.Services.GetRequiredService<PanelPhonePairingService>();
        pairing.CreatePairQr();
        var pairToken = pairing.GetOutstandingPairTokens()[0].Token;
        var claim = pairing.ClaimCoreApproved(pairToken, "Test Phone", "TestUA", "192.168.1.50", "itest-device",
            overRelay: false, claimedOverHttps: true);
        Assert.True(claim.Ok, claim.Error);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", claim.SessionToken);
        return client;
    }

    private static async Task<T?> ReadAs<T>(HttpResponseMessage res, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        => JsonSerializer.Deserialize(await res.Content.ReadAsStringAsync(), info);

    [Fact]
    public async Task File_ReturnsPickedPath_And_UsesAnyFileSingleMode()
    {
        var res = await DesktopClient().PostAsJsonAsync("/system/pick-path", new PickPathBody { Folder = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var parsed = await ReadAs(res, AppJsonContext.Default.PickPathResponse);
        Assert.Equal("/stub/picked.txt", parsed!.Path);
        Assert.Equal(FileDialogPickMode.AnyFileSingle, _picker.LastMode);
    }

    [Fact]
    public async Task Folder_UsesFolderMode()
    {
        var res = await DesktopClient().PostAsJsonAsync("/system/pick-path", new PickPathBody { Folder = true });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(FileDialogPickMode.Folder, _picker.LastMode);
    }

    [Fact]
    public async Task Cancelled_ReturnsNullPath_With200()
    {
        _picker.Next = new FileDialogPickResult { Cancelled = true };

        var res = await DesktopClient().PostAsJsonAsync("/system/pick-path", new PickPathBody { Folder = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var parsed = await ReadAs(res, AppJsonContext.Default.PickPathResponse);
        Assert.Null(parsed!.Path);
    }

    [Fact]
    public async Task DialogError_ReturnsNullPath_With200()
    {
        _picker.Next = new FileDialogPickResult { Error = true, Msg = "a file dialog is already open on the PC" };

        var res = await DesktopClient().PostAsJsonAsync("/system/pick-path", new PickPathBody { Folder = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var parsed = await ReadAs(res, AppJsonContext.Default.PickPathResponse);
        Assert.Null(parsed!.Path);
    }

    [Fact]
    public async Task PanelSession_CannotPickPath()
    {
        var panel = PanelClient();
        var res = await panel.PostAsJsonAsync("/system/pick-path", new PickPathBody { Folder = false });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task NoToken_Is401()
    {
        var anon = _factory.CreateClient();
        var res = await anon.PostAsJsonAsync("/system/pick-path", new PickPathBody { Folder = false });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public void NotOnRelayAllowlist()
    {
        Assert.False(Nexus.Service.Relay.RelayHttpAllowlist.IsAllowed("POST", "/system/pick-path"));
    }
}
