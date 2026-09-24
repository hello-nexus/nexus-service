using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Peripherals.LianLiTl;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting;

/// <summary>
/// Commits the Uni Fan TL controller's firmware animation whenever the saved look
/// changes. The controller has no per-LED path, so there is nothing to stream: one
/// command per fan (or per declared group) sets a whole look the firmware then
/// animates on its own.
/// </summary>
public sealed class TlLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 500;

    private readonly TlFanHub _hub;
    private readonly IConfigStore _store;
    private readonly FeatureGates _gates;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Last look committed to hardware, keyed to the connection it was committed on;
    // a reconnect leaves the controller with no look, so the generation takes part.
    private string? _lastSig;

    public TlLightingFrameWriter(TlFanHub hub, IConfigStore store, FeatureGates? gates = null)
    {
        _hub = hub;
        _store = store;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[lianli-tl] lighting error: {ex.Message}");
            }

            try
            {
                await Task.Delay(TickPeriodMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Tick()
    {
        if (!_gates.Lighting)
        {
            return;
        }

        if (!_hub.IsConnected)
        {
            // A disconnected controller forgets its look, so the next connect must
            // re-commit even when nothing in settings moved.
            _lastSig = null;
            return;
        }

        var snap = _hub.Snapshot;
        if (snap.ChannelCount == 0)
        {
            return;
        }

        var ls = _store.Load().Devices.TlLighting;
        var sig = ComputeSig(ls, snap, _hub.ConnectGeneration);
        if (_lastSig is not null && string.Equals(_lastSig, sig, StringComparison.Ordinal))
        {
            return;
        }

        if (!Commit(ls, snap))
        {
            ServiceLog.Warn("[lianli-tl] lighting commit incomplete; one or more writes failed");
        }
        _lastSig = sig;
    }

    private bool Commit(TlLightingSettings ls, TlHubSnapshot snap)
    {
        var mode = TlLightingModes.Find(ls.Mode);
        var colors = PackColors(ls, out int colorCount);
        var scope = NormalizeScope(ls.Scope);


        bool allOk = true;
        if (scope == "all")
        {
            for (int i = 0; i < snap.ChannelCount; i++)
            {
                allOk &= _hub.SetFanLight(
                    snap.Port[i], snap.FanIndex[i], mode.ModeByte,
                    ls.Brightness, ls.Speed, ls.Direction, colors, colorCount, mode.IsOff);
            }
            return allOk;
        }

        bool topHalf = scope == "top";
        var off = TlLightingModes.Find(TlLightingModes.OffKey);
        for (int port = 0; port < snap.PortFanCounts.Length; port++)
        {
            if (snap.PortFanCounts[port] <= 0)
            {
                continue;
            }
            int lit = topHalf ? TlFanProtocol.TopGroup(port) : TlFanProtocol.BottomGroup(port);
            int dark = topHalf ? TlFanProtocol.BottomGroup(port) : TlFanProtocol.TopGroup(port);
            allOk &= _hub.SetGroupLight(
                lit, mode.ModeByte, ls.Brightness, ls.Speed, ls.Direction, colors, colorCount, mode.IsOff);
            allOk &= _hub.SetGroupLight(
                dark, off.ModeByte, 0, 0, 0, colors, 0, disabled: true);
        }
        return allOk;
    }

    /// <summary>Up to four colours packed R,G,B - the wire order this controller takes.</summary>
    private static byte[] PackColors(TlLightingSettings ls, out int colorCount)
    {
        int count = ls.Colors is null ? 0 : ls.Colors.Count;
        if (count > TlLightingModes.MaxColors)
        {
            count = TlLightingModes.MaxColors;
        }
        var packed = new byte[TlLightingModes.MaxColors * 3];
        for (int i = 0; i < count; i++)
        {
            var (r, g, b) = ParseHexColor(ls.Colors![i]);
            packed[i * 3] = r;
            packed[i * 3 + 1] = g;
            packed[i * 3 + 2] = b;
        }
        colorCount = count;
        return packed;
    }

    private static string NormalizeScope(string? scope) => scope switch
    {
        "top" => "top",
        "bottom" => "bottom",
        _ => "all",
    };

    private static string ComputeSig(TlLightingSettings ls, TlHubSnapshot snap, int generation)
    {
        var colors = ls.Colors is null ? "" : string.Join(',', ls.Colors);
        var topology = string.Join('/', snap.PortFanCounts);
        return $"{generation}|{ls.Mode}|{ls.Speed}|{ls.Direction}|{ls.Brightness}|{NormalizeScope(ls.Scope)}|{colors}|{topology}";
    }

    private static (byte r, byte g, byte b) ParseHexColor(string hex)
    {
        var s = (hex ?? "").TrimStart('#');
        if (s.Length < 6)
        {
            return (0, 0, 0);
        }
        if (!byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, null, out var r)) { r = 0; }
        if (!byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, null, out var g)) { g = 0; }
        if (!byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, null, out var b)) { b = 0; }
        return (r, g, b);
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}
