using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The ASUS Ryujin III's 320x240 LCD. The only panel here that takes raw pixels rather
/// than JPEG, and the only one that needs its HID channel as well as the bulk pipe: the
/// bytes go out on bulk and a HID report commits them to the display.
/// </summary>
public sealed class RyujinPanelDriver : IBulkPanelDriver
{
    // Failed pushes in a row before the session is reopened.
    private const int StreakToReopen = 3;
    // A pipe write that burns the transport's full timeout is a wedged link on its own. Each
    // pipe class keeps that timeout private, so this stays a margin under it by hand.
    private const long StallMs = 4000;
    private static readonly int[] ReopenBackoffMs = { 1000, 2000, 5000 };

    private readonly byte[] _bgr = new byte[RyujinProtocol.FrameBytes];
    private readonly Func<long> _clock;
    private bool _hasFrame;
    private volatile string? _lastFailure;
    private volatile bool _reopenWanted;
    private int _failStreak;
    private int _attempts;

    public RyujinPanelDriver() : this(() => Environment.TickCount64)
    {
    }

    public RyujinPanelDriver(Func<long> clock)
    {
        _clock = clock;
    }

    public string HandlerId => "asus-ryujin-lcd";
    public string Name => "ASUS Ryujin LCD";
    public int VendorId => 0x0B05;

    /// <summary>
    /// Only this product id carries a screen. Other Ryujin controllers share the VID and
    /// have no panel at all.
    /// </summary>
    public IReadOnlyList<int> ProductIds { get; } = new[] { RyujinProtocol.ProductIdWithLcd };

    public string Surface => Models.Panel.PanelSurfaces.LcdSquare;
    public int Fps => 20;
    public byte WritePipeId => 0x01;
    public byte ReadPipeId => 0x00;
    public bool NeedsHidChannel => true;

    /// <summary>A starved panel falls back to its stored animation.</summary>
    public int KeepaliveMs => 1000;

    public string? LastFailure => _lastFailure;

    public int TakeReopenDelayMs()
    {
        if (!_reopenWanted)
        {
            return -1;
        }
        _reopenWanted = false;
        int attempt = Interlocked.Increment(ref _attempts) - 1;
        return ReopenBackoffMs[Math.Min(attempt, ReopenBackoffMs.Length - 1)];
    }

    /// <summary>Fixed geometry: this panel neither reports nor negotiates a size.</summary>
    public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        if (hid is null)
        {
            return null;
        }
        _hasFrame = false;
        _lastFailure = null;
        _failStreak = 0;
        _reopenWanted = false;
        foreach (var command in RyujinProtocol.EncodeLiveStart())
        {
            if (!hid.Write(command))
            {
                return null;
            }
            Thread.Sleep(RyujinProtocol.StartCommandGapMs);
        }
        return (RyujinProtocol.Width, RyujinProtocol.Height);
    }

    public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra)
    {
        if (hid is null)
        {
            return false;
        }
        RyujinProtocol.EncodeFrame(bgra, _bgr);
        _hasFrame = true;
        return Push(pipe, hid);
    }

    // Still pushes after a failure: on static content the keepalive is the only retry, and a
    // failed one has to count toward the reopen streak.
    public bool Resend(IBulkUsbPipe pipe, IHidDevice? hid) => !_hasFrame || (hid is not null && Push(pipe, hid));

    private bool Push(IBulkUsbPipe pipe, IHidDevice hid)
    {
        long start = _clock();
        string? failure = null;
        for (int offset = 0; offset < _bgr.Length; offset += RyujinProtocol.ChunkBytes)
        {
            int length = Math.Min(RyujinProtocol.ChunkBytes, _bgr.Length - offset);
            if (!pipe.Write(_bgr.AsSpan(offset, length)))
            {
                failure = "bulk pipe write";
                break;
            }
        }
        // Nothing appears until this lands, so a failed commit is a dropped frame even
        // though every byte reached the panel.
        if (failure is null && !hid.Write(RyujinProtocol.EncodeCommit()))
        {
            failure = "hid commit";
        }
        if (failure is null)
        {
            _lastFailure = null;
            _failStreak = 0;
            Volatile.Write(ref _attempts, 0);
            return true;
        }
        _lastFailure = failure;
        _failStreak++;
        if (_failStreak >= StreakToReopen || _clock() - start >= StallMs)
        {
            _reopenWanted = true;
        }
        return false;
    }

    public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
    }
}
