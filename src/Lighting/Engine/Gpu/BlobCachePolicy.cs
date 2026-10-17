using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>One allow or deny entry of a directory's access list, reduced to what the policy reads.</summary>
internal readonly record struct AceInfo(string Sid, bool Allow, int Rights);

/// <summary>
/// Decides whether the GPU blob caches may touch the disk, and where. On
/// Windows they live under the install directory, which a non-admin cannot
/// write when it is under Program Files; the policy proves that from the
/// directory's access list instead of assuming it (a custom install location
/// often inherits user-create rights) and requires the LocalSystem service. Off
/// Windows the caches sit in the per-user data root, restricted to its owner.
/// The decision is made once and logged.
/// </summary>
internal static class BlobCachePolicy
{
    // Rights that let an account add, change or remove files, or change who can: write data, append data, write EA, write attributes, delete child, delete, write DAC, write owner, and the generic write and all bits an unmapped entry can carry.
    private const int WriteLikeRights = 0x2 | 0x4 | 0x10 | 0x100 | 0x40 | 0x10000 | 0x40000 | 0x80000 | 0x40000000 | 0x10000000;

    // Accounts that may hold write rights: SYSTEM, built-in Administrators, TrustedInstaller, and CREATOR OWNER (which only ever names whoever already owns the object).
    private static readonly HashSet<string> AdminSids = new(StringComparer.Ordinal)
    {
        "S-1-5-18",
        "S-1-5-32-544",
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
        "S-1-3-0",
    };

    private static readonly object Gate = new();
    private static bool? _decision;

    /// <summary>Root of both caches.</summary>
    public static string CacheRoot() => OperatingSystem.IsWindows()
        ? Path.Combine(AppContext.BaseDirectory, "cache")
        : Path.Combine(Nexus.Service.Persistence.NexusDataPaths.NexusRoot(), "cache");

    /// <summary>True when the caches may use the disk. Decided on first call.</summary>
    public static bool Allowed()
    {
        lock (Gate)
        {
            if (_decision is null)
            {
                _decision = Decide(out var why);
                GpuContext.Log($"[gpu] blob cache {(_decision.Value ? "on" : "off")}: {why} ({CacheRoot()})");
            }
            return _decision.Value;
        }
    }

    /// <summary>True when any allow entry grants write-like rights to an account outside the admin set. Pure over the list.</summary>
    internal static bool GrantsNonAdminWrite(IEnumerable<AceInfo> aces, out string? sid)
    {
        foreach (var a in aces)
        {
            if (a.Allow && (a.Rights & WriteLikeRights) != 0 && !AdminSids.Contains(a.Sid))
            {
                sid = a.Sid;
                return true;
            }
        }
        sid = null;
        return false;
    }

    /// <summary>True when <paramref name="ownerSid"/> is SYSTEM, Administrators or TrustedInstaller. An owner keeps implicit WRITE_DAC whatever the list says, so any other owner can re-grant itself write.</summary>
    internal static bool IsAdminOwner(string? ownerSid) =>
        ownerSid is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private static bool Decide(out string why)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return DecideUnix(out why);
            }
            if (!WindowsDirectorySecurity.IsLocalSystem())
            {
                why = "not the LocalSystem service";
                return false;
            }
            var root = CacheRoot();
            var install = Path.GetFullPath(AppContext.BaseDirectory);
            if (!Refuse(install, out why) || !Refuse(Directory.CreateDirectory(root).FullName, out why))
            {
                return false;
            }
            why = "install directory is admin-only";
            return true;
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return false;
        }
    }

    // Returns true when the directory is acceptable; otherwise sets why and returns false.
    private static bool Refuse(string dir, out string why)
    {
        why = "";
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }
        var info = new DirectoryInfo(dir);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            why = $"'{dir}' is a reparse point";
            return false;
        }
        var owner = (new DirectorySecurity(dir, AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        if (!IsAdminOwner(owner))
        {
            why = $"'{dir}' is owned by {owner ?? "an unreadable owner"}";
            return false;
        }
        var rules = new DirectorySecurity(dir, AccessControlSections.Access).GetAccessRules(true, true, typeof(SecurityIdentifier));
        var aces = new List<AceInfo>();
        foreach (FileSystemAccessRule r in rules)
        {
            aces.Add(new AceInfo(((SecurityIdentifier)r.IdentityReference).Value, r.AccessControlType == AccessControlType.Allow, (int)r.FileSystemRights));
        }
        if (GrantsNonAdminWrite(aces, out var sid))
        {
            why = $"'{dir}' grants write rights to {sid}";
            return false;
        }
        return true;
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static bool DecideUnix(out string why)
    {
        var root = CacheRoot();
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var mode = File.GetUnixFileMode(root);
        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            why = "cache directory is group or world writable";
            return false;
        }
        why = "cache directory restricted to its owner";
        return true;
    }
}
