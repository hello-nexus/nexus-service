using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Peripherals.StreamDeck;

public enum StreamDeckProtocolGeneration { Gen1, Gen2 }

public enum StreamDeckImageFormat { None, Bmp, Jpeg }

public enum StreamDeckRotation { Rot0, Rot90, Rot180, Rot270 }

public enum StreamDeckMirror { None, X, Y, Both }

public enum StreamDeckDialPlacement { None, Below, Above, Sides }

public enum StreamDeckScreenKind { TouchStrip, InfoScreen, DialScreen }

/// <summary>How a model lights its dial rings: Studio takes one 0x0F report per ring, Galleon one 0x24 feature report per LED.</summary>
public enum StreamDeckRingKind { None, StudioReport, GalleonFeature }

/// <summary>A drawable screen beyond the keys, in logical (pre-rotation) pixels.</summary>
public sealed record StreamDeckScreen(int Width, int Height, StreamDeckScreenKind Kind);

/// <summary>
/// Per-model capability row: layout, key image shape, wire protocol
/// generation, and quirks. Verified=true means bench-confirmed (Mini only,
/// 2026-07-10); every other row is transcribed from the MIT references and
/// unproven on real hardware.
/// </summary>
public sealed class StreamDeckModel
{
    public string Name { get; }

    /// <summary>Brand-prefixed product name for the UI; <see cref="Name"/> stays bare for model matching.</summary>
    public string DisplayName => VendorId == StreamDeckModels.CorsairVendorId ? $"Corsair {Name}" : $"Elgato Stream Deck {DisplayModel ?? Name}";

    /// <summary>Marketed model token when it differs from <see cref="Name"/> (Plus is sold as "+").</summary>
    private string? DisplayModel { get; init; }

    public int ProductId { get; }
    public int KeyCount { get; }
    public int Rows { get; }
    public int Columns { get; }
    public int KeyPixelSize { get; }
    public StreamDeckImageFormat ImageFormat { get; }
    public StreamDeckRotation Rotation { get; }
    public StreamDeckMirror Mirror { get; }
    public StreamDeckProtocolGeneration Protocol { get; }
    public bool Verified { get; }

    public int VendorId { get; private init; } = StreamDeckModels.VendorId;

    /// <summary>Key bitmap width; equals <see cref="KeyPixelSize"/> except on Studio.</summary>
    public int KeyWidth { get; private init; }

    /// <summary>Key bitmap height; equals <see cref="KeyPixelSize"/> except on Studio.</summary>
    public int KeyHeight { get; private init; }

    public int Encoders { get; private init; }
    public StreamDeckDialPlacement DialPlacement { get; private init; }
    public StreamDeckScreen? Screen { get; private init; }

    /// <summary>Capacitive touch keys reported after the LCD keys (Neo).</summary>
    public int TouchKeys { get; private init; }

    public int EncoderRingLeds { get; private init; }
    public StreamDeckRingKind RingKind { get; private init; }

    /// <summary>Pixel transform for screen images, same vocabulary as <see cref="Transform"/>.</summary>
    public string ScreenTransform { get; private init; } = "none";

    /// <summary>Required HID usage of the collection to open; 0 matches any collection.</summary>
    public int HidUsage { get; private init; }

    /// <summary>Required HID usage page of the collection to open; 0 matches any page.</summary>
    public int HidUsagePage { get; private init; }

    /// <summary>Interval of the 0x03 0x27 keep-alive feature report; 0 sends none (Galleon).</summary>
    public int KeepAliveIntervalMs { get; private init; }

    /// <summary>Delay between open and the first command (Galleon).</summary>
    public int OpenSettleMs { get; private init; }

    /// <summary>
    /// How many LEDs a dial's ring colour list is rotated by before it hits
    /// the wire, per the ring's physical start point (node-elgato-stream-deck
    /// ledRingOffset).
    /// </summary>
    public int RingColorOffset(int dial) => RingKind switch
    {
        StreamDeckRingKind.StudioReport => dial == 1 ? 12 : 0,
        StreamDeckRingKind.GalleonFeature => dial == 0 ? 3 : 1,
        _ => 0,
    };

    /// <summary>
    /// Whether a HID collection is the Stream Deck interface for this model.
    /// Galleon shares a VID/PID across its keyboard and vendor collections, so
    /// it must match the Stream Deck usage and usage page, and on Windows
    /// (paths carry mi_NN) interface 0. Off Windows the enumerators expose no
    /// interface number, so the usage page is the discriminator.
    /// </summary>
    public bool AcceptsCollection(Hid.HidDeviceInfo info)
    {
        if (HidUsage == 0)
        {
            return true;
        }
        if (info.Usage != HidUsage || HidUsagePage != 0 && info.UsagePage != HidUsagePage)
        {
            return false;
        }
        var mi = info.Path.IndexOf("mi_", System.StringComparison.OrdinalIgnoreCase);
        return mi < 0 || info.Path.AsSpan(mi + 3).StartsWith("00");
    }

    /// <summary>True for models whose input reports carry dial, touch or touch-key payloads.</summary>
    public bool HasExpandedInput => Encoders > 0 || TouchKeys > 0 || Screen is not null;

    /// <summary>
    /// Gen1 image report length in bytes (report id + header + payload pages).
    /// 1024 for every gen1 model except the Original, whose firmware still
    /// uses the pre-1024 report size (elgato-streamdeck WriteImageParameters::for_key).
    /// </summary>
    public int ImageReportLength { get; }

    /// <summary>
    /// True only for the Original (0x0060): its firmware halves the image
    /// payload across exactly 2 pages instead of chunking at
    /// ImageReportLength - header, and numbers pages from 1 instead of 0
    /// (elgato-streamdeck lib.rs write_image_data_reports / send_image).
    /// </summary>
    public bool HalvedImagePayload { get; }

    /// <summary>Gen1 image page numbering base: 0 for every model except the Original (1).</summary>
    public int ImagePageNumberBase { get; }

    /// <summary>
    /// True only for the Original: both input-report key states and outgoing
    /// image key indices are addressed right-to-left within each row on the
    /// wire (python-elgato-streamdeck StreamDeckOriginal._convert_key_id_origin;
    /// elgato-streamdeck util::flip_key_index). Self-inverse, so the same
    /// remap converts canonical<->raw in both directions.
    /// </summary>
    public bool KeyIndexRightToLeft { get; }

    private StreamDeckModel(
        string name, int productId, int keyCount, int rows, int columns, int keyPixelSize,
        StreamDeckImageFormat imageFormat, StreamDeckRotation rotation, StreamDeckMirror mirror,
        StreamDeckProtocolGeneration protocol, bool verified,
        int imageReportLength = 1024, bool halvedImagePayload = false, int imagePageNumberBase = 0,
        bool keyIndexRightToLeft = false)
    {
        Name = name;
        ProductId = productId;
        KeyCount = keyCount;
        Rows = rows;
        Columns = columns;
        KeyPixelSize = keyPixelSize;
        KeyWidth = keyPixelSize;
        KeyHeight = keyPixelSize;
        ImageFormat = imageFormat;
        Rotation = rotation;
        Mirror = mirror;
        Protocol = protocol;
        Verified = verified;
        ImageReportLength = imageReportLength;
        HalvedImagePayload = halvedImagePayload;
        ImagePageNumberBase = imagePageNumberBase;
        KeyIndexRightToLeft = keyIndexRightToLeft;
    }

    /// <summary>
    /// Maps a canonical (user-facing) key index to the raw hardware index, or
    /// back again - the remap is its own inverse. Identity for every model
    /// except the Original.
    /// </summary>
    public int RemapKeyIndex(int index) =>
        KeyIndexRightToLeft ? StreamDeckModels.FlipWithinRow(index, Columns) : index;

    /// <summary>
    /// Interrupt-IN input report buffer length: report id byte plus one byte
    /// per key for gen1 (StreamDeckProtocol.DecodeGen1Input's offset i+1), or
    /// the gen2 header plus one byte per key (StreamDeckProtocol.Gen2InputHeaderLength).
    /// </summary>
    public int InputReportBufferLength => Protocol == StreamDeckProtocolGeneration.Gen1
        ? 1 + KeyCount
        : HasExpandedInput
            ? ExpandedInputBufferLength
            : StreamDeckProtocol.Gen2InputHeaderLength + KeyCount;

    /// <summary>
    /// Caller buffer for models with dial or touch reports: the Windows and
    /// macOS Read paths copy from caps-sized internal buffers, so a smaller
    /// buffer truncates a 512 byte input report.
    /// </summary>
    public const int ExpandedInputBufferLength = 512;

    /// <summary>Standard BITMAPFILEHEADER+BITMAPINFOHEADER size (matches StreamDeckProtocol.BuildBlankBmp).</summary>
    private const int BmpHeaderLength = 54;

    /// <summary>
    /// Whether a byte length is a plausible wire image for this model: for
    /// BMP, the exact uncompressed 24bpp size (an odd/wrong length would make
    /// BuildImagePages' HalvedImagePayload halving split unevenly, and any
    /// oversized upload risks the gen1 page-number byte wrapping past 255);
    /// for JPEG, any non-empty payload (compressed size is inherently
    /// variable; the route's own upload byte cap is the only bound needed).
    /// </summary>
    public bool IsValidWireImageLength(int length) => ImageFormat switch
    {
        StreamDeckImageFormat.Bmp => length == BmpHeaderLength + KeyPixelSize * KeyPixelSize * 3,
        StreamDeckImageFormat.Jpeg => length > 0,
        _ => false,
    };

    /// <summary>
    /// The /streamdeck/decks DTO's wire transform (nexus-web's
    /// deckKeyTransform.ts DeckKeyTransform: "none" | "flipBoth" |
    /// "mirrorXRot90" | "rot90Ccw"), the pixel transform a rendered key bitmap needs
    /// before it matches what this model expects on the wire.
    /// </summary>
    public string Transform =>
        ImageFormat == StreamDeckImageFormat.None
            ? "none"
            : Mirror == StreamDeckMirror.X && Rotation == StreamDeckRotation.Rot90
                ? "mirrorXRot90"
                : Mirror == StreamDeckMirror.None && Rotation == StreamDeckRotation.Rot90
                    ? "rot90Ccw"
                    : Mirror == StreamDeckMirror.None && Rotation == StreamDeckRotation.Rot0
                        ? "none"
                        : "flipBoth";

    internal static StreamDeckModel Gen1(
        string name, int productId, int keyCount, int rows, int columns, int keyPixelSize,
        bool verified,
        StreamDeckRotation rotation = StreamDeckRotation.Rot90, StreamDeckMirror mirror = StreamDeckMirror.X,
        bool keyIndexRightToLeft = false, int imageReportLength = 1024,
        bool halvedImagePayload = false, int imagePageNumberBase = 0) => new(
        name, productId, keyCount, rows, columns, keyPixelSize,
        StreamDeckImageFormat.Bmp, rotation, mirror,
        StreamDeckProtocolGeneration.Gen1, verified,
        imageReportLength, halvedImagePayload, imagePageNumberBase, keyIndexRightToLeft);

    internal static StreamDeckModel Gen2(
        string name, int productId, int keyCount, int rows, int columns, int keyPixelSize) => new(
        name, productId, keyCount, rows, columns, keyPixelSize,
        StreamDeckImageFormat.Jpeg, StreamDeckRotation.Rot0, StreamDeckMirror.Both,
        StreamDeckProtocolGeneration.Gen2, verified: false);

    /// <summary>
    /// A gen2 model with dials, a screen beyond the keys, or touch keys
    /// (Plus, Plus XL, Neo, Studio, Galleon). Keys are non-mirrored unless
    /// the caller says otherwise; keyWidth differs from keyPixelSize only on Studio.
    /// </summary>
    internal static StreamDeckModel Expanded(
        string name, int productId, int keyCount, int rows, int columns, int keyPixelSize,
        StreamDeckRotation rotation = StreamDeckRotation.Rot0, StreamDeckMirror mirror = StreamDeckMirror.None,
        int keyWidth = 0, int encoders = 0, StreamDeckDialPlacement dialPlacement = StreamDeckDialPlacement.None,
        StreamDeckScreen? screen = null, string screenTransform = "none", int touchKeys = 0,
        int ringLeds = 0, StreamDeckRingKind ringKind = StreamDeckRingKind.None,
        int vendorId = StreamDeckModels.VendorId, int hidUsage = 0, int hidUsagePage = 0, int keepAliveIntervalMs = 0,
        int openSettleMs = 0, string? displayModel = null) => new(
        name, productId, keyCount, rows, columns, keyPixelSize,
        StreamDeckImageFormat.Jpeg, rotation, mirror,
        StreamDeckProtocolGeneration.Gen2, verified: false)
    {
        VendorId = vendorId,
        KeyWidth = keyWidth > 0 ? keyWidth : keyPixelSize,
        Encoders = encoders,
        DialPlacement = dialPlacement,
        Screen = screen,
        TouchKeys = touchKeys,
        EncoderRingLeds = ringLeds,
        RingKind = ringKind,
        ScreenTransform = screenTransform,
        HidUsage = hidUsage,
        HidUsagePage = hidUsagePage,
        KeepAliveIntervalMs = keepAliveIntervalMs,
        OpenSettleMs = openSettleMs,
        DisplayModel = displayModel,
    };

    internal static StreamDeckModel InputOnly(string name, int productId, int keyCount, int rows, int columns) => new(
        name, productId, keyCount, rows, columns, keyPixelSize: 0,
        StreamDeckImageFormat.None, StreamDeckRotation.Rot0, StreamDeckMirror.None,
        StreamDeckProtocolGeneration.Gen2, verified: false);
}

/// <summary>
/// The button-only Stream Deck capability table, cross-verified against the
/// MIT elgato-streamdeck crate
/// (src/info.rs, fetched 2026-07-10) and python-elgato-streamdeck
/// (StreamDeckMini.py / StreamDeckOriginal.py), plus the dial and screen
/// models (Plus, Plus XL, Neo, Studio, Galleon K100 SD).
/// </summary>
public static class StreamDeckModels
{
    public const int VendorId = 0x0FD9;

    public const int CorsairVendorId = 0x1B1C;

    public static readonly IReadOnlyList<StreamDeckModel> All = new List<StreamDeckModel>
    {
        // Gen1: BMP, mirror-X + rot90 for the Mini family, halved-payload +
        // right-to-left remap for the legacy Original. Bare names (no
        // "Stream Deck " prefix) per plan streamdeck-support.md §1 - the
        // web's transformForModel fallback normalizes and matches against
        // these exact strings.
        StreamDeckModel.Gen1("Original", 0x0060, 15, 3, 5, 72,
            verified: false, rotation: StreamDeckRotation.Rot0, mirror: StreamDeckMirror.Both,
            keyIndexRightToLeft: true, imageReportLength: 8191, halvedImagePayload: true, imagePageNumberBase: 1),
        StreamDeckModel.Gen1("Mini", 0x0063, 6, 2, 3, 80, verified: true),
        StreamDeckModel.Gen1("Mini MK.2", 0x0090, 6, 2, 3, 80, verified: false),
        StreamDeckModel.Gen1("Mini Discord", 0x00b3, 6, 2, 3, 80, verified: false),
        StreamDeckModel.Gen1("Mini MK.2 Module", 0x00b8, 6, 2, 3, 80, verified: false),

        // Gen2: JPEG, flip-both.
        StreamDeckModel.Gen2("Original V2", 0x006d, 15, 3, 5, 72),
        StreamDeckModel.Gen2("MK.2", 0x0080, 15, 3, 5, 72),
        StreamDeckModel.Gen2("MK.2 Scissor", 0x00a5, 15, 3, 5, 72),
        StreamDeckModel.Gen2("MK.2 Module", 0x00b9, 15, 3, 5, 72),
        StreamDeckModel.Gen2("XL", 0x006c, 32, 4, 8, 96),
        StreamDeckModel.Gen2("XL V2", 0x008f, 32, 4, 8, 96),
        StreamDeckModel.Gen2("XL V2 Module", 0x00ba, 32, 4, 8, 96),
        // Expanded gen2: dials, strips, info screens, touch keys. Plus has no
        // key transform; Plus XL rotates keys and strip 90 CCW; Neo flips both
        // for keys and the info screen. KeyCount stays Rows*Columns; Neo's 2
        // touch keys report after the LCD keys and are TouchKeys, not keys.
        StreamDeckModel.Expanded("Plus", 0x0084, 8, 2, 4, 120, displayModel: "+",
            encoders: 4, dialPlacement: StreamDeckDialPlacement.Below,
            screen: new StreamDeckScreen(800, 100, StreamDeckScreenKind.TouchStrip)),
        StreamDeckModel.Expanded("Plus XL", 0x00c6, 36, 4, 9, 112, displayModel: "+ XL",
            rotation: StreamDeckRotation.Rot90, encoders: 6, dialPlacement: StreamDeckDialPlacement.Below,
            screen: new StreamDeckScreen(1200, 100, StreamDeckScreenKind.TouchStrip), screenTransform: "rot90Ccw"),
        StreamDeckModel.Expanded("Neo", 0x009a, 8, 2, 4, 96,
            mirror: StreamDeckMirror.Both,
            screen: new StreamDeckScreen(248, 58, StreamDeckScreenKind.InfoScreen), screenTransform: "flipBoth",
            touchKeys: 2),
        // Studio and Galleon come from the node-elgato-stream-deck definitions
        // only (no Elgato HID doc covers them).
        StreamDeckModel.Expanded("Studio", 0x00aa, 32, 2, 16, 112, keyWidth: 144,
            encoders: 2, dialPlacement: StreamDeckDialPlacement.Sides, ringLeds: 24,
            ringKind: StreamDeckRingKind.StudioReport),
        StreamDeckModel.Expanded("Galleon K100 SD", 0x2b18, 12, 4, 3, 160,
            encoders: 2, dialPlacement: StreamDeckDialPlacement.Above,
            screen: new StreamDeckScreen(720, 384, StreamDeckScreenKind.DialScreen),
            ringLeds: 4, ringKind: StreamDeckRingKind.GalleonFeature,
            vendorId: StreamDeckModels.CorsairVendorId, hidUsage: 0x01, hidUsagePage: 0x0C,
            keepAliveIntervalMs: 500, openSettleMs: 200),

        // Input-only: no key screens, buttons drive input dispatch only.
        StreamDeckModel.InputOnly("Pedal", 0x0086, 3, 1, 3),
    };

    public static StreamDeckModel? ByProductId(int productId) => All.FirstOrDefault(m => m.ProductId == productId);

    internal static int FlipWithinRow(int index, int columns)
    {
        var col = index % columns;
        return (index - col) + (columns - 1 - col);
    }
}
