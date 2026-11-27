using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Lifecycle;
using Nexus.Service.Platform;

namespace Nexus.Service.Lifecycle;

/// <summary>Wire strings for <see cref="PawnIoStatus.State"/> and <see cref="PawnIoStatus.FixAction"/>.</summary>
public static class PawnIoStates
{
    public const string Ok = "ok";
    public const string NotInstalled = "notInstalled";
    public const string InstallFailed = "installFailed";
    public const string RestartRequired = "restartRequired";
    public const string NotRunning = "notRunning";
    public const string Blocked = "blocked";
    public const string PermissionDeclined = "permissionDeclined";

    public const string FixInstall = "install";
    public const string FixRepair = "repair";
    public const string FixRestart = "restart";
    public const string FixNone = "none";
}

/// <summary>The OS-facing probes and actions PawnIoGate needs, so classification and the fix flow run without Windows.</summary>
public interface IPawnIoSystem
{
    /// <summary>False off Windows; the gate then reports ok forever.</summary>
    bool IsSupported { get; }

    bool DeviceOpen();

    bool ServiceRegistered();

    /// <summary>True when the PawnIO service is marked for deletion, a state only a reboot clears.</summary>
    bool ServiceMarkedForDeletion();

    /// <summary>StartService on the PawnIO service. 0 for started or already running, else the Win32 error.</summary>
    int StartService();

    /// <summary>Device Manager problem code of the PawnIO devnode, null when none or unknown.</summary>
    int? DevnodeProblemCode();

    Task<PawnIoInstallResult> EnsureInstalledAsync(CancellationToken ct);

    Task DelayAsync(TimeSpan delay, CancellationToken ct);

    void RestartPc();
}

/// <summary>
/// Owns the "PawnIO driver required" state. The service no longer depends on
/// PawnIO in the SCM; this class starts the driver itself and, only when it
/// is still unusable after an explicit start, a bounded wait and a re-check,
/// classifies why so the dashboard can show a full-screen gate.
///
/// Everything here is keyed off a not-ok state. A PC whose driver opens never
/// leaves "ok": every method returns before doing any work once the device
/// opens.
/// </summary>
public sealed class PawnIoGate
{
    // ERROR_SERVICE_MARKED_FOR_DELETE, ERROR_INVALID_IMAGE_HASH, ERROR_DRIVER_BLOCKED.
    internal const int ErrorMarkedForDelete = 1072;
    internal const int ErrorInvalidImageHash = 577;
    internal const int ErrorDriverBlocked = 1275;

    // CM_PROB_DRIVER_BLOCKED and CM_PROB_UNSIGNED_DRIVER.
    internal const int ProblemDriverBlocked = 48;
    internal const int ProblemUnsignedDriver = 52;

    // Longer than LhmComputer's boot gate wait, so a slow driver start is not misread as dead.
    internal static readonly TimeSpan BootConfirmWait = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan StartWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IPawnIoProvider _provider;
    private readonly IPawnIoSystem _sys;
    private readonly object _lock = new();

    private bool _confirmedNotOk;
    private bool _fixing;
    private bool _redetectRequested;
    private int _lastStartError;
    private PawnIoInstallResult? _lastOutcome;

    public PawnIoGate(IPawnIoProvider provider, IPawnIoSystem sys)
    {
        _provider = provider;
        _sys = sys;
    }

    /// <summary>Raised once when the boot check confirms the driver is unusable.</summary>
    public event Action<PawnIoStatus>? NotOkConfirmed;

    public bool IsSupported => _sys.IsSupported;

    public bool IsNotOkConfirmed
    {
        get { lock (_lock) { return _confirmedNotOk; } }
    }

    /// <summary>Keeps only the outcomes the gate can explain; a success clears the previous one.</summary>
    public void RecordOutcome(PawnIoInstallResult outcome)
    {
        lock (_lock)
        {
            _lastOutcome = outcome is PawnIoInstallResult.UserDenied
                or PawnIoInstallResult.Failed
                or PawnIoInstallResult.UpgradeStagedPendingReboot
                ? outcome
                : null;
        }
    }

    public PawnIoStatus GetStatus()
    {
        var installed = _provider.IsInstalled;
        var open = _provider.IsOpen;

        bool confirmed;
        bool fixing;
        lock (_lock)
        {
            if (open)
            {
                _confirmedNotOk = false;
            }
            confirmed = _confirmedNotOk;
            fixing = _fixing;
        }

        if (!_sys.IsSupported || open || !confirmed)
        {
            return new PawnIoStatus { Installed = installed, Open = open, Fixing = fixing };
        }

        return Build(installed, open, fixing);
    }

    /// <summary>
    /// Starts the PawnIO service when the device is not openable and waits,
    /// bounded, for it to open. A no-op (one device probe) when it already
    /// opens, which is every healthy boot after the first service start.
    /// </summary>
    public async Task<bool> EnsureRunningAsync(TimeSpan wait, CancellationToken ct = default)
    {
        if (!_sys.IsSupported || _sys.DeviceOpen())
        {
            return true;
        }

        if (_sys.ServiceRegistered() && !_sys.ServiceMarkedForDeletion())
        {
            var err = _sys.StartService();
            lock (_lock) { _lastStartError = err; }
            if (err != 0)
            {
                ServiceLog.Warn($"[pawnio] start failed with error {err}");
            }
        }

        return await WaitForDeviceAsync(wait, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Boot-time verdict, run after the install/repair pass. Starts the
    /// service once more, waits, re-checks, and only then latches the not-ok
    /// state and raises <see cref="NotOkConfirmed"/>. Returns true when the
    /// driver is usable.
    /// </summary>
    public async Task<bool> ConfirmAfterBootAsync(CancellationToken ct = default)
    {
        if (!_sys.IsSupported || _sys.DeviceOpen())
        {
            return true;
        }

        if (await EnsureRunningAsync(BootConfirmWait, ct).ConfigureAwait(false))
        {
            return true;
        }

        lock (_lock) { _confirmedNotOk = true; }
        var status = GetStatus();
        ServiceLog.Warn($"[pawnio] driver unusable after start and wait: state {status.State}, code {status.ErrorCode?.ToString() ?? "none"}");
        NotOkConfirmed?.Invoke(status);
        return false;
    }

    /// <summary>
    /// Runs install or repair for the current state through the installer's
    /// existing paths. Only acts while the gate is confirmed not ok and the
    /// fix action is install or repair; any other call returns the status.
    /// </summary>
    public async Task<PawnIoStatus> FixAsync(CancellationToken ct = default)
    {
        if (!_sys.IsSupported)
        {
            return GetStatus();
        }

        var before = GetStatus();
        if (before.FixAction is not (PawnIoStates.FixInstall or PawnIoStates.FixRepair))
        {
            return before;
        }

        lock (_lock)
        {
            if (_fixing)
            {
                return before;
            }
            _fixing = true;
        }

        try
        {
            var open = false;
            if (_sys.ServiceRegistered() && !_sys.ServiceMarkedForDeletion())
            {
                var err = _sys.StartService();
                lock (_lock) { _lastStartError = err; }
                open = await WaitForDeviceAsync(StartWait, ct).ConfigureAwait(false);
            }

            if (!open)
            {
                var outcome = await _sys.EnsureInstalledAsync(ct).ConfigureAwait(false);
                RecordOutcome(outcome);
                ServiceLog.Info($"[pawnio] fix outcome: {outcome}");
                open = await WaitForDeviceAsync(StartWait, ct).ConfigureAwait(false);
            }

            if (open)
            {
                lock (_lock)
                {
                    _confirmedNotOk = false;
                    _lastOutcome = null;
                    _lastStartError = 0;
                    _redetectRequested = true;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ServiceLog.Error($"[pawnio] fix failed: {ex.Message}");
            RecordOutcome(PawnIoInstallResult.Failed);
        }
        finally
        {
            lock (_lock) { _fixing = false; }
        }

        return GetStatus();
    }

    /// <summary>True once after a successful fix, so the caller can rerun hardware detection.</summary>
    public bool TakeRedetectRequest()
    {
        lock (_lock)
        {
            var requested = _redetectRequested;
            _redetectRequested = false;
            return requested;
        }
    }

    public bool RestartPc()
    {
        if (!_sys.IsSupported)
        {
            return false;
        }

        _sys.RestartPc();
        return true;
    }

    private async Task<bool> WaitForDeviceAsync(TimeSpan wait, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            if (_sys.DeviceOpen())
            {
                return true;
            }

            if (waited >= wait)
            {
                return false;
            }

            await _sys.DelayAsync(PollInterval, ct).ConfigureAwait(false);
            waited += PollInterval;
        }
    }

    private PawnIoStatus Build(bool installed, bool open, bool fixing)
    {
        int startError;
        PawnIoInstallResult? outcome;
        lock (_lock)
        {
            startError = _lastStartError;
            outcome = _lastOutcome;
        }

        var (state, code) = Classify(
            _sys.ServiceRegistered(),
            _sys.ServiceMarkedForDeletion(),
            startError,
            _sys.DevnodeProblemCode(),
            outcome);
        return new PawnIoStatus
        {
            Installed = installed,
            Open = open,
            State = state,
            ErrorCode = code,
            FixAction = FixActionFor(state),
            Fixing = fixing,
        };
    }

    /// <summary>
    /// Why the device does not open, for a driver already confirmed unusable.
    /// Restart-fixable and blocked causes win over install outcomes because
    /// they are the root cause; anything unexplained that has a registered
    /// service is notRunning.
    /// </summary>
    internal static (string State, int? ErrorCode) Classify(
        bool serviceRegistered,
        bool markedForDeletion,
        int startError,
        int? devnodeProblem,
        PawnIoInstallResult? lastOutcome)
    {
        int? code = startError != 0 ? startError : devnodeProblem;

        if (markedForDeletion || startError == ErrorMarkedForDelete)
        {
            return (PawnIoStates.RestartRequired, ErrorMarkedForDelete);
        }

        if (lastOutcome == PawnIoInstallResult.UpgradeStagedPendingReboot)
        {
            return (PawnIoStates.RestartRequired, code);
        }

        if (startError is ErrorInvalidImageHash or ErrorDriverBlocked
            || devnodeProblem is ProblemDriverBlocked or ProblemUnsignedDriver)
        {
            return (PawnIoStates.Blocked, code);
        }

        if (lastOutcome == PawnIoInstallResult.UserDenied)
        {
            return (PawnIoStates.PermissionDeclined, code);
        }

        if (lastOutcome == PawnIoInstallResult.Failed)
        {
            return (PawnIoStates.InstallFailed, code);
        }

        return serviceRegistered
            ? (PawnIoStates.NotRunning, code)
            : (PawnIoStates.NotInstalled, code);
    }

    internal static string FixActionFor(string state) => state switch
    {
        PawnIoStates.NotInstalled or PawnIoStates.InstallFailed or PawnIoStates.PermissionDeclined => PawnIoStates.FixInstall,
        PawnIoStates.NotRunning => PawnIoStates.FixRepair,
        PawnIoStates.RestartRequired => PawnIoStates.FixRestart,
        _ => PawnIoStates.FixNone,
    };
}
