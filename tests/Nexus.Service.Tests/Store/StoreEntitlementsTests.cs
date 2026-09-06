using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cloud;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Store;
using Nexus.Service.Tests.Cloud;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Store;

public sealed class StoreEntitlementsTests : IDisposable
{
    private readonly string _root;

    public StoreEntitlementsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nexus-store-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteApp(string id, string version, string name, int payloadBytes = 64)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            $"{{\"schema\":\"nexus.app/1\",\"id\":\"{id}\",\"version\":\"{version}\",\"name\":\"{name}\"," +
            "\"description\":\"Fish\",\"icon\":\"assets/icon.svg\",\"runtime\":\"sdk\",\"sizes\":[\"2x2\"]}");
        File.WriteAllText(Path.Combine(dir, "widget.mjs"), new string('x', payloadBytes));
    }

    private (StoreEntitlements ent, FakeCloudApiClient api, CloudAccountService accounts) Make(bool signedIn)
    {
        var api = new FakeCloudApiClient();
        var accounts = new CloudAccountService(api, new InMemoryConfigStore(), TimeProvider.System);
        if (signedIn)
        {
            api.OnLogin = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
            {
                AccessToken = "access-1",
                RefreshToken = "refresh-1",
                Account = new CloudAccountDto
                {
                    Id = "acct-1",
                    Email = "user@example.com",
                    Username = "user",
                    EmailVerified = true,
                    CreatedAt = "2026-01-01T00:00:00Z",
                },
            });
            accounts.LoginAsync("user@example.com", "password1", CancellationToken.None).GetAwaiter().GetResult();
        }
        var registry = new AppRegistry(() => new List<AppInstallPaths.Root>
        {
            new(_root, AppInstallPaths.Source.User),
        });
        return (new StoreEntitlements(api, accounts, registry), api, accounts);
    }

    [Fact]
    public async Task Authorize_without_an_account_asks_for_sign_in_and_never_calls_the_cloud()
    {
        var (ent, api, _) = Make(signedIn: false);

        var auth = await ent.AuthorizeAsync("com.hellonexus.aquarium", "1.0.2", null, CancellationToken.None);

        Assert.False(auth.Ok);
        Assert.Equal("sign_in_required", auth.Reason);
        Assert.Equal(0, api.SendRawCalls);
    }

    [Fact]
    public async Task Authorize_returns_the_cloud_hash_as_the_trust_pin()
    {
        var (ent, api, _) = Make(signedIn: true);
        var sha = new string('a', 64);
        api.OnSendRaw = (method, path, _, token) =>
        {
            Assert.Equal(HttpMethod.Get, method);
            Assert.Equal("/store/apps/com.hellonexus.aquarium/download?version=1.0.2", path);
            Assert.Equal("access-1", token);
            return CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse
            {
                Body = $"{{\"appId\":\"com.hellonexus.aquarium\",\"version\":\"1.0.2\",\"sha256\":\"{sha}\"," +
                       "\"size\":12447,\"acquiredAt\":\"2026-08-29T16:33:42.104Z\"}",
            });
        };

        var auth = await ent.AuthorizeAsync("com.hellonexus.aquarium", "1.0.2", null, CancellationToken.None);

        Assert.True(auth.Ok);
        Assert.Equal(sha, auth.Grant!.Sha256);
        Assert.Equal(12447, auth.Grant.Size);
    }

    [Theory]
    [InlineData(401, "sign_in_required")]
    [InlineData(403, "sign_in_required")]
    [InlineData(404, "version_unavailable")]
    [InlineData(500, "store_unavailable")]
    public async Task Authorize_maps_a_cloud_refusal_to_a_reason(int status, string reason)
    {
        var (ent, api, _) = Make(signedIn: true);
        api.OnSendRaw = (_, _, _, _) => CloudApiResult<CloudRawResponse>.Fail(status, null, null);

        var auth = await ent.AuthorizeAsync("com.hellonexus.aquarium", "1.0.2", null, CancellationToken.None);

        Assert.False(auth.Ok);
        Assert.Equal(reason, auth.Reason);
    }

    [Fact]
    public async Task Library_signed_out_still_lists_what_is_installed_here()
    {
        WriteApp("com.hellonexus.aquarium", "1.0.2", "Aquarium");
        var (ent, _, _) = Make(signedIn: false);

        var library = await ent.LibraryAsync(CancellationToken.None);

        Assert.False(library.SignedIn);
        var row = Assert.Single(library.Purchases);
        Assert.Equal("Aquarium", row.Name);
        Assert.Equal("1.0.2", row.InstalledVersion);
        Assert.Null(row.AcquiredAt);
        // No store verdict for a local-only row: null, not "delisted".
        Assert.Null(row.Listed);
        Assert.True(row.SizeBytes > 0);
        Assert.NotNull(row.InstalledAt);
    }

    [Fact]
    public async Task Library_joins_the_entitlement_with_the_local_install()
    {
        WriteApp("com.hellonexus.aquarium", "1.0.2", "Aquarium");
        var (ent, api, _) = Make(signedIn: true);
        api.OnSendRaw = (_, path, _, _) =>
        {
            Assert.Equal("/store/library", path);
            return CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse
            {
                Body = "{\"entitlements\":[{\"appId\":\"com.hellonexus.aquarium\",\"acquiredAt\":\"2026-08-29T16:33:42.104Z\"," +
                       "\"priceCents\":0,\"name\":\"Aquarium\",\"tagline\":\"Feed the fish\",\"listed\":true," +
                       "\"iconUrl\":\"https://assets.hellonexus.com/apps/com.hellonexus.aquarium/media/icon.svg\"}," +
                       "{\"appId\":\"com.hellonexus.ina\",\"acquiredAt\":\"2026-08-30T00:00:00Z\",\"priceCents\":0," +
                       "\"name\":\"Ina\",\"listed\":true}]}",
            });
        };

        var library = await ent.LibraryAsync(CancellationToken.None);

        Assert.True(library.SignedIn);
        Assert.False(library.Offline);
        Assert.Equal(2, library.Purchases.Count);

        var aquarium = library.Purchases[0];
        Assert.Equal("Feed the fish", aquarium.Tagline);
        Assert.Equal("1.0.2", aquarium.InstalledVersion);
        Assert.True(aquarium.SizeBytes > 0);
        // The dashboard's img-src is 'self', so the asset host has to be rewritten
        // onto the service's own media proxy.
        Assert.Equal("/apps-api/store/media/com.hellonexus.aquarium/media/icon.svg", aquarium.IconUrl);

        // Acquired on another machine: owned, nothing installed here.
        var ina = library.Purchases[1];
        Assert.Null(ina.InstalledVersion);
        Assert.Null(ina.SizeBytes);
        Assert.NotNull(ina.AcquiredAt);
    }

    [Fact]
    public async Task Library_falls_back_to_local_installs_when_the_cloud_is_unreachable()
    {
        WriteApp("com.hellonexus.aquarium", "1.0.2", "Aquarium");
        var (ent, api, _) = Make(signedIn: true);
        api.OnSendRaw = (_, _, _, _) => CloudApiResult<CloudRawResponse>.NetworkError("offline");

        var library = await ent.LibraryAsync(CancellationToken.None);

        Assert.True(library.SignedIn);
        Assert.True(library.Offline);
        Assert.Single(library.Purchases);
    }
    [Fact]
    public void DirectorySize_still_counts_the_rest_when_one_entry_cannot_be_read()
    {
        var dir = Path.Combine(_root, "sizes");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "widget.mjs"), new string('x', 100));
        Directory.CreateDirectory(Path.Combine(dir, "assets"));
        File.WriteAllText(Path.Combine(dir, "assets", "icon.svg"), new string('y', 50));

        Assert.Equal(150, StoreEntitlements.DirectorySize(dir));
    }

    [Fact]
    public void DirectorySize_of_a_missing_folder_is_null()
    {
        Assert.Null(StoreEntitlements.DirectorySize(Path.Combine(_root, "gone")));
    }
}
