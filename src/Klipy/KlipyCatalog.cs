using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Klipy;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Telemetry;

namespace Nexus.Service.Klipy;

public interface IKlipyCatalog
{
    /// <summary>Empty query returns the trending page.</summary>
    Task<KlipySearchResponse> SearchAsync(string? query, int page, CancellationToken ct);

    /// <summary>Grid thumbnail bytes for a slug seen in an earlier search, or empty on any miss.</summary>
    Task<byte[]> GetThumbAsync(string slug);

    /// <summary>The GIF an import should download, or null when the slug is unknown to this process.</summary>
    KlipyResolvedGif? Resolve(string slug);

    /// <summary>Downloads the slug's clip into <paramref name="destDir"/> and returns
    /// its path, whose extension matches the container fetched. Null when the slug
    /// is unknown, the fetch failed, or the file exceeded the cap.</summary>
    Task<string?> DownloadAsync(string slug, string destDir, CancellationToken ct);

    /// <summary>Best-effort share ping; Klipy counts it for partner analytics.</summary>
    Task TriggerShareAsync(string slug);
}

/// <summary>What a search memoized for one slug: the two URLs an import and a
/// grid cell need, plus the dimensions the client crops against.</summary>
public readonly record struct KlipyResolvedGif(string ImportUrl, string ImportExt, string ThumbUrl, int Width, int Height);

/// <summary>
/// Search/trending + thumbnail proxy for api.klipy.com. Service-side because the
/// app key is a URL path segment and a Q-series panel has no IP route of its own.
/// A search memoizes slug -> file URL and every memoized URL is checked to be
/// https on a klipy.com host, so a caller can reach neither another host nor a
/// host of its own choosing. Items typed "ad" are dropped; the ad_* parameters
/// that request them are never sent.
/// </summary>
public sealed class KlipyCatalog : IKlipyCatalog
{
    private const string DefaultBaseUrl = "https://api.klipy.com/api/v1";
    private const int PerPage = 24;
    private const int SlugCacheCap = 512;
    private const int ThumbCacheCap = 256;
    private const long MaxThumbBytes = 2_000_000;
    private const long MaxImportBytes = 25_000_000;

    private const string MediaHostSuffix = ".klipy.com";

    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ThumbTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MissTtl = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _http;
    private readonly IConfigStore _store;
    private readonly string _baseUrl;
    private readonly Func<string?> _appKey;
    private readonly Func<string?, bool> _isAllowedMediaUrl;

    // Telemetry opt-out means no stable cross-session id, but Klipy still wants the parameter.
    private readonly string _sessionCustomerId = Guid.NewGuid().ToString("N");

    private readonly object _lock = new();
    private readonly Dictionary<string, KlipyResolvedGif> _slugs = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _slugLru = new();
    private readonly Dictionary<string, byte[]> _thumbs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _thumbMisses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<byte[]>> _thumbsInFlight = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _thumbLru = new();

    public KlipyCatalog(IHttpClientFactory http, IConfigStore store)
        : this(http, store, DefaultBaseUrl, KlipyAppKey.Resolve)
    {
    }

    // Test seam: a loopback base URL, a fixed key, and - for the tests that
    // serve media from that same loopback stub - a relaxed media-URL guard.
    internal KlipyCatalog(
        IHttpClientFactory http,
        IConfigStore store,
        string baseUrl,
        Func<string?> appKey,
        Func<string?, bool>? mediaUrlGuard = null)
    {
        _http = http;
        _store = store;
        _baseUrl = baseUrl.TrimEnd('/');
        _appKey = appKey;
        _isAllowedMediaUrl = mediaUrlGuard ?? IsKlipyMediaUrl;
    }

    /// <summary>Slugs become URL path segments and cache keys, so anything
    /// outside the safe set is refused rather than sanitized.</summary>
    public static bool IsValidSlug(string? slug)
    {
        if (string.IsNullOrEmpty(slug) || slug.Length > 128)
        {
            return false;
        }
        foreach (var ch in slug)
        {
            bool ok = ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    public async Task<KlipySearchResponse> SearchAsync(string? query, int page, CancellationToken ct)
    {
        var key = _appKey();
        if (string.IsNullOrEmpty(key))
        {
            return new KlipySearchResponse { Error = true, Msg = "No Klipy app key configured" };
        }

        if (page < 1)
        {
            page = 1;
        }

        var trimmed = query?.Trim() ?? "";
        var path = trimmed.Length == 0
            ? $"gifs/trending?page={page}&per_page={PerPage}"
            : $"gifs/search?q={Uri.EscapeDataString(trimmed)}&page={page}&per_page={PerPage}";

        try
        {
            using var client = _http.CreateClient();
            client.Timeout = SearchTimeout;
            var url = $"{_baseUrl}/{key}/{path}&customer_id={CustomerId()}";
            var envelope = await client.GetFromJsonAsync(url, AppJsonContext.Default.KlipyEnvelope, ct)
                .ConfigureAwait(false);

            var items = new List<KlipyGifDto>();
            foreach (var item in envelope?.Data?.Items ?? Array.Empty<KlipyWireItem>())
            {
                var dto = Adopt(item);
                if (dto is not null)
                {
                    items.Add(dto);
                }
            }

            return new KlipySearchResponse { Items = items, HasNext = envelope?.Data?.HasNext ?? false };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[klipy] search failed: {ex.Message}");
            return new KlipySearchResponse { Error = true, Msg = "Klipy is unreachable" };
        }
    }

    /// <summary>Maps one wire item and memoizes its URLs, or null when it is an
    /// ad slot or is missing the files an import and a grid cell need.</summary>
    private KlipyGifDto? Adopt(KlipyWireItem item)
    {
        if (string.Equals(item.Type, "ad", StringComparison.OrdinalIgnoreCase) || !IsValidSlug(item.Slug))
        {
            return null;
        }

        // mp4 first: the same clip as gif runs 10-70x larger (a 498px gif can
        // pass 30 MB where its mp4 is under 500 KB), and both importers decode it.
        var mp4 = FirstWithUrl(item.File?.Hd?.Mp4, item.File?.Md?.Mp4, item.File?.Sm?.Mp4);
        var import = mp4 ?? FirstWithUrl(item.File?.Hd?.Gif, item.File?.Md?.Gif, item.File?.Sm?.Gif);
        var importExt = mp4 is not null ? ".mp4" : ".gif";
        // webp only: the thumbnail route serves these bytes as image/webp.
        var thumb = FirstWithUrl(item.File?.Sm?.Webp, item.File?.Md?.Webp, item.File?.Xs?.Webp, item.File?.Hd?.Webp);
        // Dimensions drive the client's centre crop, so an item that omits them
        // is dropped rather than imported against a guessed frame.
        if (import is null || thumb is null || import.Width is not > 0 || import.Height is not > 0)
        {
            return null;
        }

        var slug = item.Slug!;
        lock (_lock)
        {
            _slugs[slug] = new KlipyResolvedGif(import.Url!, importExt, thumb.Url!, import.Width!.Value, import.Height!.Value);
            TouchSlug(slug);
            while (_slugLru.Count > SlugCacheCap && _slugLru.Last is { } oldest)
            {
                _slugLru.RemoveLast();
                _slugs.Remove(oldest.Value);
            }
        }

        return new KlipyGifDto
        {
            Slug = slug,
            Title = item.Title ?? "",
            Width = import.Width!.Value,
            Height = import.Height!.Value,
            BlurPreview = item.BlurPreview,
        };
    }

    private KlipyFileMeta? FirstWithUrl(params KlipyFileMeta?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (_isAllowedMediaUrl(candidate?.Url))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>The service fetches these URLs itself, so a redirected or
    /// tampered payload must not be able to aim it at another host.</summary>
    internal static bool IsKlipyMediaUrl(string? url) =>
        !string.IsNullOrEmpty(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.EndsWith(MediaHostSuffix, StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("klipy.com", StringComparison.OrdinalIgnoreCase));

    public KlipyResolvedGif? Resolve(string slug)
    {
        lock (_lock)
        {
            if (_slugs.TryGetValue(slug, out var resolved))
            {
                TouchSlug(slug);
                return resolved;
            }
        }
        return null;
    }

    public async Task<string?> DownloadAsync(string slug, string destDir, CancellationToken ct)
    {
        if (Resolve(slug) is not { } resolved)
        {
            Console.Error.WriteLine($"[klipy] download {slug}: not in this process's search memo");
            return null;
        }

        var destPath = Path.Combine(destDir, $"nexus-klipy-{Guid.NewGuid()}{resolved.ImportExt}");
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = DownloadTimeout;
            using var response = await client
                .GetAsync(resolved.ImportUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Console.Error.WriteLine($"[klipy] download {slug}: HTTP {(int)response.StatusCode} from {resolved.ImportUrl}");
                return null;
            }
            if ((response.Content.Headers.ContentLength ?? 0) > MaxImportBytes)
            {
                Console.Error.WriteLine($"[klipy] download {slug}: {response.Content.Headers.ContentLength} bytes exceeds the cap");
                return null;
            }

            bool complete;
            using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var dest = File.Create(destPath))
            {
                complete = await CopyCappedAsync(source, dest, MaxImportBytes, ct).ConfigureAwait(false);
            }

            if (complete && new FileInfo(destPath).Length > 0)
            {
                return destPath;
            }
            Console.Error.WriteLine($"[klipy] download {slug}: response exceeded the cap or was empty");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[klipy] download {slug} failed: {ex.Message}");
        }
        try { File.Delete(destPath); }
        catch { }
        return null;
    }

    /// <summary>False the moment the source would pass <paramref name="cap"/>: a missing Content-Length must not write an unbounded file, and a truncated GIF is a failed import.</summary>
    private static async Task<bool> CopyCappedAsync(Stream source, Stream dest, long cap, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > cap)
            {
                return false;
            }
            await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return true;
    }

    public Task<byte[]> GetThumbAsync(string slug)
    {
        if (!IsValidSlug(slug))
        {
            return Task.FromResult(Array.Empty<byte>());
        }

        lock (_lock)
        {
            if (_thumbs.TryGetValue(slug, out var cached))
            {
                TouchThumb(slug);
                return Task.FromResult(cached);
            }
            if (_thumbMisses.TryGetValue(slug, out var until))
            {
                if (DateTime.UtcNow < until)
                {
                    return Task.FromResult(Array.Empty<byte>());
                }
                _thumbMisses.Remove(slug);
            }
            if (_thumbsInFlight.TryGetValue(slug, out var pending))
            {
                return pending;
            }
            if (!_slugs.TryGetValue(slug, out var resolved))
            {
                return Task.FromResult(Array.Empty<byte>());
            }

            var task = FetchThumbAsync(slug, resolved.ThumbUrl);
            _thumbsInFlight[slug] = task;
            return task;
        }
    }

    private async Task<byte[]> FetchThumbAsync(string slug, string url)
    {
        byte[] bytes = Array.Empty<byte>();
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = ThumbTimeout;
            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK &&
                (response.Content.Headers.ContentLength ?? 0) <= MaxThumbBytes)
            {
                // Streamed under the cap: a response with no Content-Length
                // would otherwise buffer to HttpClient's default before the check.
                using var buffer = new MemoryStream();
                using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                if (await CopyCappedAsync(source, buffer, MaxThumbBytes, CancellationToken.None).ConfigureAwait(false))
                {
                    bytes = buffer.ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[klipy] thumb {slug} failed: {ex.Message}");
            bytes = Array.Empty<byte>();
        }

        lock (_lock)
        {
            _thumbsInFlight.Remove(slug);
            if (bytes.Length > 0)
            {
                _thumbs[slug] = bytes;
                TouchThumb(slug);
                while (_thumbLru.Count > ThumbCacheCap && _thumbLru.Last is { } oldest)
                {
                    _thumbLru.RemoveLast();
                    _thumbs.Remove(oldest.Value);
                }
            }
            else
            {
                _thumbMisses[slug] = DateTime.UtcNow + MissTtl;
            }
        }
        return bytes;
    }

    public async Task TriggerShareAsync(string slug)
    {
        var key = _appKey();
        if (string.IsNullOrEmpty(key) || !IsValidSlug(slug))
        {
            return;
        }

        try
        {
            using var client = _http.CreateClient();
            client.Timeout = SearchTimeout;
            using var content = JsonContent.Create(
                new KlipyCustomerBody { CustomerId = CustomerId() },
                AppJsonContext.Default.KlipyCustomerBody);
            using var response = await client
                .PostAsync($"{_baseUrl}/{key}/gifs/share/{slug}", content)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[klipy] share ping failed: {ex.Message}");
        }
    }

    private string CustomerId() => InstallIdentity.Resolve(_store) ?? _sessionCustomerId;

    private void TouchSlug(string slug)
    {
        _slugLru.Remove(slug);
        _slugLru.AddFirst(slug);
    }

    private void TouchThumb(string slug)
    {
        _thumbLru.Remove(slug);
        _thumbLru.AddFirst(slug);
    }
}
