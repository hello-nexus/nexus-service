using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Widgets;

namespace Nexus.Service.Store;

/// <summary>
/// Read-only passthrough to the cloud store catalog.
/// </summary>
/// <remarks>
/// The catalog is proxied so its media can be. The CSP the service serves the
/// dashboard under allows the catalog fetch itself (<c>connect-src</c> lists
/// api.hellonexus.com) but not the imagery: <c>img-src</c> has no entry for
/// assets.hellonexus.com, so a screenshot loaded straight from the bucket is
/// blocked. Passing the listing through here is what makes rewriting those URLs
/// onto a same-origin route possible, and a panel with no route off the LAN
/// reaches the catalog for free.
///
/// Only the client's own facts are forwarded (its version, whether the surface
/// has a pointer), so the catalog can answer with versions this machine can
/// actually install.
/// </remarks>
public sealed class StoreCatalogProxy
{
    private readonly HttpClient _http;

    public const string DefaultCloudApi = "https://api.hellonexus.com";

    public StoreCatalogProxy(HttpClient http) => _http = http;

    public static string CloudApiBase()
    {
        var configured = Environment.GetEnvironmentVariable("NEXUS_CLOUD_API");
        return string.IsNullOrWhiteSpace(configured) ? DefaultCloudApi : configured.TrimEnd('/');
    }

    /// <summary>Storefront listing. Returns null when the catalog is unreachable.</summary>
    public async Task<string?> ListAsync(string? nexusVersion, bool? touch, CancellationToken ct) =>
        RewriteMedia(await GetAsync($"/store/apps{Query(nexusVersion, touch)}", ct));

    /// <summary>One app's store page. Returns null when unreachable or unknown.</summary>
    public async Task<string?> DetailAsync(string appId, string? nexusVersion, bool? touch, CancellationToken ct)
    {
        if (!AppIds.IsValid(appId)) return null;
        return RewriteMedia(await GetAsync($"/store/apps/{appId}{Query(nexusVersion, touch)}", ct));
    }

    /// <summary>
    /// Points the catalog's absolute media URLs at this service's own proxy. The
    /// dashboard's img-src is 'self', so a cross-origin asset host would render
    /// as a broken image; proxying keeps the cloud contract absolute (a web
    /// storefront wants that) while the in-app client stays same-origin.
    /// </summary>
    public static string? RewriteMedia(string? json)
    {
        if (string.IsNullOrEmpty(json)) return json;
        return json.Replace($"{AssetsBase()}/apps/", "/apps-api/store/media/", StringComparison.Ordinal);
    }

    /// <summary>Assets host the media proxy is allowed to fetch from.</summary>
    public static string AssetsBase()
    {
        var configured = Environment.GetEnvironmentVariable("NEXUS_STORE_ASSETS");
        return string.IsNullOrWhiteSpace(configured) ? StoreInstaller.DefaultAssetsBase : configured.TrimEnd('/');
    }

    /// <summary>Store media is screenshots and icons; anything else the bucket
    /// hands back is refused rather than relayed onto the dashboard origin.</summary>
    private static readonly HashSet<string> MediaContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif", "image/x-icon", "image/svg+xml",
    };

    public const int MaxMediaBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Streams one media object. The path is confined to the assets host's
    /// apps/ prefix, so this can never be turned into a general fetch proxy;
    /// the reply is confined to image types under <see cref="MaxMediaBytes"/>,
    /// so it can never render as a page on the dashboard origin either.
    /// </summary>
    public async Task<(byte[] bytes, string contentType)?> MediaAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains("..", StringComparison.Ordinal)) return null;
        try
        {
            using var res = await _http.GetAsync($"{AssetsBase()}/apps/{path}", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            var type = res.Content.Headers.ContentType?.MediaType ?? "";
            if (!MediaContentTypes.Contains(type)) return null;
            if (res.Content.Headers.ContentLength is > MaxMediaBytes) return null;
            await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxMediaBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            return (buffer.ToArray(), type.ToLowerInvariant());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[store] media {path} failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Artifact location and hash for a version, which is what the installer
    /// verifies against. A caller never gets to supply those itself.
    /// </summary>
    public Task<string?> DownloadInfoAsync(string appId, string? version, string? nexusVersion, CancellationToken ct)
    {
        if (!AppIds.IsValid(appId)) return Task.FromResult<string?>(null);
        var q = $"?nexusVersion={Uri.EscapeDataString(nexusVersion ?? "")}";
        if (!string.IsNullOrEmpty(version)) q += $"&version={Uri.EscapeDataString(version)}";
        return GetAsync($"/store/apps/{appId}/download{q}", ct);
    }

    private static string Query(string? nexusVersion, bool? touch)
    {
        var q = $"?nexusVersion={Uri.EscapeDataString(nexusVersion ?? "")}";
        if (touch.HasValue) q += $"&touch={(touch.Value ? "true" : "false")}";
        return q;
    }

    private async Task<string?> GetAsync(string path, CancellationToken ct)
    {
        try
        {
            using var res = await _http.GetAsync(CloudApiBase() + path, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[store] catalog {path} failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
