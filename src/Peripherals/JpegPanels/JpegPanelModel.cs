using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// One cooler LCD that takes a whole JPEG per frame over plain HID. These are all the same
/// shape - open a vendor HID interface, chunk a JPEG across output reports - so one hub
/// drives the family and a model is just a row here.
///
/// Every row but <see cref="HydroShiftLcd"/> and <see cref="GalahadIiLcd"/> was reconstructed
/// from third-party protocol documentation and has never been run against hardware: we own
/// no unit of any of them.
/// They ship experimental with Nexus Control off by default so a build never grabs an
/// untested cooler on its own.
/// </summary>
public sealed record JpegPanelModel(
    string HandlerId,
    string Name,
    int VendorId,
    IReadOnlyList<int> ProductIds,
    int Width,
    int Height,
    bool Circular,
    int ReportLength,
    JpegPanelHeaderStyle HeaderStyle,
    byte Selector,
    string Surface,
    IReadOnlyList<byte[]>? InitReports = null,
    IReadOnlyList<byte[]>? ShutdownReports = null)
{
    /// <summary>
    /// Control channel for a panel that needs a handshake rather than fixed init reports.
    /// Stateful, so a model that has one owns its own instance.
    /// </summary>
    public IJpegPanelHandshake? Handshake { get; init; }

    /// <summary>The panel's backlight is host-settable, so its record carries a brightness.</summary>
    public bool SupportsBrightness => Handshake is IJpegPanelBrightness;

    /// <summary>Documented frame rate for the model; the profile's ceiling.</summary>
    public int Fps { get; init; } = 30;

    /// <summary>
    /// Turns the frame a quarter turn counter-clockwise on its way to the glass, for a panel
    /// whose scanout is turned relative to the frames it accepts. Square panels only: turning
    /// a rectangular one would swap the dimensions the encoder reads back.
    /// </summary>
    public bool QuarterTurnCcw { get; init; }

    /// <summary>
    /// The Galahad II's round glass. Shares its protocol - and so its handshake - with the
    /// HydroShift LCD; the pump head is the only thing that differs between them.
    /// </summary>
    public static readonly JpegPanelModel GalahadIiLcd = new(
        HandlerId: "lianli-galahad2-lcd",
        Name: "Lian Li Galahad II LCD",
        VendorId: 0x0416,
        ProductIds: new[] { 0x7395 },
        Width: 480,
        Height: 480,
        Circular: true,
        ReportLength: 1024,
        HeaderStyle: JpegPanelHeaderStyle.LianLiSequenced,
        Selector: 0x0E,
        Surface: Models.Panel.PanelSurfaces.LcdRound)
    {
        Fps = GalahadFps,
        Handshake = new LianLiAioHandshake(
            "lianli-galahad2-lcd", GalahadFps, LianLiAioHandshake.Galahad2BrightnessMode),
    };

    /// <summary>Documented rate for the Galahad II glass.</summary>
    private const int GalahadFps = 24;

    /// <summary>
    /// Lian Li HydroShift LCD, the one model in this table verified against hardware. Its
    /// three product ids are the 360S, 360R and 360TL, which differ in fans and pump
    /// envelope but present one 480x480 square panel on one HID interface (usage page
    /// 0xFF1A, 1024-byte output reports).
    /// </summary>
    /// <summary>Documented rate for the HydroShift glass; the panel is told it and paced to it.</summary>
    private const int HydroShiftFps = 24;

    public static readonly JpegPanelModel HydroShiftLcd = new(
        HandlerId: "lianli-hydroshift-lcd",
        Name: "Lian Li HydroShift LCD",
        VendorId: 0x0416,
        ProductIds: new[] { 0x7398, 0x7399, 0x739A },
        Width: 480,
        Height: 480,
        Circular: false,
        ReportLength: 1024,
        HeaderStyle: JpegPanelHeaderStyle.LianLiSequenced,
        Selector: 0x0E,
        Surface: Models.Panel.PanelSurfaces.LcdSquare)
    {
        Fps = HydroShiftFps,
        Handshake = new LianLiAioHandshake("lianli-hydroshift-lcd", HydroShiftFps),
        // Measured on a 360S (firmware N9,01,HS,SQ,HydroShift,V3.A.008,0.3): a pushed frame
        // lands a quarter turn clockwise, while the panel draws its own boot logo upright.
        // The LCD-control rotation byte is ignored by this firmware - 0, 1, 2 and 3 all
        // render identically - so the turn has to happen here.
        QuarterTurnCcw = true,
    };

    public static readonly JpegPanelModel CorsairXc7 = new(
        HandlerId: "corsair-xc7-lcd",
        Name: "Corsair XC7 RGB Elite LCD",
        VendorId: 0x1B1C,
        ProductIds: new[] { 0x0C42 },
        Width: 480,
        Height: 480,
        Circular: true,
        ReportLength: 1024,
        HeaderStyle: JpegPanelHeaderStyle.CorsairChunked,
        Selector: 0x1F,
        Surface: Models.Panel.PanelSurfaces.LcdRound);

    public static readonly JpegPanelModel CorsairCapellix = new(
        HandlerId: "corsair-capellix-lcd",
        Name: "Corsair Elite Capellix LCD",
        VendorId: 0x1B1C,
        ProductIds: new[] { 0x0C39, 0x0C33 },
        Width: 480,
        Height: 480,
        Circular: false,
        ReportLength: 1024,
        HeaderStyle: JpegPanelHeaderStyle.CorsairChunked,
        Selector: 0x40,
        Surface: Models.Panel.PanelSurfaces.LcdSquare);

    /// <summary>
    /// ID-Cooling FX-LCD. Its init pair turns the panel on ("DIS") and sets the backlight
    /// to 0x19 ("LIG"); shutdown clears it ("CLE") and hands the panel back to the
    /// firmware ("HAN"). Without the init the panel takes frames and shows nothing.
    /// </summary>
    public static readonly JpegPanelModel IdCoolingFx = new(
        HandlerId: "idcooling-fx-lcd",
        Name: "ID-Cooling FX-LCD",
        VendorId: 0x2000,
        ProductIds: new[] { 0x3000 },
        Width: 240,
        Height: 240,
        Circular: true,
        ReportLength: 1025,
        HeaderStyle: JpegPanelHeaderStyle.IdCoolingTagged,
        Selector: 0x00,
        Surface: Models.Panel.PanelSurfaces.LcdRound,
        InitReports: new[]
        {
            new byte[] { 0x00, 0x43, 0x52, 0x54, 0x00, 0x00, 0x44, 0x49, 0x53 },
            new byte[] { 0x00, 0x43, 0x52, 0x54, 0x00, 0x00, 0x4C, 0x49, 0x47, 0x00, 0x00, 0x19 },
        },
        ShutdownReports: new[]
        {
            new byte[] { 0x00, 0x43, 0x52, 0x54, 0x00, 0x00, 0x43, 0x4C, 0x45, 0x00, 0x00, 0x44, 0x43 },
            new byte[] { 0x00, 0x43, 0x52, 0x54, 0x00, 0x00, 0x48, 0x41, 0x4E },
        })
    { Fps = 40 };

    /// <summary>
    /// ASRock's AIO LCDs. Its image frames are plain chunked reports like the rest of the
    /// family, but connect / real-time-display / disconnect ride an escaped text protocol -
    /// see <see cref="AsRockLcdHandshake"/>. One row covers the four product ids the
    /// documented for this controller; they differ only in marketing name.
    /// </summary>
    public static readonly JpegPanelModel AsRockLcd = new(
        HandlerId: "asrock-lcd",
        Name: "ASRock LCD",
        VendorId: 0x26CE,
        ProductIds: new[] { 0x0012, 0x0A10, 0x0A11, 0x0A12 },
        Width: 480,
        Height: 480,
        Circular: false,
        ReportLength: 1025,
        HeaderStyle: JpegPanelHeaderStyle.AsRockFramed,
        Selector: 0x5C,
        Surface: Models.Panel.PanelSurfaces.LcdSquare)
    { Handshake = new AsRockLcdHandshake() };

    public static readonly JpegPanelModel[] All =
    {
        GalahadIiLcd,
        HydroShiftLcd,
        CorsairXc7,
        CorsairCapellix,
        IdCoolingFx,
        AsRockLcd,
    };

    /// <summary>
    /// Deliberately not in this table, because they are not HID-JPEG devices:
    /// the ASUS Ryujin (raw BGR888 over bulk), the Thermalright family and the Lian Li
    /// Universal Screen 88 (JPEG over bulk, the latter behind a DES-encrypted header),
    /// and the ASRock LCD (an escaped text protocol). They need their own transports.
    /// The Corsair iCUE LINK LCD and XD5 are absent for the opposite reason: the service
    /// already drives them through <c>CorsairLinkLcd</c>, and a second claim on the same
    /// HID interface would fight it.
    /// </summary>
    public static JpegPanelModel? Find(int vendorId, int productId)
    {
        foreach (var model in All)
        {
            if (model.VendorId != vendorId)
            {
                continue;
            }
            for (int i = 0; i < model.ProductIds.Count; i++)
            {
                if (model.ProductIds[i] == productId)
                {
                    return model;
                }
            }
        }
        return null;
    }

    public static JpegPanelModel? ByHandlerId(string handlerId)
    {
        foreach (var model in All)
        {
            if (string.Equals(model.HandlerId, handlerId, StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }
        }
        return null;
    }
}
