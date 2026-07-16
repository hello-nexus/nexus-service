using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Displays;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// TouchMappingGuard.RunPassAsync against fake registry/devnode/snapshot
/// dependencies - no Windows APIs, no BackgroundService.StartAsync timing
/// (per the failure-log entry on that pattern being unreliable under xUnit's
/// full-suite parallelism; the pass method is called directly).
/// </summary>
public sealed class TouchMappingGuardTests
{
    private const string Y70MonitorPath =
        @"\\?\DISPLAY#RTK0004#5&1264575b&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string Y70DigitizerPath =
        @"\\?\HID#VID_27C0&PID_0859&MI_00&Col01#a&2d89150c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string Y70SecondCollectionPath =
        @"\\?\HID#VID_27C0&PID_0859&MI_00&Col02#a&2d89150c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    private sealed class FakeSnapshotSource : ITouchMapSnapshotSource
    {
        private readonly List<TouchMapSnapshot?> _sequence;
        private int _index;
        public int CallCount { get; private set; }

        public FakeSnapshotSource(params TouchMapSnapshot?[] sequence) => _sequence = new List<TouchMapSnapshot?>(sequence);

        public Task<TouchMapSnapshot?> GetSnapshotAsync(CancellationToken ct = default)
        {
            CallCount++;
            var snapshot = _sequence[Math.Min(_index, _sequence.Count - 1)];
            if (_index < _sequence.Count - 1) _index++;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FakeRegistryWriter : IDigimonRegistryWriter
    {
        public int WriteCount { get; private set; }
        public string? LastDigitizerPath { get; private set; }
        public string? LastMonitorPath { get; private set; }
        public List<string> WrittenDigitizerPaths { get; } = new();

        public void Write(string digitizerInterfacePath, string monitorInterfacePath)
        {
            WriteCount++;
            LastDigitizerPath = digitizerInterfacePath;
            LastMonitorPath = monitorInterfacePath;
            WrittenDigitizerPaths.Add(digitizerInterfacePath);
        }
    }

    private sealed class FakeDevnodeRestarter : ITouchDigitizerDevnodeRestarter
    {
        public int RestartCount { get; private set; }
        public bool ReturnValue = true;
        public HashSet<string> FailForPaths { get; } = new(StringComparer.Ordinal);

        public bool Restart(string digitizerInterfacePath)
        {
            RestartCount++;
            return ReturnValue && !FailForPaths.Contains(digitizerInterfacePath);
        }
    }

    private static TouchMapSnapshot MismatchedSnapshot() => new()
    {
        Displays = { new TouchMapDisplayInfo { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
        Digitizers = { new TouchMapDigitizerInfo { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "primary-monitor" } },
    };

    private static TouchMapSnapshot CorrectSnapshot() => new()
    {
        Displays = { new TouchMapDisplayInfo { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
        Digitizers = { new TouchMapDigitizerInfo { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "y70-1" } },
    };

    private static TouchMapSnapshot TwoMismatchedSnapshot() => new()
    {
        Displays = { new TouchMapDisplayInfo { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
        Digitizers =
        {
            new TouchMapDigitizerInfo { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "primary-monitor" },
            new TouchMapDigitizerInfo { InterfacePath = Y70SecondCollectionPath, AssociatedDisplayId = "primary-monitor" },
        },
    };

    private static TouchMapSnapshot TwoCorrectSnapshot() => new()
    {
        Displays = { new TouchMapDisplayInfo { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
        Digitizers =
        {
            new TouchMapDigitizerInfo { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "y70-1" },
            new TouchMapDigitizerInfo { InterfacePath = Y70SecondCollectionPath, AssociatedDisplayId = "y70-1" },
        },
    };

    /// <summary>First collection now correct; second still mapped to the wrong display.</summary>
    private static TouchMapSnapshot OnePendingSnapshot() => new()
    {
        Displays = { new TouchMapDisplayInfo { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
        Digitizers =
        {
            new TouchMapDigitizerInfo { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "y70-1" },
            new TouchMapDigitizerInfo { InterfacePath = Y70SecondCollectionPath, AssociatedDisplayId = "primary-monitor" },
        },
    };

    [Fact]
    public async Task NoHelper_when_the_snapshot_source_returns_null()
    {
        var guard = new TouchMappingGuard(
            new FakeSnapshotSource((TouchMapSnapshot?)null), new FakeRegistryWriter(), new FakeDevnodeRestarter());

        Assert.Equal(TouchMappingPassResult.NoHelper, (await guard.RunPassAsync()).Result);
    }

    [Fact]
    public async Task NoPanel_when_no_catalog_display_is_attached()
    {
        var guard = new TouchMappingGuard(
            new FakeSnapshotSource(new TouchMapSnapshot()), new FakeRegistryWriter(), new FakeDevnodeRestarter());

        Assert.Equal(TouchMappingPassResult.NoPanel, (await guard.RunPassAsync()).Result);
    }

    [Fact]
    public async Task NoDigitizer_never_writes_the_registry()
    {
        var writer = new FakeRegistryWriter();
        var snapshot = new TouchMapSnapshot();
        snapshot.Displays.Add(new TouchMapDisplayInfo { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath });
        var guard = new TouchMappingGuard(new FakeSnapshotSource(snapshot), writer, new FakeDevnodeRestarter());

        var outcome = await guard.RunPassAsync();

        Assert.Equal(TouchMappingPassResult.NoDigitizer, outcome.Result);
        Assert.Equal(0, writer.WriteCount);
    }

    [Fact]
    public async Task AlreadyCorrect_never_writes_the_registry_or_restarts_the_devnode()
    {
        var writer = new FakeRegistryWriter();
        var restarter = new FakeDevnodeRestarter();
        var guard = new TouchMappingGuard(new FakeSnapshotSource(CorrectSnapshot()), writer, restarter);

        var outcome = await guard.RunPassAsync();

        Assert.Equal(TouchMappingPassResult.AlreadyCorrect, outcome.Result);
        Assert.Equal(0, writer.WriteCount);
        Assert.Equal(0, restarter.RestartCount);
    }

    [Fact]
    public async Task Repairs_and_verifies_a_mismatch()
    {
        var writer = new FakeRegistryWriter();
        var restarter = new FakeDevnodeRestarter();
        var source = new FakeSnapshotSource(MismatchedSnapshot(), CorrectSnapshot());
        var guard = new TouchMappingGuard(
            source, writer, restarter,
            verifyTimeout: TimeSpan.FromSeconds(1), verifyPollInterval: TimeSpan.FromMilliseconds(10));

        var outcome = await guard.RunPassAsync();

        Assert.Equal(TouchMappingPassResult.Repaired, outcome.Result);
        Assert.Equal("", outcome.Detail);
        Assert.Equal(1, writer.WriteCount);
        Assert.Equal(Y70DigitizerPath, writer.LastDigitizerPath);
        Assert.Equal(Y70MonitorPath, writer.LastMonitorPath);
        Assert.Equal(1, restarter.RestartCount);
    }

    [Fact]
    public async Task Failed_when_the_devnode_restart_fails()
    {
        var writer = new FakeRegistryWriter();
        var restarter = new FakeDevnodeRestarter { ReturnValue = false };
        var guard = new TouchMappingGuard(
            new FakeSnapshotSource(MismatchedSnapshot()), writer, restarter,
            verifyTimeout: TimeSpan.FromMilliseconds(50), verifyPollInterval: TimeSpan.FromMilliseconds(10));

        var outcome = await guard.RunPassAsync();

        Assert.Equal(TouchMappingPassResult.Failed, outcome.Result);
        Assert.Equal("devnode-restart-failed", outcome.Detail);
        Assert.Equal(1, writer.WriteCount);
        Assert.Equal(1, restarter.RestartCount);
    }

    [Fact]
    public async Task Failed_when_the_repair_never_verifies_within_the_timeout()
    {
        var writer = new FakeRegistryWriter();
        var restarter = new FakeDevnodeRestarter();
        // Every call, including the verification polls, still reports the mismatch.
        var source = new FakeSnapshotSource(MismatchedSnapshot());
        var guard = new TouchMappingGuard(
            source, writer, restarter,
            verifyTimeout: TimeSpan.FromMilliseconds(60), verifyPollInterval: TimeSpan.FromMilliseconds(20));

        var outcome = await guard.RunPassAsync();

        Assert.Equal(TouchMappingPassResult.Failed, outcome.Result);
        Assert.Equal("verify-timeout", outcome.Detail);
        Assert.True(source.CallCount >= 2);
    }

    [Fact]
    public async Task A_second_pass_after_a_verified_repair_is_a_noop()
    {
        var writer = new FakeRegistryWriter();
        var restarter = new FakeDevnodeRestarter();
        var guard = new TouchMappingGuard(new FakeSnapshotSource(CorrectSnapshot()), writer, restarter);

        var first = await guard.RunPassAsync();
        var second = await guard.RunPassAsync();

        Assert.Equal(TouchMappingPassResult.AlreadyCorrect, first.Result);
        Assert.Equal(TouchMappingPassResult.AlreadyCorrect, second.Result);
        Assert.Equal(0, writer.WriteCount);
        Assert.Equal(0, restarter.RestartCount);
    }

    [Fact]
    public async Task Repairs_every_mismatched_digitizer_collection_in_one_pass()
    {
        var writer = new FakeRegistryWriter();
        var restarter = new FakeDevnodeRestarter();
        var source = new FakeSnapshotSource(TwoMismatchedSnapshot(), TwoCorrectSnapshot());
        var guard = new TouchMappingGuard(
            source, writer, restarter,
            verifyTimeout: TimeSpan.FromSeconds(1), verifyPollInterval: TimeSpan.FromMilliseconds(10));

        var outcome = await guard.RunPassAsync();

        Assert.Equal(TouchMappingPassResult.Repaired, outcome.Result);
        Assert.Equal(2, writer.WriteCount);
        Assert.Contains(Y70DigitizerPath, writer.WrittenDigitizerPaths);
        Assert.Contains(Y70SecondCollectionPath, writer.WrittenDigitizerPaths);
        Assert.Equal(2, restarter.RestartCount);
    }

    [Fact]
    public async Task Failed_when_only_one_of_several_mismatched_digitizers_fails_to_restart()
    {
        var writer = new FakeRegistryWriter();
        var restarter = new FakeDevnodeRestarter();
        restarter.FailForPaths.Add(Y70SecondCollectionPath);
        var guard = new TouchMappingGuard(
            new FakeSnapshotSource(TwoMismatchedSnapshot()), writer, restarter,
            verifyTimeout: TimeSpan.FromMilliseconds(50), verifyPollInterval: TimeSpan.FromMilliseconds(10));

        var outcome = await guard.RunPassAsync();

        Assert.Equal(TouchMappingPassResult.Failed, outcome.Result);
        Assert.Equal("devnode-restart-failed", outcome.Detail);
        Assert.Equal(2, writer.WriteCount);
        Assert.Equal(2, restarter.RestartCount);
    }

    [Fact]
    public async Task Failed_when_only_one_of_several_mismatched_digitizers_verifies()
    {
        var writer = new FakeRegistryWriter();
        var restarter = new FakeDevnodeRestarter();
        var source = new FakeSnapshotSource(TwoMismatchedSnapshot(), OnePendingSnapshot());
        var guard = new TouchMappingGuard(
            source, writer, restarter,
            verifyTimeout: TimeSpan.FromMilliseconds(60), verifyPollInterval: TimeSpan.FromMilliseconds(20));

        var outcome = await guard.RunPassAsync();

        Assert.Equal(TouchMappingPassResult.Failed, outcome.Result);
        Assert.Equal("verify-timeout", outcome.Detail);
    }
}
