using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.Tryx.Panorama.Crypto;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>One catalog entry: the fields the panel install flow needs to show
/// a pick list and then fetch + decrypt the asset.</summary>
public sealed class TryxCloudMaterial
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string CoverUrl { get; init; } = "";
}

/// <summary>
/// Client for Kanali's Tryx cloud wallpaper catalog (<c>kanali2-api.tryxzone.com</c>).
///
/// Wire format, decoded from the desktop app's axios wrapper (`mr`/`lM`/`Mf` in
/// the Kanali Electron bundle): every request and response body is an SM2
/// ciphertext (GM/T 0003.4, mode C1C3C2 / Rv=1) over the JSON-encoded payload,
/// and that ciphertext hex is itself carried as a bare JSON string scalar - not
/// `{data:"..."}`. `mr` builds the request with `Content-Type:
/// application/json` and passes axios the raw hex STRING; axios's default
/// transformRequest still runs `JSON.stringify` on it because the
/// Content-Type-is-json check short-circuits its is-this-a-plain-object check,
/// so the literal wire body is a JSON string scalar (`"04a1b2..."`, quotes
/// included). The response mirrors this: the server answers a JSON string
/// scalar, and axios's default `responseType:"json"` unwraps it back to a bare
/// string before the SM2 decrypt (`Mf`) runs on it - so both directions here
/// serialize/deserialize the ciphertext through the plain `string`
/// JsonTypeInfo, not a wrapper object.
///
/// `lM` (encrypt) prepends a literal "04" byte the underlying SM2 library
/// doesn't add on its own; <see cref="Sm2.Encrypt"/> mirrors the library and
/// leaves that prefix out, so this client adds it explicitly to match `lM`
/// exactly. The embedded public key (encrypts client to server) and private
/// key (decrypts server to client) are independently provisioned - they are
/// not a mathematical keypair, so do not expect one to derive the other.
///
/// UNVERIFIED: the exact response envelope field names (code/data/errorMsg)
/// and the SM4 mode used to encrypt the downloadable assets (assumed ECB with
/// PKCS7 padding, the most streamable option and the default most published
/// SM4 references use when a mode isn't stated) are read from static
/// analysis only - no live call against this API has been made. Confirm
/// against a real response before shipping.
/// </summary>
public sealed class TryxCloudCatalog
{
    private const string BaseUrl = "https://kanali2-api.tryxzone.com/api";

    /// <summary>Encrypts every outgoing request body. Server-held; only the
    /// server can decrypt what this key encrypts.</summary>
    private const string RequestPublicKeyHex =
        "04e4da9b393d64baff3294f9d57c2939596dfee21e0388b3c7688b9e066d28ce907dc933f45d7694c3b0beded76ae48a9295e49054fca75594d99a698a3c3ea294";

    /// <summary>Decrypts every incoming response body. Paired with a public key
    /// the server holds for this client, not with <see cref="RequestPublicKeyHex"/>.</summary>
    private const string ResponsePrivateKeyHex =
        "405456c4e0f493d31beb0af38a6f248b5a522d1ae0835589e905aec07efcf54a";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    { Timeout = TimeSpan.FromMinutes(2) };

    // Signed CDN cover URLs from the last catalog fetch, keyed by material id, so the
    // cover-proxy route can stream one without re-querying the API.
    private static readonly ConcurrentDictionary<int, string> CoverUrls = new();

    /// <summary>Query the catalog for a product code (Panorama = "PANO_1011"),
    /// then resolve the returned ids to full records.</summary>
    public async Task<List<TryxCloudMaterial>> GetCatalogAsync(string code, CancellationToken ct)
    {
        var queryJson = JsonSerializer.Serialize(
            new TryxMaterialQueryRequest { Code = code }, TryxCloudJsonContext.Default.TryxMaterialQueryRequest);
        var ids = await SendAsync(
            "/app-material/query", queryJson, TryxCloudJsonContext.Default.TryxCloudEnvelopeListInt32, ct)
            .ConfigureAwait(false);
        if (ids is null || ids.Count == 0)
        {
            return new List<TryxCloudMaterial>();
        }

        var getJson = JsonSerializer.Serialize(
            new TryxMaterialGetRequest { IdList = ids }, TryxCloudJsonContext.Default.TryxMaterialGetRequest);
        var records = await SendAsync(
            "/app-material/get", getJson, TryxCloudJsonContext.Default.TryxCloudEnvelopeListTryxMaterialRecord, ct)
            .ConfigureAwait(false);
        if (records is null)
        {
            return new List<TryxCloudMaterial>();
        }

        foreach (var r in records) CoverUrls[r.Id] = r.CoverFileUrl;
        return records.Select(r => new TryxCloudMaterial
        {
            Id = r.Id,
            Name = r.Name,
            CoverUrl = r.CoverFileUrl,
        }).ToList();
    }

    /// <summary>Opens the CDN cover image for a material, or null if unknown/unreachable.
    /// Caller owns the returned stream. On a cold cache (fresh service start) the catalog is
    /// fetched first so a cover request that arrives before any catalog load still resolves.</summary>
    public async Task<Stream?> OpenCoverAsync(int id, CancellationToken ct)
    {
        if (!CoverUrls.TryGetValue(id, out var url))
        {
            try { await GetCatalogAsync("PANO_1011", ct).ConfigureAwait(false); }
            catch { /* offline; nothing to serve */ }
            if (!CoverUrls.TryGetValue(id, out url)) return null;
        }
        var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            resp.Dispose();
            return null;
        }
        return await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> GetDownloadUrlAsync(int id, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(
            new TryxMaterialGetUrlRequest { Id = id }, TryxCloudJsonContext.Default.TryxMaterialGetUrlRequest);
        var url = await SendAsync("/app-material/getUrl", json, TryxCloudJsonContext.Default.TryxCloudEnvelopeString, ct)
            .ConfigureAwait(false);
        return url ?? throw new InvalidOperationException("Tryx cloud API returned no download URL.");
    }

    /// <summary>The material's SM4 key RSA-wrapped for the panel (base64), as the panel's decrypt
    /// job takes it. This endpoint answers in the clear, not SM2-wrapped like the others.</summary>
    public async Task<string> GetPanelWrappedSm4KeyAsync(CancellationToken ct)
    {
        var body = (await PostAsync("/app-sm/rsa/get-sm4-key", "{}", ct).ConfigureAwait(false)).Trim();
        if (body.StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(body);
            body = doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String
                ? data.GetString() ?? ""
                : "";
        }
        else if (body.StartsWith('"'))
        {
            body = JsonSerializer.Deserialize(body, TryxCloudJsonContext.Default.String) ?? "";
        }
        if (body.Length == 0) throw new InvalidOperationException("Tryx cloud API returned no wrapped SM4 key.");
        return body;
    }

    /// <summary>Downloads the material still encrypted ([16-byte IV] + SM4-CBC) to
    /// <paramref name="destPath"/> and returns its panel file name, which Kanali derives from the
    /// download URL's extension.</summary>
    public async Task<string> DownloadEncryptedAsync(int id, string destPath, CancellationToken ct)
    {
        var url = await GetDownloadUrlAsync(id, ct).ConfigureAwait(false);
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(destPath))
        {
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }
        return DownloadFileName(id, url);
    }

    /// <summary>Kanali's name for a downloaded material: download_&lt;id&gt;.&lt;type&gt;.h264_&lt;W&gt;x&lt;H&gt;,
    /// type from the URL path's extension (.mp4/.avi -> mp4, .gif, .png, .jpg/.jpeg -> jpg, else mp4).</summary>
    internal static string DownloadFileName(int id, string url)
    {
        var path = url.Split('?')[0];
        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".gif" => "gif",
            ".png" => "png",
            ".jpg" or ".jpeg" => "jpg",
            _ => "mp4",
        };
        return $"download_{id}.{type}.h264_2240x1080";
    }

    // ── SM2-wrapped request/response envelope ───────────────────────────────

    // POSTs the SM2-encrypted request and returns the raw response body.
    private static async Task<string> PostAsync(string path, string plaintextJson, CancellationToken ct)
    {
        var cipherHex = "04" + Sm2.Encrypt(plaintextJson, RequestPublicKeyHex);

        using var content = new StringContent(cipherHex, Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(BaseUrl + path, content, ct).ConfigureAwait(false);
        var responseBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var bodyHead = responseBody.Length > 300 ? responseBody[..300] : responseBody;
            Platform.ServiceLog.Warn($"[tryx] cloud {path} -> {(int)resp.StatusCode}; body={bodyHead}");
            resp.EnsureSuccessStatusCode();
        }
        return responseBody;
    }

    private static async Task<TOut?> SendAsync<TOut>(
        string path, string plaintextJson, JsonTypeInfo<TryxCloudEnvelope<TOut>> envelopeInfo, CancellationToken ct)
    {
        // The server's SM2 gateway wants the ciphertext hex as the RAW request body, not a
        // JSON-quoted string (Kanali's bundled axios sends the string as-is; a quoted body is
        // rejected with a 500). The 200 response is likewise raw hex, not a JSON scalar.
        var responseBody = await PostAsync(path, plaintextJson, ct).ConfigureAwait(false);
        // Tolerate a quoted or unquoted hex response.
        var responseHex = responseBody.Trim().Trim('"');
        if (string.IsNullOrEmpty(responseHex))
        {
            throw new InvalidOperationException($"Tryx cloud API returned an empty response for {path}.");
        }

        var decryptedJson = Sm2.Decrypt(responseHex, ResponsePrivateKeyHex);
        var envelope = JsonSerializer.Deserialize(decryptedJson, envelopeInfo)
            ?? throw new InvalidOperationException($"Tryx cloud API returned an unparsable envelope for {path}.");

        if (envelope.Code != 200 && envelope.Code != 201)
        {
            throw new InvalidOperationException($"Tryx cloud API error on {path}: code={envelope.Code} {envelope.ErrorMsg}");
        }

        return envelope.Data;
    }
}

// ── Wire DTOs (SM2-encrypted plaintext bodies, GM/T-decrypted response bodies) ──

public sealed class TryxMaterialQueryRequest
{
    [JsonPropertyName("code")] public string Code { get; init; } = "";
}

public sealed class TryxMaterialGetRequest
{
    [JsonPropertyName("idList")] public List<int> IdList { get; init; } = new();
}

public sealed class TryxMaterialGetUrlRequest
{
    [JsonPropertyName("id")] public int Id { get; init; }
}

public sealed class TryxMaterialRecord
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("coverFileUrl")] public string CoverFileUrl { get; set; } = "";
    [JsonPropertyName("previewFileUrl")] public string PreviewFileUrl { get; set; } = "";
    [JsonPropertyName("hardwareInfo")] public List<TryxHardwareInfoEntry> HardwareInfo { get; set; } = new();
}

public sealed class TryxHardwareInfoEntry
{
    [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
}

public sealed class TryxCloudEnvelope<T>
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("data")] public T? Data { get; set; }
    [JsonPropertyName("errorMsg")] public string? ErrorMsg { get; set; }
}
