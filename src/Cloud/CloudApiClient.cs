using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Serialization;

namespace Nexus.Service.Cloud;

/// <summary>
/// Outcome of one CloudApiClient call. <see cref="Offline"/> means the
/// request never reached the server (DNS/connect/timeout) - distinct from a
/// server-returned error status, so callers can tell "cloud unreachable" from
/// "cloud rejected this".
/// </summary>
public sealed class CloudApiResult<T>
{
    public bool Success { get; init; }
    public int StatusCode { get; init; }
    public T? Value { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>ISO timestamp from the upstream error body's retryAt field (e.g. username_cooldown). Null unless the server sent one.</summary>
    public string? ErrorRetryAt { get; init; }
    public bool Offline { get; init; }

    public static CloudApiResult<T> Ok(T value, int statusCode = 200) =>
        new() { Success = true, StatusCode = statusCode, Value = value };

    public static CloudApiResult<T> Fail(int statusCode, string? code, string? message, string? retryAt = null) =>
        new() { Success = false, StatusCode = statusCode, ErrorCode = code, ErrorMessage = message, ErrorRetryAt = retryAt };

    public static CloudApiResult<T> NetworkError(string message) =>
        new() { Success = false, Offline = true, ErrorMessage = message };
}

/// <summary>Empty success marker for calls whose body carries nothing the caller needs.</summary>
public sealed class CloudVoid
{
    public static readonly CloudVoid Instance = new();
}

/// <summary>Verbatim upstream response for a raw byte-forwarded call - the body is never parsed, so a caller with no typed DTO for the endpoint can still relay it as-is.</summary>
public sealed class CloudRawResponse
{
    public string Body { get; init; } = "";
    public string ContentType { get; init; } = "application/json";
}

/// <summary>
/// Seam over the api.hellonexus.com account/profile-sync surface. Real HTTP in
/// <see cref="CloudApiClient"/>; tests substitute a fake so CloudAccountService
/// and CloudProfileSyncService are unit-testable without a live server.
/// </summary>
public interface ICloudApiClient
{
    Task<CloudApiResult<CloudVoid>> RegisterAsync(CloudRegisterRequest body, CancellationToken ct);
    Task<CloudApiResult<CloudAuthSession>> LoginAsync(CloudLoginRequest body, CancellationToken ct);
    Task<CloudApiResult<CloudAuthSession>> RefreshAsync(string refreshToken, CancellationToken ct);
    Task<CloudApiResult<CloudVoid>> LogoutAsync(string refreshToken, CancellationToken ct);
    Task<CloudApiResult<CloudVoid>> RecoveryStartAsync(CloudRecoveryStartRequest body, CancellationToken ct);
    Task<CloudApiResult<CloudRecoveryPollResponse>> RecoveryPollAsync(CloudRecoveryPollRequest body, CancellationToken ct);
    Task<CloudApiResult<CloudVoid>> ChangePasswordAsync(string accessToken, CloudChangePasswordRequest body, CancellationToken ct);
    Task<CloudApiResult<CloudVoid>> ChangeUsernameAsync(string accessToken, CloudChangeUsernameRequest body, CancellationToken ct);
    Task<CloudApiResult<CloudVoid>> SetPrivateAsync(string accessToken, bool isPrivate, CancellationToken ct);
    Task<CloudApiResult<CloudVoid>> DeleteAccountAsync(string accessToken, string? currentPassword, CancellationToken ct);
    Task<CloudApiResult<CloudAvatarUploadResponse>> UploadAvatarAsync(string accessToken, byte[] bytes, string contentType, CancellationToken ct);
    Task<CloudApiResult<CloudVoid>> PutDeviceAsync(string accessToken, string installId, CloudDevicePutRequest body, CancellationToken ct);
    Task<CloudApiResult<System.Collections.Generic.List<CloudProfileSummaryDto>>> ListProfilesAsync(string accessToken, CancellationToken ct);
    Task<CloudApiResult<CloudProfileDto>> GetProfileAsync(string accessToken, string profileId, CancellationToken ct);
    Task<CloudApiResult<CloudPutProfileResult>> PutProfileAsync(string accessToken, string profileId, CloudPutProfileRequest body, CancellationToken ct);
    Task<CloudApiResult<CloudVoid>> DeleteProfileAsync(string accessToken, string profileId, CancellationToken ct);

    /// <summary>
    /// Forwards a raw JSON request body to <paramref name="path"/> and returns
    /// the upstream response body/content-type verbatim, with no DTO on either
    /// side. Success covers every HTTP response actually received (including a
    /// non-2xx one, e.g. a validation 400) - StatusCode always carries the real
    /// upstream status; only a network/DNS/timeout failure sets Offline.
    /// </summary>
    Task<CloudApiResult<CloudRawResponse>> PostRawAsync(string path, string rawJsonBody, string? accessToken, CancellationToken ct);

    /// <summary>
    /// Same passthrough contract as <see cref="PostRawAsync"/> for an
    /// arbitrary HTTP method. <paramref name="rawJsonBody"/> null omits a
    /// request body entirely (GET/DELETE); non-null sends it as
    /// application/json (PUT/PATCH/POST).
    /// </summary>
    Task<CloudApiResult<CloudRawResponse>> SendRawAsync(HttpMethod method, string path, string? rawJsonBody, string? accessToken, CancellationToken ct);
}

public sealed class CloudApiClient : ICloudApiClient
{
    private const string DefaultBaseUrl = "https://api.hellonexus.com";
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _http;
    private readonly string _baseUrl;
    private readonly TimeSpan _requestTimeout;

    public CloudApiClient(IHttpClientFactory http)
        : this(http, Environment.GetEnvironmentVariable("NEXUS_API_BASE")?.TrimEnd('/') ?? DefaultBaseUrl, DefaultRequestTimeout)
    {
    }

    // Test-only ctor: an explicit base URL/timeout, never reading or mutating
    // the process-wide NEXUS_API_BASE environment variable, which would race
    // any other test that constructs a real CloudApiClient (e.g. through
    // NexusAppFactory's DI container) under parallel test execution.
    internal CloudApiClient(IHttpClientFactory http, string baseUrl, TimeSpan requestTimeout)
    {
        _http = http;
        _baseUrl = baseUrl;
        _requestTimeout = requestTimeout;
    }

    public Task<CloudApiResult<CloudVoid>> RegisterAsync(CloudRegisterRequest body, CancellationToken ct) =>
        PostVoidAsync("/auth/register", body, AppJsonContext.Default.CloudRegisterRequest, ct);

    public Task<CloudApiResult<CloudAuthSession>> LoginAsync(CloudLoginRequest body, CancellationToken ct) =>
        PostAsync("/auth/login", body, AppJsonContext.Default.CloudLoginRequest, AppJsonContext.Default.CloudAuthSession, ct);

    public Task<CloudApiResult<CloudAuthSession>> RefreshAsync(string refreshToken, CancellationToken ct) =>
        PostAsync("/auth/refresh", new CloudRefreshRequest { RefreshToken = refreshToken },
            AppJsonContext.Default.CloudRefreshRequest, AppJsonContext.Default.CloudAuthSession, ct);

    public Task<CloudApiResult<CloudVoid>> LogoutAsync(string refreshToken, CancellationToken ct) =>
        PostVoidAsync("/auth/logout", new CloudLogoutRequest { RefreshToken = refreshToken },
            AppJsonContext.Default.CloudLogoutRequest, ct);

    public Task<CloudApiResult<CloudVoid>> RecoveryStartAsync(CloudRecoveryStartRequest body, CancellationToken ct) =>
        PostVoidAsync("/auth/recovery/start", body, AppJsonContext.Default.CloudRecoveryStartRequest, ct);

    public Task<CloudApiResult<CloudRecoveryPollResponse>> RecoveryPollAsync(CloudRecoveryPollRequest body, CancellationToken ct) =>
        PostAsync("/auth/recovery/poll", body, AppJsonContext.Default.CloudRecoveryPollRequest, AppJsonContext.Default.CloudRecoveryPollResponse, ct);

    public Task<CloudApiResult<CloudVoid>> ChangePasswordAsync(string accessToken, CloudChangePasswordRequest body, CancellationToken ct) =>
        PostVoidAsync("/account/password", body, AppJsonContext.Default.CloudChangePasswordRequest, ct, accessToken);

    public Task<CloudApiResult<CloudVoid>> ChangeUsernameAsync(string accessToken, CloudChangeUsernameRequest body, CancellationToken ct) =>
        PostVoidAsync("/account/username", body, AppJsonContext.Default.CloudChangeUsernameRequest, ct, accessToken);

    public async Task<CloudApiResult<CloudVoid>> SetPrivateAsync(string accessToken, bool isPrivate, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(accessToken);
            using var content = JsonContent.Create(new CloudSetPrivateRequest { IsPrivate = isPrivate }, AppJsonContext.Default.CloudSetPrivateRequest);
            using var request = new HttpRequestMessage(HttpMethod.Patch, _baseUrl + "/account") { Content = content };
            using var res = await client.SendAsync(request, ct).ConfigureAwait(false);
            return await ToVoidResultAsync(res, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return CloudApiResult<CloudVoid>.NetworkError(ex.Message);
        }
    }

    public async Task<CloudApiResult<CloudVoid>> DeleteAccountAsync(string accessToken, string? currentPassword, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(accessToken);
            using var content = JsonContent.Create(new CloudDeleteAccountRequest { CurrentPassword = currentPassword }, AppJsonContext.Default.CloudDeleteAccountRequest);
            using var request = new HttpRequestMessage(HttpMethod.Delete, _baseUrl + "/account") { Content = content };
            using var res = await client.SendAsync(request, ct).ConfigureAwait(false);
            return await ToVoidResultAsync(res, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return CloudApiResult<CloudVoid>.NetworkError(ex.Message);
        }
    }

    public async Task<CloudApiResult<CloudAvatarUploadResponse>> UploadAvatarAsync(string accessToken, byte[] bytes, string contentType, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(accessToken);
            using var form = new MultipartFormDataContent();
            using var imageContent = new ByteArrayContent(bytes);
            imageContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            // Field name must be "file": nexus-api's FileInterceptor('file')
            // rejects any other multipart field with 400 "Unexpected field".
            form.Add(imageContent, "file", "avatar.png");
            using var res = await client.PostAsync(_baseUrl + "/account/avatar", form, ct).ConfigureAwait(false);
            return await ToResultAsync(res, AppJsonContext.Default.CloudAvatarUploadResponse, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return CloudApiResult<CloudAvatarUploadResponse>.NetworkError(ex.Message);
        }
    }

    public Task<CloudApiResult<CloudVoid>> PutDeviceAsync(string accessToken, string installId, CloudDevicePutRequest body, CancellationToken ct) =>
        PutVoidAsync($"/account/devices/{Uri.EscapeDataString(installId)}", body, AppJsonContext.Default.CloudDevicePutRequest, ct, accessToken);

    public async Task<CloudApiResult<System.Collections.Generic.List<CloudProfileSummaryDto>>> ListProfilesAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(accessToken);
            using var res = await client.GetAsync(_baseUrl + "/account/profiles", ct).ConfigureAwait(false);
            return await ToResultAsync(res, AppJsonContext.Default.ListCloudProfileSummaryDto, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return CloudApiResult<System.Collections.Generic.List<CloudProfileSummaryDto>>.NetworkError(ex.Message);
        }
    }

    public async Task<CloudApiResult<CloudProfileDto>> GetProfileAsync(string accessToken, string profileId, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(accessToken);
            using var res = await client.GetAsync(_baseUrl + "/account/profiles/" + Uri.EscapeDataString(profileId), ct).ConfigureAwait(false);
            return await ToResultAsync(res, AppJsonContext.Default.CloudProfileDto, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return CloudApiResult<CloudProfileDto>.NetworkError(ex.Message);
        }
    }

    public async Task<CloudApiResult<CloudPutProfileResult>> PutProfileAsync(string accessToken, string profileId, CloudPutProfileRequest body, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(accessToken);
            using var content = JsonContent.Create(body, AppJsonContext.Default.CloudPutProfileRequest);
            using var res = await client.PutAsync(_baseUrl + "/account/profiles/" + Uri.EscapeDataString(profileId), content, ct).ConfigureAwait(false);
            // 409 carries a meaningful conflict body, not just an error - read it like a success shape.
            if (res.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                var conflict = await ReadJsonAsync(res, AppJsonContext.Default.CloudPutProfileResult, ct).ConfigureAwait(false);
                return conflict is not null
                    ? CloudApiResult<CloudPutProfileResult>.Ok(conflict, (int)res.StatusCode)
                    : CloudApiResult<CloudPutProfileResult>.Fail((int)res.StatusCode, "conflict", "Profile revision conflict.");
            }
            return await ToResultAsync(res, AppJsonContext.Default.CloudPutProfileResult, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return CloudApiResult<CloudPutProfileResult>.NetworkError(ex.Message);
        }
    }

    public async Task<CloudApiResult<CloudVoid>> DeleteProfileAsync(string accessToken, string profileId, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(accessToken);
            using var res = await client.DeleteAsync(_baseUrl + "/account/profiles/" + Uri.EscapeDataString(profileId), ct).ConfigureAwait(false);
            return await ToVoidResultAsync(res, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return CloudApiResult<CloudVoid>.NetworkError(ex.Message);
        }
    }

    public Task<CloudApiResult<CloudRawResponse>> PostRawAsync(string path, string rawJsonBody, string? accessToken, CancellationToken ct) =>
        SendRawAsync(HttpMethod.Post, path, rawJsonBody, accessToken, ct);

    public async Task<CloudApiResult<CloudRawResponse>> SendRawAsync(HttpMethod method, string path, string? rawJsonBody, string? accessToken, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(accessToken);
            using var request = new HttpRequestMessage(method, _baseUrl + path);
            if (rawJsonBody is not null)
            {
                request.Content = new StringContent(rawJsonBody, System.Text.Encoding.UTF8, "application/json");
            }
            using var res = await client.SendAsync(request, ct).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var contentType = res.Content.Headers.ContentType?.MediaType ?? "application/json";
            // Any HTTP response we actually received is "Success" here - the
            // caller forwards the body/status verbatim regardless of whether
            // upstream itself returned an error status.
            return CloudApiResult<CloudRawResponse>.Ok(
                new CloudRawResponse { Body = body, ContentType = contentType }, (int)res.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return CloudApiResult<CloudRawResponse>.NetworkError(ex.Message);
        }
    }

    // ── shared plumbing ─────────────────────────────────────────────────

    private HttpClient CreateClient(string? accessToken = null)
    {
        var client = _http.CreateClient();
        client.Timeout = _requestTimeout;
        if (!string.IsNullOrEmpty(accessToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        return client;
    }

    private async Task<CloudApiResult<TResponse>> PostAsync<TRequest, TResponse>(
        string path, TRequest body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> requestType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseType, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient();
            using var content = JsonContent.Create(body, requestType);
            using var res = await client.PostAsync(_baseUrl + path, content, ct).ConfigureAwait(false);
            return await ToResultAsync(res, responseType, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return CloudApiResult<TResponse>.NetworkError(ex.Message);
        }
    }

    private async Task<CloudApiResult<CloudVoid>> PostVoidAsync<TRequest>(
        string path, TRequest body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> requestType,
        CancellationToken ct, string? accessToken = null)
    {
        try
        {
            using var client = CreateClient(accessToken);
            using var content = JsonContent.Create(body, requestType);
            using var res = await client.PostAsync(_baseUrl + path, content, ct).ConfigureAwait(false);
            return await ToVoidResultAsync(res, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return CloudApiResult<CloudVoid>.NetworkError(ex.Message);
        }
    }

    private async Task<CloudApiResult<CloudVoid>> PutVoidAsync<TRequest>(
        string path, TRequest body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> requestType,
        CancellationToken ct, string? accessToken = null)
    {
        try
        {
            using var client = CreateClient(accessToken);
            using var content = JsonContent.Create(body, requestType);
            using var res = await client.PutAsync(_baseUrl + path, content, ct).ConfigureAwait(false);
            return await ToVoidResultAsync(res, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return CloudApiResult<CloudVoid>.NetworkError(ex.Message);
        }
    }

    private static async Task<CloudApiResult<CloudVoid>> ToVoidResultAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode)
        {
            return CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance, (int)res.StatusCode);
        }
        var (code, message, retryAt) = await ReadErrorAsync(res, ct).ConfigureAwait(false);
        return CloudApiResult<CloudVoid>.Fail((int)res.StatusCode, code, message, retryAt);
    }

    private static async Task<CloudApiResult<T>> ToResultAsync<T>(
        HttpResponseMessage res, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> responseType, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode)
        {
            var value = await ReadJsonAsync(res, responseType, ct).ConfigureAwait(false);
            return value is not null
                ? CloudApiResult<T>.Ok(value, (int)res.StatusCode)
                : CloudApiResult<T>.Fail((int)res.StatusCode, "invalid_response", "Empty or malformed response body.");
        }
        var (code, message, retryAt) = await ReadErrorAsync(res, ct).ConfigureAwait(false);
        return CloudApiResult<T>.Fail((int)res.StatusCode, code, message, retryAt);
    }

    private static async Task<T?> ReadJsonAsync<T>(
        HttpResponseMessage res, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> responseType, CancellationToken ct)
    {
        try
        {
            return await res.Content.ReadFromJsonAsync(responseType, ct).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            return default;
        }
    }

    private static async Task<(string? code, string? message, string? retryAt)> ReadErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var body = await res.Content.ReadFromJsonAsync(AppJsonContext.Default.CloudErrorBody, ct).ConfigureAwait(false);
            return (body?.Code, body?.Message, body?.RetryAt);
        }
        catch
        {
            return (null, null, null);
        }
    }
}
