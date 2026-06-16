using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Store;

namespace Nexus.Service.Routes;

/// <summary>
/// Cloud store and account endpoints. All routes require panel auth.
/// The browser cannot reach the cloud directly (CSP connect-src 'self'),
/// so this service proxies account/library and drives the install pipeline.
/// </summary>
public static class StoreRoutes
{
    public static void MapStoreEndpoints(this WebApplication app)
    {
        // Account link

        app.MapPost("/apps-api/account/link/start",
            async (AccountLinkService link, CancellationToken ct) =>
        {
            var grant = await link.StartLinkAsync(ct).ConfigureAwait(false);
            if (grant is null)
            {
                return Results.Json(
                    new StoreAccountLinkStartResponse { UserCode = "", VerificationUri = "" },
                    AppJsonContext.Default.StoreAccountLinkStartResponse,
                    statusCode: 502);
            }
            var response = new StoreAccountLinkStartResponse
            {
                UserCode = grant.UserCode,
                VerificationUri = grant.VerificationUri,
                VerificationUriComplete = grant.VerificationUriComplete,
            };
            return Results.Json(response, AppJsonContext.Default.StoreAccountLinkStartResponse);
        }).AllowPanel();

        app.MapGet("/apps-api/account",
            (IConfigStore store, AccountLinkService link) =>
        {
            var settings = store.Load();
            var token = settings.CloudAccount.AccessToken;
            var linked = !string.IsNullOrEmpty(token);
            var response = new StoreAccountStatusResponse
            {
                Linked = linked,
                LinkPending = !linked && link.IsPollRunning,
                Account = linked
                    ? new StoreAccountInfo
                    {
                        Id = settings.CloudAccount.AccountId,
                        Email = settings.CloudAccount.AccountEmail,
                    }
                    : null,
            };
            return Results.Json(response, AppJsonContext.Default.StoreAccountStatusResponse);
        }).AllowPanel();

        app.MapPost("/apps-api/account/unlink",
            (AccountLinkService link) =>
        {
            link.Unlink();
            return Results.Json(
                new StoreAccountStatusResponse { Linked = false },
                AppJsonContext.Default.StoreAccountStatusResponse);
        }).AllowPanel();

        // Cloud proxy

        app.MapGet("/apps-api/account/library",
            async (IConfigStore store, CloudApiClient cloud, CancellationToken ct) =>
        {
            var settings = store.Load();
            var token = settings.CloudAccount.AccessToken;
            if (string.IsNullOrEmpty(token))
            {
                return Results.Json(
                    new CloudLibraryResponse(),
                    AppJsonContext.Default.CloudLibraryResponse);
            }
            var library = await cloud.GetLibraryAsync(token, ct).ConfigureAwait(false);
            return Results.Json(
                library ?? new CloudLibraryResponse(),
                AppJsonContext.Default.CloudLibraryResponse);
        }).AllowPanel();

        // Install pipeline

        app.MapPost("/apps-api/install",
            async (StoreInstallRequest body, CloudStoreInstaller installer, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.AppId))
            {
                return Results.Json(
                    new StoreInstallResponse { Ok = false, Reason = "invalid_app_id" },
                    AppJsonContext.Default.StoreInstallResponse,
                    statusCode: 400);
            }
            var result = await installer.InstallAsync(body.AppId, ct).ConfigureAwait(false);
            return Results.Json(result, AppJsonContext.Default.StoreInstallResponse);
        }).AllowPanel();

        app.MapDelete("/apps-api/install/{appId}",
            async (string appId, CloudStoreInstaller installer, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(appId))
            {
                return Results.Json(
                    new StoreInstallResponse { Ok = false, Reason = "invalid_app_id" },
                    AppJsonContext.Default.StoreInstallResponse,
                    statusCode: 400);
            }
            var result = await installer.UninstallAsync(appId, ct).ConfigureAwait(false);
            return Results.Json(result, AppJsonContext.Default.StoreInstallResponse);
        }).AllowPanel();
    }
}
