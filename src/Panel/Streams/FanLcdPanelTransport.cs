using System;
using System.IO;
using System.Linq;
using Nexus.Service.Peripherals.FanLcd;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Byte sink turning the overlay's raw BGRA frames into JPEG pushes on one fan screen. Frames
/// are fixed-size, so the stream is cut into whole frames before encoding. A frame is dropped
/// once the screen leaves panel mode: a session lingers after that, and the content worker owns
/// the screen by then (<see cref="IFanLcdScreens.SetPanelOwned"/>).
/// </summary>
public sealed class FanLcdPanelTransport : IStreamedPanelTransport, IOrientablePanelTransport
{
    private readonly IFanLcdScreens _screens;
    private readonly string _screenSerial;
    private readonly BgraJpegEncoder _encoder = new(FanLcdContent.Size, FanLcdContent.Size);
    private readonly byte[] _frame;
    private readonly PanelOrientationFilter _orientation = new();
    private int _filled;
    private bool _disposed;
    private bool _encodeFailLogged;

    public FanLcdPanelTransport(IFanLcdScreens screens, string screenSerial, string panelSerial)
    {
        _screens = screens;
        _screenSerial = screenSerial;
        Serial = panelSerial;
        _frame = new byte[_encoder.FrameBytes];
    }

    public string Serial { get; }

    public bool IsOpen => !_disposed && Attached();

    /// <summary>
    /// The half turn and the mirror are applied to the frame in panel mode. The screen's firmware rotation
    /// is its own device-page setting and is never written from here, so quarter turns are not taken.
    /// </summary>
    public void BindOrientation(Func<(int Rotation, bool Mirror)> source) =>
        _orientation.Bind(() =>
        {
            var (rotation, mirror) = source();
            return (rotation == 180 ? 180 : 0, mirror);
        });

    public void Open()
    {
        if (!Attached())
        {
            throw new IOException($"{_screens.LogTag} screen {_screenSerial} not attached");
        }
        _filled = 0;
    }

    /// <summary>The screen has no player to start; the first pushed frame is the start.</summary>
    public void StartPlayer()
    {
    }

    public void Write(ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!payload.IsEmpty)
        {
            int take = Math.Min(_frame.Length - _filled, payload.Length);
            payload[..take].CopyTo(_frame.AsSpan(_filled));
            _filled += take;
            payload = payload[take..];
            if (_filled < _frame.Length)
            {
                continue;
            }
            _filled = 0;
            PushFrame();
        }
    }

    private void PushFrame()
    {
        byte[] jpeg;
        try
        {
            jpeg = _encoder.Encode(_orientation.Apply(_frame, FanLcdContent.Size, FanLcdContent.Size)).ToArray();
        }
        catch (Exception ex)
        {
            if (!_encodeFailLogged)
            {
                _encodeFailLogged = true;
                ServiceLog.Warn($"[{_screens.LogTag}] panel frame encode failed for {_screenSerial}: {ex.GetType().Name}: {ex.Message}");
            }
            return;
        }
        _encodeFailLogged = false;
        _screens.PushPanelFrame(_screenSerial, jpeg);
    }

    private bool Attached() => _screens.AttachedSerials().Contains(_screenSerial, StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _encoder.Dispose();
    }
}
