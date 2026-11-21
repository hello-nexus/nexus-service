using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Gallery;

namespace Nexus.Service.Gallery;

public sealed class GalleryImportRequest
{
    /// <summary>The path as the user's session sees it, e.g. a mapped drive.</summary>
    public string Path { get; set; } = "";
    /// <summary>file, folder or auto (the copier stats the path).</summary>
    public string Kind { get; set; } = "";
    /// <summary>Existing empty folder the copy lands in, created by the service.</summary>
    public string DestDir { get; set; } = "";
}

public sealed class GalleryImportResult
{
    /// <summary>Resolved kind: file or folder.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Full path of the copied file or folder inside DestDir.</summary>
    public string CopiedPath { get; set; } = "";
    public string? Error { get; set; }
    /// <summary><see cref="GalleryErrorCodes"/> value when Error is set.</summary>
    public string Code { get; set; } = "";
}

/// <summary>
/// Copies a file or folder the service cannot see into gallery storage. On
/// Windows the service is LocalSystem, so a mapped network drive (R:) only
/// exists in the user's session and the copy runs in the user-session helper.
/// </summary>
public interface IGalleryImporter
{
    Task<GalleryImportResult> ImportAsync(GalleryImportRequest request, CancellationToken ct);
}

public sealed class UnsupportedGalleryImporter : IGalleryImporter
{
    public Task<GalleryImportResult> ImportAsync(GalleryImportRequest request, CancellationToken ct) =>
        Task.FromResult(new GalleryImportResult { Error = "file not found", Code = GalleryErrorCodes.Unreachable });
}

/// <summary>The copy itself; runs in whichever process can read the source.</summary>
public static class GalleryImportCopier
{
    // Imports are copies on the system drive, so a whole NAS share must not fill it.
    public const long MaxImportBytes = 2L * 1024 * 1024 * 1024;

    private static readonly EnumerationOptions FolderScanOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MaxRecursionDepth = 8,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
    };

    public static GalleryImportResult Copy(GalleryImportRequest request, CancellationToken ct = default, long maxBytes = MaxImportBytes)
    {
        string source;
        try
        {
            source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Path));
        }
        catch
        {
            return Fail("invalid path", "");
        }

        if (!Directory.Exists(request.DestDir))
            return Fail("import folder missing", GalleryErrorCodes.ImportFailed);

        var isFolder = request.Kind switch
        {
            GallerySourceKinds.Folder => true,
            GallerySourceKinds.File => false,
            _ => Directory.Exists(source),
        };
        var name = Path.GetFileName(source);
        if (string.IsNullOrEmpty(name))
            return Fail("cannot import a whole drive", GalleryErrorCodes.ImportFailed);
        var dest = Path.Combine(request.DestDir, name);

        try
        {
            if (!isFolder)
            {
                if (!File.Exists(source)) return Fail("file not found", "");
                if (!GalleryLibrary.IsMediaFile(source)) return Fail("unsupported media format", "");
                if (new FileInfo(source).Length > maxBytes) return Fail("file too large to copy", GalleryErrorCodes.TooLarge);
                File.Copy(source, dest);
                return new GalleryImportResult { Kind = GallerySourceKinds.File, CopiedPath = dest };
            }

            if (!Directory.Exists(source)) return Fail("folder not found", "");
            var files = Directory.EnumerateFiles(source, "*", FolderScanOptions);
            long total = 0;
            var count = 0;
            var plan = new System.Collections.Generic.List<(string From, string To)>();
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!GalleryLibrary.IsMediaFile(file)) continue;
                if (count >= GalleryLibrary.MaxItems) break;
                total += new FileInfo(file).Length;
                if (total > maxBytes) return Fail("folder too large to copy", GalleryErrorCodes.TooLarge);
                plan.Add((file, Path.Combine(dest, Path.GetRelativePath(source, file))));
                count++;
            }

            Directory.CreateDirectory(dest);
            foreach (var (from, to) in plan)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to);
            }

            return new GalleryImportResult { Kind = GallerySourceKinds.Folder, CopiedPath = dest };
        }
        catch (OperationCanceledException)
        {
            return Fail("import cancelled", GalleryErrorCodes.ImportFailed);
        }
        catch (Exception ex)
        {
            return Fail($"copy failed: {ex.Message}", GalleryErrorCodes.ImportFailed);
        }
    }

    private static GalleryImportResult Fail(string msg, string code) => new() { Error = msg, Code = code };
}
