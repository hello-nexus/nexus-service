using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
#if LINUX
using Nexus.Service.Platform.Linux;
#endif

namespace Nexus.Service.Platform;

/// <summary>
/// Which OS-native dialog <see cref="IFileDialogPicker.PickAsync"/> shows.
/// <see cref="Folder"/> picks a single directory (gallery + deck Browse);
/// <see cref="AnyFileSingle"/> picks one file with no type filter (deck
/// Browse); <see cref="MediaMultiSelect"/> picks one or more image or video
/// files (gallery only). The member order is the helper wire format (the
/// enum crosses as its integer), so only ever append.
/// </summary>
public enum FileDialogPickMode
{
    MediaMultiSelect,
    AnyFileSingle,
    Folder,
}

public sealed class FileDialogPickResult
{
    public List<string> Paths { get; set; } = new();
    public bool Cancelled { get; set; }
    public bool Error { get; set; }
    public string Msg { get; set; } = "";
}

public interface IFileDialogPicker
{
    Task<FileDialogPickResult> PickAsync(FileDialogPickMode mode, CancellationToken ct);
}

/// <summary>
/// Opens the OS-native file/folder picker on the host PC and returns the
/// chosen absolute path(s). The dialog always appears on the host machine -
/// browsers never expose real filesystem paths, so a remote dashboard
/// triggering this sees the dialog open on the PC. One dialog at a time.
///
/// Windows: the service is Session-0 LocalSystem and cannot show UI, so the
/// dialog runs in the user-session helper (IFileOpenDialog via the
/// dialog.pick helper command). macOS: the service runs as the user inside
/// Nexus.app, so osascript's `choose file`/`choose folder` works directly.
/// Linux: the root daemon spawns zenity/kdialog inside the user's session
/// via the setpriv wrapper (same trick as the screencast helper).
///
/// Shared across the gallery (image+video multiselect / folder) and the deck
/// action openFile/openFolder Browse buttons (any-file single-select /
/// folder).
/// </summary>
public sealed class FileDialogPicker : IFileDialogPicker
{
    private const int DialogTimeoutMs = 600_000;

    private readonly IServiceProvider _services;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileDialogPicker(IServiceProvider services)
    {
        _services = services;
    }

    public async Task<FileDialogPickResult> PickAsync(FileDialogPickMode mode, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            return Fail("a file dialog is already open on the PC");
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return await PickWindowsAsync(mode, ct);
            }

            if (OperatingSystem.IsMacOS())
            {
                return await PickMacAsync(mode, ct);
            }

            if (OperatingSystem.IsLinux())
            {
                return await PickLinuxAsync(mode, ct);
            }

            return Fail("native dialogs are not supported on this platform");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FileDialogPickResult> PickWindowsAsync(FileDialogPickMode mode, CancellationToken ct)
    {
#if WINDOWS
        var registry = _services.GetService<Helper.HelperRegistry>();
        if (registry is null)
        {
            return Fail("user-session helper unavailable");
        }

        var result = await Helper.Domains.FileDialogCommands.PickAsync(registry, mode, ct).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return Fail(result.Error);
        }

        return result.Cancelled
            ? new FileDialogPickResult { Cancelled = true }
            : new FileDialogPickResult { Paths = result.Paths };
#else
        await Task.CompletedTask;
        return Fail("not built for windows");
#endif
    }

    private static async Task<FileDialogPickResult> PickMacAsync(FileDialogPickMode mode, CancellationToken ct)
    {
        // osascript exits non-zero on user cancel ("User canceled. (-128)");
        // ShellExecutor surfaces that as empty stdout, which maps to Cancelled.
        // "tell me to activate" fronts the chooser - without it the dialog can
        // open behind the Nexus window.
        var script = mode switch
        {
            FileDialogPickMode.Folder =>
                "tell me to activate\n"
                + "return POSIX path of (choose folder with prompt \"Choose a folder\")",
            FileDialogPickMode.AnyFileSingle =>
                "tell me to activate\n"
                + "return POSIX path of (choose file with prompt \"Choose a file\")",
            _ =>
                "tell me to activate\n"
                + "set out to \"\"\n"
                + "repeat with f in (choose file with prompt \"Add images or videos to the Nexus gallery\" of type {\"public.image\", \"public.movie\"} with multiple selections allowed)\n"
                + "set out to out & POSIX path of f & \"\\n\"\n"
                + "end repeat\n"
                + "return out",
        };
        var stdout = await ShellExecutor.RunAsync("/usr/bin/osascript", DialogTimeoutMs, ct, "-e", script).ConfigureAwait(false);
        return FromLines(stdout);
    }

#if LINUX
    private static async Task<FileDialogPickResult> PickLinuxAsync(FileDialogPickMode mode, CancellationToken ct)
    {
        string tool;
        List<string> args;
        if (ToolExists("zenity"))
        {
            tool = "zenity";
            args = mode switch
            {
                FileDialogPickMode.Folder => new List<string> { "--file-selection", "--directory" },
                FileDialogPickMode.AnyFileSingle => new List<string> { "--file-selection" },
                _ => new List<string>
                {
                    "--file-selection", "--multiple", "--separator=\n",
                    "--file-filter=Images and videos | *.jpg *.jpeg *.png *.webp *.gif *.bmp *.avif *.mp4 *.m4v *.webm *.mov",
                },
            };
        }
        else if (ToolExists("kdialog"))
        {
            tool = "kdialog";
            args = mode switch
            {
                FileDialogPickMode.Folder => new List<string> { "--getexistingdirectory", "." },
                FileDialogPickMode.AnyFileSingle => new List<string> { "--getopenfilename", "." },
                _ => new List<string>
                {
                    "--getopenfilename", ".",
                    "Images and videos (*.jpg *.jpeg *.png *.webp *.gif *.bmp *.avif *.mp4 *.m4v *.webm *.mov)",
                    "--multiple", "--separate-output",
                },
            };
        }
        else
        {
            return Fail("no dialog tool found on this PC (install zenity or kdialog)");
        }

        // Root daemon → run the dialog inside the user's compositor session.
        var (file, wrapped) = LinuxSession.WrapSpawnAsSessionUser(tool, args);
        var stdout = await ShellExecutor.RunAsync(file, DialogTimeoutMs, ct, wrapped.ToArray()).ConfigureAwait(false);
        return FromLines(stdout);
    }

    private static bool ToolExists(string tool) =>
        !string.IsNullOrWhiteSpace(ShellExecutor.Run("which", tool));
#else
    private static Task<FileDialogPickResult> PickLinuxAsync(FileDialogPickMode mode, CancellationToken ct) =>
        Task.FromResult(Fail("not built for linux"));
#endif

    private static FileDialogPickResult FromLines(string stdout)
    {
        var paths = stdout
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && Path.IsPathFullyQualified(l))
            .ToList();
        return paths.Count == 0
            ? new FileDialogPickResult { Cancelled = true }
            : new FileDialogPickResult { Paths = paths };
    }

    private static FileDialogPickResult Fail(string msg) =>
        new() { Error = true, Msg = msg };
}
