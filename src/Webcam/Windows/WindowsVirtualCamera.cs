using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Webcam.Windows;

/// <summary>
/// Windows 11 MF virtual camera backed by the bundled NexusVCam media source.
/// Start creates the shared-memory frame ring (fixed protocol geometry), a
/// Media Foundation decoder for the armed codec, and the OS camera via the
/// DLL's flat exports; frames are decoded to NV12 and letterboxed into the
/// ring with the seqlock publish protocol. Both stream codecs are reported
/// as passthrough because the decode stage lives behind this sink - the
/// session manager hands encoded payloads straight in.
/// </summary>
public sealed class WindowsVirtualCamera : IVirtualCamera
{
    private const int EAccessDenied = unchecked((int)0x80070005);

    private readonly IVCamControl _control;
    private readonly IVCamFrameRingFactory _rings;
    private readonly IWindowsVideoDecoderFactory _decoders;
    private readonly Func<bool> _grantConsentAndRestartFrameServer;
    private readonly object _stateLock = new();

    private IVCamFrameRing? _ring;
    private VCamRingWriter? _writer;
    private IWindowsVideoDecoder? _decoder;
    private nint _handle;
    private bool _started;

    public WindowsVirtualCamera() : this(CreateControl(), CreateRingFactory(), CreateDecoderFactory()) { }

    internal WindowsVirtualCamera(
        IVCamControl control,
        IVCamFrameRingFactory rings,
        IWindowsVideoDecoderFactory decoders,
        Func<bool>? grantConsentAndRestartFrameServer = null)
    {
        _control = control;
        _rings = rings;
        _decoders = decoders;
        _grantConsentAndRestartFrameServer = grantConsentAndRestartFrameServer ?? GrantConsentAndRestartFrameServer;
    }

    // The platform guard keeps the analyzer honest; unit tests inject their own
    // delegate so they never reach the registry or the Frame Server.
    private static bool GrantConsentAndRestartFrameServer()
    {
        if (!OperatingSystem.IsWindows() || !NexusVCamControl.EnsureDesktopCameraConsent())
        {
            return false;
        }
        RestartFrameServer();
        return true;
    }

    public string Name => WebcamDefaults.CameraName;

    public WebcamCodecSupport GetCodecSupport(WebcamCodec codec) => codec switch
    {
        WebcamCodec.H264 or WebcamCodec.Mjpeg => WebcamCodecSupport.Passthrough,
        _ => WebcamCodecSupport.Unsupported,
    };

    public Task StartAsync(WebcamFormat format, CancellationToken cancellationToken)
    {
        if (GetCodecSupport(format.Codec) != WebcamCodecSupport.Passthrough)
            throw new NotSupportedException($"codec {format.Codec} is not supported by the Windows virtual camera");

        lock (_stateLock)
        {
            StopLocked();

            // First time the user enables the webcam on this PC, grant the
            // machine-wide desktop-camera consent - but DON'T create the camera
            // on this pass. The Camera Frame Server reads that consent once at
            // its own process start and caches it, so a create right after the
            // grant still blocks on a prompt SYSTEM can't answer. Restart the
            // Frame Server so it re-reads the fresh Allow, then surface a
            // retriable signal; the next start creates cleanly (the web widget
            // retries once so it's seamless). Only ever happens once per machine,
            // on the user's opt-in - never at install.
            if (_grantConsentAndRestartFrameServer())
            {
                throw new InvalidOperationException(
                    "Enabling camera access for this PC; start the camera again to begin.");
            }

            _control.EnsureAvailable();

            IVCamFrameRing? ring = null;
            IWindowsVideoDecoder? decoder = null;
            try
            {
                ring = _rings.Create(VCamProtocol.DefaultWidth, VCamProtocol.DefaultHeight, VCamProtocol.SlotCount);
                var writer = new VCamRingWriter(ring.Mapping, VCamProtocol.DefaultWidth, VCamProtocol.DefaultHeight, VCamProtocol.SlotCount);
                writer.InitializeHeader((uint)Environment.ProcessId, Environment.TickCount64);
                decoder = _decoders.Create(format.Codec, format.Width, format.Height);

                // AllUsers needs an elevated or LocalSystem caller (the service
                // path); a dev run falls back to a per-user camera.
                var hr = _control.Create(Name, allUsers: true, out var handle);
                if (hr == EAccessDenied)
                    hr = _control.Create(Name, allUsers: false, out handle);
                if (hr < 0)
                {
                    throw new InvalidOperationException(
                        $"virtual camera activation failed (hr=0x{hr:X8}); check the Windows camera privacy toggle and that the Frame Server service is running");
                }

                _ring = ring;
                _writer = writer;
                _decoder = decoder;
                _handle = handle;
                _started = true;
                ring = null;
                decoder = null;
            }
            finally
            {
                decoder?.Dispose();
                ring?.Dispose();
            }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_stateLock)
        {
            StopLocked();
        }
        return Task.CompletedTask;
    }

    // Bounce the Windows Camera Frame Server so it re-reads camera consent that
    // was just granted (it caches the value at process start). It's a Manual,
    // trigger-started service, so killing its host is enough - the next camera
    // activation restarts it fresh. Best-effort.
    private static void RestartFrameServer()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = "/F /FI \"SERVICES eq FrameServer\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit(5000);
        }
        catch
        {
            // Couldn't bounce it; the retry may need a moment longer, but the
            // consent is granted so it converges.
        }
    }

    public ValueTask WriteFrameAsync(ReadOnlyMemory<byte> payload, in WebcamFrameInfo info)
    {
        lock (_stateLock)
        {
            if (!_started || payload.Length == 0)
                return ValueTask.CompletedTask;
            try
            {
                if (_decoder!.TryDecode(payload.Span, info.TimestampMs, out var frame)
                    && _writer!.TryWriteFrame(frame.Data.Span, frame.Width, frame.Height, Environment.TickCount64))
                {
                    _ring!.SignalFrameReady();
                }
            }
            catch
            {
                // A corrupt frame must not tear down the stream; the consumer
                // keeps its previous image and recovers on the next keyframe.
            }
        }
        return ValueTask.CompletedTask;
    }

    private void StopLocked()
    {
        if (!_started)
            return;
        _started = false;
        var handle = _handle;
        _handle = 0;
        try
        {
            // Remove-then-release inside the DLL; failure leaves at worst a
            // stale session camera that the next create reopens and replaces.
            _control.Destroy(handle);
        }
        catch
        {
        }
        _decoder?.Dispose();
        _decoder = null;
        _writer = null;
        _ring?.Dispose();
        _ring = null;
    }

    // Guards are inlined (not a shared helper) so the platform-compat
    // analyzer recognizes them at the annotated constructor calls.
    private static IVCamControl CreateControl()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WindowsVirtualCamera requires Windows");
        return new NexusVCamControl();
    }

    private static IVCamFrameRingFactory CreateRingFactory()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WindowsVirtualCamera requires Windows");
        return new WindowsVCamRingFactory();
    }

    private static IWindowsVideoDecoderFactory CreateDecoderFactory()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WindowsVirtualCamera requires Windows");
        return new MfVideoDecoderFactory();
    }
}
