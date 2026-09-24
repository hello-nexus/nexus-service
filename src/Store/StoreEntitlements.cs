using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cloud;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;
using Nexus.Service.Widgets;

namespace Nexus.Service.Store;

/// <summary>Outcome of asking the cloud whether this account may download an app.</summary>
/// <param name="Reason">Null on success; otherwise the machine-readable failure the client renders.</param>
public readonly record struct StoreAuthorization(bool Ok, string? Reason, StoreDownloadGrant? Grant);

/// <summary>
/// The account side of the store: the download gate and the purchase library.
/// </summary>
/// <remarks>
/// Getting an app is an account act - the cloud records the entitlement the
/// first time an account downloads one, and that record is what Manage
/// purchases lists. So an install without a linked account is refused here
/// rather than in the UI, which is only where the sign-in prompt appears.
///
/// This is an ownership record, not DRM: the artifact on the asset host stays
/// publicly fetchable, and a determined user can always fetch it directly.
/// </remarks>
public sealed class StoreEntitlements
{
    private readonly ICloudApiClient _api;
    private readonly CloudAccountService _accounts;
    private readonly AppRegistry _registry;

    public StoreEntitlements(ICloudApiClient api, CloudAccountService accounts, AppRegistry registry)
    {
        _api = api;
        _accounts = accounts;
        _registry = registry;
    }

    /// <summary>Whether an account is linked at all. A refused or expired token also reads as sign_in_required, so a caller that waives the gate has to tell the two apart.</summary>
    public bool HasLinkedAccount => !string.IsNullOrEmpty(_accounts.ActiveAccountId);

    /// <summary>
    /// Asks the cloud for a download grant, which both authorizes the install
    /// and records the purchase. The grant's hash is authoritative - the local
    /// installer verifies against it rather than against anything the page sent.
    /// </summary>
    public async Task<StoreAuthorization> AuthorizeAsync(
        string appId, string version, string? nexusVersion, CancellationToken ct)
    {
        if (!AppIds.IsValid(appId)) return new StoreAuthorization(false, "invalid_app_id", null);
        if (!StoreInstaller.IsValidVersion(version)) return new StoreAuthorization(false, "invalid_version", null);

        var accountId = _accounts.ActiveAccountId;
        if (string.IsNullOrEmpty(accountId)) return new StoreAuthorization(false, "sign_in_required", null);

        var path = $"/store/apps/{appId}/download?version={Uri.EscapeDataString(version)}";
        if (!string.IsNullOrWhiteSpace(nexusVersion))
        {
            path += $"&nexusVersion={Uri.EscapeDataString(nexusVersion)}";
        }

        var result = await _accounts.WithAuthAsync(
            accountId, token => _api.SendRawAsync(HttpMethod.Get, path, null, token, ct), ct).ConfigureAwait(false);

        // SendRawAsync reports Success for any response it actually received, so
        // the status is what decides here; only a network failure sets Offline.
        if (result.Offline) return new StoreAuthorization(false, "store_unavailable", null);
        // A launch-day refusal is also a 403, and it is not something signing in
        // fixes - the storefront normally never offers the install, so this is
        // the minute-wide window where the client's catalog copy is stale.
        if (result.StatusCode == 403 && result.Value is not null
            && result.Value.Body.Contains("not_yet_released", StringComparison.Ordinal))
        {
            return new StoreAuthorization(false, "not_yet_released", null);
        }
        if (result.StatusCode is 401 or 403) return new StoreAuthorization(false, "sign_in_required", null);
        if (result.StatusCode == 404) return new StoreAuthorization(false, "version_unavailable", null);
        if (!result.Success || result.StatusCode >= 300 || result.Value is null)
        {
            return new StoreAuthorization(false, "store_unavailable", null);
        }

        var grant = TryParse(result.Value.Body, AppJsonContext.Default.StoreDownloadGrant);
        if (grant is null || string.IsNullOrWhiteSpace(grant.Sha256))
        {
            return new StoreAuthorization(false, "store_unavailable", null);
        }
        return new StoreAuthorization(true, null, grant);
    }

    /// <summary>
    /// Manage purchases: the account's cloud entitlements joined with what this
    /// machine has on disk. Both halves are optional - an app acquired on
    /// another machine is listed without local facts, and one sideloaded here is
    /// listed without an entitlement.
    /// </summary>
    public async Task<StoreLibraryResponse> LibraryAsync(CancellationToken ct)
    {
        var local = LocalInstalls();
        var accountId = _accounts.ActiveAccountId;
        if (string.IsNullOrEmpty(accountId))
        {
            return new StoreLibraryResponse { SignedIn = false, Purchases = local.Values.ToList() };
        }

        var result = await _accounts.WithAuthAsync(
            accountId, token => _api.SendRawAsync(HttpMethod.Get, "/store/library", null, token, ct), ct).ConfigureAwait(false);

        var library = result is { Success: true, StatusCode: < 300, Value: not null }
            ? TryParse(result.Value.Body, AppJsonContext.Default.CloudStoreLibraryResponse)
            : null;
        if (library is null)
        {
            // The purchases the machine can still prove are shown rather than an
            // empty page: a store outage must not read as "you own nothing".
            return new StoreLibraryResponse { SignedIn = true, Offline = true, Purchases = local.Values.ToList() };
        }

        var purchases = new List<StorePurchase>();
        foreach (var row in library.Entitlements)
        {
            local.Remove(row.AppId, out var installed);
            purchases.Add(new StorePurchase
            {
                AppId = row.AppId,
                Name = string.IsNullOrEmpty(row.Name) ? installed?.Name ?? row.AppId : row.Name,
                Tagline = row.Tagline,
                Description = row.Description,
                // The dashboard's img-src is 'self', so a cloud icon URL has to
                // ride the media proxy the same way the catalog's does.
                IconUrl = StoreCatalogProxy.RewriteMedia(row.IconUrl) ?? installed?.IconUrl,
                AcquiredAt = row.AcquiredAt,
                PriceCents = row.PriceCents,
                Listed = row.Listed,
                InstalledVersion = installed?.InstalledVersion,
                InstalledAt = installed?.InstalledAt,
                SizeBytes = installed?.SizeBytes,
            });
        }
        purchases.AddRange(local.Values);
        return new StoreLibraryResponse { SignedIn = true, Purchases = purchases };
    }

    /// <summary>
    /// User-installed apps as purchase rows, keyed by id. Bundled apps are
    /// excluded: they ship with Nexus and were never acquired.
    /// </summary>
    private Dictionary<string, StorePurchase> LocalInstalls()
    {
        var map = new Dictionary<string, StorePurchase>(StringComparer.Ordinal);
        foreach (var entry in _registry.All())
        {
            if (entry.Source != AppInstallPaths.Source.User) continue;
            map[entry.Id] = new StorePurchase
            {
                AppId = entry.Id,
                Name = string.IsNullOrEmpty(entry.Manifest.Name) ? entry.Id : entry.Manifest.Name,
                Description = entry.Manifest.Description ?? "",
                IconUrl = IsBundleRelativeAssetPath(entry.Manifest.Icon)
                    ? $"/apps-api/installed/{entry.Id}/asset/{entry.Manifest.Icon}"
                    : null,
                InstalledVersion = entry.Manifest.Version,
                InstalledAt = InstalledAt(entry.RootPath),
                SizeBytes = DirectorySize(entry.RootPath),
            };
        }
        return map;
    }

    /// <summary>Same rule the installed-asset route applies: a manifest icon is only linkable when it stays inside the bundle.</summary>
    private static bool IsBundleRelativeAssetPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.Contains("..", StringComparison.Ordinal)
        && !path.StartsWith('/')
        && !path.StartsWith('\\');

    /// <summary>
    /// When the app folder appeared. Linux does not always carry a creation
    /// time; an unset one comes back as the epoch, so last-write stands in.
    /// </summary>
    internal static DateTimeOffset? InstalledAt(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists) return null;
            var created = info.CreationTimeUtc;
            return created.Year > 1980 ? created : info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Bytes the installed app occupies. Null when the folder cannot be read.</summary>
    internal static long? DirectorySize(string path)
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // One unreadable entry must not cost the whole figure. A name
                    // Win32 normalizes away (a macOS "._." AppleDouble ends in a
                    // dot, which the path layer strips) enumerates fine and then
                    // throws FileNotFoundException on the length read.
                }
            }
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static T? TryParse<T>(string body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
