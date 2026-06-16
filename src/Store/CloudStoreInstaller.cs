using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Persistence;
using Nexus.Service.Telemetry;
using Nexus.Service.Widgets;

namespace Nexus.Service.Store;

/// <summary>
/// Install/uninstall pipeline for cloud-sourced .nexus-app bundles.
/// Downloads, extracts, registers, and reports to the cloud.
/// </summary>
public sealed class CloudStoreInstaller
{
    private readonly CloudApiClient _cloud;
    private readonly IConfigStore _store;
    private readonly AppRegistry _registry;
    private readonly Func<IReadOnlyList<AppInstallPaths.Root>> _rootsProvider;

    public CloudStoreInstaller(CloudApiClient cloud, IConfigStore store, AppRegistry registry)
        : this(cloud, store, registry, () => AppInstallPaths.Enumerate())
    {
    }

    /// <summary>Test seam: lets unit tests supply custom install roots.</summary>
    public CloudStoreInstaller(CloudApiClient cloud, IConfigStore store, AppRegistry registry,
        Func<IReadOnlyList<AppInstallPaths.Root>> rootsProvider)
    {
        _cloud = cloud;
        _store = store;
        _registry = registry;
        _rootsProvider = rootsProvider;
    }

    /// <summary>
    /// Full install pipeline: acquire entitlement, get download URL, download+extract,
    /// refresh registry, report to cloud. Returns ok=false with reason when any
    /// step fails cleanly (artifact_unavailable, not_linked, acquire_failed).
    /// </summary>
    public async Task<StoreInstallResponse> InstallAsync(string appId, CancellationToken ct)
    {
        if (!AppIds.IsValid(appId))
        {
            return new StoreInstallResponse { AppId = appId, Ok = false, Reason = "invalid_app_id" };
        }

        var settings = _store.Load();
        var token = settings.CloudAccount.AccessToken;
        if (string.IsNullOrEmpty(token))
        {
            return new StoreInstallResponse { AppId = appId, Ok = false, Reason = "not_linked" };
        }

        var acquired = await _cloud.AcquireAppAsync(token, appId, ct).ConfigureAwait(false);
        if (!acquired)
        {
            return new StoreInstallResponse { AppId = appId, Ok = false, Reason = "acquire_failed" };
        }

        var info = await _cloud.GetDownloadInfoAsync(token, appId, ct).ConfigureAwait(false);
        if (info is null || string.IsNullOrEmpty(info.Url))
        {
            return new StoreInstallResponse { AppId = appId, Ok = false, Reason = "artifact_unavailable" };
        }

        var userRoot = ResolveUserRoot();
        if (userRoot is null)
        {
            return new StoreInstallResponse { AppId = appId, Ok = false, Reason = "no_user_apps_dir" };
        }

        Directory.CreateDirectory(userRoot);
        var destDir = Path.Combine(userRoot, appId);

        var (ok, reason) = await _cloud.DownloadAndExtractAsync(info.Url, destDir, ct).ConfigureAwait(false);
        if (!ok)
        {
            return new StoreInstallResponse { AppId = appId, Ok = false, Reason = reason ?? "artifact_unavailable" };
        }

        _registry.Refresh();

        var installId = InstallIdentity.Resolve(_store) ?? "";
        var deviceLabel = Environment.MachineName;
        _ = _cloud.ReportInstallAsync(token, new CloudInstallReport
        {
            AppId = appId,
            InstallId = installId,
            Version = info.Version,
            DeviceLabel = deviceLabel,
        }, CancellationToken.None);

        return new StoreInstallResponse { AppId = appId, Ok = true };
    }

    /// <summary>
    /// Remove the user-installed app dir and report to the cloud.
    /// </summary>
    public async Task<StoreInstallResponse> UninstallAsync(string appId, CancellationToken ct)
    {
        if (!AppIds.IsValid(appId))
        {
            return new StoreInstallResponse { AppId = appId, Ok = false, Reason = "invalid_app_id" };
        }

        var userRoot = ResolveUserRoot();
        if (userRoot is null)
        {
            return new StoreInstallResponse { AppId = appId, Ok = false, Reason = "no_user_apps_dir" };
        }

        var appDir = Path.Combine(userRoot, appId);
        if (Directory.Exists(appDir))
        {
            try
            {
                Directory.Delete(appDir, recursive: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[store] uninstall delete failed: {ex.Message}");
                return new StoreInstallResponse { AppId = appId, Ok = false, Reason = "delete_failed" };
            }
        }

        _registry.Refresh();

        var settings = _store.Load();
        var token = settings.CloudAccount.AccessToken;
        if (!string.IsNullOrEmpty(token))
        {
            var installId = InstallIdentity.Resolve(_store) ?? "";
            _ = _cloud.ReportUninstallAsync(token, appId, installId, CancellationToken.None);
        }

        return new StoreInstallResponse { AppId = appId, Ok = true };
    }

    private string? ResolveUserRoot()
    {
        foreach (var root in _rootsProvider())
        {
            if (root.Source == AppInstallPaths.Source.User)
            {
                return root.Path;
            }
        }
        return null;
    }
}
