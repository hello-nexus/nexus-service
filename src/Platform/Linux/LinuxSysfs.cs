using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Shared sysfs/file helpers for the Linux providers (serial discovery, hwmon
/// fan control, backlight brightness). Centralizes the read/parse/write
/// boilerplate so each provider doesn't re-roll it. Every method swallows IO
/// errors - a sysfs node can vanish or be permission-gated mid-access - and
/// returns null / false rather than throwing.
/// </summary>
internal static partial class LinuxSysfs
{
    /// <summary>Hand a file the root daemon wrote into a user's home to that user; false on failure.</summary>
    internal static bool TryChown(string path, uint uid, uint gid)
    {
        try { return OperatingSystem.IsLinux() && chown(path, uid, gid) == 0; }
        catch { return false; }
    }

    /// <summary>
    /// Opens the path, symlinks followed, for reading only if it is a regular file; null otherwise. The open is
    /// non-blocking and the type is checked on the open descriptor (statx, whose layout is the same on every
    /// architecture), so a FIFO swapped in after the path check cannot block the caller.
    /// </summary>
    internal static FileStream? OpenRegularFile(string path)
    {
        if (!OperatingSystem.IsLinux())
            return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null;
        // Path check first: opening a device node as root has side effects (arms a watchdog, resets a USB serial port).
        var pathInfo = new byte[StatxSize];
        if (statx(AtFdCwd, path, 0, StatxType, pathInfo) != 0 || !IsRegularMode(pathInfo))
            return null;
        var fd = open(path, ORdOnly | ONonBlock | OCloExec);
        if (fd < 0)
            return null;
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try
        {
            var buffer = new byte[StatxSize];
            if (statx(fd, "", AtEmptyPath, StatxType, buffer) != 0 || !IsRegularMode(buffer))
            {
                handle.Dispose();
                return null;
            }
            return new FileStream(handle, FileAccess.Read, 1, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            return null;
        }
    }

    // Linux generic values (x86, arm); statx(2) layout is fixed across architectures.
    private const int ORdOnly = 0;
    private const int ONonBlock = 0x800;
    private const int OCloExec = 0x80000;
    private const int AtEmptyPath = 0x1000;
    private const int AtFdCwd = -100;

    private static bool IsRegularMode(byte[] statxBuffer)
        => (BitConverter.ToUInt16(statxBuffer, StatxModeOffset) & SIfMt) == SIfReg;
    private const uint StatxType = 1;
    private const int StatxSize = 256;
    private const int StatxModeOffset = 28;
    private const int SIfMt = 0xF000;
    private const int SIfReg = 0x8000;

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int statx(int dirfd, string path, int flags, uint mask, byte[] buffer);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int chown(string path, uint owner, uint group);

    /// <summary>Read a sysfs attribute as trimmed text, or null if absent/unreadable.</summary>
    internal static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    /// <summary>Read a decimal integer attribute (e.g. pwmN, fanN_input, brightness), or null.</summary>
    internal static int? ReadInt(string path)
        => int.TryParse(ReadText(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Read a hex attribute written without a 0x prefix (e.g. idVendor=3402), or null.</summary>
    internal static int? ReadHex(string path)
        => int.TryParse(ReadText(path), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Write text to a sysfs attribute; returns false on any error (e.g. EACCES).</summary>
    internal static bool WriteText(string path, string value)
    {
        try { File.WriteAllText(path, value); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Resolve a sysfs symlink (e.g. <c>hwmonN/device</c>) to its target's leaf
    /// name - a reboot-stable, unique identifier such as <c>nct6775.656</c> or a
    /// PCI BDF, unlike the volatile <c>hwmonN</c> index. Returns null if it isn't
    /// a link or can't be read.
    /// </summary>
    internal static string? ReadLinkLeaf(string path)
    {
        try
        {
            var target = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            return target is null ? null : Path.GetFileName(target.FullName.TrimEnd('/', '\\'));
        }
        catch { return null; }
    }

    /// <summary>
    /// Stable, collision-free hwmon chip id fragment: the sanitized chip name,
    /// plus the <c>device</c> symlink leaf when present, so two chips sharing a
    /// <c>name</c> (e.g. dual nct6798) don't collide and one fan silently drive
    /// the other. Shared by the fan + sensor providers so both derive the same
    /// key. Falls back to name-only when there's no device link.
    /// </summary>
    internal static string ChipKey(string dir, string name)
    {
        var leaf = ReadLinkLeaf(Path.Combine(dir, "device"));
        return string.IsNullOrEmpty(leaf) ? Sanitize(name) : $"{Sanitize(name)}-{Sanitize(leaf)}";
    }

    /// <summary>Lowercase + non-alphanumerics → '-', for single-segment route-safe ids.</summary>
    internal static string Sanitize(string raw)
    {
        var chars = raw.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }
}
