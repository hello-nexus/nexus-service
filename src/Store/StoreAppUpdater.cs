using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.FocusModes;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;
using Nexus.Service.Update;
using Nexus.Service.Widgets;

namespace Nexus.Service.Store;

/// <summary>
/// Keeps every store-installed app on the catalog's newest version this build can run.
/// </summary>
/// <remarks>
/// Uses the Install button's rules: the entitled download when an account grants it,
/// and no account needed for an app whose hardware is attached. Every app in the user
/// root is a candidate, including a copy placed there by hand: the catalog is the
/// source of truth for an id it knows. An app the catalog does not know, or one
/// installed newer than the catalog, is left alone.
/// </remarks>
public sealed class StoreAppUpdater : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly Func<IReadOnlyList<AppEntry>> _installed;
    private readonly Func<string, CancellationToken, Task<StoreCatalogVersion?>> _latest;
    private readonly Func<string, StoreCatalogVersion, CancellationToken, Task<StoreInstallRequest?>> _resolve;
    private readonly Func<StoreInstallRequest, CancellationToken, Task<StoreInstallResponse>> _install;
    private readonly Action _announce;

    public StoreAppUpdater(
        AppRegistry registry,
        StoreCatalogProxy catalog,
        StoreEntitlements entitlements,
        StoreInstaller installer,
        HardwareAppCatalog hardware,
        MultiplexHub hub)
        : this(
            () => registry.All().ToList(),
            (appId, ct) => StoreRelease.LatestAsync(catalog, appId, NexusVersion(), ct),
            (appId, version, ct) => StoreRelease.ResolveAsync(entitlements, appId, version, NexusVersion(), hardware.IsMatched(appId), ct),
            installer.UpdateAsync,
            () => PanelTopics.BroadcastAppsChanged(hub))
    {
    }

    /// <summary>Test seam: the registry snapshot, catalog read, entitlement resolve, install and announce.</summary>
    internal StoreAppUpdater(
        Func<IReadOnlyList<AppEntry>> installed,
        Func<string, CancellationToken, Task<StoreCatalogVersion?>> latest,
        Func<string, StoreCatalogVersion, CancellationToken, Task<StoreInstallRequest?>> resolve,
        Func<StoreInstallRequest, CancellationToken, Task<StoreInstallResponse>> install,
        Action announce)
    {
        _installed = installed;
        _latest = latest;
        _resolve = resolve;
        _install = install;
        _announce = announce;
    }

    private static string NexusVersion() => BuildInfo.Version.TrimStart('v', 'V');

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keeps StartAsync off the startup critical path so /ping answers.
        await Task.Yield();
        try
        { await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            { await TickAsync(stoppingToken).ConfigureAwait(false); }
            // An HttpClient timeout is an OperationCanceledException too; only shutdown ends the loop.
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            { ServiceLog.Error($"[store] app update check failed: {ex.GetType().Name}: {ex.Message}"); }
        } while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        { return false; }
    }

    /// <summary>Returns the ids updated this pass.</summary>
    internal async Task<IReadOnlyList<string>> TickAsync(CancellationToken ct)
    {
        var updated = new List<string>();
        // A snapshot: each install refreshes the registry under the loop.
        foreach (var entry in _installed())
        {
            // Focus mode holds the network; the next pass picks up what this one left.
            if (FocusNetworkGate.IsHeld) break;
            if (entry.Source != AppInstallPaths.Source.User) continue;
            var latest = await _latest(entry.Id, ct).ConfigureAwait(false);
            if (latest is null || !VersionCompare.IsNewer(latest.Version, entry.Manifest.Version)) continue;

            var request = await _resolve(entry.Id, latest, ct).ConfigureAwait(false);
            if (request is null)
            {
                ServiceLog.Info($"[store] {entry.Id} {latest.Version} is out but not installable here (no entitlement, or the store is unreachable); staying on {entry.Manifest.Version}");
                continue;
            }
            var result = await _install(request, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                ServiceLog.Warn($"[store] update of {entry.Id} to {latest.Version} failed: {result.Reason}");
                continue;
            }
            ServiceLog.Info($"[store] updated {entry.Id} {entry.Manifest.Version} -> {latest.Version}");
            updated.Add(entry.Id);
        }
        if (updated.Count > 0) _announce();
        return updated;
    }
}
