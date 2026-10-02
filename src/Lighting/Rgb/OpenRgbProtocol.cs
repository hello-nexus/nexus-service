using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Binary serializers for the OpenRGB SDK protocol. The protocol is little-endian
/// throughout, prefixed with a 16-byte header on every packet.
///
/// See <see cref="PacketId"/> for the full set. The opcodes serialized here
/// cover the handshake (HELLO / SET_NAME), enumeration (GET_COUNT / GET_DATA
/// / DEVICE_LIST_UPDATED), mode switching (SET_CUSTOM_MODE), and frame push
/// (UPDATE_LEDS / UPDATE_ZONE_LEDS / RESIZE_ZONE).
///
/// Reference: NetworkProtocol.h in CalcProgrammer1/OpenRGB.
///
/// AOT-safe: zero reflection, no LINQ on hot paths, uses BinaryPrimitives.
/// </summary>
public static class OpenRgbProtocol
{
    public const int HeaderSize = 16;
    public const int CurrentProtocolVersion = 6;

    // SET_CLIENT_FLAGS bits (NetworkProtocol.h). The local-client grant gates the settings packets.
    public const uint ClientFlagSupportsRgbController = 1u << 0;
    public const uint ClientFlagSupportsSettingsManager = 1u << 4;
    public const uint ClientFlagRequestLocalClient = 1u << 16;
    public const uint ServerFlagLocalClient = 1u << 16;

    /// <summary>ACK status codes (NetPacketStatus).</summary>
    public const uint StatusOk = 0;
    public const uint StatusInvalidId = 4;
    public const uint StatusInvalidData = 5;
    public static readonly byte[] MagicBytes = new byte[] { 0x4F, 0x52, 0x47, 0x42 }; // "ORGB"

    public enum PacketId : uint
    {
        RequestControllerCount = 0,
        RequestControllerData = 1,
        Ack = 10,
        RequestProtocolVersion = 40,
        SetClientName = 50,
        SetServerName = 51,
        SetClientFlags = 52,
        SetServerFlags = 53,
        DeviceListUpdated = 100,
        DetectionStarted = 101,
        DetectionProgressChanged = 102,
        DetectionComplete = 103,
        RequestRescanDevices = 140,
        SettingsManagerSetSettings = 253,
        RgbControllerSignalUpdate = 1150,
        SetCustomMode = 1100,
        RgbControllerUpdateMode = 1101,
        RgbControllerUpdateLeds = 1050,
        RgbControllerUpdateZoneLeds = 1051,
        RgbControllerResizeZone = 1000,
    }

    /// <summary>
    /// Write the 16-byte header into the start of <paramref name="buffer"/>.
    /// </summary>
    public static void WriteHeader(Span<byte> buffer, uint deviceIndex, PacketId packetId, uint dataSize)
    {
        if (buffer.Length < HeaderSize)
        {
            throw new ArgumentException("buffer too small for header", nameof(buffer));
        }

        buffer[0] = MagicBytes[0];
        buffer[1] = MagicBytes[1];
        buffer[2] = MagicBytes[2];
        buffer[3] = MagicBytes[3];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4, 4), deviceIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(8, 4), (uint)packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(12, 4), dataSize);
    }

    /// <summary>
    /// Parse a header from the first 16 bytes of <paramref name="buffer"/>.
    /// </summary>
    public static (uint deviceIndex, PacketId packetId, uint dataSize) ReadHeader(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize)
        {
            throw new ArgumentException("buffer too small for header", nameof(buffer));
        }

        if (buffer[0] != MagicBytes[0] || buffer[1] != MagicBytes[1] ||
            buffer[2] != MagicBytes[2] || buffer[3] != MagicBytes[3])
        {
            throw new InvalidOperationException("packet magic bytes are not 'ORGB'");
        }

        var deviceIndex = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4, 4));
        var packetId = (PacketId)BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, 4));
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(12, 4));
        return (deviceIndex, packetId, dataSize);
    }

    /// <summary>
    /// SET_CLIENT_NAME body: raw UTF-8 bytes + a trailing NUL terminator.
    /// </summary>
    public static byte[] BuildSetClientNameBody(string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var body = new byte[nameBytes.Length + 1];
        Buffer.BlockCopy(nameBytes, 0, body, 0, nameBytes.Length);
        body[nameBytes.Length] = 0;
        return body;
    }

    /// <summary>
    /// REQUEST_PROTOCOL_VERSION body: a single uint32 client protocol version.
    /// </summary>
    public static byte[] BuildProtocolVersionBody(uint version)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, version);
        return body;
    }

    /// <summary>
    /// REQUEST_CONTROLLER_DATA body: a single uint32 protocol version we want
    /// the controller serialized for.
    /// </summary>
    public static byte[] BuildRequestControllerDataBody(uint version) => BuildProtocolVersionBody(version);

    /// <summary>SET_CLIENT_FLAGS body: a single uint32 of client flag bits.</summary>
    public static byte[] BuildClientFlagsBody(uint flags) => BuildProtocolVersionBody(flags);

    /// <summary>
    /// REQUEST_CONTROLLER_COUNT reply: uint32 count, then from protocol 6 one
    /// uint32 controller id per controller. Ids address every per-controller
    /// packet at v6 and are never reused within one daemon run; below v6 the
    /// address is the list index.
    /// </summary>
    public static int[] ParseControllerAddresses(ReadOnlySpan<byte> body, uint protocolVersion)
    {
        EnsureBytes(body, 0, 4, "controller count");
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (count <= 0)
        {
            return Array.Empty<int>();
        }
        var addresses = new int[count];
        if (protocolVersion < 6)
        {
            for (var i = 0; i < count; i++)
            {
                addresses[i] = i;
            }
            return addresses;
        }
        EnsureBytes(body, 4, 4 * count, "controller ids");
        for (var i = 0; i < count; i++)
        {
            addresses[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4 + 4 * i, 4));
        }
        return addresses;
    }

    /// <summary>ACK body: uint32 acknowledged packet id, uint32 status.</summary>
    public static (PacketId acked, uint status) ParseAck(ReadOnlySpan<byte> body)
    {
        EnsureBytes(body, 0, 8, "ack");
        return ((PacketId)BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4)));
    }

    /// <summary>
    /// RGBCONTROLLER_UPDATELEDS body for device with the given LED colors.
    /// Body layout:
    ///   uint32 data_size  (total body size including itself)
    ///   uint16 led_count
    ///   for each LED: uint8 R, uint8 G, uint8 B, uint8 padding
    /// </summary>
    public static byte[] BuildUpdateLedsBody(ReadOnlySpan<RgbColor> colors)
    {
        // 4 (data_size) + 2 (led_count) + 4 * N
        var bodySize = 4 + 2 + (4 * colors.Length);
        var body = new byte[bodySize];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), (uint)bodySize);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4, 2), (ushort)colors.Length);
        for (int i = 0; i < colors.Length; i++)
        {
            var off = 6 + (4 * i);
            body[off + 0] = colors[i].R;
            body[off + 1] = colors[i].G;
            body[off + 2] = colors[i].B;
            body[off + 3] = 0; // padding
        }
        return body;
    }

    /// <summary>
    /// RGBCONTROLLER_UPDATEZONELEDS body. Pushes colors to a single zone within
    /// a device. Used so each motherboard ARGB header can be updated without
    /// touching the other headers on the same physical controller.
    /// Body layout:
    ///   uint32 data_size  (total body size including itself)
    ///   uint32 zone_index
    ///   uint16 led_count
    ///   for each LED: uint8 R, uint8 G, uint8 B, uint8 padding
    /// </summary>
    public static byte[] BuildUpdateZoneLedsBody(uint zoneIndex, ReadOnlySpan<RgbColor> colors)
    {
        // 4 (data_size) + 4 (zone_index) + 2 (led_count) + 4 * N
        var bodySize = 4 + 4 + 2 + (4 * colors.Length);
        var body = new byte[bodySize];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), (uint)bodySize);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4, 4), zoneIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8, 2), (ushort)colors.Length);
        for (int i = 0; i < colors.Length; i++)
        {
            var off = 10 + (4 * i);
            body[off + 0] = colors[i].R;
            body[off + 1] = colors[i].G;
            body[off + 2] = colors[i].B;
            body[off + 3] = 0; // padding
        }
        return body;
    }

    /// <summary>
    /// RGBCONTROLLER_RESIZEZONE body. Asks the OpenRGB server to reconfigure the
    /// LED count on a zone. Used for motherboard ARGB headers where the LED count
    /// is user-configured (ARGB has no feedback line).
    /// Body layout:
    ///   int32  zone_index
    ///   uint32 new_size
    /// </summary>
    public static byte[] BuildResizeZoneBody(int zoneIndex, uint newSize)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(0, 4), zoneIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4, 4), newSize);
        return body;
    }

    /// <summary>
    /// Parse the REQUEST_CONTROLLER_DATA reply body. We only extract the fields
    /// our integration needs (name + total led_count + zones). Everything else
    /// (mode internals, color tables, matrix data, alt-names, flags) is skipped
    /// past so we don't have to maintain the full controller struct serializer
    /// just to display a device list.
    ///
    /// Layout - verified against upstream `RGBController::GetDeviceDescription`
    /// in OpenRGB master (RGBController/RGBController.cpp). The optional fields
    /// MUST be parsed in exactly this order:
    ///
    ///   uint32 data_size
    ///   uint32 device_type
    ///   bstring name
    ///   [v1+] bstring vendor
    ///   bstring description
    ///   bstring version
    ///   bstring serial
    ///   bstring location
    ///   uint16 num_modes
    ///   uint32 active_mode
    ///   per mode:
    ///     bstring name
    ///     int32  value
    ///     uint32 flags
    ///     uint32 speed_min
    ///     uint32 speed_max
    ///     [v3+] uint32 brightness_min
    ///     [v3+] uint32 brightness_max
    ///     uint32 colors_min
    ///     uint32 colors_max
    ///     uint32 speed
    ///     [v3+] uint32 brightness
    ///     uint32 direction
    ///     uint32 color_mode
    ///     uint16 num_colors
    ///     num_colors * uint32 color
    ///   uint16 num_zones
    ///   per zone:
    ///     bstring name
    ///     uint32 type
    ///     uint32 leds_min
    ///     uint32 leds_max
    ///     uint32 leds_count
    ///     uint16 matrix_len
    ///     matrix_len bytes
    ///     [v4+] uint16 num_segments
    ///       per segment: bstring name, uint32 type, uint32 start_idx, uint32 leds_count
    ///     [v5+] uint32 zone_flags
    ///   uint16 num_leds
    ///   per led: bstring name, uint32 value
    ///   [v5+] uint16 num_led_alt_names
    ///     per alt name: bstring
    ///   [v5+] uint32 controller_flags
    ///   uint16 num_colors
    ///   num_colors * uint32 color
    /// </summary>
    public static RgbDevice ParseControllerData(int deviceIndex, ReadOnlySpan<byte> body, uint protocolVersion)
    {
        var pos = 0;

        EnsureBytes(body, pos, 4, "data_size");
        pos += 4;
        EnsureBytes(body, pos, 4, "device_type");
        var typeNum = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos, 4));
        pos += 4;

        var name = ReadBString(body, ref pos);
        var vendor = "";
        if (protocolVersion >= 1)
        {
            vendor = ReadBString(body, ref pos);
        }

        ReadBString(body, ref pos);     // description
        ReadBString(body, ref pos);     // version
        var serial = ReadBString(body, ref pos);
        var location = ReadBString(body, ref pos);

        // num_modes + active_mode + per-mode entries
        EnsureBytes(body, pos, 2, "num_modes");
        var modeCount = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos, 2));
        pos += 2;
        EnsureBytes(body, pos, 4, "active_mode");
        pos += 4; // active_mode (uint32) - present at all protocol versions
        var modes = new List<RgbMode>(modeCount);
        for (int i = 0; i < modeCount; i++)
        {
            var modeStart = pos;
            var (modeName, colorMode) = ReadMode(body, ref pos, protocolVersion, $"mode[{i}]");
            var modeBytes = body.Slice(modeStart, pos - modeStart).ToArray();
            if (protocolVersion >= 3)
            {
                ForceModeBrightnessMax(modeBytes, protocolVersion);
            }
            modes.Add(new RgbMode
            {
                Index = i,
                Name = modeName,
                ColorMode = colorMode,
                Bytes = modeBytes,
            });
        }

        // num_zones + per-zone entries
        EnsureBytes(body, pos, 2, "num_zones");
        var zoneCount = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos, 2));
        pos += 2;
        var zones = new List<RgbZone>(zoneCount);
        for (int i = 0; i < zoneCount; i++)
        {
            var zoneName = ReadBString(body, ref pos);
            // type(4) + leds_min(4) + leds_max(4) + leds_count(4)
            EnsureBytes(body, pos, 16, $"zone[{i}] fixed block");
            var zoneType = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos, 4));
            pos += 4;
            var ledsMin = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos, 4));
            var ledsMax = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos + 4, 4));
            pos += 4 + 4;
            var zoneLeds = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos, 4));
            pos += 4;
            EnsureBytes(body, pos, 2, $"zone[{i}] matrix_len");
            var matrixLen = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos, 2));
            pos += 2;
            EnsureBytes(body, pos, matrixLen, $"zone[{i}] matrix payload");
            int matrixWidth = 0, matrixHeight = 0;
            int[]? matrixMap = null;
            if (matrixLen >= 8)
            {
                matrixHeight = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos, 4));
                matrixWidth = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos + 4, 4));
                var cellCount = matrixHeight * matrixWidth;
                var expected = 8 + cellCount * 4;
                if (cellCount > 0 && matrixLen >= expected)
                {
                    matrixMap = new int[cellCount];
                    for (int c = 0; c < cellCount; c++)
                    {
                        var v = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos + 8 + c * 4, 4));
                        matrixMap[c] = v == 0xFFFFFFFFu ? -1 : (int)v;
                    }
                }
                else
                {
                    matrixWidth = 0;
                    matrixHeight = 0;
                }
            }
            pos += matrixLen;
            if (protocolVersion >= 4)
            {
                EnsureBytes(body, pos, 2, $"zone[{i}] num_segments");
                var segmentCount = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos, 2));
                pos += 2;
                for (int s = 0; s < segmentCount; s++)
                {
                    ReadBString(body, ref pos); // segment name
                    EnsureBytes(body, pos, 12, $"zone[{i}] segment[{s}] fixed block");
                    pos += 12; // type + start_idx + leds_count
                    if (protocolVersion >= 6)
                    {
                        EnsureBytes(body, pos, 2, $"zone[{i}] segment[{s}] matrix_len");
                        var segMatrixLen = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos, 2));
                        pos += 2;
                        EnsureBytes(body, pos, segMatrixLen + 4, $"zone[{i}] segment[{s}] matrix + flags");
                        pos += segMatrixLen + 4;
                    }
                }
            }
            uint zoneFlags = 0;
            if (protocolVersion >= 5)
            {
                EnsureBytes(body, pos, 4, $"zone[{i}] zone_flags");
                zoneFlags = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos, 4));
                pos += 4;
            }
            if (protocolVersion >= 6)
            {
                // active_mode, then the zone's own modes, then its display name.
                EnsureBytes(body, pos, 6, $"zone[{i}] active_mode + num_modes");
                var zoneModeCount = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos + 4, 2));
                pos += 6;
                for (int m = 0; m < zoneModeCount; m++)
                {
                    ReadMode(body, ref pos, protocolVersion, $"zone[{i}] mode[{m}]");
                }
                ReadBString(body, ref pos);
            }
            zones.Add(new RgbZone
            {
                Name = zoneName,
                ZoneType = zoneType,
                LedsMin = ledsMin,
                LedsMax = ledsMax,
                LedCount = (int)zoneLeds,
                MatrixWidth = matrixWidth,
                MatrixHeight = matrixHeight,
                MatrixMap = matrixMap,
                Flags = zoneFlags,
            });
        }

        EnsureBytes(body, pos, 2, "num_leds");
        var ledCount = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos, 2));
        pos += 2;

        var ledNames = new List<string>(ledCount);
        for (int i = 0; i < ledCount; i++)
        {
            var ledName = ReadBString(body, ref pos);
            if (protocolVersion < 6)
            {
                EnsureBytes(body, pos, 4, $"led[{i}] value");
                pos += 4; // led value, dropped at v6
            }
            ledNames.Add(ledName);
        }

        return new RgbDevice
        {
            Index = deviceIndex,
            Name = name,
            Type = typeNum,
            LedCount = ledCount,
            Vendor = vendor,
            Serial = serial,
            Location = location,
            LedNames = ledNames,
            Zones = zones,
            Modes = modes,
        };
    }

    /// <summary>
    /// Advance past one mode entry and return its name and color_mode. Fixed
    /// block: [value, dropped at v6] flags speed_min speed_max [v3+ brightness
    /// min/max] colors_min colors_max speed [v3+ brightness] direction
    /// color_mode, then uint16 num_colors and the colors.
    /// </summary>
    private static (string name, uint colorMode) ReadMode(ReadOnlySpan<byte> body, ref int pos, uint protocolVersion, string field)
    {
        var name = ReadBString(body, ref pos);
        var modeFixed = (protocolVersion >= 6 ? 0 : 4) + 8 * 4;
        if (protocolVersion >= 3)
        {
            modeFixed += 3 * 4;
        }
        EnsureBytes(body, pos, modeFixed + 2, $"{field} fixed block");
        var colorMode = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos + modeFixed - 4, 4));
        pos += modeFixed;
        var colorCount = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos, 2));
        pos += 2;
        EnsureBytes(body, pos, 4 * colorCount, $"{field} colors[]");
        pos += 4 * colorCount;
        return (name, colorMode);
    }

    /// <summary>
    /// Force a mode's brightness field up to its brightness_max, in place. OpenRGB
    /// brightness is per-mode; when we assert a mode via UPDATE_MODE we replay the
    /// device-reported bytes, and a mode whose reported brightness is below its max
    /// drives the hardware dim - a mode reporting 0 (the Corsair K100 Direct mode)
    /// drives it dark. Nexus dims in software (RgbBridge.OnFrame), so the hardware
    /// mode must run full. Only valid for protocol v3+ mode bytes: after the leading
    /// mode-name bstring, the fixed block carries brightness_max at +20 and the live
    /// brightness at +36, each 4 bytes earlier from v6, which drops the leading value.
    /// </summary>
    internal static void ForceModeBrightnessMax(byte[] modeBytes, uint protocolVersion)
    {
        // modeBytes = [name bstring (uint16 len + bytes)][fixed block][colors].
        if (modeBytes.Length < 2)
        {
            return;
        }
        var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(modeBytes.AsSpan(0, 2));
        var fixedStart = 2 + nameLen - (protocolVersion >= 6 ? 4 : 0);
        if (fixedStart + 40 > modeBytes.Length)
        {
            return;
        }
        var brightnessMax = BinaryPrimitives.ReadUInt32LittleEndian(modeBytes.AsSpan(fixedStart + 20, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(modeBytes.AsSpan(fixedStart + 36, 4), brightnessMax);
    }

    /// <summary>
    /// Build the RGBCONTROLLER_UPDATEMODE body. The OpenRGB SDK server runs
    /// <c>SetModeDescription(data)</c> then <c>UpdateMode()</c> on receipt, which
    /// calls each controller's <c>DeviceUpdateMode()</c> - for ENE-style DRAM
    /// controllers that's where the SMBus write to the hardware mode register
    /// actually happens. The lighter SET_CUSTOM_MODE packet (1100) only updates
    /// the server's in-memory active_mode and does NOT call UpdateMode(), so
    /// controllers with hardware mode registers silently reject the subsequent
    /// per-LED writes until UPDATE_MODE flips the register.
    ///
    /// Body layout:
    ///   uint32 data_size (total body size including itself)
    ///   int32  mode_idx
    ///   [mode-entry bytes - same wire format the server emitted in CONTROLLER_DATA]
    ///
    /// We echo the mode bytes back verbatim rather than re-serializing field-
    /// by-field so we don't have to maintain a full per-protocol-version writer
    /// matching <c>RGBController::GetModeDescription</c>.
    /// </summary>
    public static byte[] BuildUpdateModeBody(int modeIdx, ReadOnlySpan<byte> modeBytes)
    {
        var bodySize = 4 + 4 + modeBytes.Length;
        var body = new byte[bodySize];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), (uint)bodySize);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4, 4), modeIdx);
        modeBytes.CopyTo(body.AsSpan(8));
        return body;
    }

    /// <summary>
    /// Read an OpenRGB "bstring" - uint16 length prefix + that many UTF-8 bytes
    /// (the length includes the trailing NUL terminator). A length of 1 means
    /// an empty string with just the NUL byte.
    /// </summary>
    private static string ReadBString(ReadOnlySpan<byte> body, ref int pos)
    {
        EnsureBytes(body, pos, 2, "bstring length");
        var len = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos, 2));
        pos += 2;
        if (len == 0)
        {
            // Malformed - every bstring should at least have the NUL terminator.
            // Treat as empty without advancing.
            return string.Empty;
        }
        EnsureBytes(body, pos, len, "bstring payload");
        // The length includes the trailing NUL terminator in the body.
        // Strip the NUL when decoding.
        var contentLen = len - 1;
        var s = contentLen > 0 ? Encoding.UTF8.GetString(body.Slice(pos, contentLen)) : string.Empty;
        pos += len;
        return s;
    }

    private static void EnsureBytes(ReadOnlySpan<byte> body, int pos, int needed, string field)
    {
        if (pos < 0 || needed < 0 || pos + needed > body.Length)
        {
            throw new InvalidOperationException(
                $"OpenRGB controller data truncated reading {field}: pos={pos} needed={needed} body_len={body.Length}");
        }
    }
}
