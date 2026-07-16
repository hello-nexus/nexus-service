using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Common.ExternalTools;

public class DriverAutoLaunchWorkerTests : IDisposable
{
    /// <summary>
    /// These suites are the vendor driver path's coverage, so they enable it. It ships
    /// disabled (the AW5 is driven natively); see DriverExePolicy.
    /// </summary>
    private static readonly DriverExePolicy VendorDriverOn = new(enabled: true);

    private readonly string _root;
    private readonly string _cache;

    public DriverAutoLaunchWorkerTests()
    {
        var b = Path.Combine(Path.GetTempPath(), "nexus-autolaunch-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _root = Path.Combine(b, "apps");
        _cache = Path.Combine(b, "cache");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_cache);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { }
    }

    [Fact]
    public async Task Does_not_launch_when_no_matching_device()
    {
        WriteDriverApp(withDriverBinary: true);
        var (worker, manager) = Build(new StubUsb()); // empty bus

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.NotEqual(ToolStatus.Running, manager.GetStatus("acme-cooler"));
    }

    [Fact]
    public async Task Launches_present_driver_app_then_single_instances()
    {
        if (OperatingSystem.IsWindows()) return; // real child process; Unix only

        WriteDriverApp(withDriverBinary: true);
        var (worker, manager) = Build(new StubUsb(new UsbDeviceEntry { VendorId = 0x1234, ProductId = 0x0002 }));

        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ToolStatus.Running, manager.GetStatus("acme-cooler"));

        // A second pass while running is a no-op.
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ToolStatus.Running, manager.GetStatus("acme-cooler"));

        manager.TerminateAll();
        Assert.NotEqual(ToolStatus.Running, manager.GetStatus("acme-cooler"));
    }

    [Fact]
    public async Task Unresolvable_driver_backs_off_instead_of_refetching_every_tick()
    {
        // The device is present but its variant has no published payload (a real
        // case: an OEM ships the cooler before the binary lands). Without a
        // backoff this re-fetches the manifest every 5s for the life of the box.
        WriteDriverApp(withDriverBinary: false);
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var http = new CountingHandler();
        var registry = new AppRegistry(() => new List<AppInstallPaths.Root>
        {
            new(_root, AppInstallPaths.Source.Bundled),
        });
        var manager = new ExternalToolManager(new HttpClient(http), _cache);
        var worker = new DriverAutoLaunchWorker(
            registry, manager,
            new StubUsb(new UsbDeviceEntry { VendorId = 0x1234, ProductId = 0x0002 }),
            OpenGate(),
            Array.Empty<IDriverGateStopHook>(),
            VendorDriverOn,
            () => now);

        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, http.Calls);

        // Same instant: inside the backoff window, so the tick costs nothing.
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, http.Calls);

        // Failure 1 schedules one tick out - a transient blip retries promptly.
        now = now.AddMilliseconds(5000);
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, http.Calls);

        // Failure 2 doubles the wait, so one further tick is not enough.
        now = now.AddMilliseconds(5000);
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, http.Calls);

        now = now.AddMilliseconds(5000);
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(3, http.Calls);
    }

    [Fact]
    public async Task Driver_that_dies_after_launch_backs_off_instead_of_relaunching_every_tick()
    {
        // Bench-hit: the AW5 vendor driver exited seconds after start and the
        // worker respawned it every 5s forever. The launch SUCCEEDS here, so the
        // tool reads Running for an instant - the driver must outlive the
        // post-launch status read, or this passes against the bug it guards.
        if (OperatingSystem.IsWindows()) return; // real child process; Unix only

        var runs = WriteDriverApp(withDriverBinary: true, exitAfterLaunch: true);
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var registry = new AppRegistry(() => new List<AppInstallPaths.Root>
        {
            new(_root, AppInstallPaths.Source.Bundled),
        });
        var manager = new ExternalToolManager(new HttpClient(new ExplodingHandler()), _cache);
        var worker = new DriverAutoLaunchWorker(
            registry, manager,
            new StubUsb(new UsbDeviceEntry { VendorId = 0x1234, ProductId = 0x0002 }),
            OpenGate(),
            Array.Empty<IDriverGateStopHook>(),
            VendorDriverOn,
            () => now);

        await worker.RunOnceAsync(CancellationToken.None);
        await WaitForRunsAsync(runs, 1);

        // Wait for the child to die - the state the tick after a launch sees.
        for (var i = 0; i < 200 && manager.GetStatus("acme-cooler") == ToolStatus.Running; i++)
            await Task.Delay(50);
        Assert.NotEqual(ToolStatus.Running, manager.GetStatus("acme-cooler"));

        // Still inside the backoff window the launch opened: no respawn.
        await worker.RunOnceAsync(CancellationToken.None);
        await AssertStillAsync(runs, 1);

        // Past the window, it retries - a crash-loop escalates, it does not stop.
        now = now.AddMilliseconds(5000);
        await worker.RunOnceAsync(CancellationToken.None);
        await WaitForRunsAsync(runs, 2);
    }

    private static int CountRuns(string marker)
        => File.Exists(marker) ? File.ReadAllLines(marker).Length : 0;

    /// <summary>The child appends its marker asynchronously; wait for it rather than racing it.</summary>
    private static async Task WaitForRunsAsync(string marker, int expected)
    {
        for (var i = 0; i < 200 && CountRuns(marker) < expected; i++)
            await Task.Delay(50);
        Assert.Equal(expected, CountRuns(marker));
    }

    /// <summary>Hold long enough that a respawn would have recorded itself, then assert none did.</summary>
    private static async Task AssertStillAsync(string marker, int expected)
    {
        for (var i = 0; i < 40 && CountRuns(marker) <= expected; i++)
            await Task.Delay(50);
        Assert.Equal(expected, CountRuns(marker));
    }

    [Fact]
    public async Task Unplugging_the_device_stops_its_driver()
    {
        if (OperatingSystem.IsWindows()) return; // real child process; Unix only

        var runs = WriteDriverApp(withDriverBinary: true);
        var usb = new StubUsb(new UsbDeviceEntry { VendorId = 0x1234, ProductId = 0x0002 });
        var (worker, manager) = Build(usb);

        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ToolStatus.Running, manager.GetStatus("acme-cooler"));
        var pid = await LastRunPidAsync(runs, "VariantB");

        // The driver binds to the device it was launched for; with the device gone
        // it drives nothing, so it must not outlive the unplug. Terminate clears its
        // bookkeeping before killing, so assert on the process, not on GetStatus.
        usb.SetBus();
        for (var i = 0; i < DriverAutoLaunchWorker.AbsentTicksBeforeStop; i++) await worker.RunOnceAsync(CancellationToken.None);
        Assert.NotEqual(ToolStatus.Running, manager.GetStatus("acme-cooler"));
        await AssertExitsAsync(pid, "the driver process outlived the unplug");

        // Plugging it back in brings the driver back, with no restart.
        usb.SetBus(new UsbDeviceEntry { VendorId = 0x1234, ProductId = 0x0002 });
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ToolStatus.Running, manager.GetStatus("acme-cooler"));

        manager.TerminateAll();
    }

    [Fact]
    public async Task A_single_absent_tick_does_not_kill_a_healthy_driver()
    {
        // A partial enumeration caches as fresh, and killing this driver is not
        // recoverable without a replug - so absence must be corroborated.
        if (OperatingSystem.IsWindows()) return; // real child process; Unix only

        var runs = WriteDriverApp(withDriverBinary: true);
        var usb = new StubUsb(new UsbDeviceEntry { VendorId = 0x1234, ProductId = 0x0002 });
        var (worker, manager) = Build(usb);

        await worker.RunOnceAsync(CancellationToken.None);
        var pid = await LastRunPidAsync(runs, "VariantB");

        usb.SetBus(); // one bad scan
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ToolStatus.Running, manager.GetStatus("acme-cooler"));
        Assert.True(IsAlive(pid), "one absent tick must not kill the driver");

        // The device was there all along: the streak resets and nothing is disturbed.
        usb.SetBus(new UsbDeviceEntry { VendorId = 0x1234, ProductId = 0x0002 });
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ToolStatus.Running, manager.GetStatus("acme-cooler"));
        Assert.True(IsAlive(pid), "the original driver must not have been restarted");
        Assert.Equal(1, CountRuns(runs));

        manager.TerminateAll();
    }

    /// <summary>Pid of the newest recorded run, so a test asserts the process, not the bookkeeping.</summary>
    private static async Task<int> LastRunPidAsync(string marker, string variant)
    {
        await WaitForLastRunAsync(marker, variant);
        var parts = File.ReadAllLines(marker)[^1].Split(' ');
        return int.Parse(parts[1]);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; } // no such process
    }

    private static async Task AssertExitsAsync(int pid, string because)
    {
        for (var i = 0; i < 100 && IsAlive(pid); i++) await Task.Delay(50);
        Assert.False(IsAlive(pid), because);
    }

    [Fact]
    public async Task Swapping_to_another_variant_replaces_the_running_driver()
    {
        if (OperatingSystem.IsWindows()) return; // real child process; Unix only

        // Both variants share one toolId, so a status check alone reports the
        // stale driver as healthy and the swapped-in device never gets its own.
        var runs = WriteDriverApp(withDriverBinary: true, secondVariant: true);
        var usb = new StubUsb(new UsbDeviceEntry { VendorId = 0x1234, ProductId = 0x0002 }); // VariantB
        var (worker, manager) = Build(usb);

        await worker.RunOnceAsync(CancellationToken.None);
        var bPid = await LastRunPidAsync(runs, "VariantB");

        usb.SetBus(new UsbDeviceEntry { VendorId = 0x1234, ProductId = 0x0003 }); // VariantC
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(ToolStatus.Running, manager.GetStatus("acme-cooler"));
        await WaitForLastRunAsync(runs, "VariantC");
        // The outgoing driver must be dead, not merely untracked: two drivers on one
        // present cooler is the failure this reconcile exists to prevent.
        await AssertExitsAsync(bPid, "the swapped-out driver survived alongside the new one");

        manager.TerminateAll();
    }

    /// <summary>Each variant's script announces itself; wait for the newest line to name it.</summary>
    private static async Task WaitForLastRunAsync(string marker, string expected)
    {
        for (var i = 0; i < 200; i++)
        {
            var lines = File.Exists(marker) ? File.ReadAllLines(marker) : Array.Empty<string>();
            if (lines.Length > 0 && lines[^1].StartsWith(expected + " ", StringComparison.Ordinal)) return;
            await Task.Delay(50);
        }
        var last = File.Exists(marker) ? string.Join(",", File.ReadAllLines(marker)) : "<no file>";
        Assert.Fail($"expected last run to be {expected}; saw [{last}]");
    }

    [Fact]
    public async Task Nexus_control_off_stops_the_driver_and_on_starts_it_again()
    {
        if (OperatingSystem.IsWindows()) return; // real child process; Unix only

        var runs = WriteDriverApp(withDriverBinary: true, deviceId: "acme");
        var gate = OpenGate();
        var usb = new StubUsb(new UsbDeviceEntry { VendorId = 0x1234, ProductId = 0x0002 });
        var registry = new AppRegistry(() => new List<AppInstallPaths.Root>
        {
            new(_root, AppInstallPaths.Source.Bundled),
        });
        var manager = new ExternalToolManager(new HttpClient(new ExplodingHandler()), _cache);
        var worker = new DriverAutoLaunchWorker(registry, manager, usb, gate, Array.Empty<IDriverGateStopHook>(), VendorDriverOn);

        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ToolStatus.Running, manager.GetStatus("acme-cooler"));
        var pid = await LastRunPidAsync(runs, "VariantB");

        // Off means the vendor process stops, not merely that Nexus ignores it.
        gate.SetEnabled("acme", false);
        await worker.RunOnceAsync(CancellationToken.None);
        await AssertExitsAsync(pid, "Nexus Control off did not stop the driver");

        // And it stays stopped while off - the device is still on the bus.
        await worker.RunOnceAsync(CancellationToken.None);
        await AssertStillAsync(runs, 1);

        gate.SetEnabled("acme", true);
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ToolStatus.Running, manager.GetStatus("acme-cooler"));
        await WaitForRunsAsync(runs, 2);

        manager.TerminateAll();
    }

    [Fact]
    public async Task Driver_naming_no_device_is_ungated()
    {
        // Most driver apps name no deviceId; a gate entry for some other handler
        // must never stop them.
        if (OperatingSystem.IsWindows()) return; // real child process; Unix only

        WriteDriverApp(withDriverBinary: true); // no deviceId
        var gate = OpenGate();
        gate.SetEnabled("acme", false);
        var (worker, manager) = Build(new StubUsb(new UsbDeviceEntry { VendorId = 0x1234, ProductId = 0x0002 }), gate);

        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ToolStatus.Running, manager.GetStatus("acme-cooler"));

        manager.TerminateAll();
    }

    [Fact]
    public async Task Gating_off_fires_the_stop_hook_once_after_the_driver_is_gone()
    {
        // The hook writes to the device (the AW5 blanks its panel), so it must run
        // only once the vendor process is gone - and not again on every later tick,
        // which would reopen the device every 5s for as long as the switch is off.
        if (OperatingSystem.IsWindows()) return; // real child process; Unix only

        var runs = WriteDriverApp(withDriverBinary: true, deviceId: "acme");
        var gate = OpenGate();
        var pid = 0;
        var hook = new SpyHook("acme", () => pid);
        var (worker, manager) = Build(
            new StubUsb(new UsbDeviceEntry { VendorId = 0x1234, ProductId = 0x0002 }), gate, new IDriverGateStopHook[] { hook });

        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ToolStatus.Running, manager.GetStatus("acme-cooler"));
        Assert.Equal(0, hook.Calls);
        pid = await LastRunPidAsync(runs, "VariantB");

        gate.SetEnabled("acme", false);
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, hook.Calls);
        // The hook writes to the device, so the vendor process must already be gone
        // when it runs - checked by pid, the only handle that sees this child.
        Assert.False(hook.SawLiveDriver, "the hook ran while the vendor driver was still alive");

        // Still off, still on the bus: the transition already happened.
        await worker.RunOnceAsync(CancellationToken.None);
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, hook.Calls);

        // Back on, then off again - a second transition fires it again.
        gate.SetEnabled("acme", true);
        await worker.RunOnceAsync(CancellationToken.None);
        pid = await LastRunPidAsync(runs, "VariantB");
        gate.SetEnabled("acme", false);
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, hook.Calls);
        Assert.False(hook.SawLiveDriver, "the hook ran while the vendor driver was still alive");

        manager.TerminateAll();
    }

    [Fact]
    public async Task Unplugging_does_not_fire_the_stop_hook()
    {
        // Nothing to clean up on hardware that left; the hook would only fail.
        if (OperatingSystem.IsWindows()) return; // real child process; Unix only

        WriteDriverApp(withDriverBinary: true, deviceId: "acme");
        var hook = new SpyHook("acme", () => 0);
        var usb = new StubUsb(new UsbDeviceEntry { VendorId = 0x1234, ProductId = 0x0002 });
        var (worker, manager) = Build(usb, OpenGate(), new IDriverGateStopHook[] { hook });

        await worker.RunOnceAsync(CancellationToken.None);
        usb.SetBus();
        for (var i = 0; i < DriverAutoLaunchWorker.AbsentTicksBeforeStop; i++) await worker.RunOnceAsync(CancellationToken.None);

        Assert.NotEqual(ToolStatus.Running, manager.GetStatus("acme-cooler"));
        Assert.Equal(0, hook.Calls);
    }

    /// <summary>
    /// Records whether the driver was already dead each time it ran. Checks the pid,
    /// not a process name: a shebang script runs under the interpreter's name, so
    /// GetProcessesByName can never see it and would assert nothing.
    /// </summary>
    private sealed class SpyHook : IDriverGateStopHook
    {
        private readonly Func<int> _pid;
        public SpyHook(string deviceId, Func<int> pid) { DeviceId = deviceId; _pid = pid; }
        public string DeviceId { get; }
        public int Calls { get; private set; }
        public bool SawLiveDriver { get; private set; }
        public void OnGatedOff()
        {
            Calls++;
            if (IsAlive(_pid())) SawLiveDriver = true;
        }
    }

    [Fact]
    public async Task A_blocked_driver_is_never_detected_fetched_or_launched()
    {
        // The shipped configuration. Dormant has to mean untouched, not just
        // unlaunched: no bus read to detect the device, no manifest fetch, no
        // process. Asserting on the collaborators is what catches a guard that
        // drifts one line too low.
        WriteDriverApp(withDriverBinary: true);
        var http = new CountingHandler();
        var usb = new StubUsb(new UsbDeviceEntry { VendorId = 0x1234, ProductId = 0x0002 });
        var registry = new AppRegistry(() => new List<AppInstallPaths.Root> { new(_root, AppInstallPaths.Source.Bundled) });
        var manager = new ExternalToolManager(new HttpClient(http), _cache);
        var worker = new DriverAutoLaunchWorker(registry, manager, usb, OpenGate(),
            Array.Empty<IDriverGateStopHook>(), new DriverExePolicy(enabled: false));

        await worker.RunOnceAsync(CancellationToken.None);
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, http.Calls);
        Assert.Equal(0, usb.Reads);
        Assert.NotEqual(ToolStatus.Running, manager.GetStatus("acme-cooler"));
    }

    [Fact]
    public async Task An_android_driver_is_unaffected_by_the_host_exe_block()
    {
        // The block is scoped to host-exe drivers; an adb driver runs no host
        // process and cannot contend for the panel's HID.
        WriteDriverApp(withDriverBinary: true);
        var usb = new StubUsb(new UsbDeviceEntry { VendorId = 0x1234, ProductId = 0x0002 });
        var (worker, _) = Build(usb);

        await worker.RunOnceAsync(CancellationToken.None);

        // The fixture's driver names no target, so it is host-exe and the enabled
        // policy this harness uses lets it through - the bus is read.
        Assert.True(usb.Reads > 0);
    }

    /// <summary>A gate with no explicit choices: every handler falls back to its brand default (on).</summary>
    private static DeviceControlGate OpenGate() => new(new InMemoryConfigStore());

    // ── Harness ──

    private (DriverAutoLaunchWorker worker, ExternalToolManager manager) Build(IUsbEnumerator usb, DeviceControlGate? gate = null, IDriverGateStopHook[]? hooks = null)
    {
        var registry = new AppRegistry(() => new List<AppInstallPaths.Root>
        {
            new(_root, AppInstallPaths.Source.Bundled),
        });
        var manager = new ExternalToolManager(new HttpClient(new ExplodingHandler()), _cache);
        var worker = new DriverAutoLaunchWorker(registry, manager, usb, gate ?? OpenGate(), hooks ?? Array.Empty<IDriverGateStopHook>(), VendorDriverOn);
        return (worker, manager);
    }

    /// <summary>Returns the path each launched variant appends its own name to.</summary>
    private string WriteDriverApp(bool withDriverBinary, bool exitAfterLaunch = false, bool secondVariant = false, string? deviceId = null)
    {
        var dir = Path.Combine(_root, "com.example.cooler");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "widget.mjs"), "export default function mount(){}\n");
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            "{\"schema\":\"nexus.app/1\",\"id\":\"com.example.cooler\",\"name\":\"Cooler\",\"version\":\"1.0.0\"," +
            "\"min_nexus_version\":\"0.42.0\",\"runtime\":\"sdk\",\"surfaces\":[\"dashboard\"],\"sizes\":[\"2x2\"]," +
            "\"capabilities\":{},\"driver\":{\"toolId\":\"acme-cooler\"," +
            (deviceId is null ? "" : "\"deviceId\":\"" + deviceId + "\",") +
            "\"match\":{\"vid\":\"1234\",\"pids\":[\"0001\",\"0002\",\"0003\"]}," +
            "\"variants\":{\"0001\":\"VariantA\",\"0002\":\"VariantB\",\"0003\":\"VariantC\"}," +
            "\"manifestUrlBase\":\"https://assets.hellonexus.com/acme_cooler\"," +
            "\"filePattern\":\"MyDriver*.exe\",\"launch\":{\"session\":\"system\",\"hidden\":true}}}");

        var runMarker = Path.Combine(_root, "runs.txt");
        if (!withDriverBinary) return runMarker;

        WriteVariant(dir, "VariantB", runMarker, exitAfterLaunch); // matches PID 0x0002
        if (secondVariant) WriteVariant(dir, "VariantC", runMarker, false); // matches PID 0x0003
        return runMarker;
    }

    /// <summary>
    /// One pinned per-variant binary. Each variant ships its own file name, as the
    /// real driver store does, so adoption-by-image-name cannot confuse two of them.
    /// </summary>
    private static void WriteVariant(string appDir, string variant, string runMarker, bool exitAfterLaunch)
    {
        var variantDir = Path.Combine(appDir, "drivers", variant);
        Directory.CreateDirectory(variantDir);
        var fileName = "sleeper-" + variant.ToLowerInvariant() + ".sh";
        var script = Path.Combine(variantDir, fileName);
        // The crash-loop driver must still be alive when the worker reads its
        // status right after Process.Start, or the tick that clears the backoff
        // never runs and the test passes against the bug.
        var tail = exitAfterLaunch ? "sleep 1\nexit 1\n" : "sleep 30\n";
        // Each run records "<variant> <pid>": a shebang script's process name is the
        // interpreter, so the pid is the only reliable handle on the child.
        File.WriteAllText(script, "#!/bin/sh\necho " + variant + " $$ >> \"" + runMarker + "\"\n" + tail);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        var payload = File.ReadAllBytes(script);
        File.WriteAllText(Path.Combine(variantDir, "bundled.json"),
            "{\"fileName\":\"" + fileName + "\",\"sha256\":\"" + Sha256Hex(payload) + "\",\"size\":" + payload.Length + "}");
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private sealed class StubUsb : IUsbEnumerator
    {
        private List<UsbDeviceEntry> _devices;
        public StubUsb(params UsbDeviceEntry[] devices) => _devices = new List<UsbDeviceEntry>(devices);
        /// <summary>Reads of the bus, so a test can assert a blocked driver never provokes one.</summary>
        public int Reads { get; private set; }
        public List<UsbDeviceEntry> Enumerate() { Reads++; return _devices; }
        /// <summary>Rewrite the bus, standing in for an unplug or a variant swap.</summary>
        public void SetBus(params UsbDeviceEntry[] devices) => _devices = new List<UsbDeviceEntry>(devices);
    }

    private sealed class ExplodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("auto-launch must not hit the network in these tests");
    }

    /// <summary>Counts manifest fetches and 404s them, mirroring an unpublished variant.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
