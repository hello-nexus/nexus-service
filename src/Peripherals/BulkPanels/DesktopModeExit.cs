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

    /// <summary>Report id 0, the ASCII exit text, zero padding to the report length.</summary>
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
    internal const int HidSettleMs = 3000;
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
        public bool VendorAppWasRunning;
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
        var running = _vendorAppRunning();
        var present = _presence.UsbPresent(row.DesktopVendorId, row.DesktopProductId);
        if (!present)
        {
            state.Present = false;
            state.Due = false;
            state.VendorAppWasRunning = running ?? state.VendorAppWasRunning;
            return;
        }
        if (!state.Present)
        {
            state.Present = true;
            state.Due = true;
        }
        if (running is not { } appRunning)
        {
            return;
        }
        if (state.VendorAppWasRunning && !appRunning)
        {
            state.Due = true;
        }
        state.VendorAppWasRunning = appRunning;
        if (!state.Due || appRunning)
        {
            return;
        }
        state.Due = false;
        var now = _now();
        if (state.LastAttemptAt is { } last && now - last < MinAttemptSpacingMs)
        {
            return;
        }
        state.LastAttemptAt = now;
        Attempts++;
        await AttemptAsync(row, ct).ConfigureAwait(false);
    }

    private async Task AttemptAsync(DesktopModeRow row, CancellationToken ct)
    {
        var started = _now();
        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"[desktop-mode] {row.DesktopVendorId:X4}:{row.DesktopProductId:X4}");
        var warn = true;
        try
        {
            var entry = _presence.UsbEntriesFor(row.DesktopVendorId).FirstOrDefault(e => e.ProductId == row.DesktopProductId);
            line.Append(CultureInfo.InvariantCulture, $" parent={Dash(_parents(row))} product=\"{entry?.Name}\"");

            HidDeviceInfo? info = null;
            var settleEnd = started + HidSettleMs;
            while (true)
            {
                info = _hid.Find(row.DesktopVendorId, row.DesktopProductId).FirstOrDefault(i => InterfaceOf(i.Path) == row.InterfaceNumber);
                if (info is not null || _now() >= settleEnd)
                {
                    break;
                }
                await _delay(ArrivalPollMs, ct).ConfigureAwait(false);
            }
            if (info is not null)
            {
                line.Append(CultureInfo.InvariantCulture,
                    $" hid up={info.UsagePage:X4} u={info.Usage:X4} rpt={info.InputReportByteLength}/{info.OutputReportByteLength}/{info.FeatureReportByteLength} mi={InterfaceOf(info.Path):X2}");
            }

            var control = _nexusControlOn(row.HandlerId);
            var board = _presence.UsbPresent(row.CompanionVendorId, row.CompanionProductId);
            string? refusal = !control ? "nexus-control-off"
                : !board ? "board-absent"
                : info is null ? "no-hid-mi"
                : info.UsagePage != row.UsagePage ? "hid-usage-page"
                : info.InputReportByteLength != row.ReportLength || info.OutputReportByteLength != row.ReportLength ? "hid-report-length"
                : null;
            line.Append(CultureInfo.InvariantCulture, $" gates control={Flag(control)} board={Flag(board)} hid={Flag(refusal is null || refusal is not ("no-hid-mi" or "hid-usage-page" or "hid-report-length"))}");
            if (refusal is not null || info is null)
            {
                line.Append(" refused=").Append(refusal);
                return;
            }

            var how = Write(info);
            line.Append(" write=").Append(how ?? "failed");
            if (how is null)
            {
                return;
            }

            while (true)
            {
                if (_presence.UsbPresent(row.NormalVendorId, row.NormalProductId))
                {
                    line.Append(CultureInfo.InvariantCulture, $" normal identity back in {_now() - started} ms");
                    warn = false;
                    return;
                }
                if (_now() - started >= ArrivalWaitMs)
                {
                    line.Append(CultureInfo.InvariantCulture, $" normal identity not back after {_now() - started} ms");
                    return;
                }
                await _delay(ArrivalPollMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            line.Append(" cancelled");
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
        using var device = _hid.Open(info.Path);
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
