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
    private readonly byte[] _bgr = new byte[RyujinProtocol.FrameBytes];
    private bool _hasFrame;

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
    public int KeepaliveMs => 100;

    /// <summary>Fixed geometry: this panel neither reports nor negotiates a size.</summary>
    public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        if (hid is null)
        {
            return null;
        }
        _hasFrame = false;
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

    public bool Resend(IBulkUsbPipe pipe, IHidDevice? hid) => !_hasFrame || (hid is not null && Push(pipe, hid));

    private bool Push(IBulkUsbPipe pipe, IHidDevice hid)
    {
        for (int offset = 0; offset < _bgr.Length; offset += RyujinProtocol.ChunkBytes)
        {
            int length = Math.Min(RyujinProtocol.ChunkBytes, _bgr.Length - offset);
            if (!pipe.Write(_bgr.AsSpan(offset, length)))
            {
                return false;
            }
        }
        // Nothing appears until this lands, so a failed commit is a dropped frame even
        // though every byte reached the panel.
        return hid.Write(RyujinProtocol.EncodeCommit());
    }

    public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
    }
}
