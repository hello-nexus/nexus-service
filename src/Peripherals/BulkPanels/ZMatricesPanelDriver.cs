using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>One ZMatrices glass on the shared picture protocol, at the size it is streamed.</summary>
public sealed record ZMatricesModel(string HandlerId, string Name, int ProductId, int Width, int Height, double? Dpi, string Category = "cooler")
{
    /// <summary>Streams at half the glass resolution; the firmware upscales to fill it.</summary>
    public static readonly ZMatricesModel GlacierMatrix = new("zmatrices-lcd", "Aftershock Glacier Matrix LCD", 0x0026, 1120, 540, null);

    /// <summary>Bar panel streamed at its native resolution.</summary>
    public static readonly ZMatricesModel MaxE87 = new("zmatrices-max-e87", "ZMatrices MAX E87", 0x0032, 1280, 440, 218, "light");
}

/// <summary>ZMatrices LCDs (see <see cref="ZMatricesProtocol"/>).</summary>
public sealed class ZMatricesPanelDriver : IBulkPanelDriver
{
    private readonly ZMatricesModel _model;
    private BgraJpegEncoder? _jpeg;
    private byte[] _trans = Array.Empty<byte>();
    private int _transLength;
    private byte[]? _start;
    private int _pictures;
    private bool _resync;

    public ZMatricesPanelDriver(ZMatricesModel? model = null)
    {
        _model = model ?? ZMatricesModel.GlacierMatrix;
        ProductIds = new[] { _model.ProductId };
    }

    public ZMatricesModel Model => _model;
    public string HandlerId => _model.HandlerId;
    public string Name => _model.Name;
    public string Category => _model.Category;
    public int VendorId => 0x38C1;
    public IReadOnlyList<int> ProductIds { get; }
    public string Surface => Models.Panel.PanelSurfaces.LcdWide;
    public double? Dpi => _model.Dpi;
    /// <summary>The Glacier Matrix glass's measured ceiling; the MAX E87 shares it unmeasured.</summary>
    public int Fps => 44;
    public byte WritePipeId => ZMatricesProtocol.PicturePipe;

    public byte ReadPipeId => ZMatricesProtocol.InputPipe;

    public bool NeedsHidChannel => false;

    /// <summary>The glass returns to its own animation 10-16 s after the last frame.</summary>
    public int KeepaliveMs => 1000;

    public bool SupportsBrightness => true;

    public bool SupportsSecondaryMonitor => true;

    public bool HasTouch => true;

    public IPanelTouchDecoder CreateTouchDecoder() => new ZMatricesTouchParser();

    public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        if (!pipe.Write(ZMatricesProtocol.CommandPipe, ZMatricesProtocol.EncodePictureModeCommand()))
        {
            return null;
        }
        _pictures = 0;
        _resync = false;
        _jpeg?.Dispose();
        _jpeg = new BgraJpegEncoder(_model.Width, _model.Height);
        ServiceLog.Info($"[{HandlerId}] picture mode on, streaming {_model.Width}x{_model.Height}");
        return (_model.Width, _model.Height);
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
