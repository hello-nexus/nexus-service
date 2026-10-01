using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Telemetry;

namespace Nexus.Service.Cloud;

/// <summary>
/// Reports this machine's specs to the active cloud account (PUT
/// /account/devices/{installId}) right after login/switch, then rechecks on
/// a slow cadence and only re-sends when the spec snapshot actually changed
/// (hash compare) - specs don't change while the process is alive, so this
/// is normally a single PUT per login. Same warmup/steady cadence shape as
/// SystemProfileService, which does the equivalent for anonymous telemetry.
/// </summary>
public sealed class CloudDeviceReporter : BackgroundService
{
    private static readonly TimeSpan WarmupInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SteadyInterval = TimeSpan.FromMinutes(30);

    private readonly ICloudApiClient _api;
    private readonly CloudAccountService _accounts;
    private readonly SystemSpecsCollector _specs;
    private readonly IConfigStore _store;
    private readonly TimeProvider _clock;
    private readonly SystemCaseService? _caseSync;

    private volatile string? _lastReportedHash;
    private volatile string? _lastReportedAccountId;

    public CloudDeviceReporter(ICloudApiClient api, CloudAccountService accounts, SystemSpecsCollector specs, IConfigStore store, SystemCaseService caseSync)
        : this(api, accounts, specs, store, TimeProvider.System, caseSync)
    {
    }

    internal CloudDeviceReporter(ICloudApiClient api, CloudAccountService accounts, SystemSpecsCollector specs, IConfigStore store, TimeProvider clock, SystemCaseService? caseSync = null)
    {
        _api = api;
        _accounts = accounts;
        _specs = specs;
        _store = store;
        _clock = clock;
        _caseSync = caseSync;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _accounts.OnAccountActivated += accountId =>
        {
            // A new activation always reports, even if the spec hash is
            // unchanged from a previous account's report.
            _lastReportedHash = null;
            // Task.Run hands off to the thread pool so the firing login/activate
            // route never runs any part of this synchronously on its own thread.
            _ = Task.Run(() => ReportSafeAsync(accountId, CancellationToken.None));
        };

        var delay = WarmupInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (Nexus.Service.FocusModes.FocusNetworkGate.IsHeld)
            {
                delay = SteadyInterval;
            }
            else if (_accounts.ActiveAccountId is { } accountId)
            {
                var ready = await ReportSafeAsync(accountId, stoppingToken).ConfigureAwait(false);
                delay = ready ? SteadyInterval : WarmupInterval;
            }
            else
            {
                delay = SteadyInterval;
            }
        }
    }

    private async Task<bool> ReportSafeAsync(string accountId, CancellationToken ct)
    {
        try
        {
            return await ReportAsync(accountId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"[cloud-device] report failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <returns>True once a ready snapshot was sent or skipped as unchanged; false to retry sooner (specs collector still warming up).</returns>
    private async Task<bool> ReportAsync(string accountId, CancellationToken ct)
    {
        if (accountId != _accounts.ActiveAccountId)
        {
            return true;
        }

        var specs = await _specs.GetAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(specs.Processor))
        {
            return false;
        }

        var installId = _accounts.ResolveStableInstallId();
        var specsMap = ToSpecsMap(specs);
        var hash = HashSpecs(specsMap);
        if (hash == _lastReportedHash && accountId == _lastReportedAccountId)
        {
            await FlushCaseAsync(accountId, ct).ConfigureAwait(false);
            return true;
        }

        var settings = _store.Load();
        var request = new CloudDevicePutRequest
        {
            Hostname = string.IsNullOrWhiteSpace(settings.HostDisplayName) ? SafeMachineName() : settings.HostDisplayName,
            Specs = specsMap,
            AppVersion = BuildInfo.Version,
            Os = TelemetryPlatform.OsTag(),
        };

        var result = await _accounts.WithAuthAsync(accountId, token => _api.PutDeviceAsync(token, installId, request, ct), ct).ConfigureAwait(false);
        if (result.Success)
        {
            _lastReportedHash = hash;
            _lastReportedAccountId = accountId;
            await FlushCaseAsync(accountId, ct).ConfigureAwait(false);
        }
        return true;
    }

    // The device row now exists on the account, so a case pick that 404'd
    // before it (or failed offline) can go up.
    private async Task FlushCaseAsync(string accountId, CancellationToken ct)
    {
        if (_caseSync is not null)
        {
            await _caseSync.FlushPendingAsync(accountId, ct).ConfigureAwait(false);
        }
    }

    internal static Dictionary<string, string> ToSpecsMap(Nexus.Service.Models.Sensors.SystemSpecsResponse specs) => new()
    {
        ["pcName"] = specs.PcName,
        ["osBuild"] = specs.OsBuild,
        ["processor"] = specs.Processor,
        ["motherboard"] = specs.Motherboard,
        ["memory"] = specs.Memory,
        ["storage"] = specs.Storage,
        ["graphicsCard"] = specs.GraphicsCard,
        ["monitor"] = specs.Monitor,
        ["soundCard"] = specs.SoundCard,
        ["networkCard"] = specs.NetworkCard,
    };

    internal static string HashSpecs(Dictionary<string, string> specs)
    {
        var sb = new StringBuilder();
        foreach (var key in specs.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            sb.Append(key).Append('=').Append(specs[key]).Append('\u001f');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string SafeMachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch
        {
            return "";
        }
    }
}
