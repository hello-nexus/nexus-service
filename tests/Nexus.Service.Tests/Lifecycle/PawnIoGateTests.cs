using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lifecycle;
using Xunit;

namespace Nexus.Service.Tests.Lifecycle;

public class PawnIoGateTests
{
    internal sealed class FakeSystem : IPawnIoSystem
    {
        public bool IsSupported { get; set; } = true;
        public bool Open { get; set; }
        public bool OpensAfterStart { get; set; }
        public bool OpensAfterInstall { get; set; }
        public bool Registered { get; set; } = true;
        public bool MarkedForDeletion { get; set; }
        public int StartResult { get; set; }
        public int? Problem { get; set; }
        public PawnIoInstallResult InstallResult { get; set; } = PawnIoInstallResult.Repaired;
        public Task? InstallBlock { get; set; }
        public int Starts { get; private set; }
        public int Installs { get; private set; }
        public int Restarts { get; private set; }

        public bool DeviceOpen() => Open;
        public bool ServiceRegistered() => Registered;
        public bool ServiceMarkedForDeletion() => MarkedForDeletion;
        public int? DevnodeProblemCode() => Problem;

        public int StartService()
        {
            Starts++;
            if (OpensAfterStart && StartResult == 0) Open = true;
            return StartResult;
        }

        public async Task<PawnIoInstallResult> EnsureInstalledAsync(CancellationToken ct)
        {
            Installs++;
            if (InstallBlock is not null) await InstallBlock;
            if (OpensAfterInstall) Open = true;
            return InstallResult;
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
        public bool RestartResult { get; set; } = true;
        public bool RestartPc() { Restarts++; return RestartResult; }
    }

    private sealed class FakeProvider(FakeSystem sys) : IPawnIoProvider
    {
        public bool IsInstalled => sys.Registered;
        public bool IsOpen => sys.Open;
    }

    private static PawnIoGate Make(FakeSystem sys) => new(new FakeProvider(sys), sys);

    [Theory]
    [InlineData(true, true, 0, null, null, PawnIoStates.RestartRequired, 1072)]
    [InlineData(true, false, 1072, null, null, PawnIoStates.RestartRequired, 1072)]
    [InlineData(true, false, 0, null, PawnIoInstallResult.UpgradeStagedPendingReboot, PawnIoStates.RestartRequired, null)]
    [InlineData(true, false, 577, null, null, PawnIoStates.Blocked, 577)]
    [InlineData(true, false, 1275, null, PawnIoInstallResult.Failed, PawnIoStates.Blocked, 1275)]
    [InlineData(true, false, 0, 52, null, PawnIoStates.Blocked, 52)]
    [InlineData(true, false, 0, 48, null, PawnIoStates.Blocked, 48)]
    [InlineData(false, false, 0, null, PawnIoInstallResult.UserDenied, PawnIoStates.PermissionDeclined, null)]
    [InlineData(false, false, 0, null, PawnIoInstallResult.Failed, PawnIoStates.InstallFailed, null)]
    [InlineData(false, false, 0, null, null, PawnIoStates.NotInstalled, null)]
    [InlineData(true, false, 0, null, null, PawnIoStates.NotRunning, null)]
    [InlineData(true, false, 5, null, null, PawnIoStates.NotRunning, 5)]
    public void Classify_maps_signals_to_state(
        bool registered, bool deleteFlag, int startError, int? problem, PawnIoInstallResult? outcome,
        string state, int? code)
    {
        var (s, c) = PawnIoGate.Classify(registered, deleteFlag, startError, problem, outcome);
        Assert.Equal(state, s);
        Assert.Equal(code, c);
    }

    [Theory]
    [InlineData(PawnIoStates.NotInstalled, "install")]
    [InlineData(PawnIoStates.InstallFailed, "install")]
    [InlineData(PawnIoStates.PermissionDeclined, "install")]
    [InlineData(PawnIoStates.NotRunning, "repair")]
    [InlineData(PawnIoStates.RestartRequired, "restart")]
    [InlineData(PawnIoStates.Blocked, "none")]
    [InlineData(PawnIoStates.Ok, "none")]
    public void FixActionFor_follows_the_contract(string state, string action)
        => Assert.Equal(action, PawnIoGate.FixActionFor(state));

    [Fact]
    public async Task Healthy_pc_never_starts_installs_or_leaves_ok()
    {
        var sys = new FakeSystem { Open = true };
        var gate = Make(sys);

        Assert.True(await gate.EnsureRunningAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await gate.ConfirmAfterBootAsync());

        var status = gate.GetStatus();
        Assert.Equal(PawnIoStates.Ok, status.State);
        Assert.Equal("none", status.FixAction);
        Assert.Null(status.ErrorCode);
        Assert.Equal(0, sys.Starts);
        Assert.Equal(0, sys.Installs);
    }

    [Fact]
    public async Task Stopped_driver_that_starts_is_ok_and_never_gated()
    {
        var sys = new FakeSystem { OpensAfterStart = true };
        var gate = Make(sys);
        var raised = false;
        gate.NotOkConfirmed += _ => raised = true;

        Assert.True(await gate.EnsureRunningAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await gate.ConfirmAfterBootAsync());

        Assert.Equal(1, sys.Starts);
        Assert.False(raised);
        Assert.Equal(PawnIoStates.Ok, gate.GetStatus().State);
    }

    [Fact]
    public void Not_ok_is_not_reported_before_the_boot_check_confirms_it()
    {
        var gate = Make(new FakeSystem { Open = false });
        Assert.Equal(PawnIoStates.Ok, gate.GetStatus().State);
    }

    [Fact]
    public async Task Confirmed_not_ok_reports_state_and_raises_once()
    {
        var sys = new FakeSystem { StartResult = 1072, MarkedForDeletion = false };
        var gate = Make(sys);
        var raised = 0;
        gate.NotOkConfirmed += _ => raised++;

        Assert.False(await gate.ConfirmAfterBootAsync());

        var status = gate.GetStatus();
        Assert.Equal(PawnIoStates.RestartRequired, status.State);
        Assert.Equal(1072, status.ErrorCode);
        Assert.Equal("restart", status.FixAction);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Registered_but_dead_driver_is_notRunning_with_repair()
    {
        var gate = Make(new FakeSystem());
        await gate.ConfirmAfterBootAsync();
        var status = gate.GetStatus();
        Assert.Equal(PawnIoStates.NotRunning, status.State);
        Assert.Equal("repair", status.FixAction);
    }

    [Fact]
    public async Task Marked_for_deletion_is_not_started()
    {
        var sys = new FakeSystem { MarkedForDeletion = true };
        var gate = Make(sys);
        await gate.ConfirmAfterBootAsync();
        Assert.Equal(0, sys.Starts);
        Assert.Equal(PawnIoStates.RestartRequired, gate.GetStatus().State);
    }

    [Fact]
    public async Task Unsupported_platform_is_always_ok()
    {
        var sys = new FakeSystem { IsSupported = false };
        var gate = Make(sys);
        Assert.True(await gate.ConfirmAfterBootAsync());
        Assert.Equal(PawnIoStates.Ok, gate.GetStatus().State);
        Assert.False(gate.RestartPc());
        Assert.Equal(0, sys.Restarts);
    }

    [Fact]
    public async Task Fix_installs_then_reports_ok_and_requests_redetect()
    {
        var sys = new FakeSystem { Registered = false, OpensAfterInstall = true };
        var gate = Make(sys);
        await gate.ConfirmAfterBootAsync();
        Assert.Equal(PawnIoStates.NotInstalled, gate.GetStatus().State);

        var status = await gate.FixAsync();

        Assert.Equal(PawnIoStates.Ok, status.State);
        Assert.False(status.Fixing);
        Assert.Equal(1, sys.Installs);
        Assert.True(gate.TakeRedetectRequest());
        Assert.False(gate.TakeRedetectRequest());
    }

    [Fact]
    public async Task Fix_that_fails_keeps_the_gate_and_reports_installFailed()
    {
        var sys = new FakeSystem { Registered = false, InstallResult = PawnIoInstallResult.Failed };
        var gate = Make(sys);
        await gate.ConfirmAfterBootAsync();

        var status = await gate.FixAsync();

        Assert.Equal(PawnIoStates.InstallFailed, status.State);
        Assert.Equal("install", status.FixAction);
        Assert.False(gate.TakeRedetectRequest());
    }

    [Fact]
    public async Task Second_fix_while_one_runs_returns_in_progress_without_a_second_install()
    {
        var release = new TaskCompletionSource();
        var sys = new FakeSystem { Registered = false, OpensAfterInstall = true, InstallBlock = release.Task };
        var gate = Make(sys);
        await gate.ConfirmAfterBootAsync();

        var first = gate.FixAsync();
        var second = await gate.FixAsync();

        Assert.True(second.Fixing);
        Assert.Equal(1, sys.Installs);
        release.SetResult();
        Assert.Equal(PawnIoStates.Ok, (await first).State);
    }

    [Fact]
    public async Task Fix_declined_by_user_reports_permissionDeclined()
    {
        var sys = new FakeSystem { Registered = false, InstallResult = PawnIoInstallResult.UserDenied };
        var gate = Make(sys);
        await gate.ConfirmAfterBootAsync();
        Assert.Equal(PawnIoStates.PermissionDeclined, (await gate.FixAsync()).State);
    }

    [Fact]
    public async Task Fix_staged_pending_reboot_reports_restartRequired()
    {
        var sys = new FakeSystem { InstallResult = PawnIoInstallResult.UpgradeStagedPendingReboot };
        var gate = Make(sys);
        await gate.ConfirmAfterBootAsync();
        var status = await gate.FixAsync();
        Assert.Equal(PawnIoStates.RestartRequired, status.State);
        Assert.Equal("restart", status.FixAction);
    }

    [Fact]
    public async Task Fix_does_nothing_for_restart_and_blocked_states()
    {
        var sys = new FakeSystem { StartResult = 577 };
        var gate = Make(sys);
        await gate.ConfirmAfterBootAsync();
        var installsBefore = sys.Installs;

        var status = await gate.FixAsync();

        Assert.Equal(PawnIoStates.Blocked, status.State);
        Assert.Equal(installsBefore, sys.Installs);
    }

    [Fact]
    public async Task Fix_does_nothing_on_a_healthy_pc()
    {
        var sys = new FakeSystem { Open = true };
        var gate = Make(sys);
        var status = await gate.FixAsync();
        Assert.Equal(PawnIoStates.Ok, status.State);
        Assert.Equal(0, sys.Installs);
        Assert.Equal(0, sys.Starts);
    }

    [Fact]
    public async Task Fix_after_driver_opens_outside_nexus_requests_redetect()
    {
        var sys = new FakeSystem();
        var gate = Make(sys);
        await gate.ConfirmAfterBootAsync();
        sys.Open = true;

        var status = await gate.FixAsync();

        Assert.Equal(PawnIoStates.Ok, status.State);
        Assert.Equal(0, sys.Installs);
        Assert.True(gate.TakeRedetectRequest());
        gate.RestoreRedetectRequest();
        Assert.True(gate.TakeRedetectRequest());
    }

    [Fact]
    public async Task Fix_on_a_process_that_never_latched_not_ok_requests_nothing()
    {
        var gate = Make(new FakeSystem { Open = true });
        await gate.ConfirmAfterBootAsync();
        await gate.FixAsync();
        Assert.False(gate.TakeRedetectRequest());
    }

    [Fact]
    public void RestartPc_reports_failure_to_start()
    {
        var sys = new FakeSystem { RestartResult = false };
        Assert.False(Make(sys).RestartPc());
    }

    [Fact]
    public void RestartPc_calls_the_system_when_supported()
    {
        var sys = new FakeSystem();
        Assert.True(Make(sys).RestartPc());
        Assert.Equal(1, sys.Restarts);
    }
}
