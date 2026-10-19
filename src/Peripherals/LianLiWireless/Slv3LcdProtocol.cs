using System;
using System.Security.Cryptography;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Pure byte-level builders for the SL-LCD Wireless fan screen protocol
/// (VID 0x1CBE, PID 0x0005 SL-LCD / 0x0006 TL-LCD). No IO; the transport owns
/// the WinUSB endpoints.
///
/// Every push is a fixed <see cref="TransferBufferSize"/>-byte bulk write to
/// EP 0x01:
///  [0..511]   DES-CBC/PKCS7 encrypted 512-byte header (from a 504-byte plain header)
///  [512..]    raw JPEG bytes, unencrypted, zero-padded to fill the buffer
/// A single-byte-arg command (Rotate/BrigthSet/SetFrameRate) writes only the
/// 512-byte encrypted header, with no JPEG payload following.
/// </summary>
public static class Slv3LcdProtocol
{
    public const int VendorId = 0x1CBE;
    public const int ProductIdSl = 0x0005;
    public const int ProductIdTl = 0x0006;

    /// <summary>SetupDiGetClassDevs interface GUID for the generic-WinUSB LCD screens (Y70 registry).</summary>
    public const string LcdInterfaceGuid = "{88BAE032-5A81-49f0-BC3D-A4FF138216D6}";

    // Same pipe ids as the RF dongles, but this is a bulk endpoint on the LCD.
    public const byte WritePipeId = 0x01;
    public const byte ReadPipeId = 0x81;

    public const int PanelWidth = 400;
    public const int PanelHeight = 400;

    public const int TransferBufferSize = 102400;
    public const int HeaderPlainSize = 504;
    public const int HeaderCipherSize = 512;

    /// <summary>DES key = IV = ASCII "slv3tuzx". Only the header is encrypted; the JPEG rides clear.</summary>
    public static readonly byte[] DesKeyIv = { 115, 108, 118, 51, 116, 117, 122, 120 };

    public enum CmdType : byte
    {
        GetVer = 10,
        Reboot = 11,
        Rotate = 13,
        BrigthSet = 14,
        SetFrameRate = 15,
        UpdateFireWare = 40,
        PushJpg = 101,
        PushPng = 102,
        PushLayer3 = 103,
        CheckNewLcd = 128,
        GetPosIndex = 201,
    }

    /// <summary>
    /// Builds the 504-byte plaintext header for a length-carrying command
    /// (PushJpg): [0]=cmd, [2..3]=magic 0x1A 0x6D, [4..7]=timestamp LE32,
    /// [8..11]=payload length BE32. Bytes 1 and 12..503 stay zero.
    /// </summary>
    public static byte[] BuildLengthHeader(CmdType cmd, uint timestampMs, int payloadLength)
    {
        var header = new byte[HeaderPlainSize];
        header[0] = (byte)cmd;
        header[2] = 0x1A;
        header[3] = 0x6D;
        WriteUInt32LittleEndian(header, 4, timestampMs);
        WriteUInt32BigEndian(header, 8, (uint)payloadLength);
        return header;
    }

    /// <summary>
    /// Builds the 504-byte plaintext header for a single-byte-arg command
    /// (Rotate, BrigthSet, SetFrameRate): same [0..7] layout as
    /// <see cref="BuildLengthHeader"/>, with the argument at [8] alone and no
    /// JPEG payload following.
    /// </summary>
    public static byte[] BuildArgHeader(CmdType cmd, uint timestampMs, byte argByte)
    {
        var header = new byte[HeaderPlainSize];
        header[0] = (byte)cmd;
        header[2] = 0x1A;
        header[3] = 0x6D;
        WriteUInt32LittleEndian(header, 4, timestampMs);
        header[8] = argByte;
        return header;
    }

    /// <summary>
    /// Header of a layered PNG push: like <see cref="BuildLengthHeader"/> plus the layer origin
    /// at [12..13] x and [14..15] y, both big-endian.
    /// </summary>
    public static byte[] BuildLayerHeader(CmdType cmd, uint timestampMs, int payloadLength, int x, int y)
    {
        var header = BuildLengthHeader(cmd, timestampMs, payloadLength);
        header[12] = (byte)(x >> 8);
        header[13] = (byte)x;
        header[14] = (byte)(y >> 8);
        header[15] = (byte)y;
        return header;
    }

    /// <summary>The encrypted header followed by the payload, with no padding.</summary>
    public static byte[] BuildUnpaddedBuffer(ReadOnlySpan<byte> encryptedHeader, ReadOnlySpan<byte> payload)
    {
        if (encryptedHeader.Length != HeaderCipherSize)
        {
            throw new ArgumentException($"Encrypted header must be {HeaderCipherSize} bytes.", nameof(encryptedHeader));
        }
        var buffer = new byte[HeaderCipherSize + payload.Length];
        encryptedHeader.CopyTo(buffer);
        payload.CopyTo(buffer.AsSpan(HeaderCipherSize));
        return buffer;
    }

    /// <summary>
    /// DES-CBC/PKCS7 encrypts a 504-byte plaintext header to 512 bytes. 504 is
    /// already block-aligned, so PKCS7 appends one full 8-byte block of value
    /// 0x08 (padding is added even when the input is already block-aligned).
    /// </summary>
    public static byte[] EncryptHeader(ReadOnlySpan<byte> plainHeader)
    {
        if (plainHeader.Length != HeaderPlainSize)
        {
            throw new ArgumentException($"Plaintext header must be {HeaderPlainSize} bytes.", nameof(plainHeader));
        }
        using var des = DES.Create();
        des.Key = DesKeyIv;
        des.IV = DesKeyIv;
        des.Mode = CipherMode.CBC;
        des.Padding = PaddingMode.PKCS7;
        using var encryptor = des.CreateEncryptor();
        var plainArray = plainHeader.ToArray();
        return encryptor.TransformFinalBlock(plainArray, 0, plainArray.Length);
    }

    /// <summary>
    /// Assembles the fixed <see cref="TransferBufferSize"/>-byte PushJpg
    /// buffer: the 512-byte encrypted header followed by the JPEG, zero-padded
    /// to fill the rest. Throws if the JPEG would not fit.
    /// </summary>
    public static byte[] BuildTransferBuffer(ReadOnlySpan<byte> encryptedHeader, ReadOnlySpan<byte> jpeg)
    {
        if (encryptedHeader.Length != HeaderCipherSize)
        {
            throw new ArgumentException($"Encrypted header must be {HeaderCipherSize} bytes.", nameof(encryptedHeader));
        }
        var maxJpeg = TransferBufferSize - HeaderCipherSize;
        if (jpeg.Length > maxJpeg)
        {
            throw new ArgumentException(
                $"JPEG of {jpeg.Length} bytes exceeds the {maxJpeg}-byte payload capacity.", nameof(jpeg));
        }
        var buffer = new byte[TransferBufferSize];
        encryptedHeader.CopyTo(buffer);
        jpeg.CopyTo(buffer.AsSpan(HeaderCipherSize));
        return buffer;
    }

    /// <summary>Builds the full PushJpg transfer buffer from plaintext parameters in one call.</summary>
    public static byte[] BuildPushJpgBuffer(uint timestampMs, ReadOnlySpan<byte> jpeg)
    {
        var plainHeader = BuildLengthHeader(CmdType.PushJpg, timestampMs, jpeg.Length);
        var encryptedHeader = EncryptHeader(plainHeader);
        return BuildTransferBuffer(encryptedHeader, jpeg);
    }

    /// <summary>Builds the encrypted 512-byte header for a single-byte-arg command; no JPEG follows.</summary>
    public static byte[] BuildArgCommandHeader(CmdType cmd, uint timestampMs, byte argByte)
    {
        var plainHeader = BuildArgHeader(cmd, timestampMs, argByte);
        return EncryptHeader(plainHeader);
    }

    private static void WriteUInt32LittleEndian(byte[] dst, int offset, uint value)
    {
        dst[offset] = (byte)value;
        dst[offset + 1] = (byte)(value >> 8);
        dst[offset + 2] = (byte)(value >> 16);
        dst[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteUInt32BigEndian(byte[] dst, int offset, uint value)
    {
        dst[offset] = (byte)(value >> 24);
        dst[offset + 1] = (byte)(value >> 16);
        dst[offset + 2] = (byte)(value >> 8);
        dst[offset + 3] = (byte)value;
    }
}
