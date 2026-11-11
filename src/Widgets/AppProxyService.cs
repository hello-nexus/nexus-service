using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cloud;
using Nexus.Service.Common;
using Nexus.Service.Models.Widgets;

namespace Nexus.Service.Widgets;

/// <summary>
/// Tier 1 + Tier 2 outbound HTTP proxy for marketplace widgets. The host
/// owns the fetch entirely; widgets never reach the network themselves.
/// Policy enforced here:
///
/// <list type="bullet">
///   <item>URL must be a valid absolute HTTPS URL (no HTTP, no file://, no data:).</item>
///   <item>Host must match the widget manifest's <c>capabilities.net.fetch</c>
///         allowlist. Wildcard hosts (<c>*.example.com</c>) match any
///         subdomain depth (suffix match on <c>.example.com</c>).</item>
///   <item>Response body capped at <see cref="MaxBodyBytes"/> (<see cref="MaxBinaryBodyBytes"/>
///         for binary content types); oversized responses are truncated + flagged.</item>
///   <item>Per-widget rate limit (<see cref="MaxRequestsPerMinute"/> rolling).</item>
///   <item>Method allowlist: GET / POST / PUT / PATCH / DELETE / HEAD.</item>
///   <item>Forbidden headers (Cookie, Authorization, Host, the build
///         credential) silently dropped so the widget can't reuse the
///         user's host-session credentials nor forge our own.</item>
///   <item>Our own cloud API host, and only that host, receives the build
///         credential this service was published with.</item>
///   <item>Redirects are never followed; a 3xx is returned to the widget as
///         it stands, so no hop escapes the checks above.</item>
/// </list>
/// </summary>
public sealed class AppProxyService
{
    public const int MaxBodyBytes = 1 * 1024 * 1024; // 1 MiB
    /// <summary>Cap for a binary body (map tiles and the like); text and JSON keep <see cref="MaxBodyBytes"/>.</summary>
    public const int MaxBinaryBodyBytes = 4 * 1024 * 1024;
    public const int MaxRequestsPerMinute = 300;

    /// <summary>
    /// Handler for the named client: no redirects (a followed hop escapes the
    /// allowlist), and gzip/deflate/brotli decoded so bodies cross the network
    /// compressed. The body caps apply to the decoded bytes.
    /// </summary>
    public static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
    };

    /// <summary>
    /// Default User-Agent sent when the widget doesn't supply one. Public
    /// APIs (CoinGecko, OpenMeteo, ...) reject requests with empty or
    /// suspicious UAs from server IPs. Identifying as the Nexus widget proxy
    /// is also more honest than impersonating a browser.
    /// </summary>
    public const string DefaultUserAgent = "Nexus-Widget-Proxy/1.0 (+https://hellonexus.com)";

    /// <summary>Named client every proxied request uses, registered with redirects off.</summary>
    public const string ClientName = "AppProxy";

    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD",
    };

    private static readonly HashSet<string> ForbiddenRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cookie", "Authorization", "Host", "Proxy-Authorization",
        "Set-Cookie", "Cookie2", ClientCredential.HeaderName,
    };

    // Tight allowlist for response headers proxied back to the widget. We
    // never echo Set-Cookie, Strict-Transport-Security, or vendor `X-*`
    // tracing headers - anything outside this list is dropped.
    private static readonly HashSet<string> ExposedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Content-Encoding", "Content-Language",
        "Date", "ETag", "Last-Modified", "Cache-Control", "Expires",
        "Vary", "Age", "Retry-After",
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly AppRegistry _registry;
    private readonly ConcurrentDictionary<string, RateBucket> _rateBuckets = new(StringComparer.Ordinal);

    public AppProxyService(IHttpClientFactory httpFactory, AppRegistry registry)
    {
        _httpFactory = httpFactory;
        _registry = registry;
    }

    public async Task<AppProxyResponse> ExecuteAsync(AppProxyRequest req, CancellationToken ct = default)
    {
        var resp = new AppProxyResponse();
        if (!AppIds.IsValid(req.AppId))
        {
            resp.Error = "invalid widget id";
            return resp;
        }
        if (!_registry.TryGet(req.AppId, out var entry))
        {
            resp.Error = "widget not installed";
            return resp;
        }
        if (string.IsNullOrWhiteSpace(req.Url))
        {
            resp.Error = "missing url";
            return resp;
        }

        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            resp.Error = "url must be absolute https://";
            return resp;
        }

        // Defense in depth: trust ONLY the manifest's allowlist, not what
        // the client claims.
        var allowlist = entry.Manifest.Capabilities.NetFetch;
        if (!HostInAllowlist(uri.Host, allowlist))
        {
            resp.Error = $"host '{uri.Host}' is not in the manifest's net.fetch allowlist";
            return resp;
        }

        // SSRF guard: reject IP literals + hostnames that resolve into
        // loopback / link-local / private / cloud-metadata ranges. A signed
        // marketplace widget that puts `127.0.0.1` or `169.254.169.254` in
        // its `capabilities.net.fetch` would otherwise drive the host to
        // hit internal services on the user's machine.
        if (IsPrivateOrReservedAddress(uri.Host))
        {
            resp.Error = $"host '{uri.Host}' resolves to a non-routable address; refused";
            return resp;
        }

        if (!CheckRate(req.AppId))
        {
            resp.Error = "rate limit exceeded";
            return resp;
        }

        var method = (req.Method ?? "GET").ToUpperInvariant();
        if (!AllowedMethods.Contains(method))
        {
            resp.Error = $"method '{method}' is not permitted";
            return resp;
        }

        // Redirects are handed back to the widget rather than followed. A
        // followed hop escapes both the allowlist and the reserved-address
        // check, and HttpClient strips only Authorization when the origin
        // changes, so the build credential would ride along to the new host.
        bool credentialed = IsCloudApiHost(uri);
        using var http = _httpFactory.CreateClient(ClientName);
        http.Timeout = TimeSpan.FromSeconds(20);

        using var msg = new HttpRequestMessage(new HttpMethod(method), uri);
        bool widgetSetUserAgent = false;
        bool widgetSetAccept = false;
        if (req.Headers is not null)
        {
            foreach (var kv in req.Headers)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (ForbiddenRequestHeaders.Contains(kv.Key)) continue;
                if (string.Equals(kv.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
                    widgetSetUserAgent = true;
                if (string.Equals(kv.Key, "Accept", StringComparison.OrdinalIgnoreCase))
                    widgetSetAccept = true;
                msg.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }
        // Default UA + Accept when the widget doesn't supply them. Empty/
        // generic UAs trigger 403s on Cloudflare-fronted upstreams like
        // CoinGecko's free tier, and explicit Accept helps upstreams pick
        // a useful default representation.
        if (!widgetSetUserAgent)
            msg.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        if (!widgetSetAccept)
            msg.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain;q=0.9, */*;q=0.1");
        // Our API gates on the build credential; every other upstream keeps
        // seeing none, and an unofficial build sends none anywhere.
        if (credentialed)
        {
            ClientCredential.Apply(msg);
        }
        if (req.Body is not null && method != "GET" && method != "HEAD")
        {
            var content = new StringContent(req.Body, Encoding.UTF8);
            // Content-Type is a content header, so the loop above could not
            // carry it: HttpRequestHeaders drops it without an error.
            content.Headers.ContentType = ResolveContentType(HeaderValue(req.Headers, "Content-Type"));
            msg.Content = content;
        }

        try
        {
            using var upstream = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.Status = (int)upstream.StatusCode;
            resp.StatusText = upstream.ReasonPhrase ?? "";
            // Echo a narrow allowlist of upstream headers - never proxy
            // Set-Cookie, HSTS, or vendor `X-*` debug fields to widget JS.
            foreach (var h in upstream.Headers)
            {
                if (ExposedResponseHeaders.Contains(h.Key))
                {
                    resp.Headers[h.Key] = string.Join(',', h.Value);
                }
            }
            foreach (var h in upstream.Content.Headers)
            {
                if (ExposedResponseHeaders.Contains(h.Key))
                {
                    resp.Headers[h.Key] = string.Join(',', h.Value);
                }
            }

            var contentType = upstream.Content.Headers.ContentType?.MediaType ?? "";
            var maxBody = IsBinaryMediaType(contentType) ? MaxBinaryBodyBytes : MaxBodyBytes;
            using var content = await upstream.Content.ReadAsStreamAsync(ct);
            using var buffered = new MemoryStream();
            var buffer = new byte[8192];
            int totalRead = 0;
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                if (totalRead + read > maxBody)
                {
                    resp.Error = $"response exceeded {maxBody}-byte cap; truncated";
                    var allowed = maxBody - totalRead;
                    if (allowed > 0) buffered.Write(buffer, 0, allowed);
                    totalRead += allowed;
                    break;
                }
                buffered.Write(buffer, 0, read);
                totalRead += read;
            }

            var rawBytes = buffered.ToArray();
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) && rawBytes.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(rawBytes);
                    resp.Body = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    resp.BodyText = Encoding.UTF8.GetString(rawBytes);
                }
            }
            else if (IsBinaryMediaType(contentType))
            {
                resp.BodyBase64 = Convert.ToBase64String(rawBytes);
            }
            else
            {
                resp.BodyText = Encoding.UTF8.GetString(rawBytes);
            }
            resp.Ok = upstream.IsSuccessStatusCode && resp.Error is null;
        }
        catch (TaskCanceledException)
        {
            resp.Error = "upstream timeout";
        }
        catch (HttpRequestException ex)
        {
            resp.Error = ex.Message;
        }
        return resp;
    }

    /// <summary>
    /// True for a declared content type whose body is not text. A missing type
    /// stays text so existing apps see no change.
    /// </summary>
    internal static bool IsBinaryMediaType(string mediaType)
    {
        if (string.IsNullOrEmpty(mediaType)) return false;
        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var marker in TextMediaMarkers)
        {
            if (mediaType.Contains(marker, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static readonly string[] TextMediaMarkers = { "json", "xml", "javascript", "ecmascript", "x-www-form-urlencoded" };

    private bool CheckRate(string widgetId)
    {
        var now = DateTime.UtcNow;
        var bucket = _rateBuckets.GetOrAdd(widgetId, _ => new RateBucket());
        lock (bucket.Sync)
        {
            // Drop entries older than 60 s.
            while (bucket.Timestamps.Count > 0 && (now - bucket.Timestamps.Peek()).TotalSeconds > 60)
            {
                bucket.Timestamps.Dequeue();
            }
            if (bucket.Timestamps.Count >= MaxRequestsPerMinute) return false;
            bucket.Timestamps.Enqueue(now);
            return true;
        }
    }

    /// <summary>True when a widget's target is the cloud API this build talks to.</summary>
    internal static bool IsCloudApiHost(Uri uri) => IsCloudApiHost(uri, CloudApiEndpoint.Host);

    internal static bool IsCloudApiHost(Uri? uri, string cloudHost) =>
        uri is not null && !string.IsNullOrEmpty(cloudHost) &&
        string.Equals(uri.Host, cloudHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Content type for a widget-supplied body: the header it declared when
    /// that parses, else the plain-text default a bare StringContent carries.
    /// </summary>
    internal static MediaTypeHeaderValue ResolveContentType(string? declared)
    {
        if (!string.IsNullOrWhiteSpace(declared) &&
            MediaTypeHeaderValue.TryParse(declared, out var parsed) &&
            !string.IsNullOrEmpty(parsed.MediaType))
        {
            // The body goes out UTF-8 encoded whatever the widget declared, so
            // any other charset would only mislead the upstream decoder. A
            // declaration carrying no charset keeps none.
            if (!string.IsNullOrEmpty(parsed.CharSet) &&
                !string.Equals(parsed.CharSet, "utf-8", StringComparison.OrdinalIgnoreCase))
            {
                parsed.CharSet = "utf-8";
            }
            return parsed;
        }
        return new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
    }

    /// <summary>Header lookup over the request's case-sensitive dictionary.</summary>
    private static string? HeaderValue(Dictionary<string, string>? headers, string name)
    {
        if (headers is null) return null;
        foreach (var kv in headers)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        }
        return null;
    }

    /// <summary>
    /// Manifest <c>net.fetch</c> host match, shared with the openUrl host
    /// action so both gates agree on what a manifest entry permits.
    /// </summary>
    internal static bool HostInAllowlist(string host, IReadOnlyList<string> allowlist)
    {
        if (allowlist is null || allowlist.Count == 0) return false;
        foreach (var entry in allowlist)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var trimmed = entry.Trim();
            // Strip an optional `https://` prefix so authors can write
            // either `api.example.com` or `https://api.example.com`.
            if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed["https://".Length..];
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                continue; // refuse cleartext entries entirely.

            if (trimmed.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = trimmed[1..]; // ".example.com"
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    host.Length > suffix.Length)
                {
                    return true;
                }
                continue;
            }
            if (string.Equals(host, trimmed, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="host"/> is - or resolves into - any IP
    /// range that should never leave the box: loopback, link-local
    /// (169.254/16 incl. cloud-metadata), RFC1918 private (10/8, 172.16/12,
    /// 192.168/16), CG-NAT (100.64/10), benchmark (198.18/15), broadcast
    /// (255.255.255.255), IPv6 loopback (::1), unique-local (fc00::/7),
    /// link-local (fe80::/10), IPv4-mapped variants of any of the above.
    /// Hostnames are resolved synchronously; failure to resolve is treated
    /// as suspicious and refused (better than letting a stub-resolver
    /// poison split the decision later).
    /// </summary>
    internal static bool IsPrivateOrReservedAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;

        // Strip optional brackets from IPv6 literals (`[::1]`).
        var trimmed = host.Trim('[', ']');

        if (IPAddress.TryParse(trimmed, out var literal))
        {
            return IsReservedIp(literal);
        }

        // Hostname path: resolve to all addresses + reject if ANY are
        // reserved (defends against multi-A DNS rebinding where one record
        // is public and another is loopback).
        try
        {
            var addrs = Dns.GetHostAddresses(trimmed);
            if (addrs is null || addrs.Length == 0) return true;
            foreach (var addr in addrs)
            {
                if (IsReservedIp(addr)) return true;
            }
            return false;
        }
        catch
        {
            // Resolve failure: refuse. A widget that can't reach a public
            // host has no valid reason to act here.
            return true;
        }
    }

    private static bool IsReservedIp(IPAddress address)
    {
        if (address is null) return true;
        // Normalise IPv4-mapped IPv6 (::ffff:127.0.0.1) to IPv4 so the same
        // checks apply.
        var addr = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (IPAddress.IsLoopback(addr)) return true;
        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = addr.GetAddressBytes();
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 (link-local, incl. 169.254.169.254 cloud metadata)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 100.64.0.0/10 (CG-NAT)
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
            // 198.18.0.0/15 (benchmarking)
            if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) return true;
            // 0.0.0.0/8
            if (bytes[0] == 0) return true;
            // 255.255.255.255 broadcast
            if (bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255) return true;
        }
        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (addr.IsIPv6LinkLocal) return true;
            if (addr.IsIPv6SiteLocal) return true;
            if (addr.IsIPv6UniqueLocal) return true;
            if (addr.IsIPv6Multicast) return true;
            // IPv6 unspecified ::
            var v6 = addr.GetAddressBytes();
            bool allZero = true;
            for (int i = 0; i < v6.Length; i++) if (v6[i] != 0) { allZero = false; break; }
            if (allZero) return true;
        }
        return false;
    }

    private sealed class RateBucket
    {
        public readonly object Sync = new();
        public readonly Queue<DateTime> Timestamps = new();
    }
}
