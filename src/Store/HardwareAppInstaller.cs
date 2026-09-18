using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.FocusModes;
using Nexus.Service.Models.Panel;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Nexus.Service.Widgets;

namespace Nexus.Service.Store;

/// <summary>
/// Installs an app whose hardware is attached, with no account required.
/// </summary>
/// <remarks>
/// A machine matching nothing in <see cref="HardwareAppCatalog"/> never touches
/// the network. A matched one still takes the entitled path when an account is
/// linked, so the purchase is recorded.
/// </remarks>
public sealed class HardwareAppInstaller : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>Retry cadence while the only outstanding step is placing the widget, which reads no network and waits on the kiosk registering its panel record.</summary>
    private static readonly TimeSpan PlacementRetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>Only size the Ina manifest declares.</summary>
    private const string InaWidgetSize = "4x4";

    private readonly IConfigStore _store;
    private readonly HardwareAppCatalog _hardware;
    private readonly StoreEntitlements _entitlements;
    private readonly StoreCatalogProxy _catalog;
    private readonly StoreInstaller _installer;
    private readonly AppRegistry _registry;
    private readonly PanelDeviceRegistry _panels;
    private readonly MultiplexHub _hub;

    public HardwareAppInstaller(
        IConfigStore store,
        HardwareAppCatalog hardware,
        StoreEntitlements entitlements,
        StoreCatalogProxy catalog,
        StoreInstaller installer,
        AppRegistry registry,
        PanelDeviceRegistry panels,
        MultiplexHub hub)
    {
        _store = store;
        _hardware = hardware;
        _entitlements = entitlements;
        _catalog = catalog;
        _installer = installer;
        _registry = registry;
        _panels = panels;
        _hub = hub;
    }

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
            {
                if (await TickAsync(stoppingToken).ConfigureAwait(false))
                    timer.Period = PlacementRetryInterval;
                else
                    timer.Period = Interval;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[store] hardware auto-install tick failed: {ex.GetType().Name}: {ex.Message}");
            }
        } while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        { return false; }
    }

    /// <summary>Returns true when an app is installed but still waiting on a panel record to be placed on.</summary>
    internal async Task<bool> TickAsync(CancellationToken ct)
    {
        var matched = _hardware.MatchedAppIds();
        if (matched.Count == 0) return false;

        var settings = _store.Load();
        var placementPending = false;
        foreach (var appId in matched)
        {
            if (settings.UserRemovedApps.Contains(appId)) continue;
            if (settings.AutoInstalledApps.Contains(appId)) continue;
            if (!await ReconcileAsync(appId, ct).ConfigureAwait(false) && IsUserInstalled(appId))
                placementPending = true;
        }
        return placementPending;
    }

    /// <summary>Brings one app to "installed and placed", recording the id only once both halves hold so an outage or an unregistered panel retries instead of being written off as done.</summary>
    private async Task<bool> ReconcileAsync(string appId, CancellationToken ct)
    {
        if (!IsUserInstalled(appId))
        {
            if (FocusNetworkGate.IsHeld) return false;
            if (!await InstallAsync(appId, ct).ConfigureAwait(false)) return false;
        }

        var placement = Y70WidgetPlacement.AlreadyPresent;
        var panelId = "";
        if (appId == HardwareAppCatalog.InaAppId)
        {
            placement = _panels.EnsureY70Widget($"app:{appId}", InaWidgetSize, out panelId);
            if (placement == Y70WidgetPlacement.NoPanel) return false;
            // A full panel still counts as settled: the app is installed and the
            // user can place it, and retrying cannot free a slot.
            if (placement == Y70WidgetPlacement.NoRoom)
                ServiceLog.Warn($"[store] {appId} installed but the panel has no free slot");
        }

        _store.Update(s =>
        {
            if (!s.AutoInstalledApps.Contains(appId)) s.AutoInstalledApps.Add(appId);
        });
        // A running kiosk PATCHes its whole layout back, so an append it has not
        // refetched is overwritten on the next edit.
        if (placement == Y70WidgetPlacement.Placed) PanelTopics.BroadcastPanelDevice(_hub, panelId);
        _registry.TryGet(appId, out var entry);
        PanelTopics.BroadcastAppAutoInstalled(_hub, new AppAutoInstalledFrame
        {
            AppId = appId,
            AppName = string.IsNullOrEmpty(entry?.Manifest.Name) ? appId : entry.Manifest.Name,
            Placed = placement == Y70WidgetPlacement.Placed,
        });
        ServiceLog.Info($"[store] auto-installed {appId} for attached hardware");
        return true;
    }

    /// <summary>A bundled build can already carry the same id, which is not the store copy this fetches and must not count as installed.</summary>
    private bool IsUserInstalled(string appId) =>
        _registry.TryGet(appId, out var entry) && entry.Source == AppInstallPaths.Source.User;

    /// <summary>Returns true when the app is on disk afterwards.</summary>
    private async Task<bool> InstallAsync(string appId, CancellationToken ct)
    {
        var request = await ResolveAsync(appId, ct).ConfigureAwait(false);
        if (request is null) return false;
        var result = await _installer.InstallAsync(request, ct).ConfigureAwait(false);
        if (!result.Ok)
            ServiceLog.Warn($"[store] auto-install of {appId} failed: {result.Reason}");
        return result.Ok;
    }

    /// <summary>What to install: the catalog names the version, then the entitled path is tried for it so a signed-in owner still gets the purchase recorded. Null when nothing resolved, so the next tick retries.</summary>
    private async Task<StoreInstallRequest?> ResolveAsync(string appId, CancellationToken ct)
    {
        var nexusVersion = BuildInfo.Version.TrimStart('v', 'V');
        var latest = await LatestAsync(appId, nexusVersion, ct).ConfigureAwait(false);
        if (latest is null) return null;

        var request = new StoreInstallRequest
        {
            AppId = appId,
            Version = latest.Version,
            Sha256 = latest.Sha256,
            Size = latest.Size,
        };

        var auth = await _entitlements.AuthorizeAsync(appId, latest.Version, nexusVersion, ct).ConfigureAwait(false);
        if (auth is { Ok: true, Grant: not null } && !string.IsNullOrWhiteSpace(auth.Grant.Sha256))
        {
            request.Sha256 = auth.Grant.Sha256;
            if (auth.Grant.Size > 0) request.Size = auth.Grant.Size;
            return request;
        }
        // Only a machine with no account at all is waived. A refused or expired
        // token reads sign_in_required too, and waiving that would silently drop
        // the entitlement record for a user who has one.
        return auth.Reason == "sign_in_required" && !_entitlements.HasLinkedAccount ? request : null;
    }

    /// <summary>Newest version this build can run, from the catalog route that takes no token and applies no storefront filter, so an unlisted app resolves.</summary>
    private async Task<StoreCatalogVersion?> LatestAsync(string appId, string nexusVersion, CancellationToken ct)
    {
        var body = await _catalog.DetailAsync(appId, nexusVersion, null, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(body)) return null;
        StoreCatalogApp? listing;
        try
        { listing = JsonSerializer.Deserialize(body, AppJsonContext.Default.StoreCatalogApp); }
        catch (JsonException)
        { return null; }

        var latest = listing?.Latest;
        if (latest is null) return null;
        if (string.IsNullOrWhiteSpace(latest.Sha256) || !StoreInstaller.IsValidVersion(latest.Version)) return null;
        return latest;
    }
}
