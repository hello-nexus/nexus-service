using System;
using System.IO;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Byte sink that turns the overlay's raw BGRA frame stream into Kraken LCD pushes.
///
/// The interface is a byte stream, not a frame queue: the paced writer may split or
/// concatenate writes, so this buffers to exactly one frame before pushing. Frames are
/// fixed-size, which makes the boundary unambiguous.
///
/// The overlay produces BGRA (what the compositor hands back). The colour order is the
/// hub's problem, not this one's: it folds the swap into whichever encode the attached
/// panel needs, so nothing here touches pixels.
/// </summary>
public sealed class KrakenStreamTransport : IStreamedPanelTransport
{
    private readonly KrakenHub _hub;
    private readonly byte[] _frame;
    private int _filled;
    private bool _disposed;

    // A stall shows up as the queue trimming rather than an exception, so a dropped frame
    // is only worth logging once per run of drops.
    private bool _dropLogged;

    public KrakenStreamTransport(KrakenHub hub, string serial)
    {
        _hub = hub;
        Serial = serial;
        _frame = new byte[hub.LcdFrameBytes];
    }

    public bool IsOpen => !_disposed && _hub.IsConnected && _hub.HasLcd;

    public string Serial { get; }

    public void Open()
    {
        if (!_hub.IsConnected)
        {
            throw new IOException("kraken not connected");
        }
        if (!_hub.HasLcd)
        {
            throw new IOException("kraken LCD bulk pipe unavailable");
        }
        // The frame size is fixed at construction. If the cooler dropped between discovery
        // and here it reads as zero, and a zero-length frame would make Write spin forever
        // consuming nothing.
        if (_frame.Length <= 0)
        {
            throw new IOException("kraken panel size unknown");
        }
        _filled = 0;
    }

    /// <summary>The panel has no player to start; the first pushed frame is the start.</summary>
    public void StartPlayer()
    {
    }

    public void Write(ReadOnlySpan<byte> payload)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(KrakenStreamTransport));
        }
        if (_frame.Length <= 0)
        {
            return;
        }
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
            if (_hub.PushStreamFrame(_frame))
            {
                _dropLogged = false;
            }
            else if (!_dropLogged)
            {
                _dropLogged = true;
                ServiceLog.Warn("[nzxt-kraken] stream frame rejected; retrying on the next frame");
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _hub.ReleaseStream();
    }
}
