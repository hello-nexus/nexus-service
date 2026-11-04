using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Activity;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Activity;
using Nexus.Service.Monitoring.Events;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.Events;

/// <summary>
/// Pure diff/cap logic tested directly (no BackgroundService involved), plus
/// a handful of Tick(DateTime)-driven tests through real fakes - the same
/// seam PrivacyAccessWatcherTests uses for its own BackgroundService, per
/// the failure-log note against relying on BackgroundService.StartAsync's
/// synchronous prefix.
/// </summary>
public class MonitoringEventCollectorTests
{
    private sealed class FakeUsbEnumerator : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Next { get; set; } = new();
        public List<UsbDeviceEntry> Enumerate() => Next;
    }

    private sealed class FakeAppDetection : IAppDetectionProvider
    {
        public List<Detected> Next { get; set; } = new();
        public int Calls { get; private set; }

        public IReadOnlyList<Detected> GetDetected()
        {
            Calls++;
            return Next;
        }

        public bool Kill(string id) => false;
    }

    private sealed class RecordingEventStore : IMonitoringEventStore
    {
        public List<(long TUtcMs, string Kind, string Label, string? Detail, bool Custom)> Appends { get; } = new();

        public MonitoringEvent Append(long tUtcMs, string kind, string label, string? detail, bool custom)
        {
            Appends.Add((tUtcMs, kind, label, detail, custom));
            return new MonitoringEvent(Appends.Count, tUtcMs, kind, label, detail, custom);
        }

        public IReadOnlyList<MonitoringEvent> Query(long fromUtcMs, long toUtcMs, int limit) => Array.Empty<MonitoringEvent>();

        public bool DeleteCustom(long id) => false;

        public void PruneOlderThan(long cutoffUtcMs) { }
    }

    private static UsbDeviceEntry Usb(int vid, int pid, string name) =>
        new() { VendorId = vid, ProductId = pid, Name = name };

    // ----- Pure diff/cap methods -----

    [Fact]
    public void DiffUsb_ReportsOnlyKeysThatChanged()
    {
        var previous = new HashSet<MonitoringEventCollector.UsbKey> { new(0x1234, 0x0001, "Mouse") };
        var current = new HashSet<MonitoringEventCollector.UsbKey>
        {
            new(0x1234, 0x0001, "Mouse"),
            new(0x5678, 0x0002, "Keyboard"),
        };

        var (attached, detached) = MonitoringEventCollector.DiffUsb(previous, current);

        var addedKey = Assert.Single(attached);
        Assert.Equal("Keyboard", addedKey.Name);
        Assert.Empty(detached);
    }

    [Fact]
    public void DiffUsb_ReportsDetachedKeys_NoLongerPresent()
    {
        var previous = new HashSet<MonitoringEventCollector.UsbKey>
        {
            new(0x1234, 0x0001, "Mouse"),
            new(0x5678, 0x0002, "Keyboard"),
        };
        var current = new HashSet<MonitoringEventCollector.UsbKey> { new(0x1234, 0x0001, "Mouse") };

        var (attached, detached) = MonitoringEventCollector.DiffUsb(previous, current);

        Assert.Empty(attached);
        var removedKey = Assert.Single(detached);
        Assert.Equal("Keyboard", removedKey.Name);
    }

    [Fact]
    public void FormatVidPid_FormatsAsUppercaseHex()
    {
        Assert.Equal("1234:ABCD", MonitoringEventCollector.FormatVidPid(0x1234, 0xABCD));
    }

    [Fact]
    public void ExtractWindowedAppNames_OnlyReturnsNamesWithAVisibleWindow()
    {
        var procs = new List<ProcessInfo>
        {
            new() { Pid = 1, Name = "notepad.exe", HasWindow = true },
            new() { Pid = 2, Name = "svchost.exe", HasWindow = false },
        };

        var names = MonitoringEventCollector.ExtractWindowedAppNames(procs, selfPid: -1, _ => false);

        Assert.Equal(new[] { "notepad.exe" }, names);
    }

    [Fact]
    public void ExtractWindowedAppNames_TreatsAnyInstanceHavingAWindow_AsTheWholeNameHavingOne()
    {
        var procs = new List<ProcessInfo>
        {
            new() { Pid = 1, Name = "app.exe", HasWindow = false },
            new() { Pid = 2, Name = "app.exe", HasWindow = true },
        };

        var names = MonitoringEventCollector.ExtractWindowedAppNames(procs, selfPid: -1, _ => false);

        Assert.Equal(new[] { "app.exe" }, names);
    }

    [Theory]
    [InlineData("nexus-overlay")]
    [InlineData("nexus-overlay.exe")]
    [InlineData("NexusUpdater.exe")]
    [InlineData("OpenRGB-headless")]
    public void ExtractWindowedAppNames_SkipsNexusOwnProcesses(string nexusName)
    {
        var procs = new List<ProcessInfo>
        {
            new() { Pid = 1, Name = nexusName, HasWindow = true },
            new() { Pid = 2, Name = "msedgewebview2.exe", HasWindow = true },
        };

        var names = MonitoringEventCollector.ExtractWindowedAppNames(procs, selfPid: -1, _ => false);

        Assert.Equal(new[] { "msedgewebview2.exe" }, names);
    }

    [Fact]
    public void ExtractWindowedAppNames_SkipsNexusExeWhoseImageIsOurs_ButKeepsAnotherProductsNexusExe()
    {
        var procs = new List<ProcessInfo>
        {
            new() { Pid = 30, Name = "Nexus.exe", HasWindow = true },
            new() { Pid = 40, Name = "Nexus.exe", HasWindow = true },
            new() { Pid = 50, Name = "Nexus", HasWindow = true },
        };

        var names = MonitoringEventCollector.ExtractWindowedAppNames(procs, selfPid: -1, pid => pid is 30 or 50);

        Assert.Equal(new[] { "Nexus.exe" }, names);
        Assert.Empty(MonitoringEventCollector.ExtractWindowedAppNames(new[] { procs[0], procs[2] }, selfPid: -1, pid => pid is 30 or 50));
    }

    [Fact]
    public void ExtractWindowedAppNames_SkipsOurOwnPid_ButNotAnotherProductNamedNexus()
    {
        var procs = new List<ProcessInfo>
        {
            new() { Pid = 10, Name = "Nexus.exe", HasWindow = true },
            new() { Pid = 20, Name = "Nexus.exe", HasWindow = true },
        };

        Assert.Empty(MonitoringEventCollector.ExtractWindowedAppNames(new[] { procs[0] }, selfPid: 10, _ => false));
        Assert.Equal(new[] { "Nexus.exe" }, MonitoringEventCollector.ExtractWindowedAppNames(procs, selfPid: 10, _ => false));
    }

    [Fact]
    public void DetectedAppCandidates_SkipsNexusOwnProcesses()
    {
        var candidates = MonitoringEventCollector.DetectedAppCandidates(new List<Detected>
        {
            new() { Id = "1", Name = "Nexus" },
            new() { Id = "2", Name = "Safari" },
            new() { Id = "3", Name = "nexus-overlay" },
            new() { Id = "4", Name = "Nexus Dock" },
        }, selfPid: 1);

        Assert.Equal(new[] { "Safari", "Nexus Dock" }, candidates.Select(c => c.Name));
    }

    [Fact]
    public void DiffNewApps_ReturnsOnlyNamesNotInPrevious()
    {
        var previous = new HashSet<string>(new[] { "chrome.exe" }, StringComparer.OrdinalIgnoreCase);
        var current = new List<string> { "chrome.exe", "notepad.exe" };

        var newlySeen = MonitoringEventCollector.DiffNewApps(previous, current);

        Assert.Equal(new[] { "notepad.exe" }, newlySeen);
    }

    [Fact]
    public void ApplyCooldownAndCap_CapsToTheGivenLimit()
    {
        var candidates = Enumerable.Range(0, 15).Select(i => $"app-{i}.exe").ToList();
        var cooldownUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var toEmit = MonitoringEventCollector.ApplyCooldownAndCap(candidates, cooldownUntil, now, TimeSpan.FromMinutes(1), cap: 10);

        Assert.Equal(10, toEmit.Count);
    }

    [Fact]
    public void ApplyCooldownAndCap_SkipsANameStillInsideItsCooldownWindow()
    {
        var cooldownUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var first = MonitoringEventCollector.ApplyCooldownAndCap(new[] { "app.exe" }, cooldownUntil, now, TimeSpan.FromMinutes(1), cap: 10);
        Assert.Equal(new[] { "app.exe" }, first);

        var second = MonitoringEventCollector.ApplyCooldownAndCap(
            new[] { "app.exe" }, cooldownUntil, now.AddSeconds(30), TimeSpan.FromMinutes(1), cap: 10);
        Assert.Empty(second);

        var third = MonitoringEventCollector.ApplyCooldownAndCap(
            new[] { "app.exe" }, cooldownUntil, now.AddMinutes(2), TimeSpan.FromMinutes(1), cap: 10);
        Assert.Equal(new[] { "app.exe" }, third);
    }

    // ----- Tick(DateTime)-driven, through fakes -----

    [Fact]
    public void Tick_FirstCall_PrimesTheBaseline_AndEmitsNothing()
    {
        var usb = new FakeUsbEnumerator { Next = new List<UsbDeviceEntry> { Usb(0x1234, 0x0001, "Mouse") } };
        var processes = new ProcessMonitor(new MultiplexHub());
        processes.SetProcessesForTest(new List<ProcessInfo> { new() { Pid = 1, Name = "notepad.exe", HasWindow = true } });
        var store = new RecordingEventStore();
        var collector = new MonitoringEventCollector(usb, processes, new FakeAppDetection(), store, useWindowedProcesses: true);

        collector.Tick(DateTime.UtcNow);

        Assert.Empty(store.Appends);
    }

    [Fact]
    public void Tick_SecondCall_EmitsUsbAttachAndDetach_ForAChangedBus()
    {
        var usb = new FakeUsbEnumerator { Next = new List<UsbDeviceEntry> { Usb(0x1234, 0x0001, "Mouse") } };
        var processes = new ProcessMonitor(new MultiplexHub());
        processes.SetProcessesForTest(Array.Empty<ProcessInfo>());
        var store = new RecordingEventStore();
        var collector = new MonitoringEventCollector(usb, processes, new FakeAppDetection(), store, useWindowedProcesses: true);
        collector.Tick(DateTime.UtcNow);

        usb.Next = new List<UsbDeviceEntry> { Usb(0x5678, 0x0002, "Keyboard") };
        collector.Tick(DateTime.UtcNow);

        Assert.Contains(store.Appends, a => a.Kind == MonitoringEventKinds.UsbAttach && a.Label == "Keyboard");
        Assert.Contains(store.Appends, a => a.Kind == MonitoringEventKinds.UsbDetach && a.Label == "Mouse");
    }

    [Fact]
    public void Tick_SecondCall_EmitsAppOpen_ForANewlySeenWindowedApp()
    {
        var usb = new FakeUsbEnumerator();
        var processes = new ProcessMonitor(new MultiplexHub());
        processes.SetProcessesForTest(Array.Empty<ProcessInfo>());
        var store = new RecordingEventStore();
        var collector = new MonitoringEventCollector(usb, processes, new FakeAppDetection(), store, useWindowedProcesses: true);
        collector.Tick(DateTime.UtcNow);

        processes.SetProcessesForTest(new List<ProcessInfo> { new() { Pid = 1, Name = "notepad.exe", HasWindow = true } });
        collector.Tick(DateTime.UtcNow);

        // Elevation cannot be verified off Windows (ProcessElevation.IsProcessElevated
        // is a Windows-only P/Invoke and returns false everywhere else), so this
        // only asserts the app-open path, not the uac-escalation branch.
        Assert.Contains(store.Appends, a => a.Kind == MonitoringEventKinds.AppOpen && a.Label == "notepad.exe");
    }

    [Fact]
    public void Tick_SecondCall_DoesNotEmitAppEvents_ForABackgroundProcessWithNoWindow()
    {
        var usb = new FakeUsbEnumerator();
        var processes = new ProcessMonitor(new MultiplexHub());
        processes.SetProcessesForTest(Array.Empty<ProcessInfo>());
        var store = new RecordingEventStore();
        var collector = new MonitoringEventCollector(usb, processes, new FakeAppDetection(), store, useWindowedProcesses: true);
        collector.Tick(DateTime.UtcNow);

        processes.SetProcessesForTest(new List<ProcessInfo> { new() { Pid = 1, Name = "svchost.exe", HasWindow = false } });
        collector.Tick(DateTime.UtcNow);

        Assert.Empty(store.Appends);
    }

    [Fact]
    public void Tick_PerTickCap_HoldsAcrossManyNewlySeenApps()
    {
        var usb = new FakeUsbEnumerator();
        var processes = new ProcessMonitor(new MultiplexHub());
        processes.SetProcessesForTest(Array.Empty<ProcessInfo>());
        var store = new RecordingEventStore();
        var collector = new MonitoringEventCollector(usb, processes, new FakeAppDetection(), store, useWindowedProcesses: true);
        collector.Tick(DateTime.UtcNow);

        var manyApps = Enumerable.Range(0, 15)
            .Select(i => new ProcessInfo { Pid = i + 1, Name = $"app-{i}.exe", HasWindow = true })
            .ToList();
        processes.SetProcessesForTest(manyApps);
        collector.Tick(DateTime.UtcNow);

        Assert.True(store.Appends.Count <= 10);
    }

    [Fact]
    public void Tick_AFlappingUsbDevice_IsRateLimited_NotLoggedEveryTick()
    {
        var usb = new FakeUsbEnumerator { Next = new List<UsbDeviceEntry> { Usb(0x1234, 0x0001, "Flaky Hub") } };
        var processes = new ProcessMonitor(new MultiplexHub());
        processes.SetProcessesForTest(Array.Empty<ProcessInfo>());
        var store = new RecordingEventStore();
        var collector = new MonitoringEventCollector(usb, processes, new FakeAppDetection(), store, useWindowedProcesses: true);
        var start = DateTime.UtcNow;
        collector.Tick(start);

        // Re-enumeration loop: the device drops and returns every tick.
        for (var i = 1; i <= 12; i++)
        {
            usb.Next = i % 2 == 0
                ? new List<UsbDeviceEntry> { Usb(0x1234, 0x0001, "Flaky Hub") }
                : new List<UsbDeviceEntry>();
            collector.Tick(start.AddSeconds(5 * i));
        }

        // Without the cooldown this is one attach or detach per tick. The
        // cooldown is per device AND direction, so a full minute of flapping
        // records at most one of each.
        Assert.True(store.Appends.Count <= 2, $"expected at most 2 events, got {store.Appends.Count}");
    }

    [Fact]
    public void Tick_AGenuineUnplug_IsStillRecorded_RightAfterItsAttach()
    {
        var usb = new FakeUsbEnumerator { Next = new List<UsbDeviceEntry>() };
        var processes = new ProcessMonitor(new MultiplexHub());
        processes.SetProcessesForTest(Array.Empty<ProcessInfo>());
        var store = new RecordingEventStore();
        var collector = new MonitoringEventCollector(usb, processes, new FakeAppDetection(), store, useWindowedProcesses: true);
        var start = DateTime.UtcNow;
        collector.Tick(start);

        usb.Next = new List<UsbDeviceEntry> { Usb(0x1234, 0x0001, "Mouse") };
        collector.Tick(start.AddSeconds(5));
        usb.Next = new List<UsbDeviceEntry>();
        collector.Tick(start.AddSeconds(10));

        Assert.Contains(store.Appends, a => a.Kind == MonitoringEventKinds.UsbAttach && a.Label == "Mouse");
        Assert.Contains(store.Appends, a => a.Kind == MonitoringEventKinds.UsbDetach && a.Label == "Mouse");
    }

    // ----- Non-Windows app source (IAppDetectionProvider) -----
    //
    // IWindowSetProvider is Windows-only, so off Windows every
    // ProcessInfo.HasWindow is false and the windowed-name source above emits
    // nothing at all. These cover the source that actually runs there.

    [Fact]
    public void DetectedAppCandidates_CollapsesDuplicateNames_AndParsesThePid()
    {
        var candidates = MonitoringEventCollector.DetectedAppCandidates(new List<Detected>
        {
            new() { Id = "501", Name = "Safari" },
            new() { Id = "502", Name = "Safari" },
            new() { Id = "notapid", Name = "Finder" },
            new() { Id = "504", Name = "" },
        }, selfPid: -1);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(501, candidates.Single(c => c.Name == "Safari").Pid);
        Assert.Null(candidates.Single(c => c.Name == "Finder").Pid);
    }

    [Fact]
    public void Tick_DetectionSource_EmitsAppOpen_ForANewlyDetectedApp()
    {
        var usb = new FakeUsbEnumerator();
        var processes = new ProcessMonitor(new MultiplexHub());
        processes.SetProcessesForTest(Array.Empty<ProcessInfo>());
        var detection = new FakeAppDetection();
        var store = new RecordingEventStore();
        var collector = new MonitoringEventCollector(usb, processes, detection, store, useWindowedProcesses: false);
        var start = DateTime.UtcNow;
        collector.Tick(start);

        detection.Next = new List<Detected> { new() { Id = "77", Name = "Calculator" } };
        collector.Tick(start.AddSeconds(30));

        Assert.Contains(store.Appends, a => a.Kind == MonitoringEventKinds.AppOpen && a.Label == "Calculator");
    }

    [Fact]
    public void Tick_DetectionSource_SkippedSubCadenceTick_DoesNotClobberTheBaseline()
    {
        var usb = new FakeUsbEnumerator();
        var processes = new ProcessMonitor(new MultiplexHub());
        processes.SetProcessesForTest(Array.Empty<ProcessInfo>());
        var detection = new FakeAppDetection { Next = new List<Detected> { new() { Id = "1", Name = "Safari" } } };
        var store = new RecordingEventStore();
        var collector = new MonitoringEventCollector(usb, processes, detection, store, useWindowedProcesses: false);
        var start = DateTime.UtcNow;
        collector.Tick(start);

        // Inside the sub-cadence: no fresh sample, so the baseline must stay
        // as primed. Treating the skip as an empty snapshot would make the
        // next real tick re-emit Safari as newly seen.
        collector.Tick(start.AddSeconds(5));
        Assert.Equal(1, detection.Calls);

        collector.Tick(start.AddSeconds(30));
        Assert.Empty(store.Appends);
    }

    [Fact]
    public void Tick_DetectionSource_StampsTheEventAtTheProcessStart_WhenRecent()
    {
        var usb = new FakeUsbEnumerator();
        var processes = new ProcessMonitor(new MultiplexHub());
        var start = DateTime.UtcNow;
        var startMs = new DateTimeOffset(start).ToUnixTimeMilliseconds();
        processes.SetProcessesForTest(Array.Empty<ProcessInfo>());
        var detection = new FakeAppDetection();
        var store = new RecordingEventStore();
        var collector = new MonitoringEventCollector(usb, processes, detection, store, useWindowedProcesses: false);
        collector.Tick(start);

        processes.SetProcessesForTest(new List<ProcessInfo>
        {
            new() { Pid = 77, Name = "Calculator", StartedAtMs = startMs + 20_000 },
        });
        detection.Next = new List<Detected> { new() { Id = "77", Name = "Calculator" } };
        collector.Tick(start.AddSeconds(30));

        var appended = Assert.Single(store.Appends);
        Assert.Equal(startMs + 20_000, appended.TUtcMs);
    }

    [Theory]
    // Recent start wins, so the marker sits at the real launch.
    [InlineData(1_000_000L, 1_030_000L, 1_000_000L)]
    // Older than the backdate limit falls back to detection time.
    [InlineData(1_000_000L, 1_400_000L, 1_400_000L)]
    // A start in the future is not trusted.
    [InlineData(2_000_000L, 1_000_000L, 1_000_000L)]
    public void ResolveEventTime_PrefersARecentStart_AndFallsBackOtherwise(long startedAtMs, long nowMs, long expected)
    {
        Assert.Equal(expected, MonitoringEventCollector.ResolveEventTime(startedAtMs, nowMs));
    }

    [Fact]
    public void ResolveEventTime_WithNoKnownStart_UsesDetectionTime()
    {
        Assert.Equal(500L, MonitoringEventCollector.ResolveEventTime(null, 500L));
    }

    [Fact]
    public void Tick_GateOff_NoOps()
    {
        var usb = new FakeUsbEnumerator { Next = new List<UsbDeviceEntry> { Usb(0x1234, 0x0001, "Mouse") } };
        var processes = new ProcessMonitor(new MultiplexHub());
        processes.SetProcessesForTest(new List<ProcessInfo> { new() { Pid = 1, Name = "notepad.exe", HasWindow = true } });
        var store = new RecordingEventStore();
        var configStore = new Nexus.Service.Tests.InMemoryConfigStore();
        configStore.Update(s => s.Features.Monitoring = false);
        var collector = new MonitoringEventCollector(usb, processes, new FakeAppDetection(), store,
            useWindowedProcesses: true, gates: new FeatureGates(configStore));

        // Priming tick, then a real bus/app change - both must be skipped
        // wholesale, not merely primed-and-diffed-into-nothing.
        collector.Tick(DateTime.UtcNow);
        usb.Next = new List<UsbDeviceEntry> { Usb(0x5678, 0x0002, "Keyboard") };
        processes.SetProcessesForTest(new List<ProcessInfo> { new() { Pid = 2, Name = "calc.exe", HasWindow = true } });
        collector.Tick(DateTime.UtcNow);

        Assert.Empty(store.Appends);
    }
}
