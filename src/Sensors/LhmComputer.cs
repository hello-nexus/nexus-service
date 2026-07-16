using System;
using System.Diagnostics;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using Nexus.Service.Lifecycle;

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

    public LhmComputer()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
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
    }

    public Computer Instance => _computer;

    /// <summary>
    /// Completes when the background <see cref="Computer.Open"/> has finished
    /// (or failed). Consumers that need fully-enumerated hardware before they
    /// read sensor / hardware-name data should await this. Completes even if
    /// Open() threw - callers should still handle empty Hardware collections.
    /// </summary>
    public Task OpenTask => _openTask;

    /// <summary>
    /// Update all hardware sensors. Thread-safe - concurrent callers are serialized.
    /// When <paramref name="minInterval"/> is provided, skips the update if the last
    /// refresh was more recent than the interval. This collapses redundant updates
    /// from multiple callers (MonitoringBroadcaster, CurveEngine, HTTP handlers)
    /// into at most one hardware iteration per interval.
    /// Returns immediately if the background <see cref="Computer.Open"/> hasn't
    /// finished yet - callers see an empty <see cref="Computer.Hardware"/>
    /// collection and degrade to "no sensors" until warmup completes.
    /// </summary>
    public void Update(TimeSpan? minInterval = null)
    {
        if (!_openTask.IsCompletedSuccessfully) return;

        lock (_updateLock)
        {
            if (minInterval.HasValue)
            {
                var now = Environment.TickCount64;
                if (now - _lastUpdateTicks < (long)minInterval.Value.TotalMilliseconds)
                    return;
            }

            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                foreach (var sub in hw.SubHardware)
                    sub.Update();
            }
            _lastUpdateTicks = Environment.TickCount64;
        }
    }

    public void Dispose()
    {
        // Wait for the background open to finish before Close(): LHM's
        // Computer.Close() isn't documented thread-safe against an in-flight
        // Open(), and Dispose() is only called at service shutdown so the
        // wait is rare and not on any user-visible path.
        try { _openTask.Wait(); } catch { /* shutdown best-effort */ }
        _computer.Close();
    }
}
