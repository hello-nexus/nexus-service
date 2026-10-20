#if WINDOWS
using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper;

/// <summary>
/// One named-pipe connection from one helper process. Owns the pipe stream,
/// drives a read loop, serialises writes, and routes incoming "result"
/// envelopes back to the originating command via correlation id.
///
/// Lifetime: created by HelperPipeServer on accept, registered after the
/// hello handshake, disposed when the pipe drops or when the registry
/// displaces us with a newer connection from the same session.
/// </summary>
public sealed class HelperConnection : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly HelperRegistry _registry;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<HelperResult>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    public int SessionId { get; }
    public int Pid { get; }
    public string Version { get; }
    public string DownloadsDir { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    private HelperConnection(NamedPipeServerStream pipe, HelperRegistry registry, HelperHello hello)
    {
        _pipe = pipe;
        _registry = registry;
        SessionId = hello.SessionId;
        Pid = hello.Pid;
        Version = hello.Version;
        DownloadsDir = hello.DownloadsDir;
    }

    /// <summary>
    /// Entry point for HelperPipeServer after WaitForConnectionAsync. Reads
    /// the hello frame, registers the connection, runs the read loop until
    /// the pipe closes, then unregisters and disposes.
    /// </summary>
    public static async Task RunAsync(NamedPipeServerStream pipe, HelperRegistry registry, CancellationToken outerCt)
    {
        HelperConnection? conn = null;
        try
        {
            var helloEnv = await ReadEnvelopeAsync(pipe, outerCt).ConfigureAwait(false);
            if (helloEnv is null || helloEnv.Type != "hello" || helloEnv.Payload is null)
            {
                Console.Error.WriteLine("[helper-pipe] missing or invalid hello; closing");
                return;
            }
            var hello = JsonSerializer.Deserialize(
                helloEnv.Payload.Value, AppJsonContext.Default.HelperHello);
            if (hello is null)
            {
                Console.Error.WriteLine("[helper-pipe] hello payload not deserializable; closing");
                return;
            }

            // Cross-check the claimed session id against the pipe client's
            // actual session via WTS. A malicious INTERACTIVE process must
            // not be able to claim another user's session (would let user A
            // displace user B's helper on multi-user/RDP boxes).
            if (!TryGetVerifiedSessionId(pipe, out var verifiedSession))
            {
                Console.Error.WriteLine("[helper-pipe] could not verify client session; closing");
                return;
            }
            if (verifiedSession != hello.SessionId)
            {
                Console.Error.WriteLine($"[helper-pipe] session mismatch claim={hello.SessionId} actual={verifiedSession}; closing");
                return;
            }

            conn = new HelperConnection(pipe, registry, hello);
            registry.Register(conn);
            Console.WriteLine($"[helper-pipe] connected session={hello.SessionId} pid={hello.Pid} version={hello.Version}");
            await conn.ReadLoopAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[helper-pipe] connection error: {ex.Message}");
        }
        finally
        {
            if (conn is not null)
            {
                registry.Unregister(conn);
                await conn.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                try { pipe.Dispose(); } catch { }
            }
        }
    }

    private async Task ReadLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _pipe.IsConnected)
        {
            HelperEnvelope? env;
            try
            {
                env = await ReadEnvelopeAsync(_pipe, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { return; }

            if (env is null) return;

            if (env.Type == "result" && !string.IsNullOrEmpty(env.Id))
            {
                if (_pending.TryRemove(env.Id, out var tcs))
                {
                    var result = env.Payload is null
                        ? new HelperResult { Id = env.Id, Ok = true }
                        : JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.HelperResult)
                          ?? new HelperResult { Id = env.Id, Ok = false, Error = "result deserialise failed" };
                    tcs.TrySetResult(result);
                }
                continue;
            }

            _registry.RaiseInbound(this, env);
        }
    }

    /// <summary>
    /// Send a one-way envelope (no `Id`, no reply expected). Used for
    /// telemetry server-side broadcasts back to the helper, or fire-and-
    /// forget commands where the caller does not need a result.
    /// </summary>
    public Task SendAsync<TPayload>(string type, TPayload payload, JsonTypeInfo<TPayload> payloadType, CancellationToken ct = default)
    {
        var env = new HelperEnvelope
        {
            Type = type,
            Payload = JsonSerializer.SerializeToElement(payload, payloadType),
        };
        return WriteEnvelopeAsync(env, ct);
    }

    /// <summary>
    /// Send a command and await the helper's result. Times out after
    /// `timeoutMs` if the helper never replies.
    /// </summary>
    public async Task<HelperResult> SendCommandAsync<TPayload>(
        string type, TPayload payload, JsonTypeInfo<TPayload> payloadType,
        int timeoutMs = 5000, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<HelperResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            var env = new HelperEnvelope
            {
                Type = type,
                Id = id,
                Payload = JsonSerializer.SerializeToElement(payload, payloadType),
            };
            await WriteEnvelopeAsync(env, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            using var reg = timeout.Token.Register(() =>
                tcs.TrySetResult(new HelperResult { Id = id, Ok = false, Error = "timeout" }));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task WriteEnvelopeAsync(HelperEnvelope env, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(env, AppJsonContext.Default.HelperEnvelope);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Framing.WriteFrameAsync(_pipe, bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<HelperEnvelope?> ReadEnvelopeAsync(PipeStream pipe, CancellationToken ct)
    {
        var payload = await Framing.ReadFrameAsync(pipe, ct).ConfigureAwait(false);
        if (payload is null) return null;
        return JsonSerializer.Deserialize(payload, AppJsonContext.Default.HelperEnvelope);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch { }
        foreach (var kv in _pending)
        {
            kv.Value.TrySetResult(new HelperResult { Id = kv.Key, Ok = false, Error = "disconnected" });
        }
        _pending.Clear();

        // Drain in-flight writes before disposing the pipe so concurrent
        // SendAsync / SendCommandAsync callers don't observe a disposed
        // stream mid-write. Symmetric to the helper-side teardown in
        // HelperOutbound.ClearActivePipeAsync.
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { _pipe.Dispose(); }
        catch { }
        finally { _writeLock.Release(); }

        // _writeLock is intentionally not disposed: pending awaiters would
        // observe ObjectDisposedException before _cts.Cancel propagates.
        // The semaphore is per-connection and goes away with the GC.
        try { _cts.Dispose(); } catch { }
    }

    private static bool TryGetVerifiedSessionId(NamedPipeServerStream pipe, out int sessionId)
    {
        sessionId = -1;
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid)) return false;
            if (!ProcessIdToSessionId(pid, out var sid)) return false;
            sessionId = (int)sid;
            return true;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
#endif
