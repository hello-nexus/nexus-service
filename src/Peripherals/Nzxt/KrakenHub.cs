using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.PixelFormats;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// Owns the Kraken's HID control channel and its WinUSB bulk pipe, and serializes every
/// exchange. HID I/O happens under <c>_lock</c>; the LCD's bulk pixel writes run outside it
/// under <c>_lcdTransferLock</c>, since they use a different endpoint. Callers outside the
/// worker thread read <see cref="Snapshot"/> without blocking.
/// </summary>
public sealed class KrakenHub : IDisposable
{
    private const int ConnectReadTimeoutMs = 1000;
    private const int CommandReadTimeoutMs = 700;
    private const int PollReadTimeoutMs = 250;

    // Bulk pixel data goes out in chunks; 64 KiB measured ~13 MB/s on the bench unit.
    private const int BulkChunkBytes = 64 * 1024;

    private readonly object _lock = new();
    // Serialises whole LCD transfers with each other. The stream path holds this across a
    // frame while taking _lock only for the short HID steps, so the ~414 ms of pixels does
    // not block the lighting writer: pixels ride the bulk endpoint, colours ride HID.
    private readonly object _lcdTransferLock = new();
    private readonly IKrakenLcdTransportFactory? _lcdFactory;
    private IHidDevice? _device;
    private IKrakenLcdTransport? _lcd;
    private bool _disposed;

    // Which Kraken is attached, and the HID report size its descriptor declares. The line
    // spans 64-byte and 512-byte reports, and Windows rejects a write that is not exactly
    // the declared length, so every encoded report is truncated to this on the way out.
    private KrakenModel _model = KrakenModel.All[0];
    private int _reportLength = KrakenProtocol.ReportLength;

    // Panel geometry. Seeded from the model table and then overwritten by whatever the
    // device reports for itself, which is authoritative.
    private int _lcdWidth;
    private int _lcdHeight;

    // 0x72 tuple selection. Only the 2023 Kraken and Kraken Elite ever moved theirs.
    private bool _useNewSpeedChannels = true;

    private volatile bool _isConnected;
    private volatile KrakenSnapshot _snapshot = KrakenSnapshot.Empty;
    // Last image pushed, stored unrotated so a rotation change can re-render it.
    private byte[]? _lastLcdFrame;

    // Streaming state. The buckets are allocated once, then rotated: the panel rejects
    // a transfer into the bucket it is currently displaying (code 9), so a stream has
    // to write an idle one and switch to it.
    private bool _streamReady;
    private int _streamActiveBucket = -1;

    // Three, not two. Ping-pong rewrites the bucket that just left the screen on the
    // very next frame while the panel is still reading it, which shows as a band of
    // decode garbage across the bottom rows. With three, a bucket sits out a full
    // frame before it is reused.
    private const int StreamBucketCount = 3;

    // Encode scratch, allocated once: a stream runs at panel rate and a per-frame buffer
    // this size would be pure garbage.
    private byte[]? _lcdScratch;

    public KrakenHub(IKrakenLcdTransportFactory? lcdFactory = null)
    {
        _lcdFactory = lcdFactory;
    }

    public const string DeviceId = "nzxt-kraken";
    public const string ProductName = "NZXT Kraken";

    /// <summary>Lighting zone ids. Kept distinct from the cooling channel ids on the same device.</summary>
    public const string RingZoneId = "nzxt-kraken:led-ring";
    public const string FansZoneId = "nzxt-kraken:led-fans";

    public bool IsConnected => _isConnected;

    /// <summary>The attached model, or the Elite V2 row while nothing is attached.</summary>
    public KrakenModel Model
    {
        get { lock (_lock) { return _model; } }
    }

    /// <summary>Marketing name of the attached model, for UI and logs.</summary>
    public string ModelName
    {
        get { lock (_lock) { return _model.Name; } }
    }

    /// <summary>Panel width in pixels; 0 on a model with no LCD.</summary>
    public int LcdWidth
    {
        get { lock (_lock) { return _lcdWidth; } }
    }

    public int LcdHeight
    {
        get { lock (_lock) { return _lcdHeight; } }
    }

    /// <summary>Bytes in one uncompressed RGBA frame at the attached panel's size.</summary>
    public int LcdFrameBytes
    {
        get { lock (_lock) { return _lcdWidth * _lcdHeight * 4; } }
    }

    /// <summary>USB serial of the attached cooler, or null when nothing is attached.</summary>
    public string? Serial
    {
        get
        {
            lock (_lock)
            {
                return _device?.Serial;
            }
        }
    }

    /// <summary>Read this reference once, then use it for all field accesses.</summary>
    public KrakenSnapshot Snapshot => _snapshot;

    /// <summary>Zone id for a channel index, matching the order channels are reported in.</summary>
    public static string ZoneIdForChannelIndex(int channelIndex) =>
        channelIndex == 0 ? RingZoneId : FansZoneId;

    /// <summary>True when the bulk pipe opened, i.e. LCD image upload is available.</summary>
    public bool HasLcd
    {
        get
        {
            lock (_lock)
            {
                return _lcd != null;
            }
        }
    }

    public void Attach(IHidDevice device, KrakenModel model, int reportLength)
    {
        lock (_lock)
        {
            _device?.Dispose();
            _device = device;
            _model = model;
            _reportLength = reportLength > 0 ? reportLength : KrakenProtocol.ReportLength;
            _lcdWidth = model.LcdWidth;
            _lcdHeight = model.LcdHeight;
            _useNewSpeedChannels = !model.SpeedChannelsFollowFirmware;
        }
    }

    /// <summary>
    /// Writes one encoded report, cut to the length the attached device declares. Encoders
    /// build at <see cref="KrakenProtocol.ReportLength"/> and every command's payload fits
    /// well inside 64 bytes, so the tail dropped here is always padding.
    /// </summary>
    private bool WriteLocked(byte[] report)
    {
        if (_device == null)
        {
            return false;
        }
        return _device.Write(report.Length <= _reportLength
            ? report.AsSpan()
            : report.AsSpan(0, _reportLength));
    }

    /// <summary>
    /// Reads the static device facts and marks the hub connected. Called from the worker
    /// thread only.
    /// </summary>
    public bool Connect()
    {
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }

            var fwReply = ExchangeLocked(KrakenProtocol.EncodeFirmwareRequest(), 0x11, 0x01, ConnectReadTimeoutMs);
            if (fwReply == null)
            {
                return false;
            }
            var fw = KrakenProtocol.DecodeFirmware(fwReply);
            if (_model.SpeedChannelsFollowFirmware)
            {
                _useNewSpeedChannels = KrakenProtocol.UsesNewSpeedChannels(fw);
            }

            // Starts the cooler's telemetry stream. Without it the accessory table is not
            // populated yet and the lighting query answers with zero channels.
            WriteLocked(KrakenProtocol.EncodeSetUpdateInterval());
            WriteLocked(KrakenProtocol.EncodeStartReporting());

            var channels = _model.Lighting == KrakenLightingProtocol.None
                ? Array.Empty<KrakenLightingChannel>()
                : ReadLightingChannelsLocked();

            int brightness = 0;
            int orientation = 0;
            var mode = KrakenDisplayMode.Liquid;
            if (_model.HasLcd)
            {
                var lcdReply = ExchangeLocked(KrakenProtocol.EncodeLcdInfoRequest(), 0x31, 0x01, CommandReadTimeoutMs);
                var lcdInfo = lcdReply == null ? null : KrakenProtocol.DecodeLcdInfo(lcdReply);
                if (lcdInfo.HasValue)
                {
                    brightness = lcdInfo.Value.BrightnessPercent;
                    orientation = lcdInfo.Value.OrientationQuarterTurns;
                    // The panel reports its own size; the model table is only the seed, so a
                    // variant we have not measured still gets driven at its real resolution.
                    if (lcdInfo.Value.Width > 0 && lcdInfo.Value.Height > 0)
                    {
                        if (lcdInfo.Value.Width != _lcdWidth || lcdInfo.Value.Height != _lcdHeight)
                        {
                            ServiceLog.Info(
                                $"[nzxt-kraken] panel reports {lcdInfo.Value.Width}x{lcdInfo.Value.Height}, " +
                                $"table said {_lcdWidth}x{_lcdHeight}; using the device");
                        }
                        _lcdWidth = lcdInfo.Value.Width;
                        _lcdHeight = lcdInfo.Value.Height;
                    }
                }

                var modeReply = ExchangeLocked(KrakenProtocol.EncodeReadDisplayModeRequest(), 0x31, 0x03, CommandReadTimeoutMs);
                mode = (modeReply == null ? null : KrakenProtocol.DecodeDisplayMode(modeReply))
                    ?? KrakenDisplayMode.Liquid;
            }

            _snapshot = new KrakenSnapshot(
                0, 0, 0, 0, 0,
                fw?.ToString() ?? "",
                brightness, orientation, mode, channels);

            if (_model.HasLcd)
            {
                _lcd = _lcdFactory?.Open(_device.Serial);
                if (_lcd == null)
                {
                    ServiceLog.Info("[nzxt-kraken] connected without an LCD bulk pipe; image upload unavailable");
                }
            }

            _isConnected = true;
            return true;
        }
    }

    private IReadOnlyList<KrakenLightingChannel> ReadLightingChannelsLocked()
    {
        // The accessory table can answer empty right after the stream starts, and an empty
        // result would leave every RGB zone undrivable for the whole session.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var channels = ReadLightingChannelsOnceLocked();
            if (channels.Count > 0)
            {
                return channels;
            }
        }
        ServiceLog.Warn("[nzxt-kraken] no RGB channels reported; lighting stays unavailable");
        return Array.Empty<KrakenLightingChannel>();
    }

    private IReadOnlyList<KrakenLightingChannel> ReadLightingChannelsOnceLocked()
    {
        var channels = new List<KrakenLightingChannel>();
        var reply = ExchangeLocked(KrakenProtocol.EncodeLightingInfoRequest(), 0x21, 0x03, CommandReadTimeoutMs);
        if (reply == null)
        {
            return channels;
        }
        int count = KrakenProtocol.DecodeChannelCount(reply);
        for (int channel = 0; channel < count; channel++)
        {
            // A channel is a chain: each slot is one accessory daisied off the last. A
            // multi-fan radiator reports as ONE accessory covering the whole radiator
            // (an F240 is a single 0x1B, not two entries), so the slots past the first
            // are only populated when the user has chained more hardware onto the port.
            byte first = 0;
            int leds = 0;
            int rings = 0;
            int accessories = 0;
            for (int slot = 0; slot < KrakenProtocol.AccessorySlotsPerChannel; slot++)
            {
                byte accessory = KrakenProtocol.DecodeAccessory(reply, channel, slot);
                if (accessory == 0)
                {
                    continue;
                }
                if (accessories == 0)
                {
                    first = accessory;
                }
                accessories++;
                leds += KrakenProtocol.LedCountForAccessory(accessory);
                var (accRings, _) = KrakenProtocol.AccessoryRings(accessory);
                rings += accRings;
            }
            if (accessories == 0)
            {
                continue;
            }
            var name = KrakenProtocol.AccessoryName(first);
            if (accessories > 1)
            {
                name = $"{name} x{accessories}";
            }
            channels.Add(new KrakenLightingChannel((byte)(1 << channel), first, name, leds, rings));
        }
        return channels;
    }

    /// <summary>
    /// Requests telemetry and folds it into the snapshot. Returns false only when the
    /// device looks gone, which is the worker's signal to tear the handle down.
    /// </summary>
    public bool Poll()
    {
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }
            DrainLocked();
            if (!WriteLocked(KrakenProtocol.EncodeStatusRequest()))
            {
                return false;
            }
            Span<byte> buf = stackalloc byte[KrakenProtocol.ReportLength];
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int n = _device.Read(buf, PollReadTimeoutMs);
                if (n < 0)
                {
                    return false;
                }
                if (n == 0)
                {
                    // A quiet window is not a disconnect.
                    return true;
                }
                var reading = KrakenProtocol.DecodeStatus(buf[..n]);
                if (reading.HasValue)
                {
                    _snapshot = _snapshot.WithReading(reading.Value);
                    return true;
                }
            }
            return true;
        }
    }

    public bool SetPumpCurve(ReadOnlySpan<byte> duties) => SendCurve(pump: true, duties);

    public bool SetFanCurve(ReadOnlySpan<byte> duties) => SendCurve(pump: false, duties);

    /// <summary>Applies a flat duty by filling every point of the curve.</summary>
    public bool SetPumpDuty(int percent) => SetPumpCurve(FlatCurve(percent, KrakenProtocol.PumpDutyFloor));

    public bool SetFanDuty(int percent) => SetFanCurve(FlatCurve(percent, 0));

    private static byte[] FlatCurve(int percent, int floor)
    {
        byte duty = (byte)Math.Clamp(percent, floor, 100);
        var curve = new byte[KrakenProtocol.CurvePointCount];
        curve.AsSpan().Fill(duty);
        return curve;
    }

    private bool SendCurve(bool pump, ReadOnlySpan<byte> duties)
    {
        lock (_lock)
        {
            var channel = _useNewSpeedChannels
                ? (pump ? KrakenProtocol.PumpChannel : KrakenProtocol.FanChannel)
                : (pump ? KrakenProtocol.LegacyPumpChannel : KrakenProtocol.LegacyFanChannel);
            return WriteLocked(KrakenProtocol.EncodeSpeedCurve(channel, duties));
        }
    }

    /// <summary>
    /// Backlight and rotation share one command, so both are always sent together using
    /// the snapshot for whichever value the caller is not changing.
    /// </summary>
    public bool SetLcdBacklight(int brightnessPercent, int orientationQuarterTurns)
    {
        var report = KrakenProtocol.EncodeSetBacklight(brightnessPercent, orientationQuarterTurns);
        // A rotation re-uploads the stored frame, which is a full bulk transfer, so this
        // takes the transfer lock first - same order as PushStreamFrame, never the reverse.
        lock (_lcdTransferLock)
        lock (_lock)
        {
            if (_device == null || !WriteLocked(report))
            {
                return false;
            }
            int turns = orientationQuarterTurns & 0x03;
            bool rotated = turns != _snapshot.LcdOrientationQuarterTurns;
            _snapshot = _snapshot.WithLcd(Math.Clamp(brightnessPercent, 0, 100), turns);
            // The panel will not re-orient a stored image on its own.
            if (rotated && _lastLcdFrame != null && _snapshot.DisplayMode == KrakenDisplayMode.Bucket)
            {
                UploadLcdFrameLocked(_lastLcdFrame, turns);
            }
            return true;
        }
    }

    public bool SetDisplayMode(KrakenDisplayMode mode, int bucketIndex = 0)
    {
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }
            var reply = ExchangeLocked(
                KrakenProtocol.EncodeSetDisplayMode(mode, bucketIndex), 0x39, 0x01, CommandReadTimeoutMs);
            if (reply == null || !KrakenProtocol.IsAck(reply))
            {
                return false;
            }
            _snapshot = _snapshot.WithDisplayMode(mode);
            return true;
        }
    }

    public bool SetLighting(byte channelId, KrakenColorMode mode, KrakenAnimationSpeed speed, ReadOnlySpan<byte> rgbColors, bool forward = true)
    {
        var report = KrakenProtocol.EncodeColors(channelId, mode, speed, rgbColors, forward);
        lock (_lock)
        {
            return WriteLocked(report);
        }
    }

    /// <summary>
    /// Sets every LED on a channel with one report. The firmware applies it on arrival, so
    /// there is no latch, no apply command, and nothing to wait for; the NAK this answers
    /// with carries no information (it answers writes that visibly land).
    /// </summary>
    public bool SetDirectColors(byte channelId, ReadOnlySpan<byte> rgbColors)
    {
        lock (_lock)
        {
            switch (_model.Lighting)
            {
                case KrakenLightingProtocol.ChannelReport:
                    return WriteLocked(KrakenProtocol.EncodeChannelColors(channelId, rgbColors));

                case KrakenLightingProtocol.StreamedTables:
                {
                    // Both tables then the latch, in that order: the submit is what the
                    // firmware acts on, and a table written after it lands on the next frame.
                    var tables = KrakenProtocol.EncodeStreamedColors(channelId, rgbColors);
                    foreach (var table in tables)
                    {
                        if (!WriteLocked(table))
                        {
                            return false;
                        }
                    }
                    return WriteLocked(KrakenProtocol.EncodeSubmitColors(channelId));
                }

                default:
                    return false;
            }
        }
    }

    /// <summary>Per-channel LED ceiling for the attached model's lighting protocol.</summary>
    public int MaxDirectColors => Model.Lighting == KrakenLightingProtocol.StreamedTables
        ? KrakenProtocol.MaxStreamedColors
        : KrakenProtocol.MaxDirectColors;

    /// <summary>
    /// Uploads one full-panel RGBA frame and makes the panel show it.
    ///
    /// Every bucket is deleted first. That step is not optional: a stale allocation makes
    /// the setup command return success while placing the image somewhere the panel never
    /// renders, which is the failure that makes this sequence look like it works when it
    /// does not.
    ///
    /// The bucket store is flash, and a full cycle measures ~450 ms, so this is for still
    /// images. Do not drive an animation through it.
    /// </summary>
    public bool UploadLcdImage(ReadOnlySpan<byte> rgba)
    {
        var expected = LcdFrameBytes;
        if (expected <= 0 || rgba.Length != expected)
        {
            ServiceLog.Warn($"[nzxt-kraken] LCD frame must be {expected} bytes, got {rgba.Length}");
            return false;
        }

        var source = rgba.ToArray();
        lock (_lcdTransferLock)
        lock (_lock)
        {
            if (_device == null || _lcd == null)
            {
                return false;
            }
            _lastLcdFrame = source;
            _streamReady = false;
            return UploadLcdFrameLocked(source, _snapshot.LcdOrientationQuarterTurns);
        }
    }

    /// <summary>
    /// Turns one source frame into the bytes this model's panel decodes, into the reusable
    /// scratch buffer. Returns the encoded length and the bulk format byte that describes it.
    /// <paramref name="sourceIsBgra"/> is set for capture-order frames (the panel stream);
    /// the still-image route posts RGBA.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_lcdScratch))]
    private int EncodeLcdPayloadLocked(ReadOnlySpan<byte> frame, int quarterTurns, bool sourceIsBgra, out byte format)
    {
        int w = _lcdWidth, h = _lcdHeight;
        switch (_model.LcdFormat)
        {
            case KrakenLcdFormat.Q565:
                format = KrakenProtocol.BulkFormatQ565;
                EnsureLcdScratch(Q565Encoder.MaxEncodedLength(w, h));
                return Q565Encoder.Encode(frame, w, h, quarterTurns, _lcdScratch, sourceIsBgra);

            case KrakenLcdFormat.Rgb565:
                format = KrakenProtocol.BulkFormatRgb565;
                EnsureLcdScratch(Rgb565Encoder.EncodedLength(w, h));
                return Rgb565Encoder.Encode(frame, w, h, quarterTurns, _lcdScratch, sourceIsBgra);

            default:
            {
                format = KrakenProtocol.BulkFormatRgba8888;
                var wire = KrakenProtocol.ToWireRgba(frame, w, h, quarterTurns, sourceIsBgra);
                EnsureLcdScratch(wire.Length);
                wire.CopyTo(_lcdScratch.AsSpan());
                return wire.Length;
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_lcdScratch))]
    private void EnsureLcdScratch(int bytes)
    {
        if (_lcdScratch == null || _lcdScratch.Length < bytes)
        {
            _lcdScratch = new byte[bytes];
        }
    }

    private bool UploadLcdFrameLocked(byte[] source, int quarterTurns)
    {
        var lcd = _lcd;
        if (_device == null || lcd == null)
        {
            return false;
        }
        int encoded = EncodeLcdPayloadLocked(source, quarterTurns, sourceIsBgra: false, out var format);
        var payload = _lcdScratch.AsSpan(0, encoded);
        var header = KrakenProtocol.EncodeBulkHeader(format, encoded);
        int pages = KrakenProtocol.PagesFor(encoded);
        {
            // Releases the active bucket so it becomes deletable.
            ExchangeLocked(KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Liquid, 0), 0x39, 0x01, CommandReadTimeoutMs);
            for (int i = 0; i < KrakenProtocol.BucketCount; i++)
            {
                ExchangeLocked(KrakenProtocol.EncodeDeleteBucket(i), 0x33, 0x02, CommandReadTimeoutMs);
            }

            const int bucket = 0;
            var setup = ExchangeLocked(KrakenProtocol.EncodeSetupBucket(bucket, 0, pages), 0x33, 0x01, CommandReadTimeoutMs);
            if (setup == null || !KrakenProtocol.IsAck(setup))
            {
                ServiceLog.Warn("[nzxt-kraken] LCD bucket setup rejected");
                return false;
            }

            var start = ExchangeLocked(KrakenProtocol.EncodeStartTransfer(bucket), 0x37, 0x01, CommandReadTimeoutMs);
            if (start == null || !KrakenProtocol.IsAck(start))
            {
                ServiceLog.Warn("[nzxt-kraken] LCD transfer start rejected");
                return false;
            }

            // The header must be its own bulk transfer; concatenating it with the pixels
            // corrupts the upload without any error being reported.
            if (!lcd.Write(header))
            {
                return false;
            }
            for (int offset = 0; offset < payload.Length; offset += BulkChunkBytes)
            {
                int len = Math.Min(BulkChunkBytes, payload.Length - offset);
                if (!lcd.Write(payload.Slice(offset, len)))
                {
                    ServiceLog.Warn($"[nzxt-kraken] LCD bulk write failed at offset {offset}");
                    return false;
                }
            }

            ExchangeLocked(KrakenProtocol.EncodeEndTransfer(), 0x37, 0x02, CommandReadTimeoutMs);

            var activate = ExchangeLocked(
                KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Bucket, bucket), 0x39, 0x01, CommandReadTimeoutMs);
            if (activate == null || !KrakenProtocol.IsAck(activate))
            {
                ServiceLog.Warn("[nzxt-kraken] LCD bucket activation rejected");
                return false;
            }
            _snapshot = _snapshot.WithDisplayMode(KrakenDisplayMode.Bucket);
            return true;
        }
    }

    /// <summary>
    /// Pushes one frame for a live stream, triple-buffered. Allocates its buckets on the
    /// first call and rotates thereafter; unlike <see cref="UploadLcdImage"/> this does
    /// not wipe the bucket table per frame, which is what makes a stream viable.
    ///
    /// Frames arrive in capture order (BGRA); the colour swap and the rotation both ride
    /// the encode rather than costing a separate pass over 1.6 MB.
    /// </summary>
    public bool PushStreamFrame(ReadOnlySpan<byte> bgra)
    {
        var expected = LcdFrameBytes;
        if (expected <= 0 || bgra.Length != expected)
        {
            return false;
        }
        lock (_lcdTransferLock)
        {
            int target;
            int encoded;
            byte format;
            byte[] scratch;
            IKrakenLcdTransport lcd;
            lock (_lock)
            {
                if (_device == null || _lcd == null)
                {
                    return false;
                }
                if (!_streamReady && !PrepareStreamBucketsLocked())
                {
                    return false;
                }
                lcd = _lcd;
                target = (_streamActiveBucket + 1) % StreamBucketCount;
                // Rotation rides the encode: the panel does not re-orient what it is sent.
                encoded = EncodeLcdPayloadLocked(
                    bgra, _snapshot.LcdOrientationQuarterTurns, sourceIsBgra: true, out format);
                var start = ExchangeLocked(
                    KrakenProtocol.EncodeStartTransfer(target), 0x37, 0x01, CommandReadTimeoutMs);
                if (start == null || !KrakenProtocol.IsAck(start))
                {
                    _streamReady = false;
                    return false;
                }
                scratch = _lcdScratch;
            }

            // Bulk endpoint only, outside _lock: HID commands for lighting and telemetry
            // keep flowing while the pixels stream, which is what lets an animation run at
            // its own rate instead of waiting a whole frame for the pipe.
            if (!WriteBulkPayload(lcd, format, scratch.AsSpan(0, encoded)))
            {
                lock (_lock) { _streamReady = false; }
                return false;
            }

            lock (_lock)
            {
                if (_device == null)
                {
                    return false;
                }
                ExchangeLocked(KrakenProtocol.EncodeEndTransfer(), 0x37, 0x02, CommandReadTimeoutMs);
                var activate = ExchangeLocked(
                    KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Bucket, target), 0x39, 0x01, CommandReadTimeoutMs);
                if (activate == null || !KrakenProtocol.IsAck(activate))
                {
                    // Re-allocate next call: a rejected transfer usually means the bucket
                    // table no longer matches what this hub believes.
                    _streamReady = false;
                    return false;
                }
                _streamActiveBucket = target;
                _snapshot = _snapshot.WithDisplayMode(KrakenDisplayMode.Bucket);
                return true;
            }
        }
    }

    /// <summary>Header then pixels on the bulk endpoint; takes no lock of its own.</summary>
    private static bool WriteBulkPayload(IKrakenLcdTransport lcd, byte format, ReadOnlySpan<byte> payload)
    {
        var header = KrakenProtocol.EncodeBulkHeader(format, payload.Length);
        // The header must be its own bulk transfer; concatenating corrupts the upload.
        if (!lcd.Write(header))
        {
            return false;
        }
        for (int offset = 0; offset < payload.Length; offset += BulkChunkBytes)
        {
            int len = Math.Min(BulkChunkBytes, payload.Length - offset);
            if (!lcd.Write(payload.Slice(offset, len)))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Clears the bucket table and reserves the stream's non-overlapping frame slots.</summary>
    private bool PrepareStreamBucketsLocked()
    {
        // Sized for a raw frame so any encoding fits, however incompressible the content.
        int pages = KrakenProtocol.PagesFor(_lcdWidth * _lcdHeight * 4);
        ExchangeLocked(KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Liquid, 0), 0x39, 0x01, CommandReadTimeoutMs);
        for (int i = 0; i < KrakenProtocol.BucketCount; i++)
        {
            ExchangeLocked(KrakenProtocol.EncodeDeleteBucket(i), 0x33, 0x02, CommandReadTimeoutMs);
        }
        for (int bucket = 0; bucket < StreamBucketCount; bucket++)
        {
            var setup = ExchangeLocked(
                KrakenProtocol.EncodeSetupBucket(bucket, bucket * pages, pages), 0x33, 0x01, CommandReadTimeoutMs);
            if (setup == null || !KrakenProtocol.IsAck(setup))
            {
                ServiceLog.Warn($"[nzxt-kraken] stream bucket {bucket} setup rejected");
                return false;
            }
        }
        _streamActiveBucket = -1;
        _streamReady = true;
        return true;
    }

    /// <summary>
    /// Writes a command and waits for its reply. Unsolicited status reports arrive on the
    /// same pipe about once a second and are folded into the snapshot rather than discarded.
    /// Returns null when no matching reply arrived.
    /// </summary>
    private byte[]? ExchangeLocked(byte[] request, byte replyReportId, byte replySubCommand, int timeoutMs)
    {
        if (_device == null)
        {
            return null;
        }
        // Fire-and-forget writes (lighting, curves) leave their own replies queued. Reading
        // a stale one as this command's answer is how a healthy exchange reports failure, so
        // clear the queue before asking.
        DrainLocked();
        if (!WriteLocked(request))
        {
            return null;
        }
        var buf = new byte[KrakenProtocol.ReportLength];
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            int n = _device.Read(buf, timeoutMs);
            if (n < 0)
            {
                return null;
            }
            if (n == 0)
            {
                continue;
            }
            var span = buf.AsSpan(0, n);
            if (KrakenProtocol.IsStatusReply(span))
            {
                var reading = KrakenProtocol.DecodeStatus(span);
                if (reading.HasValue)
                {
                    _snapshot = _snapshot.WithReading(reading.Value);
                }
                continue;
            }
            // A NAK echoes the command it rejected; one raised by a different command must
            // not fail this exchange.
            if (span.Length > 15 && span[0] == KrakenProtocol.ReportNak)
            {
                if (span[14] == request[0] && span[15] == request[1])
                {
                    ServiceLog.Warn($"[nzxt-kraken] device rejected command {span[14]:X2} {span[15]:X2}");
                    return null;
                }
                continue;
            }
            if (span.Length > 1 && span[0] == replyReportId && span[1] == replySubCommand)
            {
                return buf[..n];
            }
        }
        return null;
    }

    /// <summary>
    /// Empties the input queue, folding any telemetry it finds into the snapshot. The bound
    /// is a safety net: the device streams status once a second, so a healthy queue is short.
    /// </summary>
    private void DrainLocked()
    {
        if (_device == null)
        {
            return;
        }
        var buf = new byte[KrakenProtocol.ReportLength];
        for (int i = 0; i < 64; i++)
        {
            int n = _device.Read(buf, 0);
            if (n <= 0)
            {
                return;
            }
            var span = buf.AsSpan(0, n);
            if (KrakenProtocol.IsStatusReply(span))
            {
                var reading = KrakenProtocol.DecodeStatus(span);
                if (reading.HasValue)
                {
                    _snapshot = _snapshot.WithReading(reading.Value);
                }
            }
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            _isConnected = false;
            _snapshot = KrakenSnapshot.Empty;
            _lcd?.Dispose();
            _lcd = null;
            _device?.Dispose();
            _device = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _isConnected = false;
            _snapshot = KrakenSnapshot.Empty;
            _lcd?.Dispose();
            _lcd = null;
            _device?.Dispose();
            _device = null;
        }
    }
}
