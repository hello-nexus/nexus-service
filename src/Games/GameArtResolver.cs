using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;

namespace Nexus.Service.Games;

/// <summary>Cover art for one game: a CDN url the client loads directly, or icon bytes the service extracted.</summary>
public sealed record GameArt(string Url, byte[] Bytes)
{
    public static readonly GameArt None = new("", Array.Empty<byte>());

    public bool IsEmpty => Url.Length == 0 && Bytes.Length == 0;
}

/// <summary>Resolves cover art for a played game; GameArt.None on any miss so the caller keeps its placeholder.</summary>
public interface IGameArtResolver
{
    Task<GameArt> ResolveAsync(string gameKey, CancellationToken ct);

    /// <summary>The installed executable's icon alone, for a caller whose store art failed to load.</summary>
    GameArt ResolveIcon(string gameKey);
}

/// <summary>
/// Steam art is read from the store's appdetails record, not built from the
/// appid: newer titles live only under store_item_assets/steam/apps/{appid}/
/// {content hash}/ and the hash is not derivable. Everything else falls back
/// to the game executable's icon; Epic exposes no keyless key-art route.
/// </summary>
public sealed class GameArtResolver : IGameArtResolver
{
    private const string DefaultStoreBaseUrl = "https://store.steampowered.com";
    private const int CacheCap = 256;

    private static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan MissTtl = TimeSpan.FromHours(6);
    /// <summary>For a result the next call could legitimately improve on: a throttled store, or an icon the helper was not up to read yet.</summary>
    private static readonly TimeSpan TransientTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    private readonly IHttpClientFactory _http;
    private readonly IGameInstallLocator _installs;
    private readonly IProcessIconProvider _icons;
    private readonly string _storeBaseUrl;

    private readonly Dictionary<string, (GameArt Art, DateTime ExpiresUtc)> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<Resolved>> _inFlight = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public GameArtResolver(IHttpClientFactory http, IGameInstallLocator installs, IProcessIconProvider icons)
        : this(http, installs, icons, DefaultStoreBaseUrl)
    {
    }

    // Test-only ctor: loopback store base url.
    internal GameArtResolver(IHttpClientFactory http, IGameInstallLocator installs, IProcessIconProvider icons, string storeBaseUrl)
    {
        _http = http;
        _installs = installs;
        _icons = icons;
        _storeBaseUrl = storeBaseUrl.TrimEnd('/');
    }

    /// <summary>A result plus how long it deserves to be trusted.</summary>
    private readonly record struct Resolved(GameArt Art, TimeSpan Ttl);

    public async Task<GameArt> ResolveAsync(string gameKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gameKey)) return GameArt.None;

        if (TryReadCache(gameKey, out var cached)) return cached;

        // Coalesced: a page of cards asks for the same key at once, and each
        // miss would otherwise be its own round trip to the store.
        Task<Resolved> work;
        lock (_lock)
        {
            if (!_inFlight.TryGetValue(gameKey, out work!))
            {
                work = ResolveUncachedAsync(gameKey, ct);
                _inFlight[gameKey] = work;
            }
        }

        Resolved resolved;
        try { resolved = await work.ConfigureAwait(false); }
        finally { lock (_lock) { _inFlight.Remove(gameKey); } }

        Store(gameKey, resolved.Art, resolved.Ttl);
        return resolved.Art;
    }

    public GameArt ResolveIcon(string gameKey)
    {
        if (string.IsNullOrWhiteSpace(gameKey)) return GameArt.None;

        var icon = ResolveExecutableIcon(gameKey);
        return icon is { Length: > 0 } ? new GameArt("", icon) : GameArt.None;
    }

    private async Task<Resolved> ResolveUncachedAsync(string gameKey, CancellationToken ct)
    {
        if (TryParseSteamAppId(gameKey, out var appId))
        {
            var resolved = await ResolveSteamAsync(appId, ct).ConfigureAwait(false);
            if (resolved.Length > 0) return new Resolved(new GameArt(resolved, Array.Empty<byte>()), PositiveTtl);

            // The installed binary's icon beats the legacy capsule path here:
            // the capsule is a guess that 404s for anything published after
            // Valve moved art behind a content hash, while the icon is on disk.
            var installed = ResolveExecutableIcon(gameKey);
            if (installed is { Length: > 0 }) return new Resolved(new GameArt("", installed), PositiveTtl);

            // Not installed, or no icon: the capsule still answers for the back
            // catalogue, and a failed store call is no evidence about this game,
            // so it must not pin a 404 for a week either way.
            return new Resolved(new GameArt(LegacyCapsuleUrl(appId), Array.Empty<byte>()), TransientTtl);
        }

        var icon = ResolveExecutableIcon(gameKey);
        if (icon is null) return new Resolved(GameArt.None, TransientTtl);
        return icon.Length > 0
            ? new Resolved(new GameArt("", icon), PositiveTtl)
            : new Resolved(GameArt.None, MissTtl);
    }

    /// <summary>The store's own header image url, or empty when the record cannot be read.</summary>
    private async Task<string> ResolveSteamAsync(int appId, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);

            var client = _http.CreateClient();
            var url = $"{_storeBaseUrl}/api/appdetails?appids={appId}&filters=basic";
            using var response = await client.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return "";

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty(appId.ToString(), out var entry)) return "";
            if (!entry.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True) return "";
            if (!entry.TryGetProperty("data", out var data)) return "";
            if (!data.TryGetProperty("header_image", out var header) || header.ValueKind != JsonValueKind.String) return "";

            var image = header.GetString() ?? "";
            // The ?t= cache-buster changes on every re-upload and would defeat
            // the browser cache across our own TTL.
            var query = image.IndexOf('?');
            if (query >= 0) image = image[..query];
            return IsSteamImageUrl(image) ? image : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            return "";
        }
    }

    // The url is redirected to, from a remote body: anything but a parsed
    // https Steam host would make this an open redirect, and a value carrying
    // CR/LF would throw setting the Location header.
    private static bool IsSteamImageUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".steampowered.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Icon bytes, empty when there is none, or null when extraction could not be attempted (IProcessIconProvider's contract).</summary>
    private byte[]? ResolveExecutableIcon(string gameKey)
    {
        if (!_installs.TryGetInstallDir(gameKey, out var installDir)) return Array.Empty<byte>();

        var exe = PickGameExecutable(installDir);
        if (exe.Length == 0) return Array.Empty<byte>();

        return _icons.GetIcon(exe);
    }

    // Below this an executable name is too short to be evidence: "T.exe" would
    // otherwise claim any folder starting with a T.
    private const int MinNameMatchLength = 4;

    // Deep enough for <Project>/Binaries/Win64/, shallow enough that a big
    // install is not walked end to end.
    private const int MaxSearchDepth = 3;

    /// <summary>
    /// The executable most likely to carry the game's own icon: one named after
    /// the install folder wins, since a game's shipping binary usually is, and
    /// the largest file is the fallback - launchers, crash handlers and
    /// redistributables sit beside it but are far smaller.
    /// </summary>
    internal static string PickGameExecutable(string installDir)
    {
        try
        {
            if (!Directory.Exists(installDir)) return "";

            // Unreal ships its binary at <Project>/Binaries/Win64/, so a store
            // whose games are mostly Unreal has nothing at the top level.
            var exes = FindExecutables(installDir, MaxSearchDepth);
            if (exes.Count == 0) return "";

            var folder = Slug(Path.GetFileName(installDir.TrimEnd(Path.DirectorySeparatorChar)));
            var named = exes
                .Where(e => NameMatches(Slug(Path.GetFileNameWithoutExtension(e)), folder))
                .ToList();

            return (named.Count > 0 ? named : exes)
                .OrderByDescending(e => new FileInfo(e).Length)
                .First();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    // Prefix either way, not equality: a "Huntdown Overtime" folder ships
    // Huntdown.exe, and a versioned folder appends to the game's own name.
    private static bool NameMatches(string exe, string folder) =>
        exe.Length >= MinNameMatchLength
        && folder.Length >= MinNameMatchLength
        && (exe == folder || folder.StartsWith(exe, StringComparison.Ordinal) || exe.StartsWith(folder, StringComparison.Ordinal));

    /// <summary>Executables at or above the shallowest depth that has any, so a top-level binary is never outranked by one buried in a subfolder.</summary>
    private static List<string> FindExecutables(string root, int maxDepth)
    {
        var level = new List<string> { root };
        for (var depth = 0; depth <= maxDepth && level.Count > 0; depth++)
        {
            var found = new List<string>();
            var next = new List<string>();
            foreach (var dir in level)
            {
                found.AddRange(Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly));
                next.AddRange(Directory.EnumerateDirectories(dir));
            }
            if (found.Count > 0) return found;
            level = next;
        }
        return new List<string>();
    }

    private static string Slug(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) buffer[length++] = char.ToLowerInvariant(ch);
        }
        return new string(buffer[..length]);
    }

    internal static bool TryParseSteamAppId(string gameKey, out int appId)
    {
        appId = 0;
        const string prefix = "steam:";
        if (!gameKey.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return int.TryParse(gameKey.AsSpan(prefix.Length), out appId) && appId > 0;
    }

    internal static string LegacyCapsuleUrl(int appId) =>
        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule_231x87.jpg";

    private bool TryReadCache(string gameKey, out GameArt art)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(gameKey, out var hit) && DateTime.UtcNow < hit.ExpiresUtc)
            {
                art = hit.Art;
                return true;
            }
        }

        art = GameArt.None;
        return false;
    }

    private void Store(string gameKey, GameArt art, TimeSpan ttl)
    {
        lock (_lock)
        {
            if (_cache.Count >= CacheCap && !_cache.ContainsKey(gameKey))
            {
                var oldest = _cache.OrderBy(e => e.Value.ExpiresUtc).First().Key;
                _cache.Remove(oldest);
            }
            _cache[gameKey] = (art, DateTime.UtcNow.Add(ttl));
        }
    }
}
