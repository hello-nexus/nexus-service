using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Platform.Linux.Render;

public sealed class CdpException : Exception
{
    public CdpException(string message)
        : base(message)
    {
    }
}

public sealed class CdpPipeClient : IDisposable
{
    private const int MaxMessageBytes = 64 * 1024 * 1024;

    private readonly Stream _toBrowser;
    private readonly Stream _fromBrowser;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Thread _reader;
    private int _nextId;
    private volatile bool _closed;

    public event Action<string, JsonElement, string?>? EventReceived;

    public event Action? Closed;

    public CdpPipeClient(Stream toBrowser, Stream fromBrowser)
    {
        _toBrowser = toBrowser;
        _fromBrowser = fromBrowser;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "cdp-pipe-reader" };
        _reader.Start();
    }

    public bool IsClosed => _closed;

    public async Task<JsonElement> SendAsync(string method, Action<Utf8JsonWriter>? writeParams = null,
        string? sessionId = null, int timeoutMs = 15000, CancellationToken ct = default)
    {
        if (_closed)
        {
            throw new IOException("devtools pipe closed");
        }
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var message = Encode(id, method, writeParams, sessionId);
            lock (_writeLock)
            {
                _toBrowser.Write(message);
                _toBrowser.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw new IOException($"devtools pipe write failed: {ex.Message}", ex);
        }
        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public void Post(string method, Action<Utf8JsonWriter>? writeParams = null, string? sessionId = null)
    {
        _ = PostAsync(method, writeParams, sessionId);
    }

    private async Task PostAsync(string method, Action<Utf8JsonWriter>? writeParams, string? sessionId)
    {
        try
        {
            await SendAsync(method, writeParams, sessionId).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    internal static byte[] Encode(int id, string method, Action<Utf8JsonWriter>? writeParams, string? sessionId)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("id", id);
            w.WriteString("method", method);
            if (sessionId is not null)
            {
                w.WriteString("sessionId", sessionId);
            }
            w.WritePropertyName("params");
            w.WriteStartObject();
            writeParams?.Invoke(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        var message = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(message);
        message[^1] = 0;
        return message;
    }

    private void ReadLoop()
    {
        var chunk = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (true)
            {
                var n = _fromBrowser.Read(chunk, 0, chunk.Length);
                if (n <= 0)
                {
                    break;
                }
                var start = 0;
                for (var i = 0; i < n; i++)
                {
                    if (chunk[i] != 0)
                    {
                        continue;
                    }
                    message.Write(chunk, start, i - start);
                    Dispatch(message.GetBuffer().AsMemory(0, (int)message.Length));
                    message.SetLength(0);
                    start = i + 1;
                }
                message.Write(chunk, start, n - start);
                if (message.Length > MaxMessageBytes)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        Close();
    }

    internal void Dispatch(ReadOnlyMemory<byte> json)
    {
        if (json.Length == 0)
        {
            return;
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
            {
                if (_pending.TryRemove(id, out var tcs))
                {
                    if (root.TryGetProperty("error", out var error))
                    {
                        var text = error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();
                        tcs.TrySetException(new CdpException(text ?? "devtools error"));
                    }
                    else
                    {
                        tcs.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
                    }
                }
                return;
            }
            if (root.TryGetProperty("method", out var methodEl) && methodEl.GetString() is { } method)
            {
                var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                var session = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
                try
                {
                    EventReceived?.Invoke(method, parameters, session);
                }
                catch
                {
                }
            }
        }
    }

    private void Close()
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new IOException("devtools pipe closed"));
        }
        _pending.Clear();
        try
        {
            Closed?.Invoke();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        try
        {
            _toBrowser.Dispose();
        }
        catch
        {
        }
        try
        {
            _fromBrowser.Dispose();
        }
        catch
        {
        }
        Close();
    }
}
