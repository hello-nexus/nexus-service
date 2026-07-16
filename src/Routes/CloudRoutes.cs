using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nexus.Service.Cloud;
using Nexus.Service.Models;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// Local /cloud/... surface for the dashboard's account UI. Desktop-bearer
/// only (no .AllowPanel()) - cloud credentials/tokens never reach a paired
/// phone session. CloudAccountService/CloudProfileSyncService hold the
/// actual tokens; these routes are thin proxies plus local session/sync
/// state.
/// </summary>
public static class CloudRoutes
{
    private const long MaxAvatarBytes = 5 * 1024 * 1024;
    private static readonly string[] AllowedAvatarContentTypes = { "image/png", "image/jpeg", "image/webp" };
    private const long MaxBenchmarkSubmitBytes = 256 * 1024;
    private const long MaxDevicePutBytes = 64 * 1024;

    public static void MapCloudEndpoints(this WebApplication app)
    {
        app.MapGet("/cloud/accounts", (CloudAccountService accounts) =>
        {
            return Results.Json(new CloudAccountsResponse
            {
                Accounts = accounts.ListAccounts(),
                ActiveAccountId = accounts.ActiveAccountId,
            }, AppJsonContext.Default.CloudAccountsResponse);
        });

        app.MapPost("/cloud/register", async (CloudRegisterBody body, CloudAccountService accounts, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password) || string.IsNullOrWhiteSpace(body.Username))
            {
                return Results.BadRequest(ApiResponse.Fail("email, password, and username are required."));
            }
            var result = await accounts.RegisterAsync(body.Email, body.Password, body.Username, ct).ConfigureAwait(false);
            return CloudResult(result);
        });

        app.MapPost("/cloud/login", async (CloudLoginBody body, CloudAccountService accounts, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Identifier) || string.IsNullOrWhiteSpace(body.Password))
            {
                return Results.BadRequest(ApiResponse.Fail("identifier and password are required."));
            }
            var (result, accountId) = await accounts.LoginAsync(body.Identifier, body.Password, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return CloudResult(result);
            }
            var account = accountId is null ? null : FindAccount(accounts, accountId);
            return Results.Json(new CloudLoginResponse { Account = account }, AppJsonContext.Default.CloudLoginResponse);
        });

        app.MapPost("/cloud/logout", async (CloudLogoutBody body, CloudAccountService accounts, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.AccountId))
            {
                return Results.BadRequest(ApiResponse.Fail("accountId is required."));
            }
            await accounts.LogoutAsync(body.AccountId, ct).ConfigureAwait(false);
            return Results.Ok(ApiResponse.Ok());
        });

        app.MapPost("/cloud/accounts/{accountId}/activate", (string accountId, CloudAccountService accounts) =>
        {
            var result = accounts.Activate(accountId);
            return CloudResult(result);
        });

        app.MapPost("/cloud/recovery/start", async (CloudRecoveryStartBody body, CloudAccountService accounts, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Email))
            {
                return Results.BadRequest(ApiResponse.Fail("email is required."));
            }
            var result = await accounts.StartRecoveryAsync(body.Email, ct).ConfigureAwait(false);
            return CloudResult(result);
        });

        app.MapGet("/cloud/recovery/status", (CloudAccountService accounts) =>
        {
            var status = accounts.GetRecoveryStatus();
            return Results.Json(new CloudRecoveryStatusResponse
            {
                Status = status.Status,
                RecoveryFresh = status.RecoveryFresh,
            }, AppJsonContext.Default.CloudRecoveryStatusResponse);
        });

        app.MapPost("/cloud/password", async (CloudPasswordBody body, CloudAccountService accounts, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.NewPassword))
            {
                return Results.BadRequest(ApiResponse.Fail("newPassword is required."));
            }
            var result = await accounts.ChangePasswordAsync(body.CurrentPassword, body.NewPassword, ct).ConfigureAwait(false);
            return CloudResult(result);
        });

        app.MapPost("/cloud/username", async (CloudUsernameBody body, CloudAccountService accounts, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Username))
            {
                return Results.BadRequest(ApiResponse.Fail("username is required."));
            }
            var result = await accounts.ChangeUsernameAsync(body.Username, ct).ConfigureAwait(false);
            return CloudResult(result);
        });

        app.MapPatch("/cloud/account", async (CloudSetPrivateBody body, CloudAccountService accounts, CancellationToken ct) =>
        {
            var result = await accounts.SetPrivateAsync(body.IsPrivate, ct).ConfigureAwait(false);
            return CloudResult(result);
        });

        // DELETE bodies are not implicitly inferred by minimal APIs (unlike
        // POST/PUT/PATCH) - [FromBody] opts in explicitly. An unmarked complex
        // parameter here throws InvalidOperationException at route-registration
        // time, taking the whole service down.
        app.MapDelete("/cloud/account", async ([FromBody] CloudDeleteAccountBody? body, CloudAccountService accounts, CancellationToken ct) =>
        {
            var result = await accounts.DeleteAccountAsync(body?.CurrentPassword, ct).ConfigureAwait(false);
            return CloudResult(result);
        });

        app.MapPost("/cloud/avatar", async (HttpRequest req, CloudAccountService accounts, CancellationToken ct) =>
        {
            var contentType = req.ContentType ?? "";
            if (Array.IndexOf(AllowedAvatarContentTypes, contentType) < 0)
            {
                return Results.BadRequest(ApiResponse.Fail("Unsupported content type. Use image/png, image/jpeg, or image/webp."));
            }
            if (req.ContentLength is long declared && declared > MaxAvatarBytes)
            {
                return Results.BadRequest(ApiResponse.Fail("Image too large (max 5MB)."));
            }

            var (bytes, tooLarge) = await ReadBoundedAsync(req.Body, MaxAvatarBytes, ct).ConfigureAwait(false);
            if (tooLarge)
            {
                return Results.BadRequest(ApiResponse.Fail("Image too large (max 5MB)."));
            }
            if (bytes is null || bytes.Length == 0)
            {
                return Results.BadRequest(ApiResponse.Fail("Empty upload."));
            }

            var result = await accounts.UploadAvatarAsync(bytes, contentType, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return CloudApiFailure(result.StatusCode, result.ErrorCode, result.ErrorMessage, result.Offline);
            }
            return Results.Json(new CloudAvatarResponse { Avatar = result.Value }, AppJsonContext.Default.CloudAvatarResponse);
        });

        app.MapGet("/cloud/sync/status", (CloudProfileSyncService sync) =>
            Results.Json(sync.GetStatus(), AppJsonContext.Default.CloudSyncStatusResponse));

        app.MapPost("/cloud/sync/now", (CloudProfileSyncService sync) =>
        {
            sync.TriggerNow();
            return Results.Ok(ApiResponse.Ok());
        });

        app.MapPost("/cloud/sync/resolve", async (CloudSyncResolveBody body, CloudProfileSyncService sync, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.ProfileId) || string.IsNullOrWhiteSpace(body.Choice))
            {
                return Results.BadRequest(ApiResponse.Fail("profileId and choice are required."));
            }
            var result = await sync.ResolveConflictAsync(body.ProfileId, body.Choice, ct).ConfigureAwait(false);
            return CloudResult(result);
        });

        // Thin forwarder: the dashboard posts the benchmark result JSON as-is
        // (no local DTO) to the cloud leaderboard, authenticated with the
        // active account's bearer when signed in, anonymous when signed out.
        // The upstream status/body are relayed verbatim either way.
        app.MapPost("/cloud/benchmarks/submit", async (HttpRequest req, CloudAccountService accounts, ICloudApiClient api, CancellationToken ct) =>
        {
            var (bytes, tooLarge) = await ReadBoundedAsync(req.Body, MaxBenchmarkSubmitBytes, ct).ConfigureAwait(false);
            if (tooLarge)
            {
                return Results.BadRequest(ApiResponse.Fail("Benchmark submission too large."));
            }
            var rawJson = bytes is null || bytes.Length == 0 ? "{}" : Encoding.UTF8.GetString(bytes);

            var accountId = accounts.ActiveAccountId;
            var result = accountId is null
                ? await api.PostRawAsync("/benchmarks/submit", rawJson, null, ct).ConfigureAwait(false)
                : await accounts.WithAuthAsync(accountId, token => api.PostRawAsync("/benchmarks/submit", rawJson, token, ct), ct).ConfigureAwait(false);

            if (!result.Success)
            {
                return CloudApiFailure(result.StatusCode, result.ErrorCode, result.ErrorMessage, result.Offline);
            }
            return Results.Text(result.Value!.Body, result.Value.ContentType, statusCode: result.StatusCode);
        });

        // Thin forwarders for the dashboard's device-management UI: same raw
        // passthrough shape as /cloud/benchmarks/submit, no local DTO, bearer
        // attached when signed in.
        app.MapGet("/cloud/account/devices", async (CloudAccountService accounts, ICloudApiClient api, CancellationToken ct) =>
        {
            var result = await ForwardDeviceRequestAsync(accounts, api, HttpMethod.Get, "/account/devices", null, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return CloudApiFailure(result.StatusCode, result.ErrorCode, result.ErrorMessage, result.Offline);
            }
            return Results.Text(result.Value!.Body, result.Value.ContentType, statusCode: result.StatusCode);
        });

        app.MapPut("/cloud/account/devices/{installId}", async (string installId, HttpRequest req, CloudAccountService accounts, ICloudApiClient api, CancellationToken ct) =>
        {
            var (bytes, tooLarge) = await ReadBoundedAsync(req.Body, MaxDevicePutBytes, ct).ConfigureAwait(false);
            if (tooLarge)
            {
                return Results.BadRequest(ApiResponse.Fail("Device update too large."));
            }
            var rawJson = bytes is null || bytes.Length == 0 ? "{}" : Encoding.UTF8.GetString(bytes);
            var path = "/account/devices/" + Uri.EscapeDataString(installId);

            var result = await ForwardDeviceRequestAsync(accounts, api, HttpMethod.Put, path, rawJson, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return CloudApiFailure(result.StatusCode, result.ErrorCode, result.ErrorMessage, result.Offline);
            }
            return Results.Text(result.Value!.Body, result.Value.ContentType, statusCode: result.StatusCode);
        });

        app.MapDelete("/cloud/account/devices/{installId}", async (string installId, CloudAccountService accounts, ICloudApiClient api, CancellationToken ct) =>
        {
            var path = "/account/devices/" + Uri.EscapeDataString(installId);
            var result = await ForwardDeviceRequestAsync(accounts, api, HttpMethod.Delete, path, null, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return CloudApiFailure(result.StatusCode, result.ErrorCode, result.ErrorMessage, result.Offline);
            }
            return Results.Text(result.Value!.Body, result.Value.ContentType, statusCode: result.StatusCode);
        });
    }

    /// <summary>Attaches the active account's bearer when signed in; forwards anonymously otherwise, leaving upstream to reject with its own status.</summary>
    private static Task<CloudApiResult<CloudRawResponse>> ForwardDeviceRequestAsync(
        CloudAccountService accounts, ICloudApiClient api, HttpMethod method, string path, string? rawJsonBody, CancellationToken ct)
    {
        var accountId = accounts.ActiveAccountId;
        return accountId is null
            ? api.SendRawAsync(method, path, rawJsonBody, null, ct)
            : accounts.WithAuthAsync(accountId, token => api.SendRawAsync(method, path, rawJsonBody, token, ct), ct);
    }

    /// <summary>Reads a request body up to maxBytes, checking the running total after every chunk so a chunked upload (no Content-Length) never buffers unbounded memory before the size check runs.</summary>
    private static async Task<(byte[]? Bytes, bool TooLarge)> ReadBoundedAsync(Stream source, long maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                return (null, true);
            }
            await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return (ms.ToArray(), false);
    }

    private static CloudAccountSummaryDto? FindAccount(CloudAccountService accounts, string accountId)
    {
        foreach (var a in accounts.ListAccounts())
        {
            if (a.AccountId == accountId)
            {
                return a;
            }
        }
        return null;
    }

    private static IResult CloudResult(CloudActionResult result)
    {
        if (result.Success)
        {
            return Results.Ok(ApiResponse.Ok());
        }
        return CloudApiFailure(result.StatusCode, result.ErrorCode, result.ErrorMessage, result.Offline, result.ErrorRetryAt);
    }

    private static IResult CloudResult<T>(CloudApiResult<T> result)
    {
        if (result.Success)
        {
            return Results.Ok(ApiResponse.Ok());
        }
        return CloudApiFailure(result.StatusCode, result.ErrorCode, result.ErrorMessage, result.Offline, result.ErrorRetryAt);
    }

    private static IResult CloudApiFailure(int statusCode, string? errorCode, string? errorMessage, bool offline, string? retryAt = null)
    {
        var msg = offline
            ? "Could not reach the Nexus cloud."
            : !string.IsNullOrEmpty(errorCode) ? errorCode
            : !string.IsNullOrEmpty(errorMessage) ? errorMessage
            : "Request failed.";
        var status = statusCode is >= 400 and < 600 ? statusCode : StatusCodes.Status502BadGateway;
        if (!string.IsNullOrEmpty(retryAt))
        {
            return Results.Json(new CloudFailureResponse { Error = true, Msg = msg, RetryAt = retryAt }, AppJsonContext.Default.CloudFailureResponse, statusCode: status);
        }
        return Results.Json(ApiResponse.Fail(msg), AppJsonContext.Default.ApiResponse, statusCode: status);
    }
}
