using System.IO;
using Nexus.Service.Models.Transfer;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Transfer;

/// <summary>
/// Native-notification request for a transfer that landed while no dashboard
/// was open to show the WebSocket toast. <see cref="FolderPath"/> set means
/// "clicking should open this folder".
/// </summary>
public sealed record TransferAttentionNotice(string Title, string Text, string? FolderPath);

/// <summary>
/// Destination folder + safe-write helper for phone→PC transfers. Resolution
/// order: explicit settings override → the interactive user's Downloads/Nexus
/// → CommonApplicationData/Nexus/inbox.
///
/// On Windows under LocalSystem the file is FOR the interactive user, so it is
/// written AS that user whenever one is logged on, the settings override
/// included: the Downloads folder is resolved from the helper's WTS-verified
/// session id (never from a path the helper - any process in that session -
/// reports), and every create/move runs impersonated under that session's
/// token. A junction in the user's Downloads then reaches only what the user
/// can already reach, instead of redirecting a SYSTEM write. With nobody logged
/// on, the override or the fallback is written as SYSTEM; the fallback is
/// locked so it cannot be a pre-planted junction, with Users keeping Modify on
/// the files inside so they can still take them away later.
/// </summary>
public sealed class TransferInbox
{
    /// <summary>
    /// A resolved destination plus, under LocalSystem, the interactive user's
    /// token the writes are impersonated under. Dispose after the request.
    /// </summary>
    public sealed class InboxTarget : IDisposable
    {
        public string Dir { get; }
        internal Microsoft.Win32.SafeHandles.SafeAccessTokenHandle? Token { get; }

        internal InboxTarget(string dir, Microsoft.Win32.SafeHandles.SafeAccessTokenHandle? token = null)
        {
            Dir = dir;
            Token = token;
        }

        public void Dispose() => Token?.Dispose();
    }

    /// <summary>Per-request cap for /transfer/items - phone videos routinely exceed the global 100 MB Kestrel limit.</summary>
    public const long MaxUploadBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxClipboardChars = 1024 * 1024;

    private const string PartialSuffix = ".nexus-partial";

    // Win32 rejects or remaps these names even when an extension follows.
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT¹", "LPT²", "LPT³",
    };

    private readonly IConfigStore _store;
    private readonly IServiceProvider _services;

    public TransferInbox(IConfigStore store, IServiceProvider services)
    {
        _store = store;
        _services = services;
    }

    /// <summary>
    /// Raised when a transfer lands with no dashboard subscribed to the
    /// "transfer" topic. Platform bootstraps subscribe to surface a native
    /// notification (Windows tray balloon today).
    /// </summary>
    public event Action<TransferAttentionNotice>? TransferNeedsAttention;

    public void RaiseAttention(TransferAttentionNotice notice)
    {
        // Raised synchronously inside the HTTP handler; a throwing subscriber
        // must not turn an already-saved upload into a 500 (→ phone retry →
        // duplicate files).
        try
        {
            TransferNeedsAttention?.Invoke(notice);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[transfer-notify] subscriber failed: {ex.Message}");
        }
    }

    public InboxTarget ResolveTarget()
    {
        var configured = _store.Load().TransferInboxPath;
        var overrideDir = string.IsNullOrWhiteSpace(configured) ? null : configured;

#if WINDOWS
        if (WindowsDirectorySecurity.IsLocalSystem())
        {
            var helper = _services.GetService<Nexus.Service.Helper.HelperRegistry>()?.GetAny();
            if (helper is not null && TryOpenSessionToken(helper.SessionId) is { } token)
            {
                var dir = overrideDir;
                if (dir is null)
                {
                    var downloads = ResolveDownloadsDir(token);
                    if (!string.IsNullOrWhiteSpace(downloads))
                        dir = Path.Combine(downloads, "Nexus");
                }
                if (dir is not null)
                    return new InboxTarget(dir, token);
                token.Dispose();
            }
            if (overrideDir is not null)
                return new InboxTarget(overrideDir);
            // Nobody is logged on to receive it. The machine-wide fallback lives
            // under %ProgramData%, where any local user can pre-create a subdir
            // as a junction; SYSTEM writes there only once it owns a locked dir.
            var fallback = FallbackDir();
            WindowsDirectorySecurity.Protect(fallback, resetOwner: true, usersModifyChildren: true);
            if (!WindowsDirectorySecurity.IsOwnedByAdmins(fallback))
                throw new InvalidOperationException("transfer inbox is not owned by SYSTEM/Administrators");
            return new InboxTarget(fallback);
        }
        // Interactive run: the service already runs as the user the helper
        // serves, so the helper-reported folder carries no privilege gap.
        if (overrideDir is not null)
            return new InboxTarget(overrideDir);
        var reported = _services.GetService<Nexus.Service.Helper.HelperRegistry>()?.GetAny()?.DownloadsDir;
        if (!string.IsNullOrWhiteSpace(reported))
            return new InboxTarget(Path.Combine(reported, "Nexus"));
        return new InboxTarget(FallbackDir());
#else
        if (overrideDir is not null)
            return new InboxTarget(overrideDir);
        var userDownloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(userDownloads))
            return new InboxTarget(Path.Combine(userDownloads, "Nexus"));
        return new InboxTarget(FallbackDir());
#endif
    }

    private static string FallbackDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nexus", "inbox");

#if WINDOWS
    private static Microsoft.Win32.SafeHandles.SafeAccessTokenHandle? TryOpenSessionToken(int sessionId)
    {
        if (sessionId < 0)
            return null;
        try
        {
            if (!WTSQueryUserToken((uint)sessionId, out var raw) || raw == IntPtr.Zero)
                return null;
            return new Microsoft.Win32.SafeHandles.SafeAccessTokenHandle(raw);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The session user's Downloads folder (relocatable via folder Properties → Location), resolved through their token.</summary>
    private static string ResolveDownloadsDir(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token)
    {
        try
        {
            var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
            if (SHGetKnownFolderPath(in downloads, 0, token.DangerousGetHandle(), out var raw) == 0 && raw != IntPtr.Zero)
            {
                try
                {
                    return System.Runtime.InteropServices.Marshal.PtrToStringUni(raw) ?? "";
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(raw);
                }
            }
        }
        catch { }
        return "";
    }

    private static Task<T> RunAsTargetAsync<T>(InboxTarget target, Func<Task<T>> body)
        => target.Token is { } token
            ? System.Security.Principal.WindowsIdentity.RunImpersonatedAsync(token, body)
            : body();

    private static void RunAsTarget(InboxTarget target, Action body)
    {
        if (target.Token is { } token)
            System.Security.Principal.WindowsIdentity.RunImpersonated(token, body);
        else
            body();
    }

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
#endif

    public Task<TransferSavedItem> SaveAsync(Stream content, string? rawFileName, InboxTarget target, CancellationToken ct)
    {
#if WINDOWS
        return RunAsTargetAsync(target, () => SaveCoreAsync(content, rawFileName, target.Dir, ct));
#else
        return SaveCoreAsync(content, rawFileName, target.Dir, ct);
#endif
    }

    private static async Task<TransferSavedItem> SaveCoreAsync(Stream content, string? rawFileName, string dir, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var name = SanitizeFileName(rawFileName);
        // Stage in the destination dir so the final rename is same-volume atomic;
        // readers never observe a partial file under its real name. The random
        // suffix keeps concurrent same-name uploads off a shared staging path.
        var partial = Path.Combine(dir, $"{name}.{Guid.NewGuid():N}{PartialSuffix}");
        try
        {
            long size;
            await using (var stream = File.Create(partial))
            {
                await content.CopyToAsync(stream, ct);
                size = stream.Length;
            }
            for (var attempt = 0; ; attempt++)
            {
                var candidate = UniqueName(dir, name);
                var dest = Path.Combine(dir, candidate);
                try
                {
                    // Claim the name with O_EXCL first: File.Move's no-overwrite
                    // check is not atomic on Unix, so concurrent same-name saves
                    // silently replace each other. The placeholder is then swapped
                    // for the real content atomically.
                    using (new FileStream(dest, FileMode.CreateNew, FileAccess.Write)) { }
                }
                // Name taken concurrently - take the next free one.
                catch (IOException) when (attempt < 50)
                {
                    continue;
                }
                try
                {
                    File.Move(partial, dest, overwrite: true);
                }
                catch
                {
                    try { File.Delete(dest); } catch { }
                    throw;
                }
                return new TransferSavedItem { Name = candidate, Size = size };
            }
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }
    }

    /// <summary>Best-effort removal of staging files orphaned by a crash mid-upload.</summary>
    public static void SweepStalePartials(InboxTarget target)
    {
#if WINDOWS
        RunAsTarget(target, () => SweepStalePartials(target.Dir));
#else
        SweepStalePartials(target.Dir);
#endif
    }

    internal static void SweepStalePartials(string dir)
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
            foreach (var stale in Directory.EnumerateFiles(dir, "*" + PartialSuffix))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(stale) < cutoff)
                        File.Delete(stale);
                }
                catch { }
            }
        }
        catch { }
    }

    internal static string SanitizeFileName(string? raw)
    {
        // GetFileName strips directory components - and with them any ../ traversal.
        // '\' is normalized first: sender-supplied names may use Windows separators
        // even when the service runs on a platform where '\' is a legal name char.
        var name = Path.GetFileName((raw ?? "").Trim().Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] < 0x20 || Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }
        name = new string(chars).TrimStart('.').TrimEnd(' ', '.');
        if (name.Length == 0)
            return "transfer";
        var ext = Path.GetExtension(name);
        // Sender-controlled; an unbounded "extension" would defeat the stem cap.
        // Win32 strips trailing spaces, so trim after capping or the reported
        // name can differ from the on-disk one.
        if (ext.Length > 24)
            ext = ext[..24].TrimEnd(' ');
        var stem = Path.GetFileNameWithoutExtension(name);
        // Win32 reserves the name up to the FIRST dot: CON.foo.txt is still CON.
        if (WindowsReservedNames.Contains(name.Split('.', 2)[0].TrimEnd(' ')))
            stem = "_" + stem;
        if (stem.Length > 120)
            stem = stem[..120];
        return stem + ext;
    }

    private static string UniqueName(string dir, string name)
    {
        if (!File.Exists(Path.Combine(dir, name)))
            return name;
        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!File.Exists(Path.Combine(dir, candidate)))
                return candidate;
        }
    }
}
