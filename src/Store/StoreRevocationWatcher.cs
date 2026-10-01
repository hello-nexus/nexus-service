using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Panel;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Nexus.Service.Widgets;

namespace Nexus.Service.Store;

/// <summary>
/// The store's kill switch: uninstalls any store-installed app whose exact version
/// the cloud has revoked. Bundled apps are never touched.
/// </summary>
public sealed class StoreRevocationWatcher : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly Func<CancellationToken, Task<string?>> _revoked;
    private readonly Func<IReadOnlyList<AppEntry>> _installed;
    private readonly Func<string, CancellationToken, Task<bool>> _uninstall;
    private readonly Action _announce;

    public StoreRevocationWatcher(
        StoreCatalogProxy catalog,
        AppRegistry registry,
        AppInstaller installer,
        StoreInstaller storeInstaller,
        PanelDeviceRegistry panels,
        MultiplexHub hub)
        : this(
            catalog.RevokedAsync,
            () => registry.All().ToList(),
            (id, ct) => storeInstaller.WithAppGateAsync(id, () =>
            {
                if (installer.Uninstall(id).Error is not null) return false;
                // Same as the uninstall route: placements die only when no bundled copy remains.
                if (!registry.TryGet(id, out _))
                {
                    foreach (var deviceId in panels.RemoveWidgetType(WidgetSettingsService.AppTypePrefix + id))
                        PanelTopics.BroadcastPanelDevice(hub, deviceId);
                }
                return true;
            }, ct),
            () => PanelTopics.BroadcastAppsChanged(hub))
    {
    }

    /// <summary>Test seam: the revocation read, registry snapshot, uninstall and announce.</summary>
    internal StoreRevocationWatcher(
        Func<CancellationToken, Task<string?>> revoked,
        Func<IReadOnlyList<AppEntry>> installed,
        Func<string, CancellationToken, Task<bool>> uninstall,
        Action announce)
    {
        _revoked = revoked;
        _installed = installed;
        _uninstall = uninstall;
        _announce = announce;
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
            { await TickAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            { ServiceLog.Error($"[store] revocation check failed: {ex.GetType().Name}: {ex.Message}"); }
        } while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        { return false; }
    }

    /// <summary>Returns the ids uninstalled this pass.</summary>
    internal async Task<IReadOnlyList<string>> TickAsync(CancellationToken ct)
    {
        var removed = new List<string>();
        var body = await _revoked(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(body)) return removed;

        StoreRevokedResponse? parsed;
        try
        { parsed = JsonSerializer.Deserialize(body, AppJsonContext.Default.StoreRevokedResponse); }
        catch (JsonException)
        { return removed; }
        if (parsed is null || parsed.Revoked.Count == 0) return removed;

        var revoked = new HashSet<(string, string)>(parsed.Revoked.Select(r => (r.AppId, r.Version)));
        foreach (var entry in _installed())
        {
            if (entry.Source != AppInstallPaths.Source.User) continue;
            if (!revoked.Contains((entry.Id, entry.Manifest.Version))) continue;
            if (!await _uninstall(entry.Id, ct).ConfigureAwait(false))
            {
                ServiceLog.Error($"[store] {entry.Id} {entry.Manifest.Version} is revoked but could not be uninstalled");
                continue;
            }
            ServiceLog.Warn($"[store] uninstalled {entry.Id} {entry.Manifest.Version}: revoked by the store");
            removed.Add(entry.Id);
        }
        if (removed.Count > 0) _announce();
        return removed;
    }
}
