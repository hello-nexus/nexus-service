using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

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
