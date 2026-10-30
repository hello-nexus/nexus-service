using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Drives each Flex fan. In the Lighting page mode the composed zone frames stream live; in any
/// other mode the unit's stored animation is built from its settings and uploaded once the
/// settings have settled. Streaming never schedules a flash save; an upload does.
/// </summary>
public sealed class FlexLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;
    private const int MinStreamIntervalMs = 33;
    private const int StreamRefreshMs = 1000;
    private const int SettleMs = 300;
    private const int DriftConfirmMs = 1500;
    private const int RetryUploadMs = 2000;
    // A receiver that never echoes the index must not be rewritten forever (each upload is a flash save).
    private const int MaxDriftRetries = 2;

    private readonly LightingEngine _engine;
    private readonly FlexHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly FlexLightingDeviceProvider _provider;
    private readonly Func<long> _nowMs;
    private readonly Func<Func<bool>, Task<bool>> _runUpload;
    private readonly FeatureGates _gates;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private RgbColor[][] _segmentBuffers = Array.Empty<RgbColor[]>();
    private byte[] _wire = Array.Empty<byte>();
    private readonly Dictionary<string, UnitState> _state = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _identifyLock = new();
    private readonly Dictionary<string, long> _identifyUntil = new(StringComparer.OrdinalIgnoreCase);
    private const int IdentifyMs = 3000;
    private const int IdentifyBlinkMs = 300;

    /// <summary>Flashes the unit white for a few seconds, then restores whatever it was showing.</summary>
    public void Identify(string mac)
    {
        lock (_identifyLock)
        {
            _identifyUntil[mac] = _nowMs() + IdentifyMs;
        }
    }

    /// <summary>True while the unit's identify flash is running.</summary>
    public bool IsIdentifying(string mac)
    {
        lock (_identifyLock)
        {
            return _identifyUntil.TryGetValue(mac, out var until) && _nowMs() < until;
        }
    }

    private bool TickIdentify(FlexUnit unit, UnitState st, long now)
    {
        long until;
        lock (_identifyLock)
        {
            if (!_identifyUntil.TryGetValue(unit.Mac, out until))
            {
                return false;
            }
            if (now >= until)
            {
                _identifyUntil.Remove(unit.Mac);
                st.RestoreWithoutSave = true;
                st.RestoreSignature = st.UploadedSignature;
                st.UploadedSignature = "";
                st.PendingSignature = "";
                st.Streaming = false;
                return false;
            }
        }
        var on = ((until - now) / IdentifyBlinkMs) % 2 == 0;
        var frame = new byte[unit.LedCount * 3];
        if (on)
        {
            Array.Fill(frame, (byte)255);
            if (unit.Family == FlexFamily.P28V2)
            {
                FinishP28Wire(frame, unit.Fans);
            }
        }
        _hub.SendLive(unit.Mac, frame);
        return true;
    }

    private sealed class UnitState
    {
        public int StreamHash;
        public long LastStreamMs = long.MinValue / 2;
        public bool Streaming;
        public string PendingSignature = "";
        public long PendingSinceMs;
        public string UploadedSignature = "";
        public long LastUploadAttemptMs = long.MinValue / 2;
        public long UploadedAtMs;
        public int DriftRetries;
        public Task<bool>? Upload;
        public string UploadSignature = "";
        public long UploadStartedMs;
        public bool RestoreWithoutSave;
        // The last upload was not flash-saved, so the receiver keeps reporting the saved look and drift checks do not apply.
        public bool UploadedUnsaved;
        public bool UploadUnsavedPending;
        public string RestoreSignature = "";
    }

    public FlexLightingFrameWriter(
        LightingEngine engine, FlexHub hub, IConfigStore store, Np50IdentifyTracker identify,
        FlexLightingDeviceProvider provider, Func<long>? nowMs = null, FeatureGates? gates = null,
        Func<Func<bool>, Task<bool>>? runUpload = null)
    {
        _runUpload = runUpload ?? (work => Task.Run(work));
        _engine = engine;
        _hub = hub;
        _store = store;
        _identify = identify;
        _provider = provider;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
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
        _cts?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch
            {
                // shutdown best-effort
            }
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
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[lianli-flex-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal void Tick()
    {
        if (!_gates.Lighting || !_hub.IsConnected)
        {
            _state.Clear();
            _argbRequests.Clear();
            return;
        }
        var settings = _store.Load();
        var now = _nowMs();
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var group = FlexStoredLook.FindMergeGroup(_hub.Units, settings.Devices.FlexUnits);
        foreach (var unit in _hub.Units)
        {
            live.Add(unit.Mac);
            if (!unit.Controllable || unit.Stale)
            {
                continue;
            }
            if (!_state.TryGetValue(unit.Mac, out var st))
            {
                st = new UnitState();
                _state[unit.Mac] = st;
            }
            settings.Devices.FlexUnits.TryGetValue(unit.Mac, out var look);
            look ??= new LianLiFlexUnitSettings();
            if (unit.Family != FlexFamily.P28V2)
            {
                ReconcileMotherboardArgb(unit, look, now);
            }
            if (FlexLightingDeviceProvider.PlaysMotherboardArgb(unit, settings.Devices.FlexUnits))
            {
                // The unit shows its ARGB input: it takes no stored uploads, no live frames and no identify flash.
                st.PendingSignature = "";
                st.UploadedSignature = "";
                st.Streaming = false;
                continue;
            }
            if (TickIdentify(unit, st, now))
            {
                continue;
            }
            // P28 accepts stored packages too, but only streaming is implemented for it.
            if (look.Mode == LianLiWirelessChainLighting.ModeCustom || unit.Family == FlexFamily.P28V2)
            {
                st.PendingSignature = "";
                TickStream(unit, st, settings, now);
            }
            else
            {
                st.Streaming = false;
                TickStored(unit, st, look, now, group, settings);
            }
        }
        foreach (var gone in _argbRequests.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _argbRequests.Remove(gone);
        }
        if (_state.Count > live.Count)
        {
            foreach (var k in new List<string>(_state.Keys))
            {
                if (!live.Contains(k))
                {
                    _state.Remove(k);
                }
            }
        }
    }

    private const long ArgbRetryMs = 5_000;
    private const int MaxArgbRequests = 3;
    private readonly Dictionary<string, (bool Want, long AtMs, int Sent)> _argbRequests = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Brings the unit's "playing the board input" flag to the saved choice: re-sent every few seconds, a few times, until the unit reports it.</summary>
    private void ReconcileMotherboardArgb(FlexUnit unit, LianLiFlexUnitSettings look, long now)
    {
        if (look.MotherboardArgb is not { } want)
        {
            _argbRequests.Remove(unit.Mac);
            return;
        }
        if (unit.Info.PlayingMotherboardArgb == want)
        {
            _argbRequests.Remove(unit.Mac);
            return;
        }
        _argbRequests.TryGetValue(unit.Mac, out var last);
        var fresh = last.Sent == 0 || last.Want != want;
        if (!fresh && (last.Sent >= MaxArgbRequests || now - last.AtMs < ArgbRetryMs))
        {
            return;
        }
        if (_hub.SetMotherboardArgb(unit.Mac, want))
        {
            _argbRequests[unit.Mac] = (want, now, fresh ? 1 : last.Sent + 1);
        }
    }

    private void TickStream(FlexUnit unit, UnitState st, NexusSettings settings, long now)
    {
        var devices = _engine.Devices;
        if (devices.Length == 0)
        {
            return;
        }
        var structure = FlexLightingDeviceProvider.BuildStructure(unit.Mac, unit.Family, unit.Fans);
        var zones = ZoneResolution.Resolve(structure, settings);
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        if (ZoneResolution.IsFullyUncontrolled(zones, uncontrolled))
        {
            st.Streaming = false;
            return;
        }
        SegmentFrameComposer.EnsureBuffers(structure, ref _segmentBuffers);
        SegmentFrameComposer.Compose(
            structure, zones, devices, settings.Devices.DisabledLightingDevices, uncontrolled,
            settings.Devices.LightingDevicePrefs, MasterBrightness.Resolve(settings.Lighting), 1.0,
            now * TimeSpan.TicksPerMillisecond, _identify, _segmentBuffers);

        var swap = settings.Devices.FlexUnits.TryGetValue(unit.Mac, out var l) && l.UpDownSwap && unit.Family == FlexFamily.TlFlex;
        var total = FillWire(unit.Family, unit.Fans, structure, swap);
        var span = _wire.AsSpan(0, total * 3);
        var hash = HashOf(span);
        var due = !st.Streaming || hash != st.StreamHash || now - st.LastStreamMs >= StreamRefreshMs;
        if (!due || now - st.LastStreamMs < MinStreamIntervalMs)
        {
            return;
        }
        if (_hub.SendLive(unit.Mac, span))
        {
            st.StreamHash = hash;
            st.LastStreamMs = now;
            st.Streaming = true;
        }
    }

    // Wire layout: fan-major blocks of LedsPerFan; each zone's LEDs go to the wire indexes of its table.
    internal int FillWire(FlexFamily family, int fans, DeviceStructure structure, bool swap)
    {
        var perFan = FlexProtocol.LedsPerFan(family);
        var total = fans * perFan;
        if (_wire.Length < total * 3)
        {
            _wire = new byte[total * 3];
        }
        Array.Clear(_wire, 0, total * 3);
        var zoneIds = FlexLightingDeviceProvider.ZonesOf(family);
        for (var z = 0; z < zoneIds.Count; z++)
        {
            var map = FlexProtocol.ZoneWireIndexes(family, zoneIds[z]);
            var buf = _segmentBuffers[z];
            for (var f = 0; f < fans; f++)
            {
                for (var k = 0; k < map.Count; k++)
                {
                    var src = (f * map.Count) + k;
                    if (src >= buf.Length)
                    {
                        continue;
                    }
                    var idx = map[k];
                    if (swap)
                    {
                        idx = (idx + (perFan / 2)) % perFan;
                    }
                    var o = ((f * perFan) + idx) * 3;
                    _wire[o] = buf[src].R;
                    _wire[o + 1] = buf[src].G;
                    _wire[o + 2] = buf[src].B;
                }
            }
        }
        if (family == FlexFamily.P28V2)
        {
            FinishP28Wire(_wire, fans);
        }
        return total;
    }

    // The last wire LED repeats the last logical one, and no channel goes above the cap, as the vendor software sends.
    internal static void FinishP28Wire(byte[] wire, int fans)
    {
        const int perFan = FlexProtocol.P28LedsPerFan;
        for (var f = 0; f < fans; f++)
        {
            var o = f * perFan * 3;
            var last = o + ((perFan - 1) * 3);
            var source = o + ((FlexProtocol.P28LogicalLedsPerFan - 1) * 3);
            for (var c = 0; c < 3; c++)
            {
                wire[last + c] = wire[source + c];
            }
            for (var k = 0; k < perFan * 3; k++)
            {
                wire[o + k] = Math.Min(wire[o + k], FlexProtocol.P28MaxChannel);
            }
        }
    }

    private static int HashOf(ReadOnlySpan<byte> data)
    {
        var hc = new HashCode();
        hc.AddBytes(data);
        return hc.ToHashCode();
    }

    private void TickStored(FlexUnit unit, UnitState st, LianLiFlexUnitSettings look, long now, FlexStoredLook.MergeGroup? group, NexusSettings settings)
    {
        if (CollectUpload(unit, st))
        {
            return;
        }
        var signature = FlexStoredLook.Signature(unit, look, group, settings);
        if (signature != st.PendingSignature)
        {
            st.PendingSignature = signature;
            st.PendingSinceMs = now;
        }
        var drifted = st.UploadedSignature == signature
            && !st.UploadedUnsaved
            && st.DriftRetries < MaxDriftRetries
            && now - st.UploadedAtMs >= DriftConfirmMs
            && _hub.SentEffectIndex(unit.Mac).Length > 0
            && !string.Equals(unit.Info.EffectIndex, _hub.SentEffectIndex(unit.Mac), StringComparison.OrdinalIgnoreCase);
        var liveSince = _hub.LiveSinceUpload(unit.Mac);
        var inSync = st.UploadedSignature == signature && !drifted && !liveSince;
        if (inSync)
        {
            return;
        }
        // First sight after a restart: a receiver already holding the persisted package needs nothing.
        if (!liveSince && st.UploadedSignature.Length == 0 && look.PackageSignature == signature
            && string.Equals(look.PackageEffectIndex, unit.Info.EffectIndex, StringComparison.OrdinalIgnoreCase))
        {
            st.UploadedSignature = signature;
            st.UploadedAtMs = now;
            return;
        }
        if (now - st.PendingSinceMs < SettleMs || now - st.LastUploadAttemptMs < RetryUploadMs)
        {
            return;
        }
        st.LastUploadAttemptMs = now;
        st.DriftRetries = st.UploadedSignature == signature ? st.DriftRetries + 1 : 0;
        var package = FlexStoredLook.Build(unit, look, group, settings);
        if (package is null)
        {
            return;
        }
        // The packets take a while to send; running them off the tick keeps the other units' frames flowing.
        var mac = unit.Mac;
        var save = !(st.RestoreWithoutSave && signature == st.RestoreSignature);
        st.UploadSignature = signature;
        st.UploadUnsavedPending = !save;
        st.UploadStartedMs = now;
        st.Upload = _runUpload(() => _hub.UploadPackage(mac, package.Compressed, package.Frames, package.Leds, package.IntervalMs, save));
    }

    // Applies a finished upload's result; true while one is still running.
    private bool CollectUpload(FlexUnit unit, UnitState st)
    {
        if (st.Upload is null)
        {
            return false;
        }
        if (!st.Upload.IsCompleted)
        {
            return true;
        }
        var ok = st.Upload.IsCompletedSuccessfully && st.Upload.Result;
        st.Upload = null;
        if (!ok)
        {
            return false;
        }
        var signature = st.UploadSignature;
        st.UploadedSignature = signature;
        st.UploadedAtMs = st.UploadStartedMs;
        st.RestoreWithoutSave = false;
        st.UploadedUnsaved = st.UploadUnsavedPending;
        var sent = _hub.SentEffectIndex(unit.Mac);
        _store.Update(s =>
        {
            if (s.Devices.FlexUnits.TryGetValue(unit.Mac, out var u))
            {
                var next = new Dictionary<string, LianLiFlexUnitSettings>(s.Devices.FlexUnits, StringComparer.Ordinal)
                {
                    [unit.Mac] = FlexStoredLook.WithPackage(u, signature, sent),
                };
                s.Devices.FlexUnits = next;
            }
        });
        return false;
    }
}
