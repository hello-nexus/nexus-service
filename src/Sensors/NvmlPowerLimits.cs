using System;
using System.Collections.Generic;
using Nexus.Service.Diagnostics.Gpu;

namespace Nexus.Service.Sensors;

/// <summary>
/// NVIDIA enforced board power limits by device name, read through NVML and
/// re-read periodically, so a limit raised in a tuning tool shows up.
/// Gives the GPU power sensor the card's own ceiling instead of one fixed
/// wattage for every card.
/// </summary>
internal sealed class NvmlPowerLimits
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitRetryInterval = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private Dictionary<string, float> _byName = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _readAtUtc = DateTime.MinValue;
    private DateTime _initTriedAtUtc = DateTime.MinValue;
    private bool _initOk;

    /// <summary>Watts, or 0 when NVML is unavailable or same-named cards report different limits.</summary>
    public float WattsFor(string deviceName)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _readAtUtc >= RefreshInterval)
            {
                _readAtUtc = now;
                _byName = Read(now);
            }
            return _byName.TryGetValue(deviceName, out var watts) ? watts : 0f;
        }
    }

    // Caller holds _gate. LHM and NVML enumerate devices in different orders, so
    // cards are matched by name.
    private Dictionary<string, float> Read(DateTime now)
    {
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (!_initOk)
        {
            if (now - _initTriedAtUtc < InitRetryInterval) return result;
            _initTriedAtUtc = now;
            try
            {
                _initOk = NvmlInterop.TryLoad() && NvmlInterop.Init() == NvmlInterop.Success;
            }
            catch
            {
                _initOk = false;
            }
            if (!_initOk) return result;
        }

        try
        {
            if (NvmlInterop.DeviceGetCount(out var count) != NvmlInterop.Success) return result;
            var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint i = 0; i < count; i++)
            {
                if (NvmlInterop.DeviceGetHandleByIndex(i, out var device) != NvmlInterop.Success) continue;
                if (NvmlInterop.GetEnforcedPowerLimitMilliwatts(device, out var milliwatts) != NvmlInterop.Success || milliwatts == 0) continue;
                var name = NvmlInterop.GetDeviceName(device);
                var watts = milliwatts / 1000f;
                if (result.TryGetValue(name, out var existing) && existing != watts) ambiguous.Add(name);
                result[name] = watts;
            }
            foreach (var name in ambiguous) result.Remove(name);
        }
        catch
        {
            result.Clear();
        }
        return result;
    }
}
