using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Media;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Peripherals.Ryujin;

/// <summary>
/// Keeps the Ryujin III Extreme open while its Nexus Control toggle is on, and runs Armoury
/// Crate's cycle: resend the duty, request status, send the sensor text slots for the built-in
/// hardware-monitor screen, request duty. This thread is the one writer, so cooling, sensor and
/// display reports never interleave. Nothing goes out while Nexus Control is off.
/// </summary>
public sealed class RyujinCoolingWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int MaxConsecutiveFailures = 3;

    private readonly IHidEnumerator _hid;
    private readonly RyujinCoolingHub _hub;
    private readonly RyujinCoolingProvider _cooling;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;
    private readonly IConfigStore _store;
    private readonly ISensorProvider _sensors;
    private readonly IBulkUsbPipeFactory? _bulk;

    private int _appliedBrightness;
    private int _appliedRotation;
    private int _appliedCount = RyujinMonitorSensors.MaxCount;
    private string[] _appliedSlots = RyujinMonitorSensors.DefaultSlots;
    private string _appliedMode = ModeMonitor;

    public const string ModeMonitor = "monitor";
    public const string ModeImage = "image";
    public const string ReasonFailed = "failed";
    public const string ReasonControlOff = "controlOff";
    public const string ReasonBulkUnavailable = "bulkUnavailable";
    public const string ReasonBulkInUse = "bulkInUse";

    /// <summary>Where the encoded AVI of the picked file is kept.</summary>
    internal static string MediaPath() => Path.Combine(MediaLibrary.DeviceStoreDir("asus-ryujin-extreme"), "media.avi");

    internal Func<string> MediaFile { get; set; } = MediaPath;

    /// <summary>Gate decisions made so far; a test waits on it instead of sleeping.</summary>
    internal int GateChecks => Volatile.Read(ref _gateChecks);

    private int _gateChecks;

    internal Func<DateTime> Now { get; set; } = () => DateTime.Now;

    public RyujinCoolingWorker(
        IHidEnumerator hid, RyujinCoolingHub hub, RyujinCoolingProvider cooling, DeviceControlGate gate, HardwarePresence presence,
        IConfigStore store, ISensorProvider sensors, IBulkUsbPipeFactory? bulk = null)
    {
        _bulk = bulk;
        _hid = hid;
        _hub = hub;
        _cooling = cooling;
        _gate = gate;
        _presence = presence;
        _store = store;
        _sensors = sensors;
        // A control change stops the writes at once instead of on the next keepalive.
        _gate.Changed += (handler, _) =>
        {
            if (string.Equals(handler, RyujinCoolingProtocol.HandlerId, StringComparison.OrdinalIgnoreCase))
            {
                _hub.Wake.Release();
            }
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Interlocked.Increment(ref _gateChecks);
                if (!_gate.IsEnabled(RyujinCoolingProtocol.HandlerId)
                    || !_presence.UsbPresent(RyujinCoolingProtocol.VendorId, RyujinCoolingProtocol.ProductId))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var device = FindAndOpen();
                if (device is null)
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                _hub.Attach(device);
                ServiceLog.Info("[asus-ryujin-cooling] connected");
                try
                {
                    await RunSessionAsync(stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    // Best effort: a pump Nexus drove goes back to the safe duty before the handle closes.
                    try
                    {
                        _cooling.ReleaseForDisconnect();
                        _hub.Flush();
                    }
                    catch (Exception ex) { ServiceLog.Warn($"[asus-ryujin-cooling] release on disconnect failed: {ex.Message}"); }
                    _hub.Detach();
                    ServiceLog.Info("[asus-ryujin-cooling] disconnected");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[asus-ryujin-cooling] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        var display = _store.Load().Devices.RyujinDisplay;
        _appliedBrightness = display.Brightness;
        _appliedRotation = display.Rotation;
        _appliedCount = RyujinMonitorSensors.NormalizeCount(display.SensorCount);
        _appliedSlots = RyujinMonitorSensors.NormalizeSlots(display.Slots);
        if (!_hub.Send(RyujinMonitorProtocol.EncodeStartup(Now(), display.Rotation, display.Brightness, _appliedCount)))
        {
            ServiceLog.Warn("[asus-ryujin-cooling] startup sequence write failed");
            return;
        }
        ServiceLog.Info("[asus-ryujin-cooling] hardware-monitor layout selected");
        _appliedMode = ModeMonitor;

        int failures = 0;
        long nextCycle = 0;
        while (!ct.IsCancellationRequested && _gate.IsEnabled(RyujinCoolingProtocol.HandlerId))
        {
            if (ApplyScreenMode(ct) | ApplyDisplayChange())
            {
                nextCycle = 0;
            }
            if (Environment.TickCount64 >= nextCycle)
            {
                _cooling.ReassertControl();
                nextCycle = Environment.TickCount64 + RyujinCoolingProtocol.KeepaliveMs;
                if (_hub.Flush() && _hub.Poll(BuildSensorFrames))
                {
                    failures = 0;
                }
                else if (++failures >= MaxConsecutiveFailures)
                {
                    return;
                }
            }
            else if (!_hub.Flush() && ++failures >= MaxConsecutiveFailures)
            {
                return;
            }
            int wait = (int)Math.Max(1, nextCycle - Environment.TickCount64);
            await _hub.Wake.WaitAsync(wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Switches between the monitor layout and the stored picture. Flash is written only when the picked file
    /// differs from what the last successful upload left there; a failed upload is not retried until a new pick.
    /// Returns true after the monitor layout is selected again, so the next cycle sends the sensor frames at once.
    /// </summary>
    private bool ApplyScreenMode(CancellationToken ct)
    {
        var wanted = _store.Load().Devices.RyujinDisplay;
        if (wanted.ScreenMode == ModeImage && wanted.MediaSha.Length > 0)
        {
            bool uploaded = false;
            if (wanted.UploadedSha != wanted.MediaSha)
            {
                if (wanted.MediaSha == wanted.UploadFailedSha)
                {
                    return _appliedMode == ModeImage && SelectMonitor(wanted);
                }
                if (!UploadMedia(wanted.MediaSha, ct))
                {
                    return _appliedMode == ModeImage && _gate.IsEnabled(RyujinCoolingProtocol.HandlerId) && SelectMonitor(wanted);
                }
                uploaded = true;
            }
            if (uploaded || _appliedMode != ModeImage)
            {
                if (_hub.Send(RyujinUploadProtocol.EncodeSelectStoredMedia()))
                {
                    _appliedMode = ModeImage;
                    ServiceLog.Info($"[asus-ryujin-cooling] stored-media layout selected, held={(uploaded ? "after upload" : "from an earlier upload")}");
                }
            }
            return false;
        }
        return _appliedMode == ModeImage && SelectMonitor(wanted);
    }

    private bool SelectMonitor(RyujinDisplaySettings wanted)
    {
        if (!_hub.Send(RyujinMonitorProtocol.EncodeRotationChange(Now(), wanted.Rotation, wanted.Brightness, _appliedCount)))
        {
            return false;
        }
        _appliedMode = ModeMonitor;
        ServiceLog.Info("[asus-ryujin-cooling] hardware-monitor layout selected again");
        return true;
    }

    private bool UploadMedia(string sha, CancellationToken ct)
    {
        try
        {
            return UploadMediaCore(sha, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(sha, $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void RecordFailure(string sha, string message, string reason = ReasonFailed)
    {
        _hub.Upload = new RyujinUploadState("failed", 0, 0, message, reason);
        ServiceLog.Warn($"[ryujin-upload] not uploaded: {message}");
        try
        {
            _store.Update(s => s.Devices.RyujinDisplay.UploadFailedSha = sha);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[ryujin-upload] failed upload not recorded: {ex.Message}");
        }
    }

    private bool UploadMediaCore(string sha, CancellationToken ct)
    {
        var path = MediaFile();
        if (!File.Exists(path))
        {
            RecordFailure(sha, "the encoded file is missing");
            return false;
        }
        var avi = File.ReadAllBytes(path);
        if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(avi)), sha, StringComparison.OrdinalIgnoreCase))
        {
            RecordFailure(sha, "the encoded file does not match the picked one");
            return false;
        }
        bool denied = false;
        using var pipe = _bulk?.Open(RyujinCoolingProtocol.VendorId, RyujinCoolingProtocol.ProductId, RyujinUploadProtocol.BulkWritePipe, RyujinUploadProtocol.BulkReadPipe, out denied);
        var device = _hub.Device;
        if (device is null || pipe is null)
        {
            if (denied)
            {
                RecordFailure(sha, "another program has the cooler's bulk interface open (close Armoury Crate and try again)", ReasonBulkInUse);
            }
            else
            {
                RecordFailure(sha, "the cooler's bulk interface cannot be opened (WinUSB is not bound to it)", ReasonBulkUnavailable);
            }
            return false;
        }
        int total = RyujinUploadProtocol.ChunkCount(avi.Length);
        _hub.Upload = new RyujinUploadState("uploading", 0, total, "");
        if (!_hub.Send(RyujinUploadProtocol.EncodeSelectStoredMedia()))
        {
            RecordFailure(sha, "the layout select write failed");
            return false;
        }
        _appliedMode = ModeImage;
        var uploader = new RyujinUploader(new RyujinHidBulkLink(device, pipe, _hub.TakeStashedAck), ServiceLog.Info, ServiceLog.Warn)
        {
            Keepalive = KeepaliveDuringUpload,
            AbortCheck = () => _gate.IsEnabled(RyujinCoolingProtocol.HandlerId) ? RyujinUploadAbort.None : RyujinUploadAbort.ControlOff,
        };
        RyujinUploadResult result;
        _hub.BeginAckCapture();
        try
        {
            result = uploader.Upload(avi, (sent, count) => _hub.Upload = new RyujinUploadState("uploading", sent, count, ""), ct);
        }
        finally
        {
            _hub.EndAckCapture();
        }
        if (!result.Ok)
        {
            switch (result.Abort)
            {
                case RyujinUploadAbort.ControlOff:
                    _hub.Upload = new RyujinUploadState("failed", 0, 0, "Nexus control was turned off during the upload", ReasonControlOff);
                    ServiceLog.Warn("[ryujin-upload] stopped: Nexus control was turned off during the upload");
                    break;
                case RyujinUploadAbort.Cancelled:
                    _hub.Upload = RyujinUploadState.Idle;
                    ServiceLog.Info("[ryujin-upload] stopped: the service is shutting down");
                    break;
                default:
                    RecordFailure(sha, result.Message);
                    break;
            }
            return false;
        }
        _store.Update(s =>
        {
            s.Devices.RyujinDisplay.UploadedSha = sha;
            s.Devices.RyujinDisplay.UploadFailedSha = "";
        });
        _hub.Upload = new RyujinUploadState("done", total, total, result.Message);
        return true;
    }

    private void KeepaliveDuringUpload()
    {
        try
        {
            _cooling.ReassertControl();
            _hub.Flush();
            _hub.Poll();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[ryujin-upload] keepalive during the upload failed: {ex.Message}");
        }
    }

    /// <summary>Sends the settings the user changed since the last send: a rotation or sensor selection change re-selects the layout (and returns true so the next cycle sends the new sensor frames at once), a brightness change is the burst of three settings reports, each behind a DC.</summary>
    private bool ApplyDisplayChange()
    {
        var wanted = _store.Load().Devices.RyujinDisplay;
        int wantedCount = RyujinMonitorSensors.NormalizeCount(wanted.SensorCount);
        var wantedSlots = RyujinMonitorSensors.NormalizeSlots(wanted.Slots);
        bool sensorsChanged = wantedCount != _appliedCount || !wantedSlots.AsSpan().SequenceEqual(_appliedSlots);
        if (wanted.Rotation != _appliedRotation || sensorsChanged)
        {
            if (_hub.Send(RyujinMonitorProtocol.EncodeRotationChange(Now(), wanted.Rotation, wanted.Brightness, wantedCount)))
            {
                ServiceLog.Info($"[asus-ryujin-cooling] display rotation {_appliedRotation} -> {wanted.Rotation}, brightness {_appliedBrightness} -> {wanted.Brightness}, sensors {_appliedCount} [{string.Join(",", _appliedSlots)}] -> {wantedCount} [{string.Join(",", wantedSlots)}]");
                _appliedRotation = wanted.Rotation;
                _appliedBrightness = wanted.Brightness;
                _appliedCount = wantedCount;
                _appliedSlots = wantedSlots;
                if (_appliedMode == ModeImage)
                {
                    _hub.Send(RyujinUploadProtocol.EncodeSelectStoredMedia());
                }
                return sensorsChanged;
            }
        }
        else if (wanted.Brightness != _appliedBrightness)
        {
            if (_hub.Send(RyujinMonitorProtocol.EncodeBrightnessBurst(_appliedRotation, _appliedBrightness, wanted.Brightness)))
            {
                ServiceLog.Info($"[asus-ryujin-cooling] display brightness {_appliedBrightness} -> {wanted.Brightness}");
                _appliedBrightness = wanted.Brightness;
            }
        }

        return false;
    }

    private IReadOnlyList<byte[]> BuildSensorFrames()
    {
        if (_appliedMode == ModeImage)
        {
            return Array.Empty<byte[]>();
        }
        var snap = _hub.Snapshot;
        bool fresh = _hub.StatusFresh;
        return RyujinMonitorFrames.Build(
            _appliedCount,
            _appliedSlots,
            new RyujinReadings(
                ReadSummary(SummarySensorKind.CpuTemp),
                ReadSummary(SummarySensorKind.GpuTemp),
                fresh ? (float)snap.LiquidTempC : null,
                fresh ? snap.PumpRpm : null,
                fresh ? snap.FanRpm : null));
    }

    private float? ReadSummary(SummarySensorKind kind)
    {
        try
        {
            return SummarySensors.Value(_sensors, kind);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[asus-ryujin-cooling] sensor read failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private IHidDevice? FindAndOpen()
    {
        HidDeviceInfo? best = null;
        foreach (var info in _hid.Find(RyujinCoolingProtocol.VendorId, RyujinCoolingProtocol.ProductId))
        {
            if (info.OutputReportByteLength < RyujinCoolingProtocol.ReportLength || info.InputReportByteLength <= 0)
            {
                continue;
            }
            if (best is null || info.OutputReportByteLength > best.OutputReportByteLength)
            {
                best = info;
            }
        }
        // Overlapped so Read honors its timeout on Windows.
        return best is null ? null : _hid.Open(best.Path, forInput: true);
    }
}
