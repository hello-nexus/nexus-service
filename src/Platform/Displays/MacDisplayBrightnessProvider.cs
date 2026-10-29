using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// macOS display enumeration and brightness control. CoreGraphics supplies the
/// attached display list; IOKit display parameters cover Apple/internal
/// brightness. External monitors use public framebuffer I2C where available
/// and fall back to Apple Silicon DCP/IOAVService I2C when framebuffer buses are
/// not surfaced in IORegistry.
/// </summary>
public sealed unsafe class MacDisplayBrightnessProvider : IDisplayBrightnessProvider
{
    private const int NativeWriteCooldownMs = 40;
    private const int DdcWriteCooldownMs = 200;
    private const byte BrightnessVcpCode = 0x10;

    public string Hint => "macOS did not report any online displays.";

    public IReadOnlyList<DisplayDto> Enumerate(IReadOnlyCollection<string>? excludedIds = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Array.Empty<DisplayDto>();

        var results = new List<DisplayDto>();
        foreach (var display in EnumerateDisplayHandles())
        {
            // Opted-out displays are reported but never touched: this provider
            // issues real DDC below, and the whole point of the opt-out is that
            // no transaction reaches the panel.
            if (excludedIds is not null && excludedIds.Contains(display.Id))
            {
                results.Add(new DisplayDto
                {
                    Id = display.Id,
                    Name = display.Name,
                    Manufacturer = display.Manufacturer,
                    Model = display.Model,
                    IsInternal = display.IsInternal,
                    DdcEnabled = false,
                    BrightnessControl = { UnsupportedReason = "Brightness control is turned off for this display." },
                });
                continue;
            }
            var dto = new DisplayDto
            {
                Id = display.Id,
                Name = display.Name,
                Manufacturer = display.Manufacturer,
                Model = display.Model,
                IsInternal = display.IsInternal,
            };

            dto.BrightnessControl.UnsupportedReason = display.IsInternal
                ? "macOS did not expose brightness control for this display."
                : "macOS did not expose IOKit brightness or DDC/CI control for this display.";

            if (TryReadNativeBrightness(display, out var nativeBrightness))
            {
                dto.Capabilities.Brightness = true;
                dto.BrightnessControl = BuildSupportedBrightnessControl(
                    nativeBrightness,
                    DisplayBrightnessControlPaths.MacosInternal,
                    DisplayBrightnessWriteModes.Immediate,
                    NativeWriteCooldownMs,
                    verifyAfterWrite: true);
            }
            else if (TryGetDdcVcp(display, BrightnessVcpCode, out var current, out var max))
            {
                dto.IsDdcCapable = true;
                dto.Capabilities.Brightness = true;
                dto.Capabilities.Contrast = TryGetDdcVcp(display, 0x12, out _, out _);
                dto.BrightnessControl = BuildSupportedBrightnessControl(
                    RawToPercent(current, max),
                    DisplayBrightnessControlPaths.DdcCi,
                    DisplayBrightnessWriteModes.Coalesced,
                    DdcWriteCooldownMs,
                    verifyAfterWrite: false);
            }

            results.Add(dto);
        }

        return results;
    }

    public int? GetBrightness(string id)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return null;

        return TryFindDisplay(id, out var display)
            ? ReadBrightness(display)
            : null;
    }

    public DisplayBrightnessDto SetBrightness(string id, int percent)
    {
        var requested = ClampPercent(percent);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return UnsupportedBrightness(id, requested, "Display brightness is not supported on this platform.");

        if (!TryFindDisplay(id, out var display))
            return FailedBrightness(id, requested, "Display not found.");

        if (TryReadNativeBrightness(display, out _))
        {
            if (TrySetNativeBrightness(display, requested, out var nativeApplied))
                return AppliedBrightness(id, requested, nativeApplied);

            return FailedBrightness(id, requested, "macOS rejected the native brightness write.");
        }

        if (TryGetDdcVcp(display, BrightnessVcpCode, out _, out var max) && max > 0)
        {
            var raw = PercentToRaw(requested, max);
            if (TrySetDdcVcp(display, BrightnessVcpCode, raw))
            {
                var applied = TryGetDdcVcp(display, BrightnessVcpCode, out var currentAfter, out var maxAfter)
                    ? RawToPercent(currentAfter, maxAfter)
                    : requested;
                return AppliedBrightness(id, requested, applied);
            }

            return FailedBrightness(id, requested, "Monitor rejected the DDC/CI brightness write.");
        }

        return UnsupportedBrightness(id, requested, "Brightness control is not available for this display.");
    }

    public DisplayBrightnessWritePolicy GetBrightnessWritePolicy(string id)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || !TryFindDisplay(id, out var display))
            return new DisplayBrightnessWritePolicy();

        if (TryReadNativeBrightness(display, out _))
        {
            return new DisplayBrightnessWritePolicy
            {
                ControlPath = DisplayBrightnessControlPaths.MacosInternal,
                WriteMode = DisplayBrightnessWriteModes.Immediate,
                MinWriteIntervalMs = NativeWriteCooldownMs,
                ReadAfterWriteDelayMs = NativeWriteCooldownMs,
                VerifyAfterWrite = true,
            };
        }

        if (TryGetDdcVcp(display, BrightnessVcpCode, out _, out _))
        {
            return new DisplayBrightnessWritePolicy
            {
                ControlPath = DisplayBrightnessControlPaths.DdcCi,
                WriteMode = DisplayBrightnessWriteModes.Coalesced,
                MinWriteIntervalMs = DdcWriteCooldownMs,
                ReadAfterWriteDelayMs = 0,
                VerifyAfterWrite = false,
            };
        }

        return new DisplayBrightnessWritePolicy();
    }

    public DisplayVcpDto? GetVcp(string id, byte code)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || !TryFindDisplay(id, out var display))
            return null;

        return TryGetDdcVcp(display, code, out var current, out var max)
            ? new DisplayVcpDto { Id = id, Code = code, Value = (int)current, MaxValue = (int)max }
            : null;
    }

    public bool SetVcp(string id, byte code, int value)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || value < 0 || !TryFindDisplay(id, out var display))
            return false;

        var write = (uint)value;
        if (TryGetDdcVcp(display, code, out _, out var max) && max > 0 && write > max)
            write = max;

        return TrySetDdcVcp(display, code, write);
    }

    // PnP/EDID controller-name matching is a Windows concept (the fragments are
    // Windows monitor hardware ids). The Y70 DDC-only models aren't a macOS
    // target, so there's nothing to match here.
    public string? FindDisplayIdByHardwareName(IReadOnlyList<string> nameFragments) => null;

    private static int? ReadBrightness(MacDisplayHandle display)
    {
        if (TryReadNativeBrightness(display, out var native))
            return native;

        return TryGetDdcVcp(display, BrightnessVcpCode, out var current, out var max)
            ? RawToPercent(current, max)
            : null;
    }

    // Internal: MacDisplayTopologyProvider reuses this enumeration so topology
    // ids/names stay byte-identical to the /displays id space.
    internal static List<MacDisplayHandle> EnumerateDisplayHandles()
    {
        var displays = new uint[32];
        var handles = new List<MacDisplayHandle>();
        if (CGGetOnlineDisplayList((uint)displays.Length, displays, out var count) != 0)
            return handles;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count && index < displays.Length; index++)
        {
            var displayId = displays[index];
            var framebuffer = CGDisplayIOServicePort(displayId);
            var vendor = CGDisplayVendorNumber(displayId);
            var model = CGDisplayModelNumber(displayId);
            var serial = CGDisplaySerialNumber(displayId);
            var id = BuildStableId(vendor, model, serial, index);
            if (!seen.Add(id))
                id = $"{id}-{index + 1}";

            var manufacturer = DecodeEisaId(vendor);
            var modelText = model == 0 ? "" : model.ToString("X4");
            var name = TryGetDisplayProductName(framebuffer);
            if (string.IsNullOrWhiteSpace(name))
                name = TryGetAvDisplayProductName(vendor, model, serial);

            if (string.IsNullOrWhiteSpace(name))
            {
                // A vendor id that is not a valid EISA code decodes to hex, which is no name to show.
                name = !IsEisaCode(manufacturer) || string.IsNullOrEmpty(modelText)
                    ? $"Display {index + 1}"
                    : $"{manufacturer} {modelText}";
            }

            handles.Add(new MacDisplayHandle(
                id,
                displayId,
                framebuffer,
                name,
                manufacturer,
                modelText,
                CGDisplayIsBuiltin(displayId) != 0,
                vendor,
                model,
                serial));
        }

        return handles;
    }

    private static bool TryFindDisplay(string id, out MacDisplayHandle display)
    {
        foreach (var candidate in EnumerateDisplayHandles())
        {
            if (string.Equals(candidate.Id, id, StringComparison.Ordinal))
            {
                display = candidate;
                return true;
            }
        }

        display = default;
        return false;
    }

    private static bool TryReadNativeBrightness(MacDisplayHandle display, out int brightness)
    {
        if (TryReadDisplayServicesBrightness(display, out brightness))
            return true;

        return TryReadIoDisplayBrightness(display, out brightness);
    }

    private static bool TrySetNativeBrightness(MacDisplayHandle display, int percent, out int applied)
    {
        applied = 0;
        var value = ClampPercent(percent) / 100f;
        if (TrySetDisplayServicesBrightness(display, value))
        {
            applied = TryReadNativeBrightness(display, out var readBack) ? readBack : percent;
            return true;
        }

        return TrySetIoDisplayBrightness(display, percent, out applied);
    }

    private static bool TryReadDisplayServicesBrightness(MacDisplayHandle display, out int brightness)
    {
        brightness = 0;
        if (display.DisplayId == 0)
            return false;

        try
        {
            if (DisplayServicesGetBrightness(display.DisplayId, out var value) != 0 ||
                float.IsNaN(value) ||
                float.IsInfinity(value))
            {
                return false;
            }

            brightness = ClampPercent((int)Math.Round(Math.Clamp(value, 0, 1) * 100));
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private static bool TrySetDisplayServicesBrightness(MacDisplayHandle display, float value)
    {
        if (display.DisplayId == 0)
            return false;

        try
        {
            return DisplayServicesSetBrightness(display.DisplayId, Math.Clamp(value, 0, 1)) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private static bool TryReadIoDisplayBrightness(MacDisplayHandle display, out int brightness)
    {
        brightness = 0;
        if (display.Framebuffer == 0)
            return false;

        var key = CreateCfString(BrightnessParameterName);
        if (key == IntPtr.Zero)
            return false;

        try
        {
            if (IODisplayGetFloatParameter(display.Framebuffer, 0, key, out var value) != 0 ||
                float.IsNaN(value) ||
                float.IsInfinity(value))
            {
                return false;
            }

            brightness = ClampPercent((int)Math.Round(Math.Clamp(value, 0, 1) * 100));
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            CFRelease(key);
        }
    }

    private static bool TrySetIoDisplayBrightness(MacDisplayHandle display, int percent, out int applied)
    {
        applied = 0;
        if (display.Framebuffer == 0)
            return false;

        var key = CreateCfString(BrightnessParameterName);
        if (key == IntPtr.Zero)
            return false;

        try
        {
            var value = ClampPercent(percent) / 100f;
            if (IODisplaySetFloatParameter(display.Framebuffer, 0, key, value) != 0)
                return false;

            _ = IODisplayCommitParameters(display.Framebuffer, 0);
            applied = TryReadNativeBrightness(display, out var readBack) ? readBack : percent;
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            CFRelease(key);
        }
    }

    private static bool TryGetDdcVcp(MacDisplayHandle display, byte code, out uint current, out uint max)
    {
        current = 0;
        max = 0;

        byte[] send =
        {
            DdcHostAddress,
            0x82,
            0x01,
            code,
            0,
        };
        send[^1] = DdcChecksum(send.AsSpan(0, send.Length - 1));

        var reply = new byte[32];
        var found = false;
        var currentValue = 0u;
        var maxValue = 0u;

        var ok = display.Framebuffer != 0 && TryWithI2cConnection(display.Framebuffer, connect =>
            TrySendDdcRequest(connect, send, reply, expectReply: true, out var replyBytes) &&
            TryParseGetVcpReply(reply.AsSpan(0, replyBytes), code, out currentValue, out maxValue) &&
            (found = true));

        if (!ok)
        {
            var avReply = new byte[11];
            ok = TryWithAvService(display, avService =>
                TrySendAvDdcRequest(
                    avService,
                    BuildAvDdcPacket(code),
                    avReply,
                    expectReply: true,
                    out var replyBytes) &&
                TryParseGetVcpReply(avReply.AsSpan(0, replyBytes), code, out currentValue, out maxValue) &&
                (found = true));
        }

        if (!ok)
        {
            return false;
        }

        if (!found)
            return false;

        current = currentValue;
        max = maxValue;
        return true;
    }

    private static bool TrySetDdcVcp(MacDisplayHandle display, byte code, uint value)
    {
        var clamped = value > ushort.MaxValue ? ushort.MaxValue : value;
        byte[] send =
        {
            DdcHostAddress,
            0x84,
            0x03,
            code,
            (byte)((clamped >> 8) & 0xFF),
            (byte)(clamped & 0xFF),
            0,
        };
        send[^1] = DdcChecksum(send.AsSpan(0, send.Length - 1));

        var ok = display.Framebuffer != 0 && TryWithI2cConnection(display.Framebuffer, connect =>
            TrySendDdcRequest(connect, send, Array.Empty<byte>(), expectReply: false, out _));
        if (!ok)
        {
            ok = TryWithAvService(display, avService =>
                TrySendAvDdcRequest(
                    avService,
                    BuildAvDdcPacket(code, (ushort)clamped),
                    Array.Empty<byte>(),
                    expectReply: false,
                    out _));
        }

        if (ok)
            Thread.Sleep(40);
        return ok;
    }

    private static bool TryWithI2cConnection(uint framebuffer, Func<IntPtr, bool> action)
    {
        try
        {
            if (IOFBGetI2CInterfaceCount(framebuffer, out var count) != 0 || count == 0)
                return false;

            for (uint bus = 0; bus < count; bus++)
            {
                if (IOFBCopyI2CInterfaceForBus(framebuffer, bus, out var i2cInterface) != 0 || i2cInterface == 0)
                    continue;

                try
                {
                    if (IOI2CInterfaceOpen(i2cInterface, 0, out var connect) != 0 || connect == IntPtr.Zero)
                        continue;

                    try
                    {
                        if (action(connect))
                            return true;
                    }
                    finally
                    {
                        _ = IOI2CInterfaceClose(connect, 0);
                    }
                }
                finally
                {
                    _ = IOObjectRelease(i2cInterface);
                }
            }
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-mac] ddc probe failed: {ex.Message}");
            return false;
        }

        return false;
    }

    private static bool TryWithAvService(MacDisplayHandle display, Func<IntPtr, bool> action)
    {
        try
        {
            var matching = IOServiceMatching(DcpAvServiceProxyClass);
            if (matching == IntPtr.Zero)
                return false;

            if (IOServiceGetMatchingServices(0, matching, out var iterator) != 0 || iterator == 0)
                return false;

            try
            {
                uint service;
                while ((service = IOIteratorNext(iterator)) != 0)
                {
                    try
                    {
                        if (!IsExternalDcpAvService(service))
                            continue;

                        var avService = IOAVServiceCreateWithService(IntPtr.Zero, service);
                        if (avService == IntPtr.Zero)
                            continue;

                        try
                        {
                            if (!AvServiceMatchesDisplay(avService, display))
                                continue;

                            if (action(avService))
                                return true;
                        }
                        finally
                        {
                            CFRelease(avService);
                        }
                    }
                    finally
                    {
                        _ = IOObjectRelease(service);
                    }
                }
            }
            finally
            {
                _ = IOObjectRelease(iterator);
            }
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-mac] av ddc probe failed: {ex.Message}");
            return false;
        }

        return false;
    }

    private static bool IsExternalDcpAvService(uint service)
    {
        var value = CreateCfString(LocationPropertyName);
        if (value == IntPtr.Zero)
            return false;

        try
        {
            var location = IORegistryEntryCreateCFProperty(service, value, IntPtr.Zero, 0);
            if (location == IntPtr.Zero)
                return false;

            try
            {
                return CFGetTypeID(location) == CFStringGetTypeID() &&
                    string.Equals(CfStringToString(location), ExternalLocationValue, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                CFRelease(location);
            }
        }
        finally
        {
            CFRelease(value);
        }
    }

    private static bool AvServiceMatchesDisplay(IntPtr avService, MacDisplayHandle display)
    {
        if (!TryCopyAvEdid(avService, out var edid))
            return false;

        return EdidMatchesDisplay(edid, display);
    }

    private static bool TryCopyAvEdid(IntPtr avService, out byte[] edid)
    {
        edid = Array.Empty<byte>();
        if (avService == IntPtr.Zero)
            return false;

        if (IOAVServiceCopyEDID(avService, out var edidData) != 0 || edidData == IntPtr.Zero)
            return false;

        try
        {
            if (CFGetTypeID(edidData) != CFDataGetTypeID())
                return false;

            var length = CFDataGetLength(edidData);
            if (length < 16 || length > 4096)
                return false;

            var source = CFDataGetBytePtr(edidData);
            if (source == IntPtr.Zero)
                return false;

            edid = new byte[(int)length];
            Marshal.Copy(source, edid, 0, edid.Length);
            return true;
        }
        finally
        {
            CFRelease(edidData);
        }
    }

    private static bool EdidMatchesDisplay(byte[] edid, MacDisplayHandle display)
    {
        if (edid.Length < 16)
            return false;

        var vendor = (uint)((edid[8] << 8) | edid[9]);
        var model = (uint)(edid[10] | (edid[11] << 8));
        var serial = (uint)(edid[12] | (edid[13] << 8) | (edid[14] << 16) | (edid[15] << 24));

        if (display.Vendor != 0 && vendor != display.Vendor)
            return false;

        if (display.ModelNumber != 0 && model != display.ModelNumber)
            return false;

        return display.SerialNumber == 0 || serial == 0 || serial == display.SerialNumber;
    }

    private static string TryGetAvDisplayProductName(uint vendor, uint model, uint serial)
    {
        var name = "";
        var probe = new MacDisplayHandle(
            "",
            0,
            0,
            "",
            "",
            "",
            false,
            vendor,
            model,
            serial);

        _ = TryWithAvService(probe, avService =>
        {
            if (!TryCopyAvEdid(avService, out var edid))
                return false;

            name = ParseEdidProductName(edid);
            return !string.IsNullOrWhiteSpace(name);
        });

        return name;
    }

    private static string ParseEdidProductName(byte[] edid)
    {
        const int DescriptorStart = 54;
        const int DescriptorLength = 18;
        const int NameLength = 13;

        for (var offset = DescriptorStart; offset + DescriptorLength <= edid.Length && offset < 126; offset += DescriptorLength)
        {
            if (edid[offset] != 0 ||
                edid[offset + 1] != 0 ||
                edid[offset + 2] != 0 ||
                edid[offset + 3] != 0xFC)
            {
                continue;
            }

            var length = 0;
            for (; length < NameLength; length++)
            {
                var value = edid[offset + 5 + length];
                if (value == 0 || value == 0x0A || value == 0x0D)
                    break;
            }

            return Encoding.ASCII.GetString(edid, offset + 5, length).Trim();
        }

        return "";
    }

    private static byte[] BuildAvDdcPacket(byte code)
    {
        byte[] packet =
        {
            0x82,
            0x01,
            code,
            0,
        };
        packet[^1] = DdcChecksum(DdcDisplayWriteAddress, packet.AsSpan(0, packet.Length - 1));
        return packet;
    }

    private static byte[] BuildAvDdcPacket(byte code, ushort value)
    {
        byte[] packet =
        {
            0x84,
            0x03,
            code,
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF),
            0,
        };
        packet[^1] = DdcChecksum((byte)(DdcDisplayWriteAddress ^ DdcHostAddress), packet.AsSpan(0, packet.Length - 1));
        return packet;
    }

    private static bool TrySendAvDdcRequest(
        IntPtr avService,
        byte[] send,
        byte[] reply,
        bool expectReply,
        out int replyBytes)
    {
        replyBytes = 0;
        fixed (byte* sendPtr = send)
        fixed (byte* replyPtr = reply)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var writeOk = false;
                for (var cycle = 0; cycle < 2; cycle++)
                {
                    Thread.Sleep(10);
                    writeOk = IOAVServiceWriteI2C(
                        avService,
                        DdcSevenBitAddress,
                        DdcHostAddress,
                        (IntPtr)sendPtr,
                        (uint)send.Length) == 0;
                }

                if (!writeOk)
                    continue;

                if (!expectReply)
                    return true;

                Thread.Sleep(50);
                var readStatus = IOAVServiceReadI2C(
                    avService,
                    DdcSevenBitAddress,
                    DdcHostAddress,
                    (IntPtr)replyPtr,
                    (uint)reply.Length);

                if (readStatus == 0 && reply.Length > 0 && DdcChecksum(DdcReplyChecksumSeed, reply.AsSpan(0, reply.Length - 1)) == reply[^1])
                {
                    replyBytes = reply.Length;
                    return true;
                }

                Thread.Sleep(20);
            }
        }

        return false;
    }

    private static bool TrySendDdcRequest(
        IntPtr connect,
        byte[] send,
        byte[] reply,
        bool expectReply,
        out int replyBytes)
    {
        replyBytes = 0;
        fixed (byte* sendPtr = send)
        fixed (byte* replyPtr = reply)
        {
            var request = new IOI2CRequest
            {
                SendTransactionType = I2cSimpleTransaction,
                ReplyTransactionType = expectReply ? I2cDdcCiReplyTransaction : I2cNoTransaction,
                SendAddress = DdcDisplayWriteAddress,
                ReplyAddress = DdcDisplayReadAddress,
                SendBytes = (uint)send.Length,
                ReplyBytes = (uint)reply.Length,
                SendBuffer = (IntPtr)sendPtr,
                ReplyBuffer = expectReply && reply.Length > 0 ? (IntPtr)replyPtr : IntPtr.Zero,
            };

            var status = IOI2CSendRequest(connect, 0, ref request);
            if (status != 0 || request.Result != 0)
                return false;

            replyBytes = expectReply ? Math.Min((int)request.ReplyBytes, reply.Length) : 0;
            return !expectReply || replyBytes > 0;
        }
    }

    private static bool TryParseGetVcpReply(ReadOnlySpan<byte> reply, byte code, out uint current, out uint max)
    {
        current = 0;
        max = 0;
        for (var commandIndex = 0; commandIndex + 7 < reply.Length; commandIndex++)
        {
            if (reply[commandIndex] != 0x02 || reply[commandIndex + 2] != code)
                continue;

            var result = reply[commandIndex + 1];
            if (result != 0)
                return false;

            max = (uint)((reply[commandIndex + 4] << 8) | reply[commandIndex + 5]);
            current = (uint)((reply[commandIndex + 6] << 8) | reply[commandIndex + 7]);
            return true;
        }

        return false;
    }

    private static string TryGetDisplayProductName(uint framebuffer)
    {
        if (framebuffer == 0)
            return "";

        var info = IODisplayCreateInfoDictionary(framebuffer, IODisplayOnlyPreferredName);
        if (info == IntPtr.Zero)
            return "";

        try
        {
            var key = CreateCfString(DisplayProductNameKey);
            if (key == IntPtr.Zero)
                return "";

            try
            {
                var value = CFDictionaryGetValue(info, key);
                if (value == IntPtr.Zero)
                    return "";

                if (CFGetTypeID(value) == CFStringGetTypeID())
                    return CfStringToString(value);

                if (CFGetTypeID(value) == CFDictionaryGetTypeID())
                    return FirstStringValue(value);
            }
            finally
            {
                CFRelease(key);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-mac] display name lookup failed: {ex.Message}");
        }
        finally
        {
            CFRelease(info);
        }

        return "";
    }

    private static string FirstStringValue(IntPtr dictionary)
    {
        var count = (int)CFDictionaryGetCount(dictionary);
        if (count <= 0 || count > 32)
            return "";

        var keys = new IntPtr[count];
        var values = new IntPtr[count];
        CFDictionaryGetKeysAndValues(dictionary, keys, values);
        foreach (var value in values)
        {
            if (value != IntPtr.Zero && CFGetTypeID(value) == CFStringGetTypeID())
                return CfStringToString(value);
        }

        return "";
    }

    private static IntPtr CreateCfString(string value) =>
        CFStringCreateWithCString(IntPtr.Zero, value, CfStringEncodingUtf8);

    private static string CfStringToString(IntPtr value)
    {
        var buffer = new byte[512];
        if (!CFStringGetCString(value, buffer, buffer.Length, CfStringEncodingUtf8))
            return "";

        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
            length = buffer.Length;

        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static string BuildStableId(uint vendor, uint model, uint serial, int index)
    {
        if (vendor != 0 || model != 0 || serial != 0)
            return $"mac-{vendor:x4}-{model:x4}-{serial:x8}";

        return $"display-{index + 1}";
    }

    private static string DecodeEisaId(uint value)
    {
        if (value == 0)
            return "";

        var c1 = (char)(((value >> 10) & 0x1F) + '@');
        var c2 = (char)(((value >> 5) & 0x1F) + '@');
        var c3 = (char)((value & 0x1F) + '@');
        if (IsUpperAscii(c1) && IsUpperAscii(c2) && IsUpperAscii(c3))
            return new string(new[] { c1, c2, c3 });

        return value.ToString("X4");
    }

    private static bool IsEisaCode(string value) => value.Length == 3 && value.All(IsUpperAscii);

    private static bool IsUpperAscii(char value) => value >= 'A' && value <= 'Z';

    private static DisplayBrightnessControlDto BuildSupportedBrightnessControl(
        int current,
        string controlPath,
        string writeMode,
        int cooldownMs,
        bool verifyAfterWrite) => new()
    {
        Supported = true,
        Min = 0,
        Max = 100,
        Current = ClampPercent(current),
        ControlPath = controlPath,
        WriteMode = writeMode,
        WriteCooldownMs = cooldownMs,
        VerifyAfterWrite = verifyAfterWrite,
    };

    private static DisplayBrightnessDto AppliedBrightness(string id, int requested, int applied)
    {
        applied = ClampPercent(applied);
        return new DisplayBrightnessDto
        {
            Id = id,
            RequestedBrightness = requested,
            AppliedBrightness = applied,
            Brightness = applied,
            Status = DisplayBrightnessWriteStatuses.Applied,
        };
    }

    private static DisplayBrightnessDto UnsupportedBrightness(string id, int requested, string error) => new()
    {
        Id = id,
        RequestedBrightness = requested,
        AppliedBrightness = 0,
        Brightness = 0,
        Status = DisplayBrightnessWriteStatuses.Unsupported,
        Error = error,
    };

    private static DisplayBrightnessDto FailedBrightness(string id, int requested, string error) => new()
    {
        Id = id,
        RequestedBrightness = requested,
        AppliedBrightness = 0,
        Brightness = 0,
        Status = DisplayBrightnessWriteStatuses.Failed,
        Error = error,
    };

    private static int ClampPercent(int value) => value < 0 ? 0 : value > 100 ? 100 : value;

    private static int RawToPercent(uint current, uint max)
    {
        if (max == 0)
            return ClampPercent((int)current);

        return ClampPercent((int)Math.Round(current * 100.0 / max));
    }

    private static uint PercentToRaw(int percent, uint max)
    {
        if (max == 0)
            return (uint)ClampPercent(percent);

        return (uint)Math.Round(ClampPercent(percent) * max / 100.0);
    }

    private static byte DdcChecksum(ReadOnlySpan<byte> bytes) =>
        DdcChecksum(DdcDisplayWriteAddress, bytes);

    private static byte DdcChecksum(uint seed, ReadOnlySpan<byte> bytes)
    {
        byte checksum = (byte)seed;
        foreach (var value in bytes)
            checksum ^= value;
        return checksum;
    }

    internal readonly record struct MacDisplayHandle(
        string Id,
        uint DisplayId,
        uint Framebuffer,
        string Name,
        string Manufacturer,
        string Model,
        bool IsInternal,
        uint Vendor,
        uint ModelNumber,
        uint SerialNumber);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct IOI2CRequest
    {
        public uint SendTransactionType;
        public uint ReplyTransactionType;
        public uint SendAddress;
        public uint ReplyAddress;
        public byte SendSubAddress;
        public byte ReplySubAddress;
        public ushort ReservedA;
        public ulong MinReplyDelay;
        public int Result;
        public uint CommFlags;
        public uint PadA;
        public uint SendBytes;
        public uint ReservedB0;
        public uint ReservedB1;
        public uint PadB;
        public uint ReplyBytes;
        public IntPtr Completion;
        public IntPtr SendBuffer;
        public IntPtr ReplyBuffer;
        public uint ReservedC0;
        public uint ReservedC1;
        public uint ReservedC2;
        public uint ReservedC3;
        public uint ReservedC4;
        public uint ReservedC5;
        public uint ReservedC6;
        public uint ReservedC7;
        public uint ReservedC8;
        public uint ReservedC9;
    }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string DisplayServices = "/System/Library/PrivateFrameworks/DisplayServices.framework/DisplayServices";
    private const uint IODisplayOnlyPreferredName = 0x00000200;
    private const uint CfStringEncodingUtf8 = 0x08000100;
    private const string BrightnessParameterName = "brightness";
    private const string DisplayProductNameKey = "DisplayProductName";
    private const string DcpAvServiceProxyClass = "DCPAVServiceProxy";
    private const string LocationPropertyName = "Location";
    private const string ExternalLocationValue = "External";
    private const byte DdcHostAddress = 0x51;
    private const byte DdcReplyChecksumSeed = 0x50;
    private const uint DdcSevenBitAddress = 0x37;
    private const uint DdcDisplayWriteAddress = 0x6E;
    private const uint DdcDisplayReadAddress = 0x6F;
    private const uint I2cNoTransaction = 0;
    private const uint I2cSimpleTransaction = 1;
    private const uint I2cDdcCiReplyTransaction = 2;

    [DllImport(CoreGraphics)]
    private static extern int CGGetOnlineDisplayList(
        uint maxDisplays,
        [Out] uint[] onlineDisplays,
        out uint displayCount);

    [DllImport(CoreGraphics)]
    private static extern uint CGDisplayVendorNumber(uint display);

    [DllImport(CoreGraphics)]
    private static extern uint CGDisplayModelNumber(uint display);

    [DllImport(CoreGraphics)]
    private static extern uint CGDisplaySerialNumber(uint display);

    [DllImport(CoreGraphics)]
    private static extern int CGDisplayIsBuiltin(uint display);

    [DllImport(CoreGraphics)]
    private static extern uint CGDisplayIOServicePort(uint display);

    [DllImport(DisplayServices)]
    private static extern int DisplayServicesGetBrightness(uint display, out float brightness);

    [DllImport(DisplayServices)]
    private static extern int DisplayServicesSetBrightness(uint display, float brightness);

    [DllImport(IOKit)]
    private static extern IntPtr IODisplayCreateInfoDictionary(uint framebuffer, uint options);

    [DllImport(IOKit)]
    private static extern int IODisplayGetFloatParameter(
        uint service,
        uint options,
        IntPtr parameterName,
        out float value);

    [DllImport(IOKit)]
    private static extern int IODisplaySetFloatParameter(
        uint service,
        uint options,
        IntPtr parameterName,
        float value);

    [DllImport(IOKit)]
    private static extern int IODisplayCommitParameters(uint service, uint options);

    [DllImport(IOKit)]
    private static extern int IOFBGetI2CInterfaceCount(uint framebuffer, out uint count);

    [DllImport(IOKit)]
    private static extern int IOFBCopyI2CInterfaceForBus(uint framebuffer, uint bus, out uint i2cInterface);

    [DllImport(IOKit)]
    private static extern int IOI2CInterfaceOpen(uint i2cInterface, uint options, out IntPtr connect);

    [DllImport(IOKit)]
    private static extern int IOI2CInterfaceClose(IntPtr connect, uint options);

    [DllImport(IOKit)]
    private static extern int IOI2CSendRequest(IntPtr connect, uint options, ref IOI2CRequest request);

    [DllImport(IOKit)]
    private static extern int IOObjectRelease(uint ioObject);

    [DllImport(IOKit)]
    private static extern IntPtr IOServiceMatching([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(IOKit)]
    private static extern int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, out uint existing);

    [DllImport(IOKit)]
    private static extern uint IOIteratorNext(uint iterator);

    [DllImport(IOKit)]
    private static extern IntPtr IORegistryEntryCreateCFProperty(
        uint entry,
        IntPtr key,
        IntPtr allocator,
        uint options);

    [DllImport(IOKit)]
    private static extern IntPtr IOAVServiceCreateWithService(IntPtr allocator, uint service);

    [DllImport(IOKit)]
    private static extern int IOAVServiceCopyEDID(IntPtr service, out IntPtr edidData);

    [DllImport(IOKit)]
    private static extern int IOAVServiceReadI2C(
        IntPtr service,
        uint chipAddress,
        uint offset,
        IntPtr outputBuffer,
        uint outputBufferSize);

    [DllImport(IOKit)]
    private static extern int IOAVServiceWriteI2C(
        IntPtr service,
        uint chipAddress,
        uint dataAddress,
        IntPtr inputBuffer,
        uint inputBufferSize);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryGetCount(IntPtr dictionary);

    [DllImport(CoreFoundation)]
    private static extern void CFDictionaryGetKeysAndValues(
        IntPtr dictionary,
        [Out] IntPtr[] keys,
        [Out] IntPtr[] values);

    [DllImport(CoreFoundation)]
    private static extern ulong CFGetTypeID(IntPtr cf);

    [DllImport(CoreFoundation)]
    private static extern ulong CFStringGetTypeID();

    [DllImport(CoreFoundation)]
    private static extern ulong CFDictionaryGetTypeID();

    [DllImport(CoreFoundation)]
    private static extern ulong CFDataGetTypeID();

    [DllImport(CoreFoundation)]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(
        IntPtr value,
        [Out] byte[] buffer,
        nint bufferSize,
        uint encoding);
}
