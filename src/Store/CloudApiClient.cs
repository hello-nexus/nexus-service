using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Store;

/// <summary>
/// HTTP client for the Nexus cloud API store and account endpoints.
/// Base URL defaults to https://api.hellonexus.com; override with
/// the NEXUS_CLOUD_API environment variable for local testing.
/// </summary>
public sealed class CloudApiClient
{
    private const string DefaultBaseUrl = "https://api.hellonexus.com";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);

    private readonly IHttpClientFactory _http;
    private readonly string _baseUrl;

    public CloudApiClient(IHttpClientFactory http)
    {
        _http = http;
        _baseUrl = Environment.GetEnvironmentVariable("NEXUS_CLOUD_API")?.TrimEnd('/') ?? DefaultBaseUrl;
    }

    /// <summary>Start device-grant flow. Returns null on network error.</summary>
    public async Task<CloudDeviceGrantStartResponse?> StartDeviceGrantAsync(CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(RequestTimeout);
            using var res = await client.PostAsync($"{_baseUrl}/auth/device/start", content: null, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[store] device/start -> {(int)res.StatusCode}");
                return null;
            }
            return await res.Content.ReadFromJsonAsync(AppJsonContext.Default.CloudDeviceGrantStartResponse, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[store] device/start failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Poll device-grant flow. Returns null on network error.</summary>
    public async Task<CloudDeviceGrantPollResponse?> PollDeviceGrantAsync(string deviceCode, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(RequestTimeout);
            using var content = JsonContent.Create(new CloudDeviceGrantPollRequest { DeviceCode = deviceCode }, AppJsonContext.Default.CloudDeviceGrantPollRequest);
            using var res = await client.PostAsync($"{_baseUrl}/auth/device/poll", content, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[store] device/poll -> {(int)res.StatusCode}");
                return null;
            }
            return await res.Content.ReadFromJsonAsync(AppJsonContext.Default.CloudDeviceGrantPollResponse, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[store] device/poll failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Fetch account info. Returns null when not linked or on error.</summary>
    public async Task<CloudAccountInfo?> GetAccountAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(RequestTimeout);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            return await client.GetFromJsonAsync($"{_baseUrl}/account/me", AppJsonContext.Default.CloudAccountInfo, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[store] account/me failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Fetch library (entitlements + installs). Returns null on error or not linked.</summary>
    public async Task<CloudLibraryResponse?> GetLibraryAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(RequestTimeout);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            return await client.GetFromJsonAsync($"{_baseUrl}/account/library", AppJsonContext.Default.CloudLibraryResponse, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[store] account/library failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Acquire entitlement for an app. Returns true on success.</summary>
    public async Task<bool> AcquireAppAsync(string accessToken, string appId, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(RequestTimeout);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await client.PostAsync(
                $"{_baseUrl}/store/apps/{Uri.EscapeDataString(appId)}/acquire", content: null, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[store] acquire {appId} -> {(int)res.StatusCode}");
            }
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[store] acquire {appId} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Get download info for an app. Returns null on error or 404.</summary>
    public async Task<CloudAppDownloadInfo?> GetDownloadInfoAsync(string accessToken, string appId, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(RequestTimeout);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var res = await client.GetAsync(
                $"{_baseUrl}/store/apps/{Uri.EscapeDataString(appId)}/download", ct).ConfigureAwait(false);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
            if (!res.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[store] download-info {appId} -> {(int)res.StatusCode}");
                return null;
            }
            return await res.Content.ReadFromJsonAsync(AppJsonContext.Default.CloudAppDownloadInfo, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[store] download-info {appId} failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Download the artifact at url into destDir (extracts the zip).
    /// Returns false when the URL is unreachable or returns 404 (artifact_unavailable).
    /// Partial directories are cleaned up on failure.
    /// </summary>
    public async Task<(bool ok, string? reason)> DownloadAndExtractAsync(
        string url, string destDir, CancellationToken ct)
    {
        var tmpFile = destDir + ".nexus-app.tmp";
        try
        {
            using var client = CreateClient(DownloadTimeout);
            using var res = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound ||
                res.StatusCode == System.Net.HttpStatusCode.Gone)
            {
                return (false, "artifact_unavailable");
            }
            if (!res.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[store] download {url} -> {(int)res.StatusCode}");
                return (false, "artifact_unavailable");
            }

            using (var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var dst = File.Create(tmpFile))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            if (Directory.Exists(destDir))
            {
                Directory.Delete(destDir, recursive: true);
            }
            Directory.CreateDirectory(destDir);
            ZipFile.ExtractToDirectory(tmpFile, destDir, overwriteFiles: true);
            return (true, null);
        }
        catch (InvalidDataException)
        {
            // Not a valid zip archive.
            CleanupPartial(destDir);
            return (false, "artifact_unavailable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[store] download failed: {ex.GetType().Name}: {ex.Message}");
            CleanupPartial(destDir);
            return (false, "artifact_unavailable");
        }
        finally
        {
            if (File.Exists(tmpFile))
            {
                try { File.Delete(tmpFile); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Report install to cloud. Best-effort; failures are logged and dropped.</summary>
    public async Task ReportInstallAsync(string accessToken, CloudInstallReport report, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(RequestTimeout);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var content = JsonContent.Create(report, AppJsonContext.Default.CloudInstallReport);
            using var res = await client.PostAsync($"{_baseUrl}/account/installs", content, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[store] report install {report.AppId} -> {(int)res.StatusCode}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[store] report install failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Report uninstall to cloud. Best-effort; failures are logged and dropped.</summary>
    public async Task ReportUninstallAsync(string accessToken, string appId, string installId, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(RequestTimeout);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await client.DeleteAsync(
                $"{_baseUrl}/account/installs/{Uri.EscapeDataString(appId)}?installId={Uri.EscapeDataString(installId)}", ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[store] report uninstall {appId} -> {(int)res.StatusCode}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[store] report uninstall failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private HttpClient CreateClient(TimeSpan timeout)
    {
        var client = _http.CreateClient();
        client.Timeout = timeout;
        return client;
    }

    private static void CleanupPartial(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { /* best effort */ }
    }
}
