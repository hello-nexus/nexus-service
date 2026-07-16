using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Reads the keeb's interrupt-IN callbacks (key-matrix presses, rotary scroll,
/// software keys, profile changes) and surfaces them. Opens its OWN read handle
/// to the vendor interface, independent of <see cref="KeebHub"/>'s write handle,
/// so the blocking read loop never contends with the 30 Hz RGB stream.
///
/// Logs decoded events: the device-key → physical (row,col) mapping can be
/// observed on the bench to reconcile the key-assignment grid.
/// </summary>
public sealed class KeebInputWorker : BackgroundService
{
    private const int ReadTimeoutMs = 200;
    private const int RetryDelayMs = 1000;

    private readonly IHidEnumerator _hid;
    private readonly KeebHub _hub;
    private readonly KeebSettingsApplier _applier;
    private readonly KeebReactiveRenderer _renderer;
    private readonly Nexus.Service.Lighting.Engine.LightingEngine _engine;
    private IHidDevice? _reader;

    public KeebInputWorker(IHidEnumerator hid, KeebHub hub, KeebSettingsApplier applier, KeebReactiveRenderer renderer, Nexus.Service.Lighting.Engine.LightingEngine engine)
    {
        _hid = hid;
        _hub = hub;
        _applier = applier;
        _renderer = renderer;
        _engine = engine;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Run(() => Loop(stoppingToken), stoppingToken);

    private async Task Loop(CancellationToken ct)
    {
        var buf = new byte[16];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_hub.IsConnected)
                {
                    CloseReader();
                    await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
                    continue;
                }
                if (_reader is null && !OpenReader())
                {
                    await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
                    continue;
                }

                var n = _reader!.Read(buf, ReadTimeoutMs);
                if (n == 0) continue; // idle: the read blocked up to ReadTimeoutMs, nothing arrived
                if (n < 0)
                {
                    // Device went away (unplug): the handle now fails reads
                    // instantly. Tear it down and back off so we don't spin a
                    // core; OpenReader re-acquires when the keeb returns.
                    CloseReader();
                    try { await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                var ev = KeebProtocol.ParseInputEvent(buf.AsSpan(0, n));
                if (ev.Kind != KeebProtocol.KeebInputKind.None) HandleEvent(ev);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                ServiceLog.Error($"[keeb-input] read loop error: {ex.GetType().Name}: {ex.Message}");
                CloseReader();
                try { await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        CloseReader();
    }

    private void HandleEvent(KeebProtocol.KeebInputEvent ev)
    {
        // Key-matrix callbacks carry the firmware (row,col) for the pressed
        // key, used to map web grid cells onto firmware key indices.
        switch (ev.Kind)
        {
            case KeebProtocol.KeebInputKind.KeyMatrix:
                _renderer.IngestKeyPress(ev.Row, ev.Column);
                break;
            case KeebProtocol.KeebInputKind.ScrollUp:
            case KeebProtocol.KeebInputKind.ScrollDown:
                ServiceLog.Info($"[keeb-input] {ev.Encoder} encoder {ev.Kind}");
                break;
            case KeebProtocol.KeebInputKind.ScrollMiddle:
                // The middle button cycles the firmware effect on the device.
                // Re-read immediately so the panel's Effect selector follows
                // without waiting on the connection-worker poll. While a
                // software effect streams the frame writer is the sole
                // settings reader (see KeebSettingsApplier) - skip then.
                ServiceLog.Info("[keeb-input] rotary middle click");
                if (_engine.CurrentEffectName == "none") _applier.SyncFromDevice();
                break;
            case KeebProtocol.KeebInputKind.SoftwareKey:
                ServiceLog.Info($"[keeb-input] software key ap={ev.ApCode} pressed={ev.Pressed}");
                break;
            case KeebProtocol.KeebInputKind.Profile:
                // Track the active profile: key assignments and macros address
                // onboard storage per profile (slot = profile*16 + index).
                ServiceLog.Info($"[keeb-input] profile -> {ev.Profile}");
                if (ev.Profile is 0 or 1) _hub.State.Profile = ev.Profile;
                break;
        }
    }

    private bool OpenReader()
    {
        // The key-matrix / rotary callbacks (0x05 0xFA ...) arrive on the keeb's FF02/F0
        // vendor collection (8-byte input reports), NOT the FF11/F0 settings collection
        // (feature reports) the hub uses, and NOT the 0001/02 mouse collection that also
        // carries 8-byte inputs. Reading the settings collection (as we did) yields no
        // callbacks - which is why key-reactive and software-mode knob events never fired.
        var infos = _hid.Find(KeebProtocol.VendorId, KeebProtocol.ProductId);
        HidDeviceInfo? info = null;
        foreach (var i in infos)
            if (i.UsagePage == 0xFF02 && i.Usage == 0xF0) { info = i; break; }
        if (info is null) return false;
        _reader = _hid.Open(info.Path, forInput: true);
        if (_reader is null) return false;
        ServiceLog.Info($"[keeb-input] reader opened on {info.Path} (usage={info.UsagePage:X4}/{info.Usage:X2} in={info.InputReportByteLength})");
        return true;
    }

    private void CloseReader()
    {
        try { _reader?.Dispose(); } catch { }
        _reader = null;
    }
}
