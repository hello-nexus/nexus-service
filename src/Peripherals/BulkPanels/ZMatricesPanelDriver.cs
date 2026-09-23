using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// ZMatrices cooler LCDs (see <see cref="ZMatricesProtocol"/>). Streams at half the
/// 2240x1080 glass; the firmware upscales to fill it.
/// </summary>
public sealed class ZMatricesPanelDriver : IBulkPanelDriver
{
    public const int Width = 1120;
    public const int Height = 540;

    private BgraJpegEncoder? _jpeg;
    private byte[] _trans = Array.Empty<byte>();
    private int _transLength;
    private byte[]? _start;
    private int _pictures;
    private bool _resync;

    public string HandlerId => "zmatrices-lcd";
    public string Name => "Aftershock Glacier Matrix LCD";
    public int VendorId => 0x38C1;
    public IReadOnlyList<int> ProductIds { get; } = new[] { 0x0026 };
    public string Surface => Models.Panel.PanelSurfaces.LcdWide;
    /// <summary>The glass shows at most ~44.5 fps.</summary>
    public int Fps => 44;
    public byte WritePipeId => ZMatricesProtocol.PicturePipe;

    public byte ReadPipeId => ZMatricesProtocol.InputPipe;

    public bool NeedsHidChannel => false;

    /// <summary>The glass returns to its own animation 10-16 s after the last frame.</summary>
    public int KeepaliveMs => 1000;

    public bool SupportsBrightness => true;

    public bool SupportsSecondaryMonitor => true;

    public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        if (!pipe.Write(ZMatricesProtocol.CommandPipe, ZMatricesProtocol.EncodePictureModeCommand()))
        {
            return null;
        }
        _pictures = 0;
        _resync = false;
        _jpeg?.Dispose();
        _jpeg = new BgraJpegEncoder(Width, Height);
        ServiceLog.Info($"[{HandlerId}] picture mode on, streaming {Width}x{Height}");
        return (Width, Height);
    }

    public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra)
    {
        if (_jpeg is null)
        {
            return false;
        }
        var jpeg = _jpeg.Encode(bgra);
        int size = ZMatricesProtocol.TransPacketCount(jpeg.Length) * ZMatricesProtocol.PacketSize;
        if (_trans.Length < size)
        {
            _trans = new byte[size];
        }
        _transLength = ZMatricesProtocol.EncodeTransPackets(jpeg, _trans);
        _start = ZMatricesProtocol.EncodeStartFrame(jpeg);
        return Send(pipe);
    }

    public bool Resend(IBulkUsbPipe pipe, IHidDevice? hid) => _start is null || Send(pipe);

    // A discarded slot gets a copy, so every frame reaches the glass. A failed transfer
    // leaves the firmware's count unknown; picture mode restarts it.
    private bool Send(IBulkUsbPipe pipe)
    {
        if (_resync)
        {
            if (!pipe.Write(ZMatricesProtocol.CommandPipe, ZMatricesProtocol.EncodePictureModeCommand()))
            {
                return false;
            }
            _pictures = 0;
            _resync = false;
        }
        bool sent = (_pictures % ZMatricesProtocol.DiscardedPictureInterval != 0 || SendPicture(pipe))
            && SendPicture(pipe);
        _resync = !sent;
        return sent;
    }

    // The firmware finds Start and the trailer by packet boundary, so each is its own transfer.
    private bool SendPicture(IBulkUsbPipe pipe)
    {
        _pictures++;
        return pipe.Write(_start)
            && pipe.Write(_trans.AsSpan(0, _transLength))
            && pipe.Write(ZMatricesProtocol.FinishFrame);
    }

    public bool SetBrightness(IBulkUsbPipe pipe, IHidDevice? hid, int percent) =>
        pipe.Write(ZMatricesProtocol.CommandPipe, ZMatricesProtocol.EncodeBrightnessCommand(percent));

    public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        _jpeg?.Dispose();
        _jpeg = null;
        _start = null;
    }
}
