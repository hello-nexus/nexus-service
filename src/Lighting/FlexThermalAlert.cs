using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;

namespace Nexus.Service.Lighting;

/// <summary>Takes the flat alert colors per unit; an empty map releases every unit.</summary>
public interface IFlexAlertSink
{
    void SetAlert(IReadOnlyDictionary<string, (byte R, byte G, byte B)> colors);
}

/// <summary>Which sensor currently owns the alert, for the settings page.</summary>
public sealed record FlexAlertState(string? Source, long? SinceUnixMs);

/// <summary>
/// Thermal Alert: a unit whose CPU or GPU runs above its threshold shows one flat color until the
/// reading falls back. The most recent trigger wins; a reading must drop two degrees under its
/// threshold to release, so a hovering temperature does not flicker.
/// </summary>
public sealed class FlexThermalAlert
{
    public const int HysteresisC = 2;
    public const int StartGraceMs = 10_000;

    private readonly IConfigStore _store;
    private readonly FlexHub _hub;
    private readonly IFlexAlertSink _sink;
    private readonly Func<long> _nowMs;
    private long _cpuSince;
    private long _gpuSince;
    private long _activeSinceUnix;
    private object? _lastUnits;
    private long _lastChangeMs;
    private string? _active;

    public FlexThermalAlert(IConfigStore store, FlexHub hub, IFlexAlertSink sink, Func<long>? nowMs = null)
    {
        _store = store;
        _hub = hub;
        _sink = sink;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _lastChangeMs = _nowMs() - StartGraceMs;
        _lastUnits = _store.Load().Devices.FlexUnits;
    }

    public FlexAlertState State => new(_active, _active is null ? null : _activeSinceUnix);

    public static (byte R, byte G, byte B) ParseColor(string hex, (byte R, byte G, byte B) fallback)
    {
        var s = hex.TrimStart('#');
        return s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? ((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : fallback;
    }

    /// <summary>One evaluation pass; the worker calls it once a second with the current readings.</summary>
    public void Tick(float? cpuTemp, float? gpuTemp)
    {
        var settings = _store.Load();
        var cfg = settings.Devices.FlexThermalAlert;
        var now = _nowMs();
        // Any settings write (a look change, an upload's bookkeeping) postpones a new alert.
        if (!ReferenceEquals(_lastUnits, settings.Devices.FlexUnits))
        {
            _lastUnits = settings.Devices.FlexUnits;
            _lastChangeMs = now;
        }
        if (!cfg.Enabled)
        {
            Release();
            return;
        }
        Evaluate(ref _cpuSince, cfg.CpuEnabled, cpuTemp, cfg.CpuThreshold, now);
        Evaluate(ref _gpuSince, cfg.GpuEnabled, gpuTemp, cfg.GpuThreshold, now);
        string? source = null;
        if (_cpuSince != 0 && _gpuSince != 0)
        {
            source = _cpuSince >= _gpuSince ? "cpu" : "gpu";
        }
        else if (_cpuSince != 0)
        {
            source = "cpu";
        }
        else if (_gpuSince != 0)
        {
            source = "gpu";
        }
        if (source is null)
        {
            Release();
            return;
        }
        if (_active is null && now - _lastChangeMs < StartGraceMs)
        {
            return;
        }
        if (_active != source)
        {
            _active = source;
            _activeSinceUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        var color = source == "cpu"
            ? ParseColor(cfg.CpuColor, (255, 0, 0))
            : ParseColor(cfg.GpuColor, (0, 0, 255));
        var colors = new Dictionary<string, (byte R, byte G, byte B)>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in _hub.Units)
        {
            if (u.Controllable && (cfg.Members.Count == 0 || cfg.Members.Exists(m => string.Equals(m, u.Mac, StringComparison.OrdinalIgnoreCase))))
            {
                colors[u.Mac] = color;
            }
        }
        _sink.SetAlert(colors);
    }

    private static void Evaluate(ref long since, bool enabled, float? temp, int threshold, long now)
    {
        if (!enabled || temp is null)
        {
            since = 0;
            return;
        }
        if (temp.Value >= threshold)
        {
            if (since == 0)
            {
                since = now == 0 ? 1 : now;
            }
        }
        else if (temp.Value < threshold - HysteresisC)
        {
            since = 0;
        }
    }

    private void Release()
    {
        if (_active is null)
        {
            return;
        }
        _active = null;
        _sink.SetAlert(new Dictionary<string, (byte, byte, byte)>());
    }
}

/// <summary>Feeds <see cref="FlexThermalAlert"/> the CPU and GPU temperature once a second.</summary>
public sealed class FlexThermalAlertWorker : BackgroundService
{
    private readonly FlexThermalAlert _alert;
    private readonly ISensorProvider _sensors;

    public FlexThermalAlertWorker(FlexThermalAlert alert, ISensorProvider sensors)
    {
        _alert = alert;
        _sensors = sensors;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _alert.Tick(
                    SummarySensors.Value(_sensors, SummarySensorKind.CpuTemp),
                    SummarySensors.Value(_sensors, SummarySensorKind.GpuTemp));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Nexus.Service.Platform.ServiceLog.Warn($"[lianli-flex] thermal alert: {ex.Message}");
            }
            await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
        }
    }
}
