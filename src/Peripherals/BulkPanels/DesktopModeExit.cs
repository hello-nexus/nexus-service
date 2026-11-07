using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// A display the vendor app's secondary-monitor toggle moved to a different USB identity, the
/// identity it returns to, and what the exit write must match before it is sent.
/// </summary>
public sealed record DesktopModeRow(
    string HandlerId,
    int DesktopVendorId,
    int DesktopProductId,
    int NormalVendorId,
    int NormalProductId,
    int CompanionVendorId,
    int CompanionProductId,
    int InterfaceNumber,
    int UsagePage,
    int ReportLength);

public static class DesktopModeTable
{
    /// <summary>The HydroShift II OLED Curved pair; its LED board must still be present.</summary>
    public static readonly DesktopModeRow Curved = new(
        HydroShift2CurveLcdDriver.Id,
        DesktopVendorId: 0x1A86, DesktopProductId: 0xAD23,
        NormalVendorId: 0x1CBE, NormalProductId: HydroShift2CurveProtocol.GlassProductId,
        CompanionVendorId: HydroShift2CurveProtocol.BoardVendorId, CompanionProductId: HydroShift2CurveProtocol.BoardProductId,
        InterfaceNumber: 1, UsagePage: 0xFF00, ReportLength: 513);

    public static IReadOnlyList<DesktopModeRow> Rows { get; } = new[] { Curved };

    private static readonly byte[] ExitText = "5f3759df"u8.ToArray();

    /// <summary>Zero report id, the ASCII exit text, zero padding to the report length.</summary>
    public static byte[] ExitReport(int reportLength)
    {
        var report = new byte[Math.Max(reportLength, ExitText.Length + 1)];
        ExitText.CopyTo(report, 1);
        return report;
    }
}

/// <summary>
/// Sends the exit to a display sitting in desktop mode, once per arrival of the device or close of
/// the vendor app, and only when every gate holds. The write is the last step; nothing here loops.
/// </summary>
public sealed class DesktopModeExit
{
    internal const int TransientWindowMs = 30_000;
    internal const int ArrivalWaitMs = 30_000;
    internal const int ArrivalPollMs = 250;
    internal const int MinAttemptSpacingMs = 30_000;

    private readonly IHidEnumerator _hid;
    private readonly HardwarePresence _presence;
    private readonly Func<bool?> _vendorAppRunning;
    private readonly Func<string, bool> _nexusControlOn;
    private readonly Func<DesktopModeRow, string> _parents;
    private readonly Func<long> _now;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly Dictionary<string, RowState> _states = new();

    public DesktopModeExit(
        IHidEnumerator hid,
        HardwarePresence presence,
        Func<bool?> vendorAppRunning,
        Func<string, bool> nexusControlOn,
        Func<DesktopModeRow, string>? parents = null,
        Func<long>? now = null,
        Func<int, CancellationToken, Task>? delay = null)
    {
        _hid = hid;
        _presence = presence;
        _vendorAppRunning = vendorAppRunning;
        _nexusControlOn = nexusControlOn;
        _parents = parents ?? (_ => "");
        _now = now ?? (() => Environment.TickCount64);
        _delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
    }

    /// <summary>Attempts made, for tests.</summary>
    internal int Attempts { get; private set; }

    private sealed class RowState
    {
        public bool Present;
        public bool Due;
        public long DueSince;
        public bool VendorAppWasRunning;
        public bool ControlOffLogged;
        public long? LastAttemptAt;
    }

    /// <summary>One pass over the table: notes arrivals and vendor app closes, and runs at most one attempt per row.</summary>
    public async Task PollAsync(CancellationToken ct)
    {
        foreach (var row in DesktopModeTable.Rows)
        {
            await PollRowAsync(row, ct).ConfigureAwait(false);
        }
    }

    private async Task PollRowAsync(DesktopModeRow row, CancellationToken ct)
    {
        if (!_states.TryGetValue(row.HandlerId, out var state))
        {
            state = new RowState();
            _states[row.HandlerId] = state;
        }
        if (!_presence.UsbPresent(row.DesktopVendorId, row.DesktopProductId))
        {
            state.Present = false;
            state.Due = false;
            state.VendorAppWasRunning = false;
            state.ControlOffLogged = false;
            return;
        }
        if (!state.Present)
        {
            state.Present = true;
            MarkDue(state);
        }
        if (_vendorAppRunning() is not { } appRunning)
        {
            return;
        }
        if (state.VendorAppWasRunning && !appRunning)
        {
            MarkDue(state);
        }
        state.VendorAppWasRunning = appRunning;
        if (!state.Due || appRunning)
        {
            return;
        }
        var now = _now();
        if (state.LastAttemptAt is { } last && now - last < MinAttemptSpacingMs)
        {
            state.Due = false;
            ServiceLog.Info($"[desktop-mode] {row.DesktopVendorId:X4}:{row.DesktopProductId:X4} attempt skipped: another ran {now - last} ms ago");
            return;
        }
        if (await AttemptAsync(row, state, ct).ConfigureAwait(false))
        {
            state.Due = false;
            state.LastAttemptAt = _now();
            Attempts++;
        }
    }

    private void MarkDue(RowState state)
    {
        state.Due = true;
        state.DueSince = _now();
        state.ControlOffLogged = false;
    }

    /// <summary>True once the episode is finished; false leaves it due for the next poll.</summary>
    private async Task<bool> AttemptAsync(DesktopModeRow row, RowState state, CancellationToken ct)
    {
        var id = $"{row.DesktopVendorId:X4}:{row.DesktopProductId:X4}";
        if (!_nexusControlOn(row.HandlerId))
        {
            if (!state.ControlOffLogged)
            {
                state.ControlOffLogged = true;
                ServiceLog.Info($"[desktop-mode] {id} waiting: Nexus Control is off for {row.HandlerId}");
            }
            return false;
        }

        var started = _now();
        var normal = _presence.UsbPresent(row.NormalVendorId, row.NormalProductId);
        var board = _presence.UsbPresent(row.CompanionVendorId, row.CompanionProductId);
        var onInterface = _hid.Find(row.DesktopVendorId, row.DesktopProductId)
            .Where(i => InterfaceOf(i.Path) == row.InterfaceNumber).ToList();
        var info = onInterface.FirstOrDefault(i =>
            i.UsagePage == row.UsagePage && i.InputReportByteLength == row.ReportLength && i.OutputReportByteLength == row.ReportLength);
        string? refusal = normal ? "normal-identity-present"
            : !board ? "board-absent"
            : onInterface.Count == 0 ? "no-hid-interface"
            : info is null ? "hid-shape"
            : null;
        var transient = refusal is "board-absent" or "no-hid-interface";
        if (transient && started - state.DueSince < TransientWindowMs)
        {
            return false;
        }

        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"[desktop-mode] {id}");
        var warn = true;
        try
        {
            var entry = _presence.UsbEntriesFor(row.DesktopVendorId).FirstOrDefault(e => e.ProductId == row.DesktopProductId);
            line.Append(CultureInfo.InvariantCulture, $" parent={Dash(_parents(row))} product=\"{entry?.Name}\"");
            var shown = info ?? onInterface.FirstOrDefault();
            if (shown is not null)
            {
                line.Append(CultureInfo.InvariantCulture,
                    $" hid up={shown.UsagePage:X4} u={shown.Usage:X4} rpt={shown.InputReportByteLength}/{shown.OutputReportByteLength}/{shown.FeatureReportByteLength} mi={InterfaceOf(shown.Path):X2}");
            }
            line.Append(CultureInfo.InvariantCulture, $" gates normal-absent={Flag(!normal)} board={Flag(board)} hid={Flag(info is not null)}");
            if (refusal is not null || info is null)
            {
                line.Append(" refused=").Append(refusal);
                return true;
            }

            var how = Write(info);
            line.Append(" write=").Append(how ?? "failed");
            if (how is null)
            {
                return true;
            }

            while (true)
            {
                if (_presence.UsbPresent(row.NormalVendorId, row.NormalProductId))
                {
                    line.Append(CultureInfo.InvariantCulture, $" normal identity back in {_now() - started} ms");
                    warn = false;
                    return true;
                }
                if (_now() - started >= ArrivalWaitMs)
                {
                    line.Append(CultureInfo.InvariantCulture, $" normal identity not back after {_now() - started} ms");
                    return true;
                }
                await _delay(ArrivalPollMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            line.Append(" cancelled");
            return true;
        }
        finally
        {
            if (warn)
            {
                ServiceLog.Warn(line.ToString());
            }
            else
            {
                ServiceLog.Info(line.ToString());
            }
        }
    }

    private string? Write(HidDeviceInfo info)
    {
        using var device = _hid.Open(info.Path, forInput: true);
        if (device is null)
        {
            return null;
        }
        var report = DesktopModeTable.ExitReport(info.OutputReportByteLength);
        if (device.Write(report))
        {
            return "ok";
        }
        // Some vendor reports are refused on the interrupt pipe; the control path is the same report, same attempt.
        return device.SetOutputReport(report) ? "ok-control" : null;
    }

    /// <summary>Hubs above the desktop-mode device, for the attempt's log line.</summary>
    internal static string DescribeParents(DesktopModeRow row)
    {
        try
        {
#if WINDOWS
            return OperatingSystem.IsWindows()
                ? Platform.Windows.WindowsDevnodes.DescribeUsbParents(row.DesktopVendorId, row.DesktopProductId)
                : "";
#else
            return "";
#endif
        }
        catch (Exception)
        {
            return "";
        }
    }

    internal static int InterfaceOf(string path)
    {
        var i = path.IndexOf("mi_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && i + 5 <= path.Length
            && int.TryParse(path.AsSpan(i + 3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var mi)
            ? mi
            : -1;
    }

    private static string Flag(bool ok) => ok ? "ok" : "no";

    private static string Dash(string value) => value.Length == 0 ? "-" : value;
}

/// <summary>Polls <see cref="DesktopModeExit"/> off the startup path.</summary>
public sealed class DesktopModeExitWorker : Microsoft.Extensions.Hosting.BackgroundService
{
    private const int PollMs = 2000;
    private readonly DesktopModeExit _exit;

    public DesktopModeExitWorker(DesktopModeExit exit)
    {
        _exit = exit;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _exit.PollAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(PollMs, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[desktop-mode] poll failed: {ex.Message}");
                await Task.Delay(PollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
