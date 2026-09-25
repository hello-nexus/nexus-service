using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;

namespace Nexus.Service.Store;

/// <summary>What the store would install for an app: the catalog's newest version, then the download an account is entitled to.</summary>
internal static class StoreRelease
{
    /// <summary>Newest version this build can run, from the catalog route that takes no token and applies no storefront filter, so an unlisted app resolves.</summary>
    public static async Task<StoreCatalogVersion?> LatestAsync(StoreCatalogProxy catalog, string appId, string nexusVersion, CancellationToken ct)
    {
        var body = await catalog.DetailAsync(appId, nexusVersion, null, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(body)) return null;
        StoreCatalogApp? listing;
        try
        { listing = JsonSerializer.Deserialize(body, AppJsonContext.Default.StoreCatalogApp); }
        catch (JsonException)
        { return null; }

        // Launch-day gate for every caller; the account-free hardware install never reaches the cloud's own refusal.
        if (listing?.ReleaseDate is { } launch && launch > DateTimeOffset.UtcNow) return null;

        var latest = listing?.Latest;
        if (latest is null) return null;
        if (string.IsNullOrWhiteSpace(latest.Sha256) || !StoreInstaller.IsValidVersion(latest.Version)) return null;
        return latest;
    }

    /// <summary>
    /// The install request for <paramref name="version"/>, pinned to the entitled hash when an account grants it.
    /// <paramref name="waiveAccount"/> (attached hardware) lets a machine with no account at all install on the
    /// catalog hash. Null when neither holds.
    /// </summary>
    public static async Task<StoreInstallRequest?> ResolveAsync(
        StoreEntitlements entitlements, string appId, StoreCatalogVersion version, string nexusVersion,
        bool waiveAccount, CancellationToken ct)
    {
        var request = new StoreInstallRequest
        {
            AppId = appId,
            Version = version.Version,
            Sha256 = version.Sha256,
            Size = version.Size,
        };

        var auth = await entitlements.AuthorizeAsync(appId, version.Version, nexusVersion, ct).ConfigureAwait(false);
        if (auth is { Ok: true, Grant: not null } && !string.IsNullOrWhiteSpace(auth.Grant.Sha256))
        {
            request.Sha256 = auth.Grant.Sha256;
            if (auth.Grant.Size > 0) request.Size = auth.Grant.Size;
            return request;
        }
        // Only a machine with no account at all is waived. A refused or expired
        // token reads sign_in_required too, and waiving that would silently drop
        // the entitlement record for a user who has one.
        return waiveAccount && auth.Reason == "sign_in_required" && !entitlements.HasLinkedAccount ? request : null;
    }
}
