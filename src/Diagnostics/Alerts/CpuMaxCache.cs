using System;

namespace Nexus.Service.Diagnostics.Alerts;

/// <summary>
/// Resolves the CPU maximum temperature for the built-in entries and keeps it steady. A hardware reading is final for a CPU model, so
/// the default cannot jitter; a table value is re-resolved each call so a reading that becomes readable later replaces it once.
/// A blank model (the provider reports none while it opens or reopens after resume) reuses the last non-blank one, and a failing
/// lookup reuses the previous value, so neither flips an untouched limit back and forth. Null until a first value is known.
/// </summary>
public sealed class CpuMaxCache
{
    private readonly Func<string?> _model;
    private readonly Func<double?> _tjMaxC;
    private readonly Action<Exception>? _onError;
    private readonly object _lock = new();
    private string? _lastModel;
    private (string Model, CpuMax Max)? _cached;

    public CpuMaxCache(Func<string?> model, Func<double?> tjMaxC, Action<Exception>? onError = null)
    {
        _model = model;
        _tjMaxC = tjMaxC;
        _onError = onError;
    }

    public CpuMax? Get()
    {
        lock (_lock)
        {
            try
            {
                var raw = _model();
                var model = string.IsNullOrWhiteSpace(raw) ? _lastModel : raw;
                if (model is null)
                {
                    return _cached?.Max;
                }
                _lastModel = model;
                if (_cached is { } cached && cached.Model == model && cached.Max.FromHardware)
                {
                    return cached.Max;
                }
                var max = CpuTemperatureDefault.Resolve(model, _tjMaxC());
                _cached = (model, max);
                return max;
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
                return _cached?.Max;
            }
        }
    }
}
