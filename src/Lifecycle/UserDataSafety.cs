using System;
using System.IO;

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
    /// Deletes a per-user folder, only when neither it, its AppData ancestors nor anything inside is a
    /// reparse point. No delayed-delete fallback: a locked leftover is logged and left. Never throws.
    /// A swap between the check and the delete is not closed here; the recursive delete itself removes a
    /// junction rather than following it.
    /// </summary>
    internal static bool TryDeleteUserDir(string dir, Action<string> log)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return true;
            }
            if (ChainHasReparsePoint(dir, 2) || !TreeHasNoReparsePoint(dir))
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
}
