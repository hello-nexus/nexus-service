using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>
/// Radeon GPUs reachable through ADLX (amdadlx64.dll, from the AMD driver). LHM's only AMD
/// fan write is ADL Overdrive5, which RX 5000 series and newer ignore.
/// </summary>
/// <remarks>Vtable slots are from the ADLX SDK C headers; ADLX extends through new interfaces, so slots never move.</remarks>
internal sealed unsafe class AdlxGpuFans
{
    /// <summary>Serializes every ADLX call and the takeover file; the SDK documents no thread-safety guarantee.</summary>
    internal static readonly object Sync = new();

    private sealed record Gpu(nint Ptr, string Name, string Pnp, bool FanTuning);

    private readonly nint _tuning;
    private readonly List<Gpu> _gpus;
    private readonly Dictionary<nint, AdlxGpuFan> _opened = new();

    private AdlxGpuFans(nint tuning, List<Gpu> gpus)
    {
        _tuning = tuning;
        _gpus = gpus;
    }

    /// <summary>Null when the AMD driver's ADLX is absent or fails to initialize.</summary>
    public static AdlxGpuFans? TryCreate()
    {
        var path = Path.Combine(Environment.SystemDirectory, "amdadlx64.dll");
        if (!File.Exists(path)) return null;
        if (!NativeLibrary.TryLoad(path, out var lib)
            || !NativeLibrary.TryGetExport(lib, "ADLXQueryFullVersion", out var query)
            || !NativeLibrary.TryGetExport(lib, "ADLXInitialize", out var init))
        {
            ServiceLog.Warn("[amd-fan] amdadlx64.dll found but its ADLX exports are not loadable");
            return null;
        }

        lock (Sync)
        {
            ulong version = 0;
            var rc = ((delegate* unmanaged[Cdecl]<ulong*, int>)query)(&version);
            if (!Adlx.Succeeded(rc))
            {
                ServiceLog.Warn($"[amd-fan] ADLXQueryFullVersion failed ({Adlx.Name(rc)})");
                return null;
            }
            var versionText = $"{version >> 48}.{(version >> 32) & 0xFFFF}.{(version >> 16) & 0xFFFF}.{version & 0xFFFF}";

            // The installed version is always one the driver accepts.
            nint system = 0;
            rc = ((delegate* unmanaged[Cdecl]<ulong, nint*, int>)init)(version, &system);
            if (!Adlx.Succeeded(rc) || system == 0)
            {
                ServiceLog.Warn($"[amd-fan] ADLXInitialize {versionText} failed ({Adlx.Name(rc)})");
                return null;
            }

            nint tuning = 0;
            rc = Adlx.GetOut(system, 8, &tuning); // IADLXSystem::GetGPUTuningServices
            if (!Adlx.Succeeded(rc) || tuning == 0)
            {
                ServiceLog.Warn($"[amd-fan] ADLX {versionText}: GetGPUTuningServices failed ({Adlx.Name(rc)})");
                return null;
            }

            nint list = 0;
            rc = Adlx.GetOut(system, 1, &list); // IADLXSystem::GetGPUs
            if (!Adlx.Succeeded(rc) || list == 0)
            {
                ServiceLog.Warn($"[amd-fan] ADLX {versionText}: GetGPUs failed ({Adlx.Name(rc)})");
                Adlx.Release(tuning);
                return null;
            }

            var gpus = new List<Gpu>();
            var count = ((delegate* unmanaged[Stdcall]<nint, uint>)Adlx.Slot(list, 3))(list); // Size
            for (uint i = 0; i < count; i++)
            {
                nint gpu = 0;
                rc = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Adlx.Slot(list, 11))(list, i, &gpu); // At_GPUList
                if (!Adlx.Succeeded(rc) || gpu == 0) continue;
                byte supported = 0;
                rc = ((delegate* unmanaged[Stdcall]<nint, nint, byte*, int>)Adlx.Slot(tuning, 10))(tuning, gpu, &supported); // IsSupportedManualFanTuning
                gpus.Add(new Gpu(gpu, Adlx.GetString(gpu, 7), Adlx.GetString(gpu, 9), Adlx.Succeeded(rc) && supported != 0)); // Name, PNPString
            }
            Adlx.Release(list);

            ServiceLog.Info($"[amd-fan] ADLX {versionText}: {string.Join("; ", gpus.Select(g => $"'{g.Name}' pnp={g.Pnp} manualFanTuning={g.FanTuning}"))}");
            return new AdlxGpuFans(tuning, gpus);
        }
    }

    /// <summary>The fan writer for the GPU whose PNP id LibreHardwareMonitor reports, or null when ADLX cannot tune its fan.</summary>
    public AdlxGpuFan? Find(string lhmPnp, out bool pnpMatched)
    {
        lock (Sync)
        {
            var gpu = _gpus.FirstOrDefault(g => PnpMatches(g.Pnp, lhmPnp));
            pnpMatched = gpu is not null;
            if (gpu is null || !gpu.FanTuning)
            {
                ServiceLog.Info($"[amd-fan] no ADLX fan tuning for {lhmPnp} ({(gpu is null ? "no PNP match" : $"'{gpu.Name}' unsupported")}); keeping the Overdrive5 path");
                return null;
            }

            if (_opened.TryGetValue(gpu.Ptr, out var opened)) return opened;
            var fan = AdlxGpuFan.TryOpen(_tuning, gpu.Ptr, gpu.Name, gpu.Pnp);
            if (fan is not null) _opened[gpu.Ptr] = fan;
            return fan;
        }
    }

    private static bool PnpMatches(string a, string b) =>
        a.Length > 0 && b.Length > 0
        && (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One card's fan through IADLXManualFanTuning. A duty is written as a flat curve; the
/// curve and Zero RPM state found at takeover are persisted until they are handed back.
/// </summary>
internal sealed unsafe class AdlxGpuFan
{
    private readonly nint _fan;  // IADLXManualFanTuning*
    private readonly nint _fan1; // IADLXManualFanTuning1*, 0 when the driver predates it
    private readonly string _name;
    private readonly string _pnp;
    private readonly AdlxFanCurve.Range _speed;
    private readonly AdlxFanCurve.Range _temp;
    private readonly bool _zeroRpmSupported;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    private bool _takenOver;
    private (int Speed, int Temp)[] _saved = Array.Empty<(int, int)>();
    private bool? _savedZeroRpm;
    private int? _duty;
    private long? _releaseRetryAtMs;
    private volatile bool _releaseRequested; // set without Sync by a TryRelease that timed out

    private AdlxGpuFan(nint fan, nint fan1, string name, string pnp, AdlxFanCurve.Range speed, AdlxFanCurve.Range temp, bool zeroRpmSupported)
    {
        _fan = fan;
        _fan1 = fan1;
        _name = name;
        _pnp = pnp;
        _speed = speed;
        _temp = temp;
        _zeroRpmSupported = zeroRpmSupported;
    }

    /// <summary>The duty Nexus last commanded, or null while the driver's own curve is in charge.</summary>
    public int? Duty
    {
        get { lock (AdlxGpuFans.Sync) return _takenOver ? _duty : null; }
    }

    /// <summary>Caller holds <see cref="AdlxGpuFans.Sync"/>.</summary>
    internal static AdlxGpuFan? TryOpen(nint tuning, nint gpu, string name, string pnp)
    {
        nint ifc = 0;
        var rc = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Adlx.Slot(tuning, 16))(tuning, gpu, &ifc); // GetManualFanTuning
        if (!Adlx.Succeeded(rc) || ifc == 0)
        {
            ServiceLog.Warn($"[amd-fan] '{name}': GetManualFanTuning failed ({Adlx.Name(rc)})");
            return null;
        }
        var fan = Adlx.Query(ifc, "IADLXManualFanTuning");
        var fan1 = Adlx.Query(ifc, "IADLXManualFanTuning1");
        Adlx.Release(ifc);

        Adlx.IntRange speed = default, temp = default;
        rc = fan == 0 ? -1 : ((delegate* unmanaged[Stdcall]<nint, Adlx.IntRange*, Adlx.IntRange*, int>)Adlx.Slot(fan, 3))(fan, &speed, &temp); // GetFanTuningRanges
        if (!Adlx.Succeeded(rc))
        {
            ServiceLog.Warn($"[amd-fan] '{name}': {(fan == 0 ? "IADLXManualFanTuning unavailable" : $"GetFanTuningRanges failed ({Adlx.Name(rc)})")}");
            if (fan != 0) Adlx.Release(fan);
            if (fan1 != 0) Adlx.Release(fan1);
            return null;
        }
        byte zeroSupported = 0;
        rc = ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Adlx.Slot(fan, 8))(fan, &zeroSupported); // IsSupportedZeroRPM

        var result = new AdlxGpuFan(fan, fan1, name, pnp,
            new AdlxFanCurve.Range(speed.Min, speed.Max, speed.Step),
            new AdlxFanCurve.Range(temp.Min, temp.Max, temp.Step),
            Adlx.Succeeded(rc) && zeroSupported != 0);
        ServiceLog.Info($"[amd-fan] '{name}': speed {speed.Min}-{speed.Max}% step {speed.Step}, temp {temp.Min}-{temp.Max}C step {temp.Step}, zeroRpm supported={result._zeroRpmSupported} on={ReadZeroRpm(fan, 9)}, curve {Describe(result.ReadCurve(fan, 4))}");
        result.RecoverLeftover();
        return result;
    }

    /// <summary>Hands back a curve a previous run took and never released (crash, kill, power loss).</summary>
    private void RecoverLeftover()
    {
        var saved = AdlxTakeovers.Load(_pnp, out var zeroRpm);
        if (saved is null) return;
        _takenOver = true;
        _saved = saved;
        _savedZeroRpm = zeroRpm;
        ServiceLog.Info($"[amd-fan] '{_name}': a previous run left the fan taken over; restoring {Describe(saved)}");
        Release();
    }

    /// <summary>False when ADLX refused the write; the fan is never taken unless its curve could be saved first.</summary>
    public bool SetDuty(int duty)
    {
        lock (AdlxGpuFans.Sync)
        {
            if (!_takenOver && !TakeOver()) return false;
            _duty = duty;
            _releaseRetryAtMs = null;
            _releaseRequested = false;

            // Zero RPM stops the fan below the driver's threshold whatever the curve says.
            var ok = !_zeroRpmSupported || WriteZeroRpm(duty == 0);
            return WriteCurve("set", null, AdlxFanCurve.Speed(duty, _speed)) == Write.Ok && ok;
        }
    }

    private bool TakeOver()
    {
        var saved = ReadCurve(_fan, 4); // GetFanTuningStates
        if (saved is null)
        {
            WarnOnce("takeover", "current curve unreadable; not taking the fan");
            return false;
        }
        var zeroRpm = _zeroRpmSupported ? ReadZeroRpm(_fan, 9) : null; // GetZeroRPMState
        if (!AdlxTakeovers.Save(_pnp, saved, zeroRpm))
        {
            WarnOnce("takeover:save", "saving the current curve failed; not taking the fan");
            return false;
        }
        _saved = saved;
        _savedZeroRpm = zeroRpm;
        _takenOver = true;
        ServiceLog.Info($"[amd-fan] '{_name}': taking over the fan, saved curve {Describe(_saved)} zeroRpm={_savedZeroRpm?.ToString() ?? "n/a"}");
        return true;
    }

    /// <summary>Restores the saved curve. False keeps the fan taken over and schedules <see cref="RetryRelease"/>.</summary>
    public bool Release()
    {
        lock (AdlxGpuFans.Sync)
        {
            if (!_takenOver) return true;
            // Released from here on, whatever the driver does: a calibration lease must not re-take the old duty.
            _duty = null;
            var zeroRpm = _savedZeroRpm;
            var result = WriteCurve("restore", _saved, 0);
            // A driver update can change the point count or ranges, so the snapshot never fits again.
            if (result == Write.Unfit && _fan1 != 0 && ReadCurve(_fan1, 23) is { } defaults) // GetDefaultFanTuningStates
            {
                WarnOnce("restore:defaults", "saved curve no longer fits the driver; restoring the driver's default curve");
                result = WriteCurve("restore-defaults", defaults, 0);
                zeroRpm = ReadZeroRpm(_fan1, 27) ?? zeroRpm; // GetDefaultZeroRPMState
            }
            if (result == Write.Unfit)
            {
                ServiceLog.Warn($"[amd-fan] '{_name}': no curve the driver accepts is left to restore; reset fan tuning in Radeon Software");
                if (_zeroRpmSupported && zeroRpm is bool restore) WriteZeroRpm(restore);
                AdlxTakeovers.Clear(_pnp);
                Forget();
                return false;
            }
            if (result != Write.Ok || (_zeroRpmSupported && zeroRpm is bool z && !WriteZeroRpm(z)))
            {
                _releaseRetryAtMs = Environment.TickCount64 + ReleaseRetryMs;
                return false;
            }

            AdlxTakeovers.Clear(_pnp);
            ServiceLog.Info($"[amd-fan] '{_name}': released, curve {Describe(_saved)} zeroRpm={_savedZeroRpm?.ToString() ?? "n/a"}");
            Forget();
            return true;
        }
    }

    /// <summary><see cref="Release"/> that gives up after <paramref name="wait"/> when another ADLX call holds the lock, leaving it to <see cref="RetryRelease"/>.</summary>
    public bool TryRelease(TimeSpan wait)
    {
        if (!Monitor.TryEnter(AdlxGpuFans.Sync, wait))
        {
            _releaseRequested = true;
            return false;
        }
        try { return Release(); }
        finally { Monitor.Exit(AdlxGpuFans.Sync); }
    }

    private void Forget()
    {
        _takenOver = false;
        _duty = null;
        _saved = Array.Empty<(int, int)>();
        _savedZeroRpm = null;
        _releaseRetryAtMs = null;
    }

    private const int ReleaseRetryMs = 10_000;

    /// <summary>Retries a release that failed, so a fan handed back by the guard or the user does not stay on Nexus's curve.</summary>
    public void RetryRelease()
    {
        lock (AdlxGpuFans.Sync)
        {
            if (_releaseRequested || (_releaseRetryAtMs is long at && Environment.TickCount64 >= at))
            {
                _releaseRequested = false;
                Release();
            }
        }
    }

    private enum Write { Ok, Refused, Unfit }

    /// <summary>Writes <paramref name="points"/>, or a flat curve at <paramref name="flatSpeed"/> when null.</summary>
    private Write WriteCurve(string op, (int Speed, int Temp)[]? points, int flatSpeed)
    {
        // The empty list is the ADLX sample's path; the current-states list is the fallback when its setters do not stick.
        foreach (var source in new[] { 5, 4 }) // GetEmptyFanTuningStates, GetFanTuningStates
        {
            nint list = 0;
            var rc = Adlx.GetOut(_fan, source, &list);
            if (!Check(rc) || list == 0)
            {
                WarnOnce($"{op}:list:{source}:{rc}", $"{op}: fan tuning state list (slot {source}) unavailable ({Adlx.Name(rc)})");
                return Write.Refused;
            }
            try
            {
                var count = (int)((delegate* unmanaged[Stdcall]<nint, uint>)Adlx.Slot(list, 3))(list); // Size
                points ??= AdlxFanCurve.Flat(flatSpeed, _temp, count);
                if (points.Length != count)
                {
                    WarnOnce($"{op}:count", $"{op}: curve has {points.Length} points, driver expects {count}");
                    return Write.Unfit;
                }
                if (!Fill(op, source, list, points)) continue;

                // IsValidFanTuningStates reports index 0 for the driver's own curve on an RX 9070 XT (ADLX 1.5),
                // so only its result code gates; SetFanTuningStates validates again.
                var invalidIndex = -1;
                rc = ((delegate* unmanaged[Stdcall]<nint, nint, int*, int>)Adlx.Slot(_fan, 6))(_fan, list, &invalidIndex); // IsValidFanTuningStates
                if (!Check(rc))
                {
                    WarnOnce($"{op}:valid:{rc}", $"{op}: IsValidFanTuningStates failed ({Adlx.Name(rc)})");
                    return Write.Refused;
                }
                if (invalidIndex != -1) InfoOnce($"{op}:index:{invalidIndex}", $"{op}: IsValidFanTuningStates reports index {invalidIndex} for {Describe(points)}; applying");

                rc = ((delegate* unmanaged[Stdcall]<nint, nint, int>)Adlx.Slot(_fan, 7))(_fan, list); // SetFanTuningStates
                if (!Check(rc))
                {
                    WarnOnce($"{op}:set:{rc}", rc == Adlx.ResetNeeded
                        ? $"{op}: refused while Radeon Software's automatic tuning is on; switch its GPU tuning to manual"
                        : $"{op}: SetFanTuningStates {Describe(points)} failed ({Adlx.Name(rc)})");
                    return rc == Adlx.InvalidArgs ? Write.Unfit : Write.Refused;
                }
                var applied = ReadCurve(_fan, 4); // GetFanTuningStates
                InfoOnce($"{op}:applied", $"{op}: wrote {Describe(points)} via list {source}, driver reports {Describe(applied)}");
                if (applied is not null && !applied.SequenceEqual(points))
                    WarnOnce($"{op}:mismatch", $"{op}: wrote {Describe(points)}, driver reports {Describe(applied)}");
                return Write.Ok;
            }
            finally
            {
                Adlx.Release(list);
            }
        }
        WarnOnce($"{op}:setters", $"{op}: fan tuning states ignore SetFanSpeed/SetTemperature on both lists");
        return Write.Refused;
    }

    /// <summary>Sets every state and reads it back; false when a setter fails or its value does not stick.</summary>
    private bool Fill(string op, int source, nint list, (int Speed, int Temp)[] points)
    {
        for (var i = 0; i < points.Length; i++)
        {
            nint state = 0;
            var rc = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Adlx.Slot(list, 11))(list, (uint)i, &state); // At_ManualFanTuningStateList
            if (!Check(rc) || state == 0)
            {
                WarnOnce($"{op}:at:{source}:{rc}", $"{op}: state {i} of list {source} unavailable ({Adlx.Name(rc)})");
                return false;
            }
            var speedRc = ((delegate* unmanaged[Stdcall]<nint, int, int>)Adlx.Slot(state, 4))(state, points[i].Speed); // SetFanSpeed
            var tempRc = ((delegate* unmanaged[Stdcall]<nint, int, int>)Adlx.Slot(state, 6))(state, points[i].Temp); // SetTemperature
            int speed = 0, temp = 0;
            ((delegate* unmanaged[Stdcall]<nint, int*, int>)Adlx.Slot(state, 3))(state, &speed); // GetFanSpeed
            ((delegate* unmanaged[Stdcall]<nint, int*, int>)Adlx.Slot(state, 5))(state, &temp); // GetTemperature
            Adlx.Release(state);
            if (!Adlx.Succeeded(speedRc) || !Adlx.Succeeded(tempRc) || speed != points[i].Speed || temp != points[i].Temp)
            {
                WarnOnce($"{op}:fill:{source}", $"{op}: list {source} state {i}: SetFanSpeed {Adlx.Name(speedRc)}, SetTemperature {Adlx.Name(tempRc)}, wrote {points[i].Speed}%@{points[i].Temp}C, reads {speed}%@{temp}C");
                return false;
            }
        }
        return true;
    }

    private bool WriteZeroRpm(bool on)
    {
        var rc = ((delegate* unmanaged[Stdcall]<nint, byte, int>)Adlx.Slot(_fan, 10))(_fan, on ? (byte)1 : (byte)0); // SetZeroRPMState
        if (Check(rc)) return true;
        WarnOnce($"zero:{on}:{rc}", $"SetZeroRPMState({on}) failed ({Adlx.Name(rc)})");
        return false;
    }

    /// <summary>True when ADLX was torn down under this object (OS shutdown, driver reset); the provider then reopens the card.</summary>
    public bool Dead { get; private set; }

    private bool Check(int rc)
    {
        if (Adlx.IsDead(rc)) Dead = true;
        return Adlx.Succeeded(rc);
    }

    private (int Speed, int Temp)[]? ReadCurve(nint obj, int slot)
    {
        nint list = 0;
        var rc = Adlx.GetOut(obj, slot, &list);
        if (!Check(rc) || list == 0)
        {
            WarnOnce($"read:{slot}:{rc}", $"reading fan curve (slot {slot}) failed ({Adlx.Name(rc)})");
            return null;
        }
        try
        {
            var count = (int)((delegate* unmanaged[Stdcall]<nint, uint>)Adlx.Slot(list, 3))(list); // Size
            var points = new (int Speed, int Temp)[count];
            for (var i = 0; i < count; i++)
            {
                nint state = 0;
                rc = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Adlx.Slot(list, 11))(list, (uint)i, &state); // At_ManualFanTuningStateList
                if (!Adlx.Succeeded(rc) || state == 0) return null;
                int speed = 0, temp = 0;
                ((delegate* unmanaged[Stdcall]<nint, int*, int>)Adlx.Slot(state, 3))(state, &speed); // GetFanSpeed
                ((delegate* unmanaged[Stdcall]<nint, int*, int>)Adlx.Slot(state, 5))(state, &temp); // GetTemperature
                Adlx.Release(state);
                points[i] = (speed, temp);
            }
            return points.Length > 0 ? points : null;
        }
        finally
        {
            Adlx.Release(list);
        }
    }

    private static bool? ReadZeroRpm(nint obj, int slot)
    {
        byte on = 0;
        var rc = ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Adlx.Slot(obj, slot))(obj, &on);
        return Adlx.Succeeded(rc) ? on != 0 : null;
    }

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key)) ServiceLog.Warn($"[amd-fan] '{_name}': {message}");
    }

    private void InfoOnce(string key, string message)
    {
        if (_warned.Add(key)) ServiceLog.Info($"[amd-fan] '{_name}': {message}");
    }

    private static string Describe((int Speed, int Temp)[]? points) =>
        points is null ? "unreadable" : string.Join(" ", points.Select(p => $"{p.Speed}%@{p.Temp}C"));
}

/// <summary>
/// Curves Nexus has taken over, one line per card (PNP id, Zero RPM, points), kept on disk
/// until handed back: a driver keeps a curve set through ADLX after the process dies.
/// Callers hold <see cref="AdlxGpuFans.Sync"/>.
/// </summary>
internal static class AdlxTakeovers
{
    private static string FilePath => Path.Combine(NexusDataPaths.NexusRoot(), "amd-gpu-fan-takeover.txt");

    public static (int Speed, int Temp)[]? Load(string pnp, out bool? zeroRpm)
    {
        zeroRpm = null;
        foreach (var line in ReadLines() ?? Array.Empty<string>())
        {
            var parts = line.Split('\t');
            if (parts.Length != 3 || parts[0] != pnp) continue;
            var points = new List<(int, int)>();
            foreach (var point in parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = point.Split('@');
                if (pair.Length != 2 || !int.TryParse(pair[0], out var speed) || !int.TryParse(pair[1], out var temp)) return null;
                points.Add((speed, temp));
            }
            zeroRpm = parts[1] switch { "1" => true, "0" => false, _ => null };
            return points.Count > 0 ? points.ToArray() : null;
        }
        return null;
    }

    public static bool Save(string pnp, (int Speed, int Temp)[] curve, bool? zeroRpm)
    {
        var zero = zeroRpm switch { true => "1", false => "0", null => "-" };
        return ReadLines() is { } lines
            && Write(lines.Where(l => !l.StartsWith(pnp + "\t", StringComparison.Ordinal))
                .Append($"{pnp}\t{zero}\t{string.Join(" ", curve.Select(p => $"{p.Speed}@{p.Temp}"))}"));
    }

    public static void Clear(string pnp)
    {
        if (ReadLines() is { } lines) Write(lines.Where(l => !l.StartsWith(pnp + "\t", StringComparison.Ordinal)));
    }

    /// <summary>Null when the file exists but cannot be read, so no caller rewrites it from nothing.</summary>
    private static string[]? ReadLines()
    {
        try { return File.Exists(FilePath) ? File.ReadAllLines(FilePath) : Array.Empty<string>(); }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[amd-fan] reading {FilePath} failed: {ex.Message}");
            return null;
        }
    }

    private static bool Write(IEnumerable<string> lines)
    {
        try
        {
            var content = string.Join("\n", lines);
            if (content.Length == 0)
            {
                File.Delete(FilePath);
                return true;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicJsonFile.Write(FilePath, content + "\n");
            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[amd-fan] writing {FilePath} failed: {ex.Message}");
            return false;
        }
    }
}

internal static unsafe class Adlx
{
    // ADLX_RESULT (ADLXDefines.h).
    internal const int InvalidArgs = 4;
    internal const int ResetNeeded = 18;

    private static readonly string[] Names =
    {
        "OK", "ALREADY_ENABLED", "ALREADY_INITIALIZED", "FAIL", "INVALID_ARGS", "BAD_VER",
        "UNKNOWN_INTERFACE", "TERMINATED", "ADL_INIT_ERROR", "NOT_FOUND", "INVALID_OBJECT",
        "ORPHAN_OBJECTS", "NOT_SUPPORTED", "PENDING_OPERATION", "GPU_INACTIVE", "GPU_IN_USE",
        "TIMEOUT_OPERATION", "NOT_ACTIVE", "RESET_NEEDED",
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct IntRange
    {
        public int Min;
        public int Max;
        public int Step;
    }

    /// <summary>ADLX_SUCCEEDED: OK, ALREADY_ENABLED and ALREADY_INITIALIZED all count as success.</summary>
    internal static bool Succeeded(int rc) => rc is 0 or 1 or 2;

    /// <summary>TERMINATED, INVALID_OBJECT, ORPHAN_OBJECTS: every interface from this ADLX instance is unusable.</summary>
    internal static bool IsDead(int rc) => rc is 7 or 10 or 11;

    internal static string Name(int rc) => rc >= 0 && rc < Names.Length ? Names[rc] : rc.ToString();

    internal static nint Slot(nint obj, int index) => (*(nint**)obj)[index];

    internal static int GetOut(nint obj, int slot, nint* result) =>
        ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(obj, slot))(obj, result);

    /// <summary>IADLXInterface::Release (slot 1).</summary>
    internal static void Release(nint obj) => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(obj, 1))(obj);

    /// <summary>IADLXInterface::QueryInterface (slot 2); 0 when the object does not implement <paramref name="iid"/>.</summary>
    internal static nint Query(nint obj, string iid)
    {
        nint result = 0;
        fixed (char* id = iid)
        {
            var rc = ((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Slot(obj, 2))(obj, id, &result);
            return Succeeded(rc) ? result : 0;
        }
    }

    internal static string GetString(nint obj, int slot)
    {
        byte* value = null;
        var rc = ((delegate* unmanaged[Stdcall]<nint, byte**, int>)Slot(obj, slot))(obj, &value);
        return Succeeded(rc) && value != null ? Marshal.PtrToStringUTF8((nint)value) ?? "" : "";
    }
}
