using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Corsair.Psu;

/// <summary>
/// Wire format of the digital Corsair PSUs (HXi and RMi) over their single HID interface. A command is
/// a 64-byte frame: an address and read/write byte, a command code, then data, zero padded. The device
/// echoes the first two bytes of every command in its reply.
/// </summary>
public static class CorsairPsuProtocol
{
    public const int VendorId = 0x1B1C;
    public const int FrameLength = 64;

    private const byte Address = 0x02;
    private const byte WriteBit = 0x00;
    private const byte ReadBit = 0x01;

    private const byte CmdFanControlMode = 0xF0;
    private const byte CmdFanCommand = 0x3B;
    private const byte CmdReadFanSpeed = 0x90;

    private const byte ModeHardware = 0x00;
    private const byte ModeSoftware = 0x01;

    /// <summary>Lowest fixed duty written in software mode.</summary>
    public const int MinDuty = 30;
    public const int MaxDuty = 100;

    /// <summary>Models with a fan we may take over, by product id.</summary>
    public static readonly IReadOnlyDictionary<int, string> Models = new Dictionary<int, string>
    {
        [0x1C03] = "Corsair HX550i",
        [0x1C04] = "Corsair HX650i",
        [0x1C05] = "Corsair HX750i",
        [0x1C06] = "Corsair HX850i",
        [0x1C07] = "Corsair HX1000i",
        [0x1C1E] = "Corsair HX1000i",
        [0x1C08] = "Corsair HX1200i",
        [0x1C1F] = "Corsair HX1500i",
        [0x1C09] = "Corsair RM550i",
        [0x1C0A] = "Corsair RM650i",
        [0x1C0B] = "Corsair RM750i",
        [0x1C0C] = "Corsair RM850i",
        [0x1C0D] = "Corsair RM1000i",
    };

    public static bool IsSupported(int vendorId, int productId) =>
        vendorId == VendorId && Models.ContainsKey(productId);

    public static int ClampDuty(int duty) => Math.Clamp(duty, MinDuty, MaxDuty);

    /// <summary>Session start: two raw bytes with no address or command echo.</summary>
    public static byte[] BuildInit() => Frame(0xFE, 0x03);

    public static byte[] BuildSetSoftwareMode() => Frame(Address | WriteBit, CmdFanControlMode, ModeSoftware);

    public static byte[] BuildSetHardwareMode() => Frame(Address | WriteBit, CmdFanControlMode, ModeHardware);

    public static byte[] BuildReadMode() => Frame(Address | ReadBit, CmdFanControlMode);

    public static byte[] BuildSetDuty(int duty) => Frame(Address | WriteBit, CmdFanCommand, (byte)ClampDuty(duty));

    public static byte[] BuildReadFanSpeed() => Frame(Address | ReadBit, CmdReadFanSpeed);

    /// <summary>True when the reply echoes the command's address and command bytes.</summary>
    public static bool IsEchoOf(ReadOnlySpan<byte> command, ReadOnlySpan<byte> reply) =>
        reply.Length >= FrameLength && command.Length >= 2 && reply[0] == command[0] && reply[1] == command[1];

    /// <summary>The fan control mode in a read reply, or null when the value is neither mode.</summary>
    public static bool? ParseSoftwareMode(ReadOnlySpan<byte> reply) => reply.Length < 3 ? null : reply[2] switch
    {
        ModeHardware => false,
        ModeSoftware => true,
        _ => null,
    };

    /// <summary>Fan speed in RPM from a read reply (a 16-bit linear value, little endian), or null when implausible.</summary>
    public static int? ParseRpm(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 4) return null;
        int raw = reply[2] | (reply[3] << 8);
        int exponent = raw >> 11;
        int mantissa = raw & 0x7FF;
        if (mantissa > 1023) mantissa -= 2048;
        if (exponent > 15) exponent -= 32;
        var value = mantissa * Math.Pow(2, exponent);
        if (double.IsNaN(value) || value < 0 || value > 20000) return null;
        return (int)Math.Round(value);
    }

    private static byte[] Frame(params byte[] bytes)
    {
        var frame = new byte[FrameLength];
        bytes.CopyTo(frame, 0);
        return frame;
    }
}
