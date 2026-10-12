using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Conflicts;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Diagnostics;

/// <summary>
/// Writes a one-time hardware/specs snapshot to the service log shortly after
/// startup, so a tester's nexus-service.log opens with the full picture of what Nexus
/// detected - system specs plus every device, monitor, RGB and cooling
/// component as read at boot. Diagnostics only; never on the boot critical path.
///
/// Gated on <see cref="SystemSpecsCollector.GetAsync"/>, which awaits the sensor
/// provider's readiness, so the snapshot reflects enumerated hardware rather
/// than a half-initialised cold-boot state. Devices that connect later still
/// log their own connect lines, so nothing is lost.
/// </summary>
public sealed class StartupDiagnosticsDumpService : BackgroundService
{
    private const string Tag = "[startup-diagnostics]";

    private readonly SystemSpecsCollector _specs;
    private readonly DeviceManager _devices;
    private readonly ILightingDeviceProvider _lighting;
    private readonly ICoolingProvider _cooling;
    private readonly IMonitorEnumerator _monitors;
    private readonly IHidEnumerator _hid;
    private readonly IConflictDetector _conflicts;
    private readonly IConflictAppInstallProbe _conflictInstalls;

    public StartupDiagnosticsDumpService(
        SystemSpecsCollector specs,
        DeviceManager devices,
        ILightingDeviceProvider lighting,
        ICoolingProvider cooling,
        IMonitorEnumerator monitors,
        IHidEnumerator hid,
        IConflictDetector conflicts,
        IConflictAppInstallProbe conflictInstalls)
    {
        _specs = specs;
        _devices = devices;
        _lighting = lighting;
        _cooling = cooling;
        _monitors = monitors;
        _hid = hid;
        _conflicts = conflicts;
        _conflictInstalls = conflictInstalls;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hand control back to StartAsync immediately; the dump gathers on a
        // thread-pool thread so /ping stays reachable during boot.
        await Task.Yield();
        if (stoppingToken.IsCancellationRequested) return;

        SystemSpecsResponse? specs = null;
        try
        {
            // Awaits the sensor provider's readiness, so the snapshot runs once
            // hardware enumeration has settled rather than mid-probe. Bounded: a
            // stalled provider must not suppress the whole snapshot - emit the rest
            // with a note rather than silently never logging anything.
            using var specsCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            specsCts.CancelAfter(TimeSpan.FromSeconds(30));
            specs = await _specs.GetAsync(specsCts.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (OperationCanceledException) { Emit("system specs unavailable: sensor provider not ready within 30s"); }
        catch (Exception ex) { Emit($"system specs read failed: {ex.Message}"); }

        Emit("===== Nexus startup snapshot =====");

        if (specs is not null)
        {
            Emit($"host: {specs.PcName} | {specs.OsBuild}");
            Emit($"cpu: {specs.Processor}");
            Emit($"gpu: {specs.GraphicsCard}");
            Emit($"board: {specs.Motherboard}");
            Emit($"memory: {specs.Memory}");
            Emit($"storage: {specs.Storage}");
            Emit($"display: {specs.Monitor}");
            Emit($"audio: {specs.SoundCard}");
            Emit($"network: {specs.NetworkCard}");
        }

#if WINDOWS
        try { Emit($"boot: {BootTypeProbe.Describe()}"); }
        catch (Exception ex) { Emit($"boot read failed: {ex.Message}"); }
#endif

        try
        {
            // Which vendor app is on the box is the first question a "Nexus stopped
            // driving my hardware" report raises, and nothing else in a submitted log
            // answers it. Installed, not running, is the useful column here: the
            // service starts in session 0, so an app that launches at logon is still
            // absent at this point. ConflictWatcher logs each one as it opens and closes.
            // Read before the device blocks: GetConflicts forces the watcher's first
            // scan, whose own line would otherwise land inside another section.
            var installed = _conflictInstalls.InstalledAppIds();
            var running = _conflicts.GetConflicts();
            Emit($"conflicts ({installed.Count} installed, {running.Count} running):");
            foreach (var def in ConflictAppCatalog.All)
            {
                if (!installed.Contains(def.Id, StringComparer.OrdinalIgnoreCase)) continue;
                var live = running.FirstOrDefault(c => string.Equals(c.Id, def.Id, StringComparison.OrdinalIgnoreCase));
                var state = live is null ? "installed" : $"running pid={live.Pid}";
                Emit($"  - [{def.Category}] {def.DisplayName} id={def.Id} {state}");
            }
            // A portable build registers no service, so it only ever shows up here.
            foreach (var c in running)
            {
                if (installed.Contains(c.Id, StringComparer.OrdinalIgnoreCase)) continue;
                Emit($"  - [{c.Category}] {c.DisplayName} id={c.Id} running pid={c.Pid} (no service)");
            }
        }
        catch (Exception ex) { Emit($"conflicts read failed: {ex.Message}"); }

        try
        {
            // Only the actually-attached devices - a registered handler reporting
            // connected=False is just "Nexus supports this, none plugged in", which
            // is noise on a machine that will never have that device.
            var devices = _devices.GetAll().Where(d => d.Connected).ToList();
            Emit($"devices ({devices.Count} connected):");
            foreach (var d in devices)
            {
                var fw = string.IsNullOrEmpty(d.FirmwareVersion) ? "-" : d.FirmwareVersion;
                Emit($"  - [{d.Category}] {d.Name} fw={fw} type={d.FirmwareType} id={d.Id}");
            }
        }
        catch (Exception ex) { Emit($"devices read failed: {ex.Message}"); }

        try
        {
            // The raw USB enumeration behind the Devices > Connected devices list
            // (GET /devices/usb/all): a device with no Nexus handler is absent from
            // the managed list above but appears here. Same fields/order as the UI.
            var usb = _devices.GetUsbDevices()
                .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Emit($"usb ({usb.Count} device(s)):");
            foreach (var u in usb)
            {
                var pid = u.ProductId.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? u.ProductId[2..] : u.ProductId;
                Emit($"  - {Dash(u.Name)} | {Dash(u.Manufacturer)} | {u.VendorId}:{pid} | {Dash(u.Class)} | {Dash(u.Serial)}");
            }
        }
        catch (Exception ex) { Emit($"usb read failed: {ex.Message}"); }

        try
        {
            // Every present HID collection, not just a recognized family - the
            // usage-page/usage/report-length detail an unsupported device's
            // OpenRGB driver needs, which the usb block above does not carry.
            var hid = _hid.FindAll().OrderBy(d => d.VendorId).ThenBy(d => d.ProductId).ToList();
            Emit($"hid ({hid.Count} device(s)):");
            foreach (var d in hid)
            {
                var topology = DescribeHidPath(d.Path);
                var line = $"  - {d.VendorId:X4}:{d.ProductId:X4} up={d.UsagePage:X4} u={d.Usage:X4} rpt={d.InputReportByteLength}/{d.OutputReportByteLength}/{d.FeatureReportByteLength}{topology} sn={(string.IsNullOrEmpty(d.Serial) ? "-" : d.Serial)}";

                // SinoWealth 010C model-id probe, restricted to the known 258A:010C
                // family (never send this vendor command to an arbitrary VID/PID).
                // Query bytes, model_id offset and the vendor usage page/id match
                // OpenRGB Controllers/SinowealthController/SinowealthControllerDetect.cpp
                // DetectSinowealthKeyboard10c. Read-only query, issued once at
                // startup. Open shares R/W, so on an already-driven supported 010C
                // board this Set+Get can interleave with the driver's traffic;
                // tolerated for a one-shot diagnostic whose target is an
                // unsupported board that nothing else drives.
                if (d.VendorId == 0x258A && d.ProductId == 0x010C && d.UsagePage == 0xFF00 && d.Usage == 0x01)
                {
                    using var dev = _hid.Open(d.Path);
                    if (dev != null)
                    {
                        var query = new byte[] { 0x06, 0x82, 0x01, 0x00, 0x01, 0x00, 0x06 };
                        var resp = new byte[520]; // OpenRGB reads this query as a 520-byte feature report
                        resp[0] = 0x06; // report id must be preset for GetFeature
                        if (dev.SetFeature(query) && dev.GetFeature(resp))
                        {
                            byte modelId = resp[13];
                            line += $" model=0x{modelId:X2} head={BitConverter.ToString(resp, 0, 16)}";
                        }
                    }
                }

                Emit(line);
            }
        }
        catch (Exception ex) { Emit($"hid read failed: {ex.Message}"); }

        try
        {
            var monitors = _monitors.Enumerate();
            Emit($"monitors ({monitors.Count}):");
            foreach (var m in monitors)
                Emit($"  - {m.Name} id={m.Id}");
        }
        catch (Exception ex) { Emit($"monitors read failed: {ex.Message}"); }

        try
        {
            var rgb = _lighting.GetAll();
            var devices = rgb.Devices ?? new System.Collections.Generic.List<Nexus.Service.Models.Devices.LightingDevice>();
            Emit($"lighting (init={rgb.IsInit}, {devices.Count} device(s)):");
            foreach (var l in devices)
                Emit($"  - [{l.Type}] {l.Name} leds={l.LedCount} id={l.Id}");
        }
        catch (Exception ex) { Emit($"lighting read failed: {ex.Message}"); }

        try
        {
            var cooling = _cooling.GetAll();
            Emit($"cooling ({cooling.Count} component(s)):");
            foreach (var c in cooling)
                Emit($"  - [{c.Type}] {c.Name} channels={c.Devices.Count} id={c.Id}");
        }
        catch (Exception ex) { Emit($"cooling read failed: {ex.Message}"); }

        Emit("===== end snapshot =====");
    }

    private static void Emit(string line) => ServiceLog.Info($"{Tag} {line}");

    private static string Dash(string value) => string.IsNullOrEmpty(value) ? "-" : value;

    /// <summary>
    /// Renders the interface and top-level collection a Windows HID path encodes, as
    /// a leading-space suffix. One interface can expose several collections, which
    /// enumerate as near-identical entries that a detector matching on interface
    /// alone cannot choose between. Empty for paths carrying neither, Linux included.
    /// </summary>
    internal static string DescribeHidPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }

        // Hardware-id segment only: the instance id and interface GUID that follow
        // can carry arbitrary vendor text that would false-match the prefixes below.
        var start = path.IndexOf('#');
        if (start < 0)
        {
            return "";
        }
        var end = path.IndexOf('#', start + 1);
        var segment = end < 0 ? path[(start + 1)..] : path[(start + 1)..end];

        string? mi = null;
        string? col = null;
        foreach (var token in segment.Split('&'))
        {
            if (mi is null && token.StartsWith("mi_", StringComparison.OrdinalIgnoreCase))
            {
                mi = token[3..];
            }
            else if (col is null && token.StartsWith("col", StringComparison.OrdinalIgnoreCase))
            {
                col = token[3..];
            }
        }

        if (mi is null && col is null)
        {
            return "";
        }
        if (col is null)
        {
            return $" mi={mi}";
        }
        return mi is null ? $" col={col}" : $" mi={mi} col={col}";
    }
}
