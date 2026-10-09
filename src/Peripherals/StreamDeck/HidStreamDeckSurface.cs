using System;
using System.Threading;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.StreamDeck;

/// <summary>
/// Real Stream Deck backed by the existing hand-rolled HID stack
/// (<see cref="IHidEnumerator"/> / <see cref="IHidDevice"/>). Mirrors the
/// KeebHub pattern (src/Peripherals/Hyte/Keeb/KeebHub.cs): all IO serialised
/// on <c>_io</c>, a reused input-report buffer, write-failure backoff that
/// drops the handle after a run of consecutive failures so the connection
/// worker re-opens on the next tick.
///
/// The Mini exposes a single HID top-level collection (UsagePage 0x0C, Usage
/// 0x01, bench-confirmed 2026-07-10), so one handle carries feature writes,
/// image output reports, and interrupt-IN input reads.
/// </summary>
public sealed class HidStreamDeckSurface : IStreamDeckSurface
{
    private const int ConsecutiveWriteFailureThreshold = 5;

    private readonly IHidEnumerator _hid;
    private readonly object _io = new();
    private IHidDevice? _device;
    private readonly byte[] _inputBuf;
    private int _consecutiveWriteFailures;
    private int _featureReportLength = StreamDeckProtocol.FeatureReportBufferLength;
    private Timer? _keepAlive;
    private Timer? _settle;
    private bool _ready;

    /// <summary>Raised on a timer thread once a model with an open settle delay accepts commands; never raised for models without one.</summary>
    public event Action? Ready;

    public StreamDeckModel Model { get; }
    public string Serial { get; private set; } = "";
    public string FirmwareVersion { get; private set; } = "";
    public bool IsConnected => _device is not null;

    public bool IsReady
    {
        get
        {
            lock (_io)
            {
                return _device is not null && _ready;
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(false, nameof(_device))]
    private bool Unavailable => _device is null || !_ready;

    public HidStreamDeckSurface(IHidEnumerator hid, StreamDeckModel model)
    {
        _hid = hid;
        Model = model;
        _inputBuf = new byte[model.InputReportBufferLength];
    }

    /// <summary>Opens the device at the given HID interface. True if already open; false only when the open itself fails.</summary>
    public bool Connect(HidDeviceInfo info)
    {
        lock (_io)
        {
            if (_device is not null)
            {
                return true;
            }
            var dev = _hid.Open(info.Path, forInput: true);
            if (dev is null)
            {
                ServiceLog.Error($"[streamdeck] open failed for {info.Path} ({Model.Name})");
                return false;
            }
            _device = dev;
            Serial = !string.IsNullOrWhiteSpace(info.Serial) ? info.Serial! : StableIdFromPath(info.Path);
            _consecutiveWriteFailures = 0;
            if (Model.HasExpandedInput)
            {
                _featureReportLength = info.FeatureReportByteLength > 0
                    ? info.FeatureReportByteLength
                    : StreamDeckProtocol.FeatureReportBufferLength;
            }
            if (Model.OpenSettleMs > 0)
            {
                // The Galleon ignores commands right after open (node-elgato-stream-deck waits for it); writes are refused until the timer fires.
                _ready = false;
                _settle = new Timer(_ => FinishOpen(), null, Model.OpenSettleMs, Timeout.Infinite);
            }
            else
            {
                _ready = true;
                InitLocked();
            }
            return true;
        }
    }

    // Elgato's init: firmware read, black fill, firmware sleep off (bench capture 2026-10-08).
    private void InitLocked()
    {
        ReadFirmwareVersionLocked();
        if (Model.HasExpandedInput)
        {
            _device!.SetFeature(StreamDeckProtocol.BuildGen2FillScreenFeature(0, 0, 0, _featureReportLength));
            _device.SetFeature(StreamDeckProtocol.BuildGen2SleepDurationFeature(0, _featureReportLength));
        }
        StartKeepAliveLocked();
    }

    private void FinishOpen()
    {
        lock (_io)
        {
            if (_device is null)
            {
                return;
            }
            _ready = true;
            InitLocked();
        }
        Ready?.Invoke();
    }

    public void Disconnect()
    {
        lock (_io)
        {
            _settle?.Dispose();
            _settle = null;
            _ready = false;
            StopKeepAliveLocked();
            try { _device?.Dispose(); } catch { /* best effort */ }
            _device = null;
        }
    }

    private void StartKeepAliveLocked()
    {
        if (Model.KeepAliveIntervalMs <= 0 || _keepAlive is not null)
        {
            return;
        }
        // Galleon drops back to its keyboard mode without a 0x27 ping every 500 ms (node-elgato-stream-deck).
        _keepAlive = new Timer(_ => SendKeepAlive(), null, 0, Model.KeepAliveIntervalMs);
    }

    private void StopKeepAliveLocked()
    {
        _keepAlive?.Dispose();
        _keepAlive = null;
    }

    private void SendKeepAlive()
    {
        lock (_io)
        {
            if (_device is null)
            {
                StopKeepAliveLocked();
                return;
            }
            if (!_device.SetFeature(StreamDeckProtocol.BuildGalleonKeepAliveFeature(_featureReportLength)))
            {
                // A silent keep-alive drops the Galleon back to keyboard mode, so keep pinging; the failure threshold drops the handle and a reconnect restarts the timer.
                RecordWriteFailureLocked("keep-alive");
                return;
            }
            _consecutiveWriteFailures = 0;
        }
    }

    public bool SetBrightness(int percent)
    {
        lock (_io)
        {
            // Pedal is screenless and has no brightness control (python
            // StreamDeckPedal.set_brightness is a no-op) - ImageFormat.None
            // is unique to it among the button-only catalog, so it also
            // serves as the "does this deck have a screen" check.
            if (Unavailable || Model.ImageFormat == StreamDeckImageFormat.None)
            {
                return false;
            }
            var feature = Model.Protocol == StreamDeckProtocolGeneration.Gen1
                ? StreamDeckProtocol.BuildBrightnessFeature(percent)
                : StreamDeckProtocol.BuildGen2BrightnessFeature(percent);
            if (_device.SetFeature(feature))
            {
                _consecutiveWriteFailures = 0;
                return true;
            }
            return RecordWriteFailureLocked("brightness");
        }
    }

    public bool Reset()
    {
        lock (_io)
        {
            if (Unavailable || Model.ImageFormat == StreamDeckImageFormat.None)
            {
                return false;
            }
            var feature = Model.Protocol == StreamDeckProtocolGeneration.Gen1
                ? StreamDeckProtocol.BuildResetFeature()
                : StreamDeckProtocol.BuildGen2ResetFeature();
            if (_device.SetFeature(feature))
            {
                _consecutiveWriteFailures = 0;
                return true;
            }
            return RecordWriteFailureLocked("reset");
        }
    }

    public bool SetKeyImage(int keyIndex, ReadOnlyMemory<byte> wireBytes)
    {
        lock (_io)
        {
            if (Unavailable || Model.ImageFormat == StreamDeckImageFormat.None
                || keyIndex < 0 || keyIndex >= Model.KeyCount
                || !Model.IsValidWireImageLength(wireBytes.Length))
            {
                return false;
            }
            var rawIndex = Model.RemapKeyIndex(keyIndex);
            var pages = Model.Protocol == StreamDeckProtocolGeneration.Gen1
                ? StreamDeckProtocol.BuildImagePages(wireBytes.Span, rawIndex, Model)
                : StreamDeckProtocol.BuildGen2ImagePages(wireBytes.Span, rawIndex, Model);
            foreach (var page in pages)
            {
                if (!_device.Write(page))
                {
                    return RecordWriteFailureLocked("image-page");
                }
            }
            _consecutiveWriteFailures = 0;
            return true;
        }
    }

    public bool ClearKey(int keyIndex)
    {
        // The blank-image constant only covers the 80x80 BMP family (Mini and
        // its siblings) and the JPEG models; the Original (72x72 BMP) gets no
        // blank until a matching image is available.
        if (Model.ImageFormat == StreamDeckImageFormat.Jpeg)
        {
            return SetKeyImage(keyIndex, _blankJpeg ??= BuildBlankJpeg());
        }
        if (Model.ImageFormat != StreamDeckImageFormat.Bmp || Model.KeyPixelSize != 80)
        {
            return false;
        }
        return SetKeyImage(keyIndex, StreamDeckProtocol.BuildBlankBmp(Model.KeyPixelSize));
    }

    private byte[]? _blankJpeg;

    // Black is orientation- and transform-invariant, so it goes out as rendered.
    private byte[] BuildBlankJpeg()
    {
        using var black = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(
            Model.KeyWidth, Model.KeyHeight, new SixLabors.ImageSharp.PixelFormats.Rgba32(0, 0, 0, 255));
        return Rendering.RenderKit.EncodeJpeg(black);
    }

    public bool SetScreenRegion(int x, int y, int width, int height, ReadOnlyMemory<byte> wireBytes)
    {
        lock (_io)
        {
            var screen = Model.Screen;
            if (Unavailable || screen is null || wireBytes.IsEmpty
                || x < 0 || y < 0 || width <= 0 || height <= 0
                || x + width > screen.Width || y + height > screen.Height)
            {
                return false;
            }
            return WritePagesLocked(StreamDeckProtocol.BuildRegionImagePages(wireBytes.Span, x, y, width, height), "screen-region");
        }
    }

    public bool SetInfoScreen(ReadOnlyMemory<byte> wireBytes)
    {
        lock (_io)
        {
            if (Unavailable || Model.Screen?.Kind != StreamDeckScreenKind.InfoScreen || wireBytes.IsEmpty)
            {
                return false;
            }
            return WritePagesLocked(StreamDeckProtocol.BuildNeoInfoScreenPages(wireBytes.Span), "info-screen");
        }
    }

    public bool FillScreen(byte r, byte g, byte b) =>
        SetFeatureLocked(Model.HasExpandedInput, StreamDeckProtocol.BuildGen2FillScreenFeature(r, g, b, _featureReportLength), "fill-screen");

    public bool FillKey(int keyIndex, byte r, byte g, byte b) =>
        SetFeatureLocked(
            Model.HasExpandedInput && keyIndex >= 0 && keyIndex < Model.KeyCount + Model.TouchKeys,
            StreamDeckProtocol.BuildGen2FillKeyFeature(keyIndex, r, g, b, _featureReportLength), "fill-key");

    public bool SetSleepDuration(int seconds) =>
        SetFeatureLocked(Model.HasExpandedInput, StreamDeckProtocol.BuildGen2SleepDurationFeature(seconds, _featureReportLength), "sleep-duration");

    public bool SetRing(int dial, ReadOnlySpan<byte> rgbTriplets)
    {
        lock (_io)
        {
            if (Unavailable || dial < 0 || dial >= Model.Encoders || rgbTriplets.Length < Model.EncoderRingLeds * 3)
            {
                return false;
            }
            switch (Model.RingKind)
            {
                case StreamDeckRingKind.StudioReport:
                    return WritePagesLocked(new() { StreamDeckProtocol.BuildStudioRingReport(Model, dial, rgbTriplets) }, "ring");
                case StreamDeckRingKind.GalleonFeature:
                    foreach (var report in StreamDeckProtocol.BuildGalleonRingFeatures(Model, dial, rgbTriplets, _featureReportLength))
                    {
                        if (!_device.SetFeature(report))
                        {
                            return RecordWriteFailureLocked("ring");
                        }
                    }
                    _consecutiveWriteFailures = 0;
                    return true;
                default:
                    return false;
            }
        }
    }

    public bool SetCenterLed(int dial, byte r, byte g, byte b)
    {
        lock (_io)
        {
            if (Unavailable || Model.RingKind != StreamDeckRingKind.StudioReport || dial < 0 || dial >= Model.Encoders)
            {
                return false;
            }
            return WritePagesLocked(new() { StreamDeckProtocol.BuildStudioCenterLedReport(dial, r, g, b) }, "center-led");
        }
    }

    private bool SetFeatureLocked(bool supported, byte[] report, string where)
    {
        lock (_io)
        {
            if (Unavailable || !supported)
            {
                return false;
            }
            if (_device.SetFeature(report))
            {
                _consecutiveWriteFailures = 0;
                return true;
            }
            return RecordWriteFailureLocked(where);
        }
    }

    // Caller holds _io.
    private bool WritePagesLocked(System.Collections.Generic.List<byte[]> pages, string where)
    {
        foreach (var page in pages)
        {
            if (!_device!.Write(page))
            {
                return RecordWriteFailureLocked(where);
            }
        }
        _consecutiveWriteFailures = 0;
        return true;
    }

    public StreamDeckInput? ReadInput(int timeoutMs)
    {
        lock (_io)
        {
            if (_device is null)
            {
                return null;
            }
            var n = _device.Read(_inputBuf, timeoutMs);
            if (n < 0)
            {
                ServiceLog.Error($"[streamdeck] {Model.Name} (serial={Serial}) read failed, dropping interface");
                try { _device.Dispose(); } catch { /* best effort */ }
                _device = null;
                return null;
            }
            if (n == 0)
            {
                return null;
            }
            return StreamDeckProtocol.DecodeInput(_inputBuf.AsSpan(0, n), Model);
        }
    }

    // Caller holds _io.
    private void ReadFirmwareVersionLocked()
    {
        var dev = _device!;
        try
        {
            var request = Model.Protocol == StreamDeckProtocolGeneration.Gen1
                ? StreamDeckProtocol.BuildFirmwareFeatureRequest()
                : StreamDeckProtocol.BuildGen2FirmwareFeatureRequest();
            if (!dev.GetFeature(request))
            {
                return;
            }
            FirmwareVersion = Model.Protocol == StreamDeckProtocolGeneration.Gen1
                ? StreamDeckProtocol.ExtractAsciiString(request)
                : StreamDeckProtocol.ExtractAsciiString(request, StreamDeckProtocol.Gen2FirmwareStringOffset);
        }
        catch { /* best effort - firmware version is cosmetic */ }
    }

    // A single dropped report is transient (USB jitter); only tear down the
    // interface after a sustained run so the next connection-worker tick
    // re-enumerates. Caller must hold _io.
    private bool RecordWriteFailureLocked(string where)
    {
        var n = ++_consecutiveWriteFailures;
        if (n >= ConsecutiveWriteFailureThreshold)
        {
            ServiceLog.Error($"[streamdeck] {Model.Name} (serial={Serial}): {n} consecutive write failures ({where}) - dropping interface");
            _consecutiveWriteFailures = 0;
            StopKeepAliveLocked();
            try { _device?.Dispose(); } catch { /* best effort */ }
            _device = null;
        }
        return false;
    }

    private static string StableIdFromPath(string path)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in path) { h ^= c; h *= 16777619; }
            return $"sd-{h:x8}";
        }
    }

    public void Dispose() => Disconnect();
}
