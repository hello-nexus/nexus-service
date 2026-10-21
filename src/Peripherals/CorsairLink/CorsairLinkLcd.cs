using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>
/// Controls the LCD HID interface (AIO pump LCD or XD5 Elite LCD). This is a
/// separate USB interface from the iCUE LINK hub: VID 0x1B1C, PID 0x0C4E (AIO)
/// or 0x0C43 (XD5), OutputReportByteLength 1024. The LCD uses numbered HID report
/// id 0x02 (buffer[0]); the 1024-byte write buffer is byte-identical to OpenLinkHub
/// transferToLcd. All public operations run under <see cref="_lock"/>.
/// </summary>
public sealed class CorsairLinkLcd
{
    // lsh.go lcdBufferSize / lcdHeaderSize.
    internal const int LcdBufferSize = 1024;
    internal const int LcdHeaderSize = 8;
    internal const int MaxPayloadPerChunk = LcdBufferSize - LcdHeaderSize; // 1016
    // lcd.go product ids (decimal 3150 / 3139).
    internal const int AioPid = 0x0C4E;
    internal const int Xd5Pid = 0x0C43;
    internal const int LcdVendorId = 0x1B1C;
    /// <summary>Device-list row id, Nexus Control gate key, and streamed-panel profile kind.</summary>
    public const string DeviceId = "corsair-link-lcd";
    // Both LCD modules are the same 480x480 round glass (lsh.go lcdWidth / lcdHeight).
    public const int PanelWidth = 480;
    public const int PanelHeight = 480;
    // The LCD's HID output report incl. the report-id byte at [0]; matches OLH lcdBufferSize.
    internal const int LcdOutputReportByteLength = LcdBufferSize;

    private readonly object _lock = new();
    // 1024-byte HID output report: [0]=0x02 report id, header [1..7], payload [8..].
    // Allocated once and reused under _lock.
    private readonly byte[] _writeBuffer = new byte[LcdOutputReportByteLength];
    private readonly Nexus.Service.Lighting.LcdLockSleepSet? _lockSleep;
    private IHidDevice? _device;
    private volatile bool _attached;
    private volatile string? _serial;
    private volatile object? _streamOwner;
    private long _lastStreamFrameMs;
    // The handle is opened whenever the chain reports an LCD, but the glass stays the
    // vendor app's until Nexus Control is on and something actually paints it. Only then
    // is there anything to hand back, so only then is the shutdown sequence ours to send.
    private bool _claimed;
    // Frames are ignored until host mode is on, and a hand-back (Nexus's or iCUE's exit)
    // turns it off, so the first frame after attach or a hand-back turns it back on.
    private bool _hostMode;
    // A gone LCD fails every write; a busy one can fail one. Dropping the handle on a
    // run of failures re-opens it without flapping on a single miss.
    private const int FailedWritesBeforeDrop = 3;
    private int _failedWrites;
    private bool _retired;
    private int _repaintRequested;
    private int _attachGeneration;

    public CorsairLinkLcd(Nexus.Service.Lighting.LcdLockState? lockState = null)
    {
        if (lockState is not null)
        {
            _lockSleep = new Nexus.Service.Lighting.LcdLockSleepSet(lockState);
        }
    }

    /// <summary>
    /// True while the session lock holds the glass dark. Decided once per lock from
    /// <paramref name="sleepWhenLocked"/>, so a screen that attaches or re-opens mid-lock joins
    /// and a setting flipped mid-lock changes nothing until the next lock. Applies to every LCD
    /// this class drives; there is no model gate.
    /// </summary>
    public bool IsLockSleeping(Func<bool> sleepWhenLocked) =>
        _lockSleep is not null && _lockSleep.IsSleeping("lcd", sleepWhenLocked);

    /// <summary>True when a usable LCD HID device is open.</summary>
    public bool HasDevice => _attached;

    /// <summary>Chain serial of the attached LCD, or null when nothing is attached.</summary>
    public string? Serial => _serial;

    /// <summary>
    /// Bumped on every successful attach. The hub's shutdown sequence resets brightness to
    /// 100 on the way out, so whoever owns those settings re-asserts them when this moves.
    /// </summary>
    public int AttachGeneration => System.Threading.Volatile.Read(ref _attachGeneration);

    /// <summary>
    /// How long after the last streamed frame the glass counts as the panel's. Frames stop
    /// arriving for reasons the transport never hears about - the overlay dying, a focus
    /// mode turning panels off - and this firmware clears the panel when they do, so the
    /// media worker takes back a stream that has gone quiet rather than leaving it black.
    /// </summary>
    private const long StreamIdleMs = 2000;

    /// <summary>
    /// True while a panel session is actively streaming widget frames. The media worker
    /// stands down for the duration: both push whole frames down the same pipe, and a
    /// still interleaved with a 30 fps stream reads as flicker, not a picture.
    /// </summary>
    public bool IsStreamLive =>
        _streamOwner is not null
        && (_storing || System.Environment.TickCount64 - System.Threading.Volatile.Read(ref _lastStreamFrameMs) < StreamIdleMs);

    // An upload holds the pipe for seconds; the stream it paused still owns the glass.
    private volatile bool _storing;

    /// <summary>
    /// Marks <paramref name="owner"/> as the panel session holding the glass. The claim is
    /// by identity: a faulted transport disposing after its replacement has opened must not
    /// release the live one.
    /// </summary>
    public void ClaimStream(object owner)
    {
        _streamOwner = owner;
        System.Threading.Volatile.Write(ref _repaintRequested, 0);
        System.Threading.Volatile.Write(ref _lastStreamFrameMs, System.Environment.TickCount64);
    }

    /// <summary>
    /// Stamps a rendered frame as having reached the glass. Only the render side calls this;
    /// the transport's own keepalive re-sends deliberately do not, so a stalled renderer
    /// still expires.
    /// </summary>
    public void NoteStreamFrame(object owner)
    {
        if (ReferenceEquals(_streamOwner, owner))
        {
            System.Threading.Volatile.Write(ref _lastStreamFrameMs, System.Environment.TickCount64);
        }
    }

    /// <summary>
    /// True once after a screen select took the glass from <paramref name="owner"/>'s stream:
    /// the stream re-pushes its last frame instead of waiting for the next render.
    /// </summary>
    public bool TakeRepaintRequest(object owner) =>
        ReferenceEquals(_streamOwner, owner) && System.Threading.Interlocked.Exchange(ref _repaintRequested, 0) == 1;

    /// <summary>Releases the claim, but only if <paramref name="owner"/> still holds it.</summary>
    public void ReleaseStream(object owner)
    {
        if (ReferenceEquals(_streamOwner, owner))
        {
            _streamOwner = null;
        }
    }

    /// <summary>
    /// Scans <paramref name="chain"/> for an LCD chain device (type 6 or 14), then opens the
    /// matching HID interface by VID, PID, serial, and OutputReportByteLength. OLH init does
    /// not send a power-on report (lsh.go:452-458); the worker applies rotation + brightness.
    /// </summary>
    public void DiscoverAndAttach(IReadOnlyList<CorsairLinkDevice> chain, IHidEnumerator hid)
    {
        lock (_lock)
        {
            if (_retired) return;
            CorsairLinkDevice? lcdDev = null;
            foreach (var d in chain)
            {
                if (d.Type == 6 || d.Type == 14)
                {
                    lcdDev = d;
                    break;
                }
            }
            if (lcdDev is null) return;

            var opened = TryOpenMatchingHid(hid, AioPid, lcdDev.Serial)
                ?? TryOpenMatchingHid(hid, Xd5Pid, lcdDev.Serial)
                ?? TryOpenSoleHid(hid, lcdDev.Type == 14 ? Xd5Pid : AioPid);
            if (opened is null) return;

            _device = opened;
            _serial = lcdDev.Serial;
            _attached = true;
            System.Threading.Interlocked.Increment(ref _attachGeneration);
        }
    }

    private static IHidDevice? TryOpenMatchingHid(IHidEnumerator hid, int pid, string serial)
    {
        foreach (var info in hid.Find(LcdVendorId, pid))
        {
            if (info.OutputReportByteLength != LcdOutputReportByteLength) continue;
            if (info.Serial != serial) continue;
            var dev = hid.Open(info.Path, forInput: false);
            if (dev is not null) return dev;
        }
        return null;
    }

    // A TITAN RX LCD's USB serial differs from its chain serial, so a sole LCD on the
    // type's PID is taken without a serial match, as OLH GetAioLCDSerial does.
    private static IHidDevice? TryOpenSoleHid(IHidEnumerator hid, int pid)
    {
        HidDeviceInfo? sole = null;
        foreach (var info in hid.Find(LcdVendorId, pid))
        {
            if (info.OutputReportByteLength != LcdOutputReportByteLength) continue;
            if (sole is not null) return null;
            sole = info;
        }
        return sole is null ? null : hid.Open(sole.Path, forInput: false);
    }

    // Repeated failed writes mean the LCD left the bus (it re-enumerates on its own); the
    // hub session re-opens it on its next poll. Nothing is sent to the gone handle.
    private void DropDevice()
    {
        ServiceLog.Warn("[corsair-lcd] writes failing; reopening the screen");
        _failedWrites = 0;
        _device?.Dispose();
        _device = null;
        _attached = false;
        _serial = null;
        _claimed = false;
        _hostMode = false;
    }

    /// <summary>
    /// Service-stop release: the fast teardown skips the connection worker's own release, so
    /// the glass is handed back here (to the stored image or the coolant screen) and no late
    /// re-attach takes it again before the process exits.
    /// </summary>
    public void ReleaseForShutdown(bool handBack)
    {
        lock (_lock) _retired = true;
        Detach(handBack);
    }

    /// <summary>Sends the shutdown feature-report sequence and disposes the device.</summary>
    /// <param name="handBack">False when another program now owns the LCD: the shutdown sequence would override its screen.</param>
    public void Detach(bool handBack = true)
    {
        lock (_lock)
        {
            if (_device is null) return;
            if (_claimed && handBack)
            {
                SendShutdown();
            }
            _claimed = false;
            _hostMode = false;
            _device.Dispose();
            _device = null;
            _attached = false;
            _serial = null;
        }
    }

    /// <summary>
    /// Splits <paramref name="jpeg"/> into chunks and writes each to the LCD.
    /// The firmware renders (latches) when it receives a chunk shorter than 1016 bytes.
    /// If remaining data is exactly 1016 bytes it is capped to 1015 to force the latch.
    /// </summary>
    public bool SendFrame(System.ReadOnlySpan<byte> jpeg)
    {
        lock (_lock)
        {
            if (_device is null) return false;

            _claimed = true;
            if (!_hostMode)
            {
                _hostMode = _device.SetFeature(HostModeOn);
            }
            var lcdSlice = _writeBuffer.AsSpan(0, LcdBufferSize);
            var offset = 0;
            var total = jpeg.Length;
            var chunkIndex = 0;

            while (offset < total)
            {
                var remaining = total - offset;
                var isLast = remaining <= MaxPayloadPerChunk;
                var chunkSize = ComputeChunkSize(offset, total, isLast);

                lcdSlice.Clear();
                FillChunkHeader(lcdSlice, chunkIndex, chunkSize, isLast);
                jpeg.Slice(offset, chunkSize).CopyTo(lcdSlice.Slice(LcdHeaderSize));

                if (!_device.Write(_writeBuffer))
                {
                    if (++_failedWrites >= FailedWritesBeforeDrop) DropDevice();
                    return false;
                }
                _failedWrites = 0;

                offset += chunkSize;
                chunkIndex++;
            }
            return true;
        }
    }

    public bool SetBrightness(byte value)
    {
        lock (_lock)
        {
            if (_device is null) return false;
            _claimed = true;
            return _device.SetFeature(BuildBrightnessReport(value));
        }
    }

    public bool SetRotation(byte value)
    {
        lock (_lock)
        {
            if (_device is null) return false;
            _claimed = true;
            return _device.SetFeature(BuildRotationReport(value));
        }
    }

    /// <summary>
    /// What the glass shows once the host lets go: the image stored in the LCD's flash, or
    /// the firmware's coolant screen. Set from settings; the shutdown sequence carries it.
    /// </summary>
    public bool ShowsStoredImage { get; set; }

    /// <summary>
    /// Writes <paramref name="file"/> (a <see cref="CorsairLinkLcdDeviceFile"/>) into the
    /// LCD's flash and commits it. It plays only once the stored screen is selected.
    /// </summary>
    public bool StoreImage(byte[] file)
    {
        lock (_lock)
        {
            if (_device is null) return false;
            _storing = true;
            try
            {
                return StoreImageLocked(_device, file);
            }
            finally
            {
                _storing = false;
            }
        }
    }

    private bool StoreImageLocked(IHidDevice device, byte[] file)
    {
        var sent = 0;
        foreach (var report in CorsairLinkLcdDeviceFile.UploadReports(file))
        {
            if (!device.Write(report))
            {
                ServiceLog.Warn($"[corsair-lcd] stored-image write failed at report {sent} ({file.Length} B file)");
                return false;
            }
            sent++;
        }
        // The upload held the pipe; a panel stream that was live still owns the glass.
        if (_streamOwner is not null)
        {
            System.Threading.Volatile.Write(ref _lastStreamFrameMs, System.Environment.TickCount64);
        }
        if (!device.SetFeature(CorsairLinkLcdDeviceFile.CommitReport(file)))
        {
            ServiceLog.Warn($"[corsair-lcd] stored-image commit refused after {sent} reports");
            return false;
        }
        ServiceLog.Info($"[corsair-lcd] stored a {file.Length} B image in {sent} reports");
        return true;
    }

    /// <summary>
    /// Selects the stored image or the coolant screen as the hand-back screen and shows it
    /// now. With <paramref name="resumePainting"/> false host mode stays off, so the device keeps its own screen. iCUE wraps the choice in host mode on, choice, host mode off. Frames are ignored
    /// out of host mode, so if Nexus was painting the glass host mode goes back on.
    /// </summary>
    public bool SelectHandBackScreen(bool storedImage, bool resumePainting = true)
    {
        lock (_lock)
        {
            if (_device is null) return false;
            ShowsStoredImage = storedImage;
            var painting = _hostMode;
            var source = storedImage ? (byte)0x00 : (byte)0x01;
            var ok = _device.SetFeature(HostModeOn)
                && _device.SetFeature(new byte[] { 0x03, 0x1b, source, 0x01 })
                && _device.SetFeature(new byte[] { 0x03, 0x1e, source, 0x01 })
                && _device.SetFeature(HostModeOff);
            _hostMode = false;
            if (ok && painting && resumePainting)
            {
                ok = _hostMode = _device.SetFeature(HostModeOn);
                System.Threading.Volatile.Write(ref _repaintRequested, 1);
            }
            if (!ok) ServiceLog.Warn($"[corsair-lcd] hand-back screen select refused (stored={storedImage})");
            return ok;
        }
    }

    // Shutdown sequence per lsh.go:659-677, with 1e naming the hand-back screen
    // (00 stored image, 01 coolant screen), as iCUE sends it on exit.
    private void SendShutdown()
    {
        var source = ShowsStoredImage ? (byte)0x00 : (byte)0x01;
        _device!.SetFeature(new byte[] { 0x03, 0x1e, source, 0x01 });
        _device!.SetFeature(HostModeOff);
        _device!.SetFeature(new byte[] { 0x03, 0x0b, 0x64, 0x01 });
    }

    /// <summary>
    /// Returns chunk payload size. For the last chunk, caps exactly-1016 to 1015 so
    /// the firmware always receives a sub-1016 final packet and renders the frame.
    /// </summary>
    internal static int ComputeChunkSize(int offset, int totalLength, bool isLast)
    {
        if (!isLast) return MaxPayloadPerChunk;
        var remaining = totalLength - offset;
        return remaining == MaxPayloadPerChunk ? MaxPayloadPerChunk - 1 : remaining;
    }

    /// <summary>
    /// Fills the 8-byte LCD header at <paramref name="buffer"/>[0..7]; buffer[0]=0x02 is the
    /// HID report id. Layout: [0x02, 0x05, 0x01, isLast(0/1), chunkIdx&amp;0xFF, 0x00, lenLo, lenHi].
    /// </summary>
    internal static void FillChunkHeader(System.Span<byte> buffer, int chunkIndex, int dataLen, bool isLast)
    {
        buffer[0] = 0x02;
        buffer[1] = 0x05;
        buffer[2] = 0x01;
        buffer[3] = isLast ? (byte)0x01 : (byte)0x00;
        buffer[4] = (byte)(chunkIndex & 0xFF);
        buffer[5] = 0x00;
        buffer[6] = (byte)(dataLen & 0xFF);
        buffer[7] = (byte)((dataLen >> 8) & 0xFF);
    }

    private static readonly byte[] HostModeOn = { 0x03, 0x1d, 0x01, 0x01 };
    private static readonly byte[] HostModeOff = { 0x03, 0x1d, 0x00, 0x01 };

    internal static byte[] BuildBrightnessReport(byte value) =>
        new byte[] { 0x03, 0x0b, value, 0x01 };

    internal static byte[] BuildRotationReport(byte value) =>
        new byte[] { 0x03, 0x0c, value, 0x01 };
}
