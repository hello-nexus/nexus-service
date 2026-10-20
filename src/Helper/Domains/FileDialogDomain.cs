#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

public sealed class FileDialogRequest
{
    public FileDialogPickMode Mode { get; set; }
    /// <summary>Folder path for dialog.openFolder; unused by dialog.pick.</summary>
    public string Path { get; set; } = "";
}

public sealed class FileDialogResult
{
    public List<string> Paths { get; set; } = new();
    public bool Cancelled { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Service-side facade for the native file/folder picker. The Session-0
/// LocalSystem service cannot show UI, so IFileOpenDialog runs in the
/// user-session helper. The timeout is deliberately long - the user may
/// browse for a while; the caller's CancellationToken (request abort)
/// stops the wait early, though an already-shown dialog stays open until
/// the user closes it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileDialogCommands
{
    public const string PickType = "dialog.pick";
    public const string OpenFolderType = "dialog.openFolder";
    private const int TimeoutMs = 600_000;
    private const int OpenFolderAckMs = 5000;
    private const int OpenFolderWriteMarginMs = 1000;

    /// <summary>
    /// Open a folder in Explorer inside the user session, brought in front
    /// of the app window. The whole send is time-bounded; a missing ack is
    /// reported as <see cref="OpenFolderOutcome.TimedOut"/> because Explorer
    /// may still appear.
    /// </summary>
    public static async Task<OpenFolderOutcome> TryOpenFolderAsync(HelperRegistry? r, string path, CancellationToken ct = default)
    {
        var conn = r?.GetAny();
        if (conn is null)
        {
            return OpenFolderOutcome.NoHelper;
        }

        var send = conn.SendCommandAsync(
            OpenFolderType,
            new FileDialogRequest { Path = path },
            AppJsonContext.Default.FileDialogRequest,
            timeoutMs: OpenFolderAckMs,
            ct: ct);
        // The ack timeout starts only after the pipe write, which a frozen helper with a full pipe never completes.
        try
        {
            await send.WaitAsync(TimeSpan.FromMilliseconds(OpenFolderAckMs + OpenFolderWriteMarginMs), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Reads a later fault (helper killed mid-write) so it is not reported as unobserved.
            _ = send.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            ServiceLog.Warn("[helper] dialog.openFolder send did not complete in time");
            return OpenFolderOutcome.TimedOut;
        }

        var result = await send.ConfigureAwait(false);
        var outcome = OpenFolderOutcomes.FromResult(result.Ok, result.Error);
        if (outcome == OpenFolderOutcome.TimedOut)
        {
            ServiceLog.Warn("[helper] dialog.openFolder not acked in time");
        }

        return outcome;
    }

    /// <summary>Opened-only view of <see cref="TryOpenFolderAsync"/> for callers that treat a timeout as failure.</summary>
    public static async Task<bool> OpenFolderAsync(HelperRegistry r, string path, CancellationToken ct = default) =>
        await TryOpenFolderAsync(r, path, ct).ConfigureAwait(false) == OpenFolderOutcome.Opened;

    public static async Task<FileDialogResult> PickAsync(HelperRegistry r, FileDialogPickMode mode, CancellationToken ct = default)
    {
        var conn = r.GetAny();
        if (conn is null)
        {
            return new FileDialogResult { Error = "no interactive user session" };
        }

        var result = await conn.SendCommandAsync(
            PickType,
            new FileDialogRequest { Mode = mode },
            AppJsonContext.Default.FileDialogRequest,
            timeoutMs: TimeoutMs,
            ct: ct).ConfigureAwait(false);

        if (!result.Ok || result.Payload is null)
        {
            return new FileDialogResult { Error = result.Error ?? "dialog failed" };
        }

        try
        {
            return JsonSerializer.Deserialize(result.Payload.Value, AppJsonContext.Default.FileDialogResult)
                ?? new FileDialogResult { Error = "bad payload" };
        }
        catch
        {
            return new FileDialogResult { Error = "bad payload" };
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class FileDialogHandler
{
    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register(FileDialogCommands.PickType, async (env, _) =>
        {
            var req = env.Payload is null
                ? new FileDialogRequest()
                : JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.FileDialogRequest) ?? new FileDialogRequest();
            var result = await Platform.Windows.NativeFileDialog.ShowAsync(req.Mode).ConfigureAwait(false);
            return new HelperResult
            {
                Id = env.Id ?? "",
                Ok = true,
                Payload = JsonSerializer.SerializeToElement(result, AppJsonContext.Default.FileDialogResult),
            };
        });

        registry.Register(FileDialogCommands.OpenFolderType, (env, _) =>
        {
            var req = env.Payload is null
                ? new FileDialogRequest()
                : JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.FileDialogRequest) ?? new FileDialogRequest();
            if (!string.IsNullOrWhiteSpace(req.Path))
            {
                Platform.Windows.ForegroundNudge.OpenFolderOverApp(req.Path);
            }

            return Task.FromResult(new HelperResult { Id = env.Id ?? "", Ok = true });
        });
    }
}
#endif
