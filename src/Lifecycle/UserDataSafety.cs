using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Guards for SYSTEM-side access to user-controlled folders under C:\Users: a
/// user can replace any folder they own with a junction, so a delete or read
/// that follows it would act on a target the user could not touch themselves.
/// </summary>
internal static class UserDataSafety
{
    /// <summary>True when the path exists and is itself a reparse point (junction or symlink); checked without following it.</summary>
    internal static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>True when the dir or any of its <paramref name="levels"/> ancestors is a reparse point.</summary>
    internal static bool ChainHasReparsePoint(string dir, int levels)
    {
        var current = dir;
        for (var i = 0; i <= levels && !string.IsNullOrEmpty(current); i++)
        {
            if (IsReparsePoint(current))
            {
                return true;
            }
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    /// <summary>True when no entry in the tree is a reparse point. Reparse points are never descended into. Any enumeration failure counts as unsafe.</summary>
    internal static bool TreeHasNoReparsePoint(string dir)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (IsReparsePoint(entry))
                {
                    return false;
                }
                if (Directory.Exists(entry) && !TreeHasNoReparsePoint(entry))
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// True when neither the per-user root, its ancestors up to and including the profile folder, nor
    /// anything inside is a reparse point. This refuses junctions that already exist; it does not stop one
    /// swapped in while a recursive delete is running (see <see cref="TryDeleteUserDir"/>).
    /// </summary>
    internal static bool IsSafeUserTree(string dir) => !ChainHasReparsePoint(dir, 3) && TreeHasNoReparsePoint(dir);

    /// <summary>
    /// Deletes a per-user folder only when <see cref="IsSafeUserTree"/> holds. No delayed-delete fallback: a locked
    /// leftover is logged and left. Never throws. Residual race: the check and the recursive delete are separate
    /// path operations, so a junction swapped in mid-delete (for example after the user holds an oplock on a file
    /// to stall the delete) is not stopped here; only junctions present at check time are refused. Closing it
    /// needs a handle-relative deleter.
    /// </summary>
    internal static bool TryDeleteUserDir(string dir, Action<string> log)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return true;
            }
            if (!IsSafeUserTree(dir))
            {
                log($"skipping {dir}: contains or sits behind a reparse point");
                return false;
            }
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            log($"could not fully delete {dir}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// True when the opened file's final resolved path (after any link) lies under <paramref name="expectedDir"/>.
    /// Closes the window between a path check and the open. Windows only; elsewhere the earlier check stands.
    /// </summary>
    internal static bool HandleIsUnder(SafeFileHandle handle, string expectedDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }
        var buffer = new char[1024];
        var len = GetFinalPathNameByHandleW(handle.DangerousGetHandle(), buffer, (uint)buffer.Length, 0);
        if (len == 0 || len >= buffer.Length)
        {
            return false;
        }
        var final = StripPrefix(new string(buffer, 0, (int)len));
        var expected = StripPrefix(Path.GetFullPath(expectedDir)).TrimEnd('\\') + "\\";
        return final.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripPrefix(string p) => p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr handle, [Out] char[] path, uint length, uint flags);
}
