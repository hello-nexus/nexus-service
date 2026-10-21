using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// Runs the Plus V2 idle refresh on the hub's own deadline. It blocks on a gate with no
/// periodic wake until a Plus V2 is connected, and an exception costs one iteration, not the loop.
/// </summary>
internal sealed class KrakenIdleRefresher
{
    private const long WarnIntervalMs = 60_000;

    private readonly Func<int> _delayMs;
    private readonly Action _refresh;
    private readonly int _faultBackoffMs;
    private volatile TaskCompletionSource _gate = NewGate();

    public KrakenIdleRefresher(Func<int> delayMs, Action refresh, int faultBackoffMs = 1000)
    {
        _faultBackoffMs = faultBackoffMs;
        _delayMs = delayMs;
        _refresh = refresh;
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Opens the gate while a Plus V2 is connected; closes it otherwise.</summary>
    public void SetActive(bool active)
    {
        if (active)
        {
            _gate.TrySetResult();
        }
        else if (_gate.Task.IsCompleted)
        {
            _gate = NewGate();
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        long lastWarnAt = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await Task.Delay(_delayMs(), ct).ConfigureAwait(false);
                    _refresh();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    long now = Environment.TickCount64;
                    if (lastWarnAt == 0 || now - lastWarnAt >= WarnIntervalMs)
                    {
                        lastWarnAt = now;
                        ServiceLog.Warn($"[nzxt-kraken] idle refresh failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    await Task.Delay(_faultBackoffMs, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
