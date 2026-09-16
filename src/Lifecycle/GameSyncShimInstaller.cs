using System.IO;
using System.Runtime.InteropServices;
#if WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
#endif

using Nexus.Service.Platform;

namespace Nexus.Service.Lifecycle;

/// <summary>Result of a single-file deploy decision.</summary>
public enum ChromaShimFileDecision
{
    /// <summary>File is absent; copy from bundle.</summary>
    Copy,
    /// <summary>File is our shim at the same content; no-op.</summary>
    Skip,
    /// <summary>File has changed content; overwrite.</summary>
    Overwrite,
    /// <summary>File belongs to a real vendor install; do not touch.</summary>
    Conflict,
}

public enum ChromaShimInstallResult
{
    Installed,
    AlreadyCurrent,
    SynapseConflict,
    NotElevated,
    BundleMissing,
    Failed,
    NotApplicable,
}

public enum VendorOverrideResult
{
    /// <summary>Vendor DLLs set aside (or already were) and every slot now holds our shim.</summary>
    Applied,
    /// <summary>Every set-aside vendor DLL is back in its slot.</summary>
    Restored,
    NotElevated,
    BundleMissing,
    Failed,
    NotApplicable,
}

/// <summary>Snapshot of shim-install state exposed on the /lighting/game-sync/state endpoint.</summary>
public sealed class ChromaShimState
{
    /// <summary>All shim DLLs are present in System32/SysWOW64 and carry our marker.</summary>
    public bool ProviderInstalled { get; init; }

    /// <summary>A real vendor DLL was found in at least one shim slot; those slots were not overwritten.</summary>
    public bool SynapseConflict { get; init; }

    /// <summary>At least one vendor DLL is set aside as <c>*.nexus-bak</c> so our shim could take its slot.</summary>
    public bool VendorOverride { get; init; }
}

/// <summary>
/// Installs or removes the Nexus Game Sync shim DLLs (Razer Chroma,
/// Alienware LightFX, and Logitech LED) into System32 and SysWOW64.
///
/// Shim DLLs are produced by the nexus-gamesync component and staged
/// under Bundled/win-x64/gamesync/x64/ and x86/ before the service publish.
/// Ten files total:
///   x64: RzChromaSDK64.dll, RzChromatic64.dll, LightFX.dll,
///        LogitechLedEnginesWrapper.dll, LogitechLed.dll  -> System32
///   x86: RzChromaSDK.dll, RzChromatic.dll, LightFX.dll,
///        LogitechLedEnginesWrapper.dll, LogitechLed.dll  -> SysWOW64
///
/// Ownership check: all Nexus shims carry CompanyName "Nexus". A DLL with
/// any other non-empty CompanyName (e.g. "Razer Inc.", "Logitech") is not
/// ours and is never overwritten. Conflict is per-file: a real vendor DLL
/// in one slot does not block installing unrelated slots.
/// </summary>
public static class GameSyncShimInstaller
{
    internal const string OurCompanyName = "Nexus";

    // A vendor DLL the user chose to override is renamed in place, never
    // deleted, so turning the override off (or uninstalling) puts it back.
    internal const string VendorBackupSuffix = ".nexus-bak";

    // Source paths inside the publish output directory.
    private static string BundleX64Dir => Path.Combine(AppContext.BaseDirectory, "tools", "gamesync", "x64");
    private static string BundleX86Dir => Path.Combine(AppContext.BaseDirectory, "tools", "gamesync", "x86");

    // x64 pair -> System32
    internal static readonly string[] X64Names = { "RzChromaSDK64.dll", "RzChromatic64.dll", "LightFX.dll", "LogitechLedEnginesWrapper.dll", "LogitechLed.dll" };

    // x86 pair -> SysWOW64
    internal static readonly string[] X86Names = { "RzChromaSDK.dll", "RzChromatic.dll", "LightFX.dll", "LogitechLedEnginesWrapper.dll", "LogitechLed.dll" };

    /// <summary>
    /// Ensures all shim files are current in System32 and SysWOW64.
    /// Idempotent: skips files already at the same content. Skips individual
    /// files (with a conflict log) when a real vendor DLL occupies the slot.
    /// </summary>
    public static ChromaShimInstallResult EnsureInstalled()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ChromaShimInstallResult.NotApplicable;
        }

#if WINDOWS
        return DoEnsureInstalled();
#else
        return ChromaShimInstallResult.NotApplicable;
#endif
    }

    /// <summary>Removes shim DLLs from System32/SysWOW64 only when they carry our marker.</summary>
    /// <remarks>Not called from the uninstaller by design (see WindowsServiceInstaller.RunUninstall):
    /// the shims are left in place rather than risk deleting a same-named real vendor DLL. A caller
    /// must add a byte-match-to-bundle check before this is safe to wire into uninstall.</remarks>
    public static void RemoveIfOurs()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

#if WINDOWS
        DoRemoveIfOurs();
#endif
    }

    /// <summary>
    /// Enabled: moves every real vendor DLL occupying a shim slot to
    /// <c>&lt;name&gt;.nexus-bak</c> and installs our shims. Disabled: removes
    /// our shim from each slot that has a backup and moves the vendor DLL back.
    /// A game that already loaded the SDK keeps it until restarted.
    /// </summary>
    public static VendorOverrideResult SetVendorOverride(bool enabled)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return VendorOverrideResult.NotApplicable;
        }

#if WINDOWS
        return enabled ? DoTakeOverVendor() : DoRestoreVendor();
#else
        return VendorOverrideResult.NotApplicable;
#endif
    }

    /// <summary>Reads the current install state without modifying the filesystem.</summary>
    public static ChromaShimState GetState()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new ChromaShimState();
        }

#if WINDOWS
        return DoGetState();
#else
        return new ChromaShimState();
#endif
    }

    /// <summary>
    /// Pure deploy decision for a single target file given pre-read metadata.
    /// Extracted for unit testing; the caller resolves the actual file bytes
    /// and description rather than reading from disk.
    /// </summary>
    internal static ChromaShimFileDecision DecideFile(
        bool destExists,
        string destCompanyName,
        bool contentMatches)
    {
        if (!destExists)
        {
            return ChromaShimFileDecision.Copy;
        }

        // Non-empty CompanyName that is not ours: real vendor DLL.
        if (!string.IsNullOrEmpty(destCompanyName) &&
            !string.Equals(destCompanyName, OurCompanyName, StringComparison.OrdinalIgnoreCase))
        {
            return ChromaShimFileDecision.Conflict;
        }

        // Our shim or unsigned DLL. Skip if content matches.
        return contentMatches ? ChromaShimFileDecision.Skip : ChromaShimFileDecision.Overwrite;
    }

#if WINDOWS
    [SupportedOSPlatform("windows")]
    private static ChromaShimInstallResult DoEnsureInstalled()
    {
        if (!IsElevated())
        {
            ServiceLog.Warn("[chroma-shim] not elevated; skipping shim install (dev run)");
            return ChromaShimInstallResult.NotElevated;
        }

        if (!Directory.Exists(BundleX64Dir) || !Directory.Exists(BundleX86Dir))
        {
            ServiceLog.Info("[chroma-shim] bundled shim directory missing; Game Sync stays inactive");
            return ChromaShimInstallResult.BundleMissing;
        }

        var system32 = Environment.SystemDirectory;
        // On a 64-bit process SystemX86 is SysWOW64 (the 32-bit system dir).
        var sysWow64 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);

        if (!Directory.Exists(system32) || !Directory.Exists(sysWow64))
        {
            ServiceLog.Error("[chroma-shim] System32/SysWOW64 directories not found");
            return ChromaShimInstallResult.Failed;
        }

        bool anyUpdated = false;
        if (!CopyPair(BundleX64Dir, X64Names, system32, ref anyUpdated))
        {
            return ChromaShimInstallResult.Failed;
        }
        if (!CopyPair(BundleX86Dir, X86Names, sysWow64, ref anyUpdated))
        {
            return ChromaShimInstallResult.Failed;
        }

        return anyUpdated ? ChromaShimInstallResult.Installed : ChromaShimInstallResult.AlreadyCurrent;
    }

    [SupportedOSPlatform("windows")]
    private static bool CopyPair(string srcDir, string[] names, string destDir, ref bool anyUpdated)
    {
        foreach (var name in names)
        {
            var src = Path.Combine(srcDir, name);
            if (!File.Exists(src))
            {
                ServiceLog.Error($"[chroma-shim] bundled DLL not found: {src}");
                return false;
            }

            var dest = Path.Combine(destDir, name);
            bool destExists = File.Exists(dest);
            string destCompany = destExists ? ReadCompanyName(dest) : "";
            bool contentMatches = destExists && FileBytesEqual(src, dest);

            var decision = DecideFile(destExists, destCompany, contentMatches);
            switch (decision)
            {
                case ChromaShimFileDecision.Skip:
                    ServiceLog.Info($"[chroma-shim] already current: {name}");
                    continue;
                case ChromaShimFileDecision.Conflict:
                    ServiceLog.Warn($"[chroma-shim] real vendor DLL at {name}; skipping");
                    continue;
                case ChromaShimFileDecision.Copy:
                case ChromaShimFileDecision.Overwrite:
                    try
                    {
                        File.Copy(src, dest, overwrite: true);
                        ServiceLog.Info($"[chroma-shim] installed {name} -> {destDir}");
                        anyUpdated = true;
                    }
                    catch (Exception ex)
                    {
                        ServiceLog.Error($"[chroma-shim] failed to copy {name}: {ex.Message}");
                        return false;
                    }
                    break;
            }
        }
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static void DoRemoveIfOurs()
    {
        if (!IsElevated())
        {
            ServiceLog.Warn("[chroma-shim] not elevated; cannot remove shims");
            return;
        }

        var system32 = Environment.SystemDirectory;
        var sysWow64 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);

        RemovePairIfOurs(system32, X64Names);
        RemovePairIfOurs(sysWow64, X86Names);
    }

    [SupportedOSPlatform("windows")]
    private static void RemovePairIfOurs(string dir, string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path))
            {
                continue;
            }
            var company = ReadCompanyName(path);
            if (!string.Equals(company, OurCompanyName, StringComparison.OrdinalIgnoreCase))
            {
                ServiceLog.Warn($"[chroma-shim] skipping removal of {name}: not our shim");
                continue;
            }
            try
            {
                File.Delete(path);
                ServiceLog.Info($"[chroma-shim] removed {path}");
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[chroma-shim] failed to remove {name}: {ex.Message}");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static VendorOverrideResult DoTakeOverVendor()
    {
        if (!IsElevated())
        {
            ServiceLog.Warn("[chroma-shim] not elevated; cannot override vendor SDK");
            return VendorOverrideResult.NotElevated;
        }

        var system32 = Environment.SystemDirectory;
        var sysWow64 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        if (!SetAsideVendorPair(system32, X64Names) || !SetAsideVendorPair(sysWow64, X86Names))
        {
            return VendorOverrideResult.Failed;
        }

        return DoEnsureInstalled() switch
        {
            ChromaShimInstallResult.Installed or ChromaShimInstallResult.AlreadyCurrent => VendorOverrideResult.Applied,
            ChromaShimInstallResult.BundleMissing => VendorOverrideResult.BundleMissing,
            _ => VendorOverrideResult.Failed,
        };
    }

    // Renaming a mapped image is allowed on Windows (deleting one is not), so
    // this works while a game still has the vendor DLL loaded.
    [SupportedOSPlatform("windows")]
    private static bool SetAsideVendorPair(string dir, string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path))
            {
                continue;
            }
            var company = ReadCompanyName(path);
            if (company.Length == 0 || string.Equals(company, OurCompanyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                File.Move(path, path + VendorBackupSuffix, overwrite: true);
                ServiceLog.Info($"[chroma-shim] set aside vendor {name} ({company}) in {dir}");
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[chroma-shim] failed to set aside {name}: {ex.Message}");
                return false;
            }
        }
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static VendorOverrideResult DoRestoreVendor()
    {
        if (!IsElevated())
        {
            ServiceLog.Warn("[chroma-shim] not elevated; cannot restore vendor SDK");
            return VendorOverrideResult.NotElevated;
        }

        var system32 = Environment.SystemDirectory;
        var sysWow64 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        var ok = RestoreVendorPair(system32, X64Names);
        ok &= RestoreVendorPair(sysWow64, X86Names);
        return ok ? VendorOverrideResult.Restored : VendorOverrideResult.Failed;
    }

    // Only a slot with a backup beside it is touched, and the file in the slot
    // is removed only when it carries our marker: a vendor DLL that Synapse
    // repaired back into place is kept and the stale backup dropped.
    [SupportedOSPlatform("windows")]
    private static bool RestoreVendorPair(string dir, string[] names)
    {
        var ok = true;
        foreach (var name in names)
        {
            var path = Path.Combine(dir, name);
            var backup = path + VendorBackupSuffix;
            if (!File.Exists(backup))
            {
                continue;
            }
            try
            {
                if (File.Exists(path))
                {
                    if (!string.Equals(ReadCompanyName(path), OurCompanyName, StringComparison.OrdinalIgnoreCase))
                    {
                        ServiceLog.Info($"[chroma-shim] vendor {name} already back in {dir}; dropping stale backup");
                        File.Delete(backup);
                        continue;
                    }
                    RemoveOurShim(path);
                }
                File.Move(backup, path);
                ServiceLog.Info($"[chroma-shim] restored vendor {name} in {dir}");
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[chroma-shim] failed to restore {name}: {ex.Message}");
                ok = false;
            }
        }
        return ok;
    }

    // A shim a running game still maps cannot be deleted; move it out of the
    // slot instead so the vendor DLL can take the name back now.
    [SupportedOSPlatform("windows")]
    private static void RemoveOurShim(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            var stale = path + ".nexus-stale";
            File.Move(path, stale, overwrite: true);
            ServiceLog.Warn($"[chroma-shim] shim in use; moved to {stale}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static ChromaShimState DoGetState()
    {
        var system32 = Environment.SystemDirectory;
        var sysWow64 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);

        bool overridden = HasBackupInDir(system32, X64Names) || HasBackupInDir(sysWow64, X86Names);

        if (HasConflictInDir(system32, X64Names) || HasConflictInDir(sysWow64, X86Names))
        {
            return new ChromaShimState { SynapseConflict = true, VendorOverride = overridden };
        }

        bool allInstalled =
            AllOurShims(system32, X64Names) &&
            AllOurShims(sysWow64, X86Names);

        return new ChromaShimState { ProviderInstalled = allInstalled, VendorOverride = overridden };
    }

    private static bool HasBackupInDir(string dir, string[] names)
    {
        foreach (var name in names)
        {
            if (File.Exists(Path.Combine(dir, name + VendorBackupSuffix)))
            {
                return true;
            }
        }
        return false;
    }

    // True when any named DLL in dir has a non-empty CompanyName that is not ours.
    [SupportedOSPlatform("windows")]
    private static bool HasConflictInDir(string dir, string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path))
            {
                continue;
            }
            var company = ReadCompanyName(path);
            if (company.Length > 0 &&
                !string.Equals(company, OurCompanyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool AllOurShims(string dir, string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path))
            {
                return false;
            }
            var company = ReadCompanyName(path);
            if (!string.Equals(company, OurCompanyName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static string ReadCompanyName(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).CompanyName ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool FileBytesEqual(string a, string b)
    {
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        if (infoA.Length != infoB.Length)
        {
            return false;
        }

        const int BufSize = 65536;
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        var bufA = new byte[BufSize];
        var bufB = new byte[BufSize];
        while (true)
        {
            int na = ReadFull(fa, bufA);
            int nb = ReadFull(fb, bufB);
            if (na != nb)
            {
                return false;
            }
            if (na == 0)
            {
                return true;
            }
            for (int i = 0; i < na; i++)
            {
                if (bufA[i] != bufB[i])
                {
                    return false;
                }
            }
        }
    }

    private static int ReadFull(Stream s, byte[] buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = s.Read(buf, total, buf.Length - total);
            if (n == 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
#endif
}
