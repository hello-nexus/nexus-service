using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.AsiahorseUmbra;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>Commits the persisted lighting source to the Umbra and, while it is "nexus", streams the engine's LEDs.</summary>
public sealed class AsiahorseUmbraLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 16;
    private const long PlanCheckMs = 100;
    private const long KeepAliveMs = 1000;
    private const int MaxReportsPerSecond = 900;

    private readonly LightingEngine _engine;
    private readonly UmbraHub _hub;
    private readonly IConfigStore _store;
    private readonly FeatureGates _gates;
    private readonly LightingFrameCap? _cap;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private long _lastPlanTimestamp;
    private int? _planSignature;
    private long _planEpoch = -1;
    private string _source = UmbraLightingPlan.SourceNexus;

    private byte[] _buffer = Array.Empty<byte>();
    private byte[] _lastSent = Array.Empty<byte>();
    private long _lastSendTimestamp;

    public AsiahorseUmbraLightingFrameWriter(LightingEngine engine, UmbraHub hub, IConfigStore store, FeatureGates? gates = null, LightingFrameCap? cap = null)
    {
        _engine = engine;
        _hub = hub;
        _store = store;
        _gates = gates ?? FeatureGates.AllEnabled;
        _cap = cap;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch { /* shutdown best-effort */ }
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose() => StopAsync(default).GetAwaiter().GetResult();

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickPeriodMs));
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex)
            { Console.Error.WriteLine($"[asiahorse-umbra-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    internal void Tick()
    {
        if (!_gates.Lighting) return;
        if (!_hub.IsConnected)
        {
            _planSignature = null;
            return;
        }

        var settings = _store.Load();
        var now = Stopwatch.GetTimestamp();
        var epoch = _hub.ConnectEpoch;
        if (_planSignature is null || epoch != _planEpoch || ElapsedMs(_lastPlanTimestamp, now) >= PlanCheckMs)
        {
            CommitPlan(settings, epoch, now);
        }
        if (_source == UmbraLightingPlan.SourceNexus && _planSignature is not null)
        {
            Stream(settings, now);
        }
    }

    private void CommitPlan(NexusSettings settings, long epoch, long now)
    {
        _lastPlanTimestamp = now;
        var ls = settings.Devices.AsiahorseUmbra;
        var source = UmbraLightingPlan.SourceOf(ls);
        var master = MasterBrightness.Effective(settings.Lighting);
        var plan = UmbraLightingPlan.Build(ls, _hub.Snapshot.Ports, master);
        var signature = UmbraLightingPlan.Signature(plan);
        if (_planSignature == signature && epoch == _planEpoch)
        {
            return;
        }
        if (_hub.SendFrames(plan))
        {
            _planSignature = signature;
            _planEpoch = epoch;
            _source = source;
            _lastSent = Array.Empty<byte>();
        }
        else
        {
            _planSignature = null;
        }
    }

    private void Stream(NexusSettings settings, long now)
    {
        var devices = _engine.Devices;
        var ports = _hub.Snapshot.Ports;
        var total = 0;
        foreach (var p in ports)
        {
            total += p.LedCount;
        }
        if (total == 0)
        {
            return;
        }

        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var master = MasterBrightness.Effective(settings.Lighting);
        if (_buffer.Length != total * 3)
        {
            _buffer = new byte[total * 3];
        }
        else
        {
            Array.Clear(_buffer);
        }

        var offset = 0;
        var anyFrame = false;
        for (var p = 0; p < ports.Length; p++)
        {
            var count = ports[p].LedCount;
            if (count == 0)
            {
                continue;
            }
            var id = AsiahorseUmbraLightingDeviceProvider.PortId(p);
            var frame = NollieLightingFrameWriter.FindFrame(devices, id);
            // A port the engine has no frame for yet streams black for its own slice only.
            if (frame is not null)
            {
                anyFrame = true;
                var mul = NollieLightingFrameWriter.ComputeBrightnessMul(id, disabled, uncontrolled, prefs, master, out var adjust);
                NollieLightingFrameWriter.FillSlice(_buffer, offset, count, frame, mul, adjust, false, 0, 0);
            }
            offset += count;
        }
        // Before the engine has any port frame (just reconnected) a send would flash every fan black.
        if (!anyFrame)
        {
            return;
        }

        var changed = !_buffer.AsSpan().SequenceEqual(_lastSent);
        var sinceSend = ElapsedMs(_lastSendTimestamp, now);
        if (!changed && sinceSend < KeepAliveMs)
        {
            return;
        }
        // The packet count bounds the frame rate so the stream stays under the report budget.
        var packets = UmbraProtocol.StreamPacketCount(total);
        if (changed && sinceSend * MaxReportsPerSecond < packets * 1000L)
        {
            return;
        }
        // Only a changed frame is held back: the unchanged re-send above is the device keepalive and never throttled.
        if (changed && _cap is not null && !_cap.TryWrite(AsiahorseUmbraLightingDeviceProvider.DeviceId, 0, settings))
        {
            return;
        }
        if (_hub.SendFrames(UmbraProtocol.EncodeStream(_buffer)))
        {
            _lastSent = (byte[])_buffer.Clone();
            _lastSendTimestamp = now;
        }
    }

    private static long ElapsedMs(long from, long to) => (to - from) * 1000 / Stopwatch.Frequency;
}
