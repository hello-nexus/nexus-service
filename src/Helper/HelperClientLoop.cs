#if WINDOWS
using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper;

/// <summary>
/// Helper-side pipe client: connect to <c>\\.\pipe\Nexus.Helper</c>, send the
/// hello envelope, then read envelopes and route them through
/// <see cref="HelperHandlerRegistry"/>. Reconnects with exponential backoff
/// when the pipe drops (service restart, transient close), so the helper
/// stays a long-lived companion regardless of service lifetime.
///
/// One instance lives for the lifetime of the helper process. Cancel the
/// token to shut it down.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperClientLoop
{
    private const int ConnectTimeoutMs = 5000;

    private readonly HelperHandlerRegistry _registry;
    private readonly HelperOutbound _outbound;
    private readonly Action? _onDisconnected;
    private readonly Action? _onConnected;

    // onDisconnected fires each time an established pipe drops (service stop,
    // crash, transient close). The tray uses it to hide the icon while the
    // service is down; the service re-pushes visibility on the next connect.
    // onConnected fires once the pipe accepts outbound sends.
    public HelperClientLoop(HelperHandlerRegistry registry, HelperOutbound outbound, Action? onDisconnected = null, Action? onConnected = null)
    {
        _registry = registry;
        _outbound = outbound;
        _onDisconnected = onDisconnected;
        _onConnected = onConnected;
    }

    public async Task RunAsync(CancellationToken exit)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!exit.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: HelperPipeServer.PipeName,
                    direction: PipeDirection.InOut,
                    options: PipeOptions.Asynchronous);

                await pipe.ConnectAsync(ConnectTimeoutMs, exit).ConfigureAwait(false);

                await SendHelloAsync(pipe, exit).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(1);

                _outbound.SetActivePipe(pipe);
                try { _onConnected?.Invoke(); } catch { /* best-effort */ }
                try
                {
                    await ReadLoopAsync(pipe, exit).ConfigureAwait(false);
                }
                finally
                {
                    // Drain pending sends before letting the using-block
                    // dispose the pipe, so HelperOutbound never tries to
                    // write to a disposed stream.
                    await _outbound.ClearActivePipeAsync().ConfigureAwait(false);
                    // Only fires for an established connection (SetActivePipe
                    // ran above); a failed connect attempt never enters here.
                    try { _onDisconnected?.Invoke(); } catch { /* best-effort */ }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[helper-client] pipe error: {ex.Message}");
                await _outbound.ClearActivePipeAsync().ConfigureAwait(false);
            }

            try { await Task.Delay(backoff, exit).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }

    private static async Task SendHelloAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        var hello = new HelperHello
        {
            SessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId,
            Pid = Environment.ProcessId,
            Version = BuildInfo.Version,
            DownloadsDir = ResolveDownloadsDir(),
        };
        var env = new HelperEnvelope
        {
            Type = "hello",
            Payload = JsonSerializer.SerializeToElement(hello, AppJsonContext.Default.HelperHello),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(env, AppJsonContext.Default.HelperEnvelope);
        // Direct write is safe here - the read loop hasn't started yet and
        // no helper-side provider can have a reference to the pipe.
        await Framing.WriteFrameAsync(pipe, bytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// SHGetKnownFolderPath rather than UserProfile + "Downloads" - the
    /// Downloads folder can be relocated via folder Properties → Location.
    /// </summary>
    private static string ResolveDownloadsDir()
    {
        try
        {
            var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
            if (SHGetKnownFolderPath(in downloads, 0, IntPtr.Zero, out var raw) == 0 && raw != IntPtr.Zero)
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

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            var env = await ReadEnvelopeAsync(pipe, ct).ConfigureAwait(false);
            if (env is null) return;
            _ = HandleAsync(pipe, env, ct);
        }
    }

    private async Task HandleAsync(NamedPipeClientStream pipe, HelperEnvelope env, CancellationToken ct)
    {
        try
        {
            var result = await _registry.DispatchAsync(env, ct).ConfigureAwait(false);
            if (env.Id is null) return; // fire-and-forget; no reply expected

            var reply = new HelperEnvelope
            {
                Type = "result",
                Id = env.Id,
                Payload = JsonSerializer.SerializeToElement(result, AppJsonContext.Default.HelperResult),
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, AppJsonContext.Default.HelperEnvelope);
            // Reply writes don't go through HelperOutbound; they're driven
            // by the read loop itself and ride the same pipe sequentially.
            await Framing.WriteFrameAsync(pipe, bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[helper-client] handle failed for type={env.Type}: {ex.Message}");
        }
    }

    private static async Task<HelperEnvelope?> ReadEnvelopeAsync(PipeStream pipe, CancellationToken ct)
    {
        var payload = await Framing.ReadFrameAsync(pipe, ct).ConfigureAwait(false);
        if (payload is null) return null;
        return JsonSerializer.Deserialize(payload, AppJsonContext.Default.HelperEnvelope);
    }
}
#endif
