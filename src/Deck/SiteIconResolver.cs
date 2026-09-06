using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;

namespace Nexus.Service.Deck;

/// <summary>Site icon for a deck key bound to a URL; empty bytes on any miss.</summary>
public interface ISiteIconResolver
{
    Task<(byte[] Bytes, string ContentType)> ResolveAsync(string url, CancellationToken ct);
}

/// <summary>
/// Resolves a website's own icon for a deck key: fetch the page, rank its
/// <c>&lt;link rel="icon"&gt;</c> tags, download the best one, fall back to
/// <c>/favicon.ico</c>. Keyed per ORIGIN, so every key on one site shares a fetch.
///
/// Bytes are served verbatim: every consumer is a Chromium surface that decodes
/// ico/svg/webp natively, and ImageSharp has no ICO decoder.
///
/// Private ranges are not blocked: a LAN address is a valid deck key. The route
/// takes ?url= verbatim, so the real bound is "any authenticated panel session",
/// which gets to read an unauthenticated image on the host or LAN and to tell a
/// 200 from a 404. Privileged routes are not reachable - PathAuthMiddleware
/// demands a token even on loopback, and a 401 body fails the magic-byte check.
/// </summary>
public sealed partial class SiteIconResolver : ISiteIconResolver
{
    /// <summary>Cap on a downloaded icon.</summary>
    internal const int MaxBytes = 256 * 1024;

    /// <summary>Separate, larger cap for the page: HttpClient aborts past MaxResponseContentBufferSize, and a 256KB ceiling would silently skip link ranking on any routine page.</summary>
    internal const int MaxHtmlBytes = 2 * 1024 * 1024;

    private const int MaxCandidates = 4;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromHours(6);

    /// <summary>A fetch that failed rather than resolved (offline at boot, upstream error) retries soon instead of pinning the origin iconless for the negative TTL.</summary>
    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(2);

    private readonly IHttpClientFactory _http;
    private readonly string _cacheDir;
    private readonly TimeSpan _positiveTtl;
    private readonly TimeSpan _negativeTtl;
    private readonly TimeSpan _failureTtl;

    private readonly Dictionary<string, Task<(byte[], string)>> _inFlight = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SiteIconResolver(IHttpClientFactory http)
        : this(http, MediaLibrary.MediaStoreDir("site-icons"), PositiveTtl, NegativeTtl, FailureTtl)
    {
    }

    // Test seam: tmp cache dir + short TTLs.
    internal SiteIconResolver(IHttpClientFactory http, string cacheDir, TimeSpan positiveTtl, TimeSpan negativeTtl, TimeSpan? failureTtl = null)
    {
        _http = http;
        _cacheDir = cacheDir;
        _positiveTtl = positiveTtl;
        _negativeTtl = negativeTtl;
        _failureTtl = failureTtl ?? negativeTtl;
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<(byte[] Bytes, string ContentType)> ResolveAsync(string url, CancellationToken ct)
    {
        var origin = OriginKey(url);
        if (origin is null)
        {
            return (Array.Empty<byte>(), "");
        }

        var key = CacheKey(origin);
        var cached = ReadCache(key);
        if (cached is not null)
        {
            return cached.Value;
        }

        Task<(byte[], string)>? existing = null;
        TaskCompletionSource<(byte[], string)>? owned = null;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_inFlight.TryGetValue(key, out existing))
            {
                owned = new TaskCompletionSource<(byte[], string)>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlight[key] = owned.Task;
            }
        }
        finally
        {
            _lock.Release();
        }

        if (existing is not null)
        {
            return await existing.ConfigureAwait(false);
        }

        var result = (Array.Empty<byte>(), "");
        try
        {
            try
            {
                result = await FetchAsync(origin, ct).ConfigureAwait(false);
                // A resolved miss earns the full negative TTL; a fetch that threw
                // (offline, cancelled by a panel reload mid-fetch) must not pin
                // the origin iconless for hours.
                WriteCache(key, result.Item1, result.Item2, _negativeTtl);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[site-icon] {origin} failed: {ex.Message}");
                WriteCache(key, Array.Empty<byte>(), "", _failureTtl);
            }
        }
        finally
        {
            await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _inFlight.Remove(key);
            }
            finally
            {
                _lock.Release();
            }
            // Coalesced waiters must be released whatever the bookkeeping did.
            owned!.TrySetResult(result);
        }

        return result;
    }

    /// <summary>Scheme + authority of an http(s) URL, lowercased; null when the URL is neither.</summary>
    internal static string? OriginKey(string url)
    {
        var trimmed = (url ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }
        // A user types a bare "example.com"; assume https rather than rejecting it.
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    private static string CacheKey(string origin) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin)))[..32].ToLowerInvariant();

    private async Task<(byte[], string)> FetchAsync(string origin, CancellationToken ct)
    {
        using var client = _http.CreateClient();
        client.Timeout = RequestTimeout;
        client.MaxResponseContentBufferSize = MaxHtmlBytes;
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", Nexus.Service.Widgets.AppProxyService.DefaultUserAgent);

        var candidates = new List<string>();
        Uri baseUri = new(origin + "/");
        try
        {
            using var page = await client.GetAsync(baseUri, ct).ConfigureAwait(false);
            if (page.IsSuccessStatusCode)
            {
                // Final uri after redirects: what a relative href resolves against.
                baseUri = page.RequestMessage?.RequestUri ?? baseUri;
                var html = await page.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                candidates.AddRange(RankLinkIcons(html, baseUri));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // A site that will not serve its HTML may still serve /favicon.ico.
        }

        // Reserve the last slot so a page declaring many icon links still falls
        // back to /favicon.ico, which the class doc and the README promise.
        var ordered = candidates.Take(MaxCandidates - 1).ToList();
        ordered.Add(new Uri(baseUri, "/favicon.ico").ToString());

        foreach (var candidate in ordered.Distinct(StringComparer.Ordinal))
        {
            var icon = await DownloadIconAsync(client, candidate, ct).ConfigureAwait(false);
            if (icon is not null)
            {
                return icon.Value;
            }
        }

        return (Array.Empty<byte>(), "");
    }

    private static async Task<(byte[], string)?> DownloadIconAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            using var res = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }
            var bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxBytes)
            {
                return null;
            }
            var contentType = SniffContentType(bytes);
            return contentType is null ? null : (bytes, contentType);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Icon hrefs, best first: raster before vector (an SVG with no intrinsic size draws blank on the deck's canvas), then declared size, then rel.</summary>
    internal static List<string> RankLinkIcons(string html, Uri baseUri)
    {
        var scored = new List<(int Raster, int Size, int Rel, string Href)>();
        foreach (Match m in LinkTagRegex().Matches(html))
        {
            var tag = m.Value;
            var rel = Attr(RelAttrRegex(), tag).ToLowerInvariant();
            var href = Attr(HrefAttrRegex(), tag);
            if (href.Length == 0 || !rel.Contains("icon", StringComparison.Ordinal))
            {
                continue;
            }
            // A mask-icon is an untinted monochrome silhouette: black on black here.
            if (rel.Contains("mask-icon", StringComparison.Ordinal))
            {
                continue;
            }

            var isApple = rel.Contains("apple-touch-icon", StringComparison.Ordinal);
            var sizes = Attr(SizesAttrRegex(), tag);
            // An apple-touch-icon with no declared size is 180x180 by convention.
            var size = ParseSize(sizes) ?? (isApple ? 180 : 0);

            if (!Uri.TryCreate(baseUri, href, out var abs) ||
                (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            var raster = abs.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            scored.Add((raster, size, isApple ? 1 : 0, abs.ToString()));
        }

        return scored
            .OrderByDescending(s => s.Raster)
            .ThenByDescending(s => s.Size)
            .ThenByDescending(s => s.Rel)
            .Select(s => s.Href)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Largest width from a <c>sizes</c> attribute ("32x32 16x16"), or null.</summary>
    internal static int? ParseSize(string sizes)
    {
        int? best = null;
        foreach (var token in sizes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var x = token.IndexOf('x', StringComparison.OrdinalIgnoreCase);
            if (x <= 0)
            {
                continue;
            }
            if (int.TryParse(token.AsSpan(0, x), NumberStyles.None, CultureInfo.InvariantCulture, out var w) &&
                (best is null || w > best))
            {
                best = w;
            }
        }
        return best;
    }

    /// <summary>Content type from the bytes' magic, or null when they are not an image we can serve.</summary>
    internal static string? SniffContentType(byte[] b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
        {
            return "image/png";
        }
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8')
        {
            return "image/gif";
        }
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' &&
            b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
        {
            return "image/webp";
        }
        // ICO/CUR: reserved 0, then type 1 (icon) or 2 (cursor), little-endian.
        if (b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && (b[2] == 0x01 || b[2] == 0x02) && b[3] == 0x00)
        {
            return "image/x-icon";
        }
        if (b.Length >= 2 && b[0] == 'B' && b[1] == 'M')
        {
            return "image/bmp";
        }
        // SVG is text: skip a BOM, whitespace, and any prolog or comment.
        var head = Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 512)).TrimStart('\uFEFF').TrimStart();
        if (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
            head.StartsWith("<!--", StringComparison.Ordinal) ||
            head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
        {
            return head.Contains("<svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml" : null;
        }
        return null;
    }

    // Disk cache: <key>.<ext> for a hit, <key>.miss for a negative; the file's own mtime is the timestamp.

    private const string MissExt = ".miss";
    private const string FailExt = ".fail";

    private static readonly (string Ext, string ContentType)[] CachedTypes =
    [
        (".png", "image/png"),
        (".jpg", "image/jpeg"),
        (".gif", "image/gif"),
        (".webp", "image/webp"),
        (".ico", "image/x-icon"),
        (".bmp", "image/bmp"),
        (".svg", "image/svg+xml"),
    ];

    private static string ExtFor(string contentType) =>
        CachedTypes.FirstOrDefault(t => t.ContentType == contentType).Ext ?? ".png";

    private (byte[], string)? ReadCache(string key)
    {
        try
        {
            foreach (var (ext, ttl) in new[] { (MissExt, _negativeTtl), (FailExt, _failureTtl) })
            {
                var marker = Path.Combine(_cacheDir, key + ext);
                if (!File.Exists(marker))
                {
                    continue;
                }
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < ttl)
                {
                    return (Array.Empty<byte>(), "");
                }
                File.Delete(marker);
            }

            foreach (var (ext, contentType) in CachedTypes)
            {
                var path = Path.Combine(_cacheDir, key + ext);
                if (!File.Exists(path))
                {
                    continue;
                }
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= _positiveTtl)
                {
                    File.Delete(path);
                    return null;
                }
                return (File.ReadAllBytes(path), contentType);
            }
        }
        catch (Exception)
        {
            // A cache read must never fail the request: an unreadable entry is a miss.
        }
        return null;
    }

    private void WriteCache(string key, byte[] bytes, string contentType, TimeSpan missTtl)
    {
        var ext = bytes.Length > 0
            ? ExtFor(contentType)
            : (missTtl == _failureTtl && _failureTtl != _negativeTtl ? FailExt : MissExt);
        var path = Path.Combine(_cacheDir, key + ext);
        var tmp = path + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            // An origin that changed icon format would otherwise keep answering
            // from the old extension, which ReadCache reaches first.
            foreach (var (otherExt, _) in CachedTypes)
            {
                if (otherExt != ext)
                {
                    Delete(Path.Combine(_cacheDir, key + otherExt));
                }
            }
            foreach (var marker in new[] { MissExt, FailExt })
            {
                if (marker != ext)
                {
                    Delete(Path.Combine(_cacheDir, key + marker));
                }
            }
        }
        catch (Exception)
        {
            // A cache write must never fail the request; the bytes are already returned.
        }
        finally
        {
            Delete(tmp);
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>First non-empty capture of an attribute regex (quoted, single-quoted, or bare).</summary>
    private static string Attr(Regex re, string tag)
    {
        var m = re.Match(tag);
        if (!m.Success)
        {
            return "";
        }
        for (var i = 1; i <= 3; i++)
        {
            if (m.Groups[i].Success && m.Groups[i].Value.Length > 0)
            {
                return m.Groups[i].Value.Trim();
            }
        }
        return "";
    }

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTagRegex();

    [GeneratedRegex(@"\brel\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex RelAttrRegex();

    [GeneratedRegex(@"\bhref\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex HrefAttrRegex();

    [GeneratedRegex(@"\bsizes\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex SizesAttrRegex();
}
