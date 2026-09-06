using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Service.Models.Displays;
using Nexus.Service.Platform.Linux;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Linux display brightness via two native paths, no external tools:
/// internal/laptop panels through the kernel backlight sysfs
/// (<c>/sys/class/backlight/*/{brightness,max_brightness}</c>), and external
/// monitors through DDC/CI over i2c-dev (<c>/dev/i2c-*</c>, slave 0x37,
/// VCP 0x10) - the same MCCS protocol the Windows/macOS providers speak, just
/// over the Linux i2c char device. AOT-safe: sysfs file IO + blittable libc
/// P/Invoke. Needs read access to <c>/dev/i2c-*</c> (i2c group) for external
/// monitors; backlight write needs access to the backlight sysfs node (udev).
/// </summary>
public sealed partial class LinuxDisplayBrightnessProvider : IDisplayBrightnessProvider
{
    private const string BacklightRoot = "/sys/class/backlight";
    private const byte DdcAddress = 0x37;
    private const byte EdidAddress = 0x50;
    private const byte VcpBrightness = 0x10;

    // VESA DDC/CI mandates a minimum ~40ms inter-message delay; we use 50ms for
    // margin (before reading a reply, and after a write before the next command).
    // These are protocol timing requirements, not arbitrary waits.
    private static readonly TimeSpan DdcReadDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DdcWriteSettle = TimeSpan.FromMilliseconds(50);
    private const int DdcReadAttempts = 3;

    private readonly object _gate = new();
    private readonly Dictionary<string, Target> _targets = new();

    private sealed record Target(bool Internal, string? BacklightDir, int I2cBus, DisplayDto Dto, string PnpId = "");

    public string Hint { get; private set; } = "";

    public IReadOnlyList<DisplayDto> Enumerate()
    {
        if (!OperatingSystem.IsLinux())
            return Array.Empty<DisplayDto>();

        var dtos = new List<DisplayDto>();
        lock (_gate)
        {
            _targets.Clear();
            foreach (var t in EnumerateBacklights().Concat(EnumerateDdc()))
            {
                _targets[t.Dto.Id] = t;
                dtos.Add(t.Dto);
            }
            Hint = dtos.Count == 0
                ? "No backlight panel and no DDC/CI display found (load i2c-dev; ensure /dev/i2c-* is readable)."
                : "";
        }
        return dtos;
    }

    public int? GetBrightness(string id)
    {
        var t = Resolve(id);
        if (t is null)
            return null;
        return t.Internal ? ReadBacklightPercent(t.BacklightDir!) : DdcBrightnessPct(t.I2cBus);
    }

    public DisplayBrightnessDto SetBrightness(string id, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        var t = Resolve(id);
        if (t is null)
            return Failed(id, percent, "Unknown display.");

        var ok = t.Internal ? WriteBacklightPercent(t.BacklightDir!, percent) : WriteDdcVcp(t.I2cBus, VcpBrightness, percent);
        if (!ok)
            return Failed(id, percent, "Write failed (check i2c/backlight permissions).");

        var applied = (t.Internal ? ReadBacklightPercent(t.BacklightDir!) : DdcBrightnessPct(t.I2cBus)) ?? percent;
        return new DisplayBrightnessDto
        {
            Id = id,
            RequestedBrightness = percent,
            AppliedBrightness = applied,
            Brightness = applied,
            Status = DisplayBrightnessWriteStatuses.Applied,
        };
    }

    public DisplayBrightnessWritePolicy GetBrightnessWritePolicy(string id)
    {
        var t = Resolve(id);
        if (t is null)
            return new DisplayBrightnessWritePolicy();
        if (t.Internal)
        {
            return new DisplayBrightnessWritePolicy
            {
                ControlPath = DisplayBrightnessControlPaths.LinuxBacklight,
                WriteMode = DisplayBrightnessWriteModes.Immediate,
            };
        }
        // DDC writes are slow (~50ms round trip) - coalesce slider drags.
        return new DisplayBrightnessWritePolicy
        {
            ControlPath = DisplayBrightnessControlPaths.DdcCi,
            WriteMode = DisplayBrightnessWriteModes.Coalesced,
            MinWriteIntervalMs = 50,
            ReadAfterWriteDelayMs = 50,
        };
    }

    public DisplayVcpDto? GetVcp(string id, byte code)
    {
        var t = Resolve(id);
        if (t is null || t.Internal)
            return null;
        var r = ReadDdcVcp(t.I2cBus, code);
        return r is null ? null : new DisplayVcpDto { Id = id, Code = code, Value = r.Value.Current, MaxValue = r.Value.Max };
    }

    public bool SetVcp(string id, byte code, int value)
    {
        var t = Resolve(id);
        if (t is null || t.Internal)
            return false;
        return WriteDdcVcpRaw(t.I2cBus, code, value);
    }

    public string? FindDisplayIdByHardwareName(IReadOnlyList<string> nameFragments)
    {
        if (nameFragments is null || nameFragments.Count == 0)
            return null;
        foreach (var dto in Enumerate())
        {
            string pnpId;
            lock (_gate)
                pnpId = _targets.TryGetValue(dto.Id, out var t) ? t.PnpId : "";
            var haystack = $"{dto.Name} {dto.Manufacturer} {dto.Model} {dto.Id} {pnpId}";
            if (nameFragments.Any(f => !string.IsNullOrEmpty(f) && haystack.Contains(f, StringComparison.OrdinalIgnoreCase)))
                return dto.Id;
        }
        return null;
    }

    private Target? Resolve(string id)
    {
        lock (_gate)
        {
            if (_targets.TryGetValue(id, out var t))
                return t;
        }
        Enumerate();
        lock (_gate)
        {
            return _targets.TryGetValue(id, out var t2) ? t2 : null;
        }
    }

    // ── Internal backlight (sysfs) ──

    private static IEnumerable<Target> EnumerateBacklights()
    {
        string[] dirs;
        try { dirs = Directory.GetDirectories(BacklightRoot); }
        catch { yield break; }

        foreach (var dir in dirs)
        {
            var max = LinuxSysfs.ReadInt(Path.Combine(dir, "max_brightness"));
            if (max is null or 0)
                continue;
            var name = Path.GetFileName(dir);
            var pct = ReadBacklightPercent(dir);
            var dto = new DisplayDto
            {
                Id = $"linux-backlight-{name}",
                Name = "Internal Display",
                Manufacturer = "",
                Model = name,
                IsInternal = true,
                IsDdcCapable = false,
                Capabilities = new DisplayCapabilitiesDto { Brightness = true },
                BrightnessControl = new DisplayBrightnessControlDto
                {
                    Supported = true,
                    Current = pct,
                    ControlPath = DisplayBrightnessControlPaths.LinuxBacklight,
                    WriteMode = DisplayBrightnessWriteModes.Immediate,
                },
            };
            yield return new Target(true, dir, -1, dto);
        }
    }

    internal static int? ReadBacklightPercent(string dir)
    {
        var max = LinuxSysfs.ReadInt(Path.Combine(dir, "max_brightness"));
        var cur = LinuxSysfs.ReadInt(Path.Combine(dir, "actual_brightness")) ?? LinuxSysfs.ReadInt(Path.Combine(dir, "brightness"));
        if (max is null or 0 || cur is null)
            return null;
        return (int)Math.Round(Math.Clamp(cur.Value, 0, max.Value) * 100.0 / max.Value);
    }

    internal static bool WriteBacklightPercent(string dir, int percent)
    {
        var max = LinuxSysfs.ReadInt(Path.Combine(dir, "max_brightness"));
        if (max is null or 0)
            return false;
        var raw = (int)Math.Round(Math.Clamp(percent, 0, 100) * max.Value / 100.0);
        return LinuxSysfs.WriteText(Path.Combine(dir, "brightness"), raw.ToString(CultureInfo.InvariantCulture));
    }

    // ── External monitors (DDC/CI over i2c-dev) ──

    private static IEnumerable<Target> EnumerateDdc()
    {
        string[] buses;
        try { buses = Directory.GetFiles("/dev", "i2c-*"); }
        catch { yield break; }

        foreach (var busPath in buses)
        {
            var n = busPath.AsSpan("/dev/i2c-".Length);
            if (!int.TryParse(n, out var bus))
                continue;
            // A bus is a DDC/CI monitor only if it answers a VCP 0x10 read.
            if (ReadDdcVcp(bus, VcpBrightness) is null)
                continue;

            var (mfg, model, pnpId) = ReadEdidIdentity(bus);
            var dto = new DisplayDto
            {
                Id = $"linux-ddc-i2c-{bus}",
                Name = string.IsNullOrEmpty(model) ? $"External Display (i2c-{bus})" : model,
                Manufacturer = mfg,
                Model = model,
                IsInternal = false,
                IsDdcCapable = true,
                Capabilities = new DisplayCapabilitiesDto { Brightness = true },
                BrightnessControl = new DisplayBrightnessControlDto
                {
                    Supported = true,
                    Current = DdcBrightnessPct(bus),
                    ControlPath = DisplayBrightnessControlPaths.DdcCi,
                    WriteMode = DisplayBrightnessWriteModes.Coalesced,
                    WriteCooldownMs = 50,
                },
            };
            yield return new Target(false, null, bus, dto, pnpId);
        }
    }

    /// <summary>Brightness (VCP 0x10) as 0..100, scaling the raw current by the monitor's reported max.</summary>
    private static int? DdcBrightnessPct(int bus)
    {
        var r = ReadDdcVcp(bus, VcpBrightness);
        if (r is null)
            return null;
        var max = Math.Max(1, r.Value.Max);
        return (int)Math.Round(Math.Clamp(r.Value.Current, 0, max) * 100.0 / max);
    }

    private static bool WriteDdcVcp(int bus, byte code, int percent)
        => WriteDdcVcpRaw(bus, code, (int)Math.Round(Math.Clamp(percent, 0, 100) * (ReadDdcVcp(bus, code)?.Max ?? 100) / 100.0));

    private static (int Current, int Max)? ReadDdcVcp(int bus, byte code)
    {
        var fd = OpenI2c(bus);
        if (fd < 0)
            return null;
        try
        {
            if (ioctl(fd, I2C_SLAVE, DdcAddress) < 0)
                return null;

            // Get VCP feature request: [0x51, 0x82, 0x01, code, checksum]
            Span<byte> req = stackalloc byte[] { 0x51, 0x82, 0x01, code, 0 };
            req[4] = (byte)(0x6E ^ req[0] ^ req[1] ^ req[2] ^ req[3]);
            Span<byte> reply = stackalloc byte[11];

            // DDC/CI over i2c is noisy: a busy bus or back-to-back exchanges can
            // return a short/garbled reply (confirmed on a Dell U2415 - rapid
            // reads intermittently NAK). Retry the request a few times, spaced.
            for (var attempt = 0; attempt < DdcReadAttempts; attempt++)
            {
                if (attempt > 0)
                    Thread.Sleep(DdcReadDelay);
                if (!I2cWrite(fd, req))
                    continue;
                Thread.Sleep(DdcReadDelay);
                if (!I2cRead(fd, reply))
                    continue;
                // reply: [0x6E, 0x88, 0x02, result, code, type, maxHi, maxLo, curHi, curLo, csum]
                if (reply[2] != 0x02 || reply[3] != 0x00)
                    continue;
                var max = (reply[6] << 8) | reply[7];
                var cur = (reply[8] << 8) | reply[9];
                if (max <= 0)
                    continue;
                return (Math.Clamp(cur, 0, max), max); // raw VCP current + max
            }
            return null;
        }
        catch { return null; }
        finally { close(fd); }
    }

    private static bool WriteDdcVcpRaw(int bus, byte code, int value)
    {
        var fd = OpenI2c(bus);
        if (fd < 0)
            return false;
        try
        {
            if (ioctl(fd, I2C_SLAVE, DdcAddress) < 0)
                return false;
            var hi = (byte)((value >> 8) & 0xFF);
            var lo = (byte)(value & 0xFF);
            // Set VCP feature: [0x51, 0x84, 0x03, code, hi, lo, checksum]
            Span<byte> msg = stackalloc byte[] { 0x51, 0x84, 0x03, code, hi, lo, 0 };
            msg[6] = (byte)(0x6E ^ msg[0] ^ msg[1] ^ msg[2] ^ msg[3] ^ msg[4] ^ msg[5]);
            var ok = I2cWrite(fd, msg);
            Thread.Sleep(DdcWriteSettle);
            return ok;
        }
        catch { return false; }
        finally { close(fd); }
    }

    private static (string Mfg, string Model, string PnpId) ReadEdidIdentity(int bus)
    {
        var fd = OpenI2c(bus);
        if (fd < 0)
            return ("", "", "");
        try
        {
            if (ioctl(fd, I2C_SLAVE, EdidAddress) < 0)
                return ("", "", "");
            Span<byte> off = stackalloc byte[] { 0x00 };
            if (!I2cWrite(fd, off))
                return ("", "", "");
            Span<byte> edid = stackalloc byte[128];
            if (!I2cRead(fd, edid))
                return ("", "", "");
            var (mfg, model) = DecodeEdid(edid);
            return (mfg, model, DecodePnpId(edid));
        }
        catch { return ("", "", ""); }
        finally { close(fd); }
    }

    /// <summary>
    /// PNP id (<c>RTK0004</c>): manufacturer plus the EDID product code at bytes
    /// 10-11 LE. The Y70 panel list keys on this form, and the 0xFC model-name
    /// descriptor <see cref="DecodeEdid"/> returns ("HYTE Y70ti") never contains it.
    /// </summary>
    internal static string DecodePnpId(ReadOnlySpan<byte> edid)
    {
        var (mfg, _) = DecodeEdid(edid);
        if (string.IsNullOrEmpty(mfg))
            return "";
        var product = (ushort)(edid[10] | (edid[11] << 8));
        return mfg + product.ToString("X4");
    }

    /// <summary>Decode manufacturer (PNP ID) + model name (descriptor 0xFC) from a 128-byte EDID block.</summary>
    internal static (string Mfg, string Model) DecodeEdid(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 128 || edid[0] != 0x00 || edid[1] != 0xFF)
            return ("", "");

        var id = (edid[8] << 8) | edid[9];
        char Letter(int v) => (char)('A' + v - 1);
        var c1 = Letter((id >> 10) & 0x1F);
        var c2 = Letter((id >> 5) & 0x1F);
        var c3 = Letter(id & 0x1F);
        // Invalid/blank PNP IDs decode to non-letters - don't ship garbage.
        var mfg = c1 is >= 'A' and <= 'Z' && c2 is >= 'A' and <= 'Z' && c3 is >= 'A' and <= 'Z'
            ? new string(new[] { c1, c2, c3 })
            : "";

        var model = "";
        for (var d = 54; d <= 108; d += 18)
        {
            if (edid[d] == 0 && edid[d + 1] == 0 && edid[d + 3] == 0xFC)
            {
                var raw = edid.Slice(d + 5, 13);
                var end = raw.IndexOf((byte)0x0A);
                model = System.Text.Encoding.ASCII.GetString(end >= 0 ? raw[..end] : raw).Trim();
                break;
            }
        }
        return (mfg, model);
    }

    private static int OpenI2c(int bus)
    {
        try { return open($"/dev/i2c-{bus}", O_RDWR | O_CLOEXEC); }
        catch { return -1; }
    }

    private static unsafe bool I2cWrite(int fd, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
            return write(fd, (nint)p, (nuint)data.Length) == data.Length;
    }

    private static unsafe bool I2cRead(int fd, Span<byte> buffer)
    {
        fixed (byte* p = buffer)
            return read(fd, (nint)p, (nuint)buffer.Length) == buffer.Length;
    }

    private static DisplayBrightnessDto Failed(string id, int percent, string error) => new()
    {
        Id = id,
        RequestedBrightness = percent,
        AppliedBrightness = 0,
        Brightness = 0,
        Status = DisplayBrightnessWriteStatuses.Failed,
        Error = error,
    };

    private const int O_RDWR = 2;
    private const int O_CLOEXEC = 0x80000;
    private const nuint I2C_SLAVE = 0x0703;

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string pathname, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static partial int ioctl(int fd, nuint request, int arg);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "read")]
    private static partial nint read(int fd, nint buf, nuint count);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "write")]
    private static partial nint write(int fd, nint buf, nuint count);
}
