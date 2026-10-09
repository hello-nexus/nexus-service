using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Nexus.Service.Rendering;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The Lian Li Universal Screen 8.8 (1CBE:A088), a 480x1920 portrait framebuffer on a bulk pipe.
///
/// Every command is a 500-byte plaintext block DES-encrypted in CBC mode, then padded out
/// to a 512-byte packet with a two-byte magic tail. The key and the IV are both the ASCII
/// string "slv3tuzx". Every command is answered by a 512-byte reply echoing the command byte,
/// then a zero-length packet. Checked against L-Connect 3 (lcd207 WinUsb) captures on a
/// firmware lianli88_0001_0023 unit.
/// </summary>
public static class UniversalScreen88Protocol
{
    public const int ProductId = 0xA088;

    /// <summary>Native framebuffer: the glass shows a landscape frame only once it is turned a quarter clockwise into this.</summary>
    public const int Width = 480;
    public const int Height = 1920;

    /// <summary>Plaintext block size before encryption and padding.</summary>
    public const int PlainLength = 500;

    /// <summary>Encrypted block plus six zeros and the two-byte tail.</summary>
    public const int PacketLength = 512;

    /// <summary>Most a command can carry after the 8-byte preamble.</summary>
    public const int MaxParams = PlainLength - 8;

    public const byte CommandGetVersion = 0x0A;
    public const byte CommandReboot = 0x0B;
    public const byte CommandRotate = 0x0D;
    public const byte CommandBrightness = 0x0E;
    public const byte CommandSetClock = 0x33;
    public const byte CommandStopClock = 0x34;
    public const byte CommandPushJpeg = 0x65;
    public const byte CommandPushPng = 0x66;
    public const byte CommandStopPlay = 0x7B;

    /// <summary>Rotate values L-Connect sends on firmware 1.2+. They turn the firmware's own screens only, never a pushed JPEG (camera-checked).</summary>
    public const byte RotatePortrait = 0;
    public const byte RotateLandscape = 1;

    /// <summary>SetClock mode that syncs the clock without changing whether the firmware clock face is enabled.</summary>
    private const byte ClockSyncOnly = 2;

    /// <summary>ASCII "slv3tuzx", used as both the DES key and the CBC IV.</summary>
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("slv3tuzx");

    /// <summary>Closes every packet; the firmware rejects a block without it.</summary>
    private static ReadOnlySpan<byte> Tail => new byte[] { 0xA1, 0x1A };

    private static byte[]? _emptyOverlay;

    /// <summary>Builds one 512-byte packet; <paramref name="timestampMs"/> (plaintext [4..8], LE) must strictly increase within a session.</summary>
    public static byte[] EncodeCommand(byte command, ReadOnlySpan<byte> parameters, uint timestampMs)
    {
        if (parameters.Length > MaxParams)
        {
            throw new ArgumentException($"at most {MaxParams} parameter bytes", nameof(parameters));
        }
        var plain = new byte[PlainLength];
        plain[0] = command;
        plain[2] = 0x1A;
        plain[3] = 0x6D;
        plain[4] = (byte)(timestampMs & 0xFF);
        plain[5] = (byte)((timestampMs >> 8) & 0xFF);
        plain[6] = (byte)((timestampMs >> 16) & 0xFF);
        plain[7] = (byte)((timestampMs >> 24) & 0xFF);
        parameters.CopyTo(plain.AsSpan(8));

        var encrypted = Encrypt(plain);
        var packet = new byte[PacketLength];
        encrypted.CopyTo(packet.AsSpan());
        // Six zeros between the ciphertext and the tail; the array is already zeroed.
        Tail.CopyTo(packet.AsSpan(PacketLength - Tail.Length));
        return packet;
    }

    /// <summary>Header carrying the image length big-endian at params[0..4], then the image, as one transfer.</summary>
    public static byte[] EncodeImage(byte command, ReadOnlySpan<byte> image, uint timestampMs)
    {
        Span<byte> length = stackalloc byte[4];
        length[0] = (byte)(image.Length >> 24);
        length[1] = (byte)(image.Length >> 16);
        length[2] = (byte)(image.Length >> 8);
        length[3] = (byte)image.Length;
        var header = EncodeCommand(command, length, timestampMs);
        var packet = new byte[header.Length + image.Length];
        header.CopyTo(packet, 0);
        image.CopyTo(packet.AsSpan(header.Length));
        return packet;
    }

    /// <summary>Year (u16 BE), month, day, hour, minute, second, then the clock mode.</summary>
    public static byte[] EncodeSyncClock(DateTime now, uint timestampMs) => EncodeCommand(CommandSetClock, new byte[]
    {
        (byte)(now.Year >> 8), (byte)now.Year, (byte)now.Month, (byte)now.Day,
        (byte)now.Hour, (byte)now.Minute, (byte)now.Second, ClockSyncOnly,
    }, timestampMs);

    /// <summary>Firmware string from a GetVersion reply (ASCII at [8..40]), or null.</summary>
    public static string? DecodeVersion(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 40 || reply[0] != CommandGetVersion)
        {
            return null;
        }
        var text = reply.Slice(8, 32);
        int end = text.IndexOf((byte)0);
        var version = Encoding.ASCII.GetString(end < 0 ? text : text[..end]);
        return version.Length > 0 ? version : null;
    }

    /// <summary>
    /// A fully transparent framebuffer-sized PNG. The firmware composites a PNG layer over
    /// every JPEG and keeps whatever was last pushed there (L-Connect's sensor overlay), so
    /// one of these goes out at connect.
    /// </summary>
    public static byte[] EmptyOverlayPng()
    {
        if (_emptyOverlay is { } cached)
        {
            return cached;
        }
        using var image = RenderKit.NewImage(Width, Height);
        return _emptyOverlay = RenderKit.EncodePng(image);
    }

    /// <summary>
    /// DES-CBC with PKCS#7 padding, key and IV both the ASCII key. 500 bytes in, 504 out.
    /// DES is long broken as a cipher; it is used here only because it is what the panel's
    /// firmware implements, and the key is public.
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
#pragma warning disable CA5351 // Broken cryptographic algorithm - dictated by the device firmware.
        using var des = DES.Create();
#pragma warning restore CA5351
        des.Key = Key;
        des.IV = Key;
        des.Mode = CipherMode.CBC;
        des.Padding = PaddingMode.PKCS7;
        using var encryptor = des.CreateEncryptor();
        var input = plain.ToArray();
        return encryptor.TransformFinalBlock(input, 0, input.Length);
    }
}
