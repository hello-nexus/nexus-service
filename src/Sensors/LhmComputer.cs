using System;
using System.Diagnostics;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Storage;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Lifecycle;
using Nexus.Service.Persistence;
using RAMSPDToolkit.Windows.Driver;
using RAMSPDToolkit.Windows.Driver.Interfaces;

namespace Nexus.Service.Sensors;

/// <summary>
/// Shared singleton wrapping the LibreHardwareMonitor Computer instance.
/// Both the sensor provider and the fan control provider share this to avoid
/// resource duplication and driver conflicts.
///
/// Thread safety: the lock in Update() ensures only one caller updates at a
/// time. Reads of sensor.Value after an Update() are safe without locking
/// (they return the last-cached value).
///
/// Boot semantics: LHM's <see cref="Computer.Open"/> loads its kernel driver,
/// walks ACPI / SMBIOS / PCI / SuperIO and costs ~50 MB working set plus 1–3 s
/// (2.5 s on T1 with IT8696E + nvidia GPU). To keep host startup off that
/// critical path, the ctor schedules Open() on the thread pool and returns
/// immediately. <see cref="Update"/> no-ops until Open() finishes, so any
/// /sensors or /cooling/* request issued in the warmup window returns empty
/// data instead of blocking - the dashboard hydrates on its next poll. On a
/// healthy boot the PawnIoBootGate wait below resolves in well under a
/// second; on an install/repair boot it holds Open until the driver is
/// usable. AutoRestoreOnStart waits for the open to finish before applying a
/// preset (via SignalLhmOpened), and CurveEngine re-drives channels as they
/// appear, so the delayed open never strands fan control.
/// </summary>
public sealed class LhmComputer : IDisposable
{
    private readonly Computer _computer;
    private readonly Task _openTask;
    private readonly object _updateLock = new();
    private long _lastUpdateTicks;
    private readonly IConfigStore _config;
    private readonly DeviceControlGate _gate;
    private readonly HashSet<string> _smartSeeded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool?> _rotational = new(StringComparer.Ordinal);

    // Hard cap of one hardware walk per second. Measured from the walk's START,
    // not its end, so the cap is a real 1Hz rather than 1Hz-plus-walk-duration
    // (a walk costs ~100ms). 990 rather than 1000 so the 1s tickers
    // (CurveEngine, MetricsSampler, MonitoringBroadcaster) always clear it
    // despite timer jitter instead of beating against it and skipping every
    // other tick, which would age their data to 2s.
    private const long NormalFloorMs = 990;

    // Fan calibration samples RPM every 250ms to detect settle; the normal
    // floor would feed it the same cached value four times.
    private const long FastFloorMs = 100;

    public LhmComputer(IConfigStore config, DeviceControlGate gate)
    {
        _config = config;
        _gate = gate;
        // MemoryGroup probes 0x50-0x57 through its own PawnIO SMBus module at
        // Open and reads each DIMM thermal sensor per walk, which knocks iCUE
        // off the same bus. Seated before Open, the stub keeps the group's
        // RAM-usage sensors and skips the DIMM path.
        var spdDisabled = !_gate.IsEnabled(SmbusDramHandler.HandlerId);
        if (spdDisabled)
        {
            DriverManager.Driver = NoSpdDriver.Instance;
            Console.WriteLine("[lhm] DIMM SPD polling off: Nexus Control for the memory device is off");
        }
        // With GPU disabled LHM never constructs its AMD/NVIDIA GPU nodes, so
        // no ADL FrameMetrics/PMLog session is opened and no per-node D3DKMT
        // statistics are queried for the process lifetime (the
        // DisableGpuMonitoring diagnostic switch - see NexusSettings).
        var gpuEnabled = !config.Load().DisableGpuMonitoring;
        if (!gpuEnabled)
        {
            Console.WriteLine("[lhm] GPU monitoring disabled by settings");
        }
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = gpuEnabled,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsNetworkEnabled = true,
            IsBatteryEnabled = true,
            IsPsuEnabled = true,
        };
        _openTask = Task.Run(async () =>
        {
            // Open() enumerates SuperIO and DIMM SPD exactly once, so a probe
            // lost to another SMBus master is a sensor missing for the whole
            // session. The user-configured window yields the boot-time bus
            // burst first; zero by default.
            await StartupDelayGate.WaitAsync().ConfigureAwait(false);

            // Open() enumerates SuperIO exactly once, and it needs the PawnIO
            // device up - wait for the boot-time install/repair to finish so
            // a just-repaired driver yields motherboard sensors in the same
            // boot. Capped in the gate; no-op on unarmed hosts.
            await PawnIoBootGate.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            var sw = Stopwatch.StartNew();
            try
            {
                _computer.Open();
                Console.WriteLine($"[lhm] background open complete in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[lhm] background open failed after {sw.ElapsedMilliseconds}ms: {ex.Message}");
            }
            finally
            {
                PawnIoBootGate.SignalLhmOpened();
            }
        });
        _gate.Changed += OnControlGateChanged;
        // A toggle between the read above and this subscription would otherwise
        // hold until the next restart.
        if (_gate.IsEnabled(SmbusDramHandler.HandlerId) == spdDisabled)
        {
            OnControlGateChanged(SmbusDramHandler.HandlerId, !spdDisabled);
        }
    }

    public Computer Instance => _computer;

    private void OnControlGateChanged(string handlerId, bool enabled)
    {
        if (!string.Equals(handlerId, SmbusDramHandler.HandlerId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        // The swap needs Open() finished; the open task never faults, so the
        // continuation always runs. State is re-read under the lock rather than
        // captured here, so two racing toggles cannot land out of order.
        _ = _openTask.ContinueWith(_ => ApplySpdGate(), TaskScheduler.Default);
    }

    /// <summary>Rebuilds the memory group live: IsMemoryEnabled removes and re-adds it on an open Computer, and the driver seated at that moment decides whether the new group probes SPD.</summary>
    private void ApplySpdGate()
    {
        try
        {
            bool enabled;
            lock (_updateLock)
            {
                enabled = _gate.IsEnabled(SmbusDramHandler.HandlerId);
                // Group first (cancels its retry task, closes its DIMMs), then
                // the driver swap. Re-enabling probes eight addresses
                // synchronously under the lock.
                _computer.IsMemoryEnabled = false;
                // Detach, never UnloadDriver: Unload disposes the PawnIO modules,
                // and the closed group's SPDAccessor finalizers reset the DIMM
                // page through them later, dereferencing the disposed module on
                // the finalizer thread and terminating the process. The old
                // modules stay open until the next re-enable re-detects buses.
                DriverManager.Driver = enabled ? null : NoSpdDriver.Instance;
                _computer.IsMemoryEnabled = true;
            }
            Console.WriteLine(enabled
                ? "[lhm] DIMM SPD polling on: Nexus Control for the memory device turned on"
                : "[lhm] DIMM SPD polling off: Nexus Control for the memory device turned off");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[lhm] DIMM SPD gate apply failed: {ex.Message}");
        }
    }

    /// <summary>A RAMSPDToolkit driver that reports open so MemoryGroup keeps it instead of installing its PawnIO one, and fails to load because it is none of the three kinds DriverManager accepts, so no bus is ever detected.</summary>
    private sealed class NoSpdDriver : IDriver
    {
        public static readonly NoSpdDriver Instance = new();
        public bool IsOpen => true;
        public bool Load() => false;
        public void Unload() { }
    }

    /// <summary>
    /// Completes when the background <see cref="Computer.Open"/> has finished
    /// (or failed). Consumers that need fully-enumerated hardware before they
    /// read sensor / hardware-name data should await this. Completes even if
    /// Open() threw - callers should still handle empty Hardware collections.
    /// </summary>
    public Task OpenTask => _openTask;

    /// <summary>
    /// Update all hardware sensors. Thread-safe - concurrent callers are serialized.
    /// Capped at one hardware walk per second, which collapses the redundant
    /// updates from multiple callers (MonitoringBroadcaster, CurveEngine, HTTP
    /// handlers) into a single iteration; callers inside that window read the
    /// values the previous walk cached. <see cref="SensorRefresh.Fast"/> lowers
    /// the cap for fan calibration, <see cref="SensorRefresh.Force"/> bypasses
    /// it and refreshes every group.
    /// Returns immediately if the background <see cref="Computer.Open"/> hasn't
    /// finished yet - callers see an empty <see cref="Computer.Hardware"/>
    /// collection and degrade to "no sensors" until warmup completes.
    /// </summary>
    public void Update(SensorRefresh refresh = SensorRefresh.Normal)
    {
        if (!_openTask.IsCompletedSuccessfully) return;

        lock (_updateLock)
        {
            var now = Environment.TickCount64;
            if (refresh != SensorRefresh.Force)
            {
                var floorMs = refresh == SensorRefresh.Fast ? FastFloorMs : NormalFloorMs;
                if (now - _lastUpdateTicks < floorMs)
                {
                    return;
                }
            }

            _lastUpdateTicks = now;
            var monitoring = _config.Load().Monitoring;
            // LibreHardwareMonitor's IntelCpu.Update sleeps 1ms per physical core
            // in its core-clock loop. At Windows' default 15.6ms timer tick that
            // is ~188ms on a 12-core part - 57% of the whole walk, all of it
            // sleeping. Holding 1ms resolution across the walk turns each of
            // those into ~1-2ms.
            using (WindowsTimerResolution.Elevate())
            {
                foreach (var hw in _computer.Hardware)
                {
                    // StorageDevice keeps its skip counter per instance and reads the static
                    // at the top of its own Update, so setting it here is per-drive. Throughput
                    // and free space are on separate paths and still refresh every walk.
                    if (hw.HardwareType == HardwareType.Storage)
                        StorageDevice.SmartUpdateCycleCount = SmartCyclesFor(hw, monitoring, refresh);

                    hw.Update();
                    foreach (var sub in hw.SubHardware)
                        sub.Update();
                }
            }
        }
    }

    private uint SmartCyclesFor(IHardware hw, MonitoringSettings monitoring, SensorRefresh refresh)
    {
        var identifier = hw.Identifier.ToString();
        var seconds = monitoring.SmartPollPerDrive
            ? monitoring.SmartPollSeconds.TryGetValue(identifier, out var configured)
                ? configured
                : SmartPollPolicy.DefaultSecondsFor(IsRotational(hw))
            : monitoring.SmartPollDefaultSeconds;

        // Never is absolute: not even a Force walk (the diagnostics health snapshot)
        // touches a drive the user has opted out of.
        if (seconds <= SmartPollPolicy.NeverSeconds) return SmartPollPolicy.ToCycleCount(seconds);

        // Read once on the walk that first sees a drive so temperature and health have
        // a value before the configured period elapses, and on any Force walk, which
        // only ever comes from an explicit read of drive health.
        if (_smartSeeded.Add(identifier) || refresh == SensorRefresh.Force) return 1;

        return SmartPollPolicy.ToCycleCount(seconds);
    }

    /// <summary>Cached: the seek-penalty query opens the physical drive, so it runs once per drive.</summary>
    public bool? IsRotational(IHardware hw)
    {
        var identifier = hw.Identifier.ToString();
        lock (_updateLock)
        {
            if (_rotational.TryGetValue(identifier, out var cached)) return cached;
            bool? value = null;
            if (hw is StorageDevice drive && drive.Storage?.StorageDeviceNumber is { } number)
            {
                value = DriveRotationProbe.IsRotational(number);
            }
            _rotational[identifier] = value;
            return value;
        }
    }

    public void Dispose()
    {
        _gate.Changed -= OnControlGateChanged;
        // Wait for the background open to finish before Close(): LHM's
        // Computer.Close() isn't documented thread-safe against an in-flight
        // Open(), and Dispose() is only called at service shutdown so the
        // wait is rare and not on any user-visible path.
        try { _openTask.Wait(); } catch { /* shutdown best-effort */ }
        _computer.Close();
    }
}


/// <summary>Holds the process timer resolution at 1ms for the duration of the
/// scope. LibreHardwareMonitor's CPU update sleeps 1ms per core; at the default
/// 15.6ms tick those sleeps dominate the whole sensor walk.</summary>
internal static partial class WindowsTimerResolution
{
    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);

    private const uint PeriodMs = 1;
    private const uint TimerrNoError = 0;

    internal static Scope Elevate() => new(TimeBeginPeriod(PeriodMs) == TimerrNoError);

    internal readonly struct Scope : IDisposable
    {
        private readonly bool _held;
        internal Scope(bool held) => _held = held;
        public void Dispose()
        {
            if (_held) TimeEndPeriod(PeriodMs);
        }
    }
}
