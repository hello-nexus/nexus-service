using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Persistence;

namespace Nexus.Service.Store;

/// <summary>
/// Manages the cloud account link state: runs the device-grant polling
/// loop in the background and persists the resulting token to settings.
/// </summary>
public sealed class AccountLinkService : IDisposable
{
    private readonly CloudApiClient _cloud;
    private readonly IConfigStore _store;

    private readonly object _syncRoot = new();
    private CancellationTokenSource? _pollCts;
    private bool _pollRunning;

    public AccountLinkService(CloudApiClient cloud, IConfigStore store)
    {
        _cloud = cloud;
        _store = store;
    }

    /// <summary>True when a device-grant poll loop is active.</summary>
    public bool IsPollRunning
    {
        get
        {
            lock (_syncRoot)
            {
                return _pollRunning;
            }
        }
    }

    /// <summary>
    /// Starts the device-grant flow. Calls cloud /auth/device/start, then
    /// begins polling in the background. Returns null when the cloud is
    /// unreachable.
    /// </summary>
    public async Task<CloudDeviceGrantStartResponse?> StartLinkAsync(CancellationToken ct)
    {
        var grant = await _cloud.StartDeviceGrantAsync(ct).ConfigureAwait(false);
        if (grant is null)
        {
            return null;
        }

        CancelPoll();

        var cts = new CancellationTokenSource();
        lock (_syncRoot)
        {
            _pollCts = cts;
            _pollRunning = true;
        }

        _ = Task.Run(() => PollLoopAsync(grant, cts.Token), cts.Token);
        return grant;
    }

    /// <summary>Clear the stored token and cancel any active poll.</summary>
    public void Unlink()
    {
        CancelPoll();
        _store.Update(s => s.CloudAccount = new CloudAccountSettings());
    }

    public void Dispose()
    {
        CancelPoll();
    }

    private void CancelPoll()
    {
        CancellationTokenSource? old;
        lock (_syncRoot)
        {
            old = _pollCts;
            _pollCts = null;
            _pollRunning = false;
        }
        try { old?.Cancel(); } catch { /* best effort */ }
        old?.Dispose();
    }

    private async Task PollLoopAsync(CloudDeviceGrantStartResponse grant, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(grant.IntervalSec, 3));
        var expiry = DateTimeOffset.TryParse(grant.ExpiresAt, out var exp) ? exp : DateTimeOffset.UtcNow.AddMinutes(10);

        try
        {
            while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < expiry)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);

                var poll = await _cloud.PollDeviceGrantAsync(grant.DeviceCode, ct).ConfigureAwait(false);
                if (poll is null)
                {
                    continue;
                }

                if (poll.Status == "approved" && !string.IsNullOrEmpty(poll.AccessToken))
                {
                    _store.Update(s =>
                    {
                        s.CloudAccount.AccessToken = poll.AccessToken!;
                        s.CloudAccount.AccountEmail = poll.Account?.Email ?? "";
                        s.CloudAccount.AccountId = poll.Account?.Id ?? "";
                    });
                    break;
                }

                if (poll.Status is "expired" or "denied")
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[store] poll loop failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (_syncRoot)
            {
                if (!ct.IsCancellationRequested)
                {
                    _pollRunning = false;
                }
            }
        }
    }
}
