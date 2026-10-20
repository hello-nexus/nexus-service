using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Nexus.Service.Models;
using Nexus.Service.Models.Obs;
using Nexus.Service.Persistence;

namespace Nexus.Service.Obs;

public sealed class ObsProvider : IObsProvider, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2.5);

    private readonly IConfigStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;
    private ClientWebSocket? _socket;

    // OBS's streaming outputDuration counts video packets across every track,
    // so multitrack streams report it inflated. The service times the stream
    // itself and uses OBS's value only to seed a stream already live when first seen.
    private long? _streamStart;
    private long _lastObsStreamMs;

    public ObsProvider(IConfigStore store, TimeProvider? time = null)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    public ObsConfigResponse GetConfig()
    {
        var settings = _store.Load().Obs;
        return new ObsConfigResponse
        {
            Host = NormalizeHostForDisplay(settings.Host),
            Port = NormalizePort(settings.Port),
            HasPassword = !string.IsNullOrEmpty(settings.Password),
        };
    }

    public void SetConfig(ObsConfigBody body)
    {
        _store.Update(s =>
        {
            if (body.Host is not null)
            {
                s.Obs.Host = string.IsNullOrWhiteSpace(body.Host) ? "127.0.0.1" : body.Host.Trim();
            }
            if (body.Port is > 0 and < 65536)
            {
                s.Obs.Port = body.Port.Value;
            }
            if (body.Password is not null)
            {
                s.Obs.Password = body.Password;
            }
        });

        _gate.Wait();
        try
        {
            ResetSocketLocked();
            _streamStart = null;
            _lastObsStreamMs = 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ObsStatusResponse> GetStatusAsync(CancellationToken cancellationToken) =>
        RunWithSocketAsync(BuildStatusLockedAsync, cancellationToken);

    public Task<ObsStatusResponse> ToggleRecordingAsync(CancellationToken cancellationToken) =>
        RunWithSocketAsync(async ct =>
        {
            var toggled = await SendRequestLockedAsync("ToggleRecord", null, ct).ConfigureAwait(false);
            var status = await BuildStatusLockedAsync(ct).ConfigureAwait(false);
            ApplyToggledOutput(toggled, status, recording: true);
            return status;
        }, cancellationToken);

    public Task<ObsStatusResponse> ToggleStreamingAsync(CancellationToken cancellationToken) =>
        RunWithSocketAsync(async ct =>
        {
            var toggled = await SendRequestLockedAsync("ToggleStream", null, ct).ConfigureAwait(false);
            var status = await BuildStatusLockedAsync(ct).ConfigureAwait(false);
            var active = ApplyToggledOutput(toggled, status, recording: false);
            if (active == true)
            {
                _streamStart = _time.GetTimestamp();
                _lastObsStreamMs = 0;
                status.StreamingDurationMs = 0;
            }
            else if (active == false)
            {
                _streamStart = null;
                _lastObsStreamMs = 0;
            }
            return status;
        }, cancellationToken);

    public Task<ObsStatusResponse> SetSceneAsync(string sceneName, CancellationToken cancellationToken) =>
        RunWithSocketAsync(async ct =>
        {
            var trimmed = sceneName.Trim();
            if (trimmed.Length == 0)
            {
                return Disconnected("Scene name is required", "error");
            }

            await SendRequestLockedAsync("SetCurrentProgramScene", writer =>
            {
                writer.WriteString("sceneName", trimmed);
            }, ct).ConfigureAwait(false);
            return await BuildStatusLockedAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    public ApiResponse Launch()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return LaunchWindows();
            }
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open")
                {
                    UseShellExecute = false,
                    ArgumentList = { "-a", "OBS" },
                });
                return ApiResponse.Ok("launched");
            }

            Process.Start(new ProcessStartInfo("obs")
            {
                UseShellExecute = true,
            });
            return ApiResponse.Ok("launched");
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail($"failed to launch OBS: {ex.Message}", ex);
        }
    }

    private static ApiResponse LaunchWindows()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio", "bin", "64bit", "obs64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "obs-studio", "bin", "64bit", "obs64.exe"),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            Process.Start(new ProcessStartInfo(path)
            {
                WorkingDirectory = Path.GetDirectoryName(path),
                UseShellExecute = true,
            });
            return ApiResponse.Ok("launched");
        }

        Process.Start(new ProcessStartInfo("obs64.exe")
        {
            UseShellExecute = true,
        });
        return ApiResponse.Ok("launched");
    }

    private async Task<ObsStatusResponse> RunWithSocketAsync(
        Func<CancellationToken, Task<ObsStatusResponse>> action,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        var acquired = false;
        try
        {
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            return await action(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (acquired)
            {
                ResetSocketLocked();
            }
            return Disconnected(ex.Message, ClassifyReason(ex));
        }
        catch (OperationCanceledException)
        {
            if (acquired)
            {
                ResetSocketLocked();
            }
            return Disconnected("OBS did not respond in time", "offline");
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
            }
        }
    }

    private async Task<ObsStatusResponse> BuildStatusLockedAsync(CancellationToken cancellationToken)
    {
        var sceneData = await SendRequestLockedAsync("GetSceneList", null, cancellationToken).ConfigureAwait(false);
        var recordData = await SendRequestLockedAsync("GetRecordStatus", null, cancellationToken).ConfigureAwait(false);
        var streamData = await SendRequestLockedAsync("GetStreamStatus", null, cancellationToken).ConfigureAwait(false);
        var settings = _store.Load().Obs;
        var streaming = ReadBool(streamData, "outputActive");

        return new ObsStatusResponse
        {
            Connected = true,
            Host = NormalizeHostForDisplay(settings.Host),
            Port = NormalizePort(settings.Port),
            ActiveScene = ReadString(sceneData, "currentProgramSceneName"),
            Scenes = ReadScenes(sceneData),
            Recording = ReadBool(recordData, "outputActive"),
            Streaming = streaming,
            RecordingDurationMs = ReadLong(recordData, "outputDuration"),
            StreamingDurationMs = TrackStreamElapsedMs(streaming, ReadLong(streamData, "outputDuration")),
        };
    }

    private long TrackStreamElapsedMs(bool streaming, long obsDurationMs)
    {
        if (!streaming)
        {
            _streamStart = null;
            _lastObsStreamMs = 0;
            return 0;
        }

        var now = _time.GetTimestamp();
        // OBS's own duration only ever grows within one stream, so a drop means a restart.
        if (_streamStart is null || obsDurationMs < _lastObsStreamMs)
        {
            _streamStart = now - (long)(obsDurationMs / 1000.0 * _time.TimestampFrequency);
        }
        _lastObsStreamMs = obsDurationMs;
        return (long)_time.GetElapsedTime(_streamStart.Value, now).TotalMilliseconds;
    }

    private async Task<JsonElement> SendRequestLockedAsync(
        string requestType,
        Action<Utf8JsonWriter>? writeRequestData,
        CancellationToken cancellationToken)
    {
        var socket = await EnsureConnectedLockedAsync(cancellationToken).ConfigureAwait(false);
        var requestId = Guid.NewGuid().ToString("N");
        await SendObsRequestAsync(socket, requestType, requestId, writeRequestData, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            using var doc = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var opEl))
            {
                continue;
            }

            var op = opEl.GetInt32();
            if (op != 7)
            {
                continue;
            }

            var d = root.GetProperty("d");
            if (ReadString(d, "requestId") != requestId)
            {
                continue;
            }

            var status = d.GetProperty("requestStatus");
            if (!ReadBool(status, "result"))
            {
                var message = ReadString(status, "comment");
                if (message.Length == 0)
                {
                    message = $"{requestType} failed";
                }
                throw new InvalidOperationException(message);
            }

            return d.TryGetProperty("responseData", out var data)
                ? data.Clone()
                : default;
        }
    }

    private async Task<ClientWebSocket> EnsureConnectedLockedAsync(CancellationToken cancellationToken)
    {
        if (_socket is { State: WebSocketState.Open })
        {
            return _socket;
        }

        ResetSocketLocked();

        var settings = _store.Load().Obs;
        var endpoint = NormalizeEndpoint(settings.Host, settings.Port);
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(endpoint.Uri, cancellationToken).ConfigureAwait(false);

            using var hello = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false);
            var helloRoot = hello.RootElement;
            if (ReadInt(helloRoot, "op") != 0)
            {
                throw new InvalidOperationException("OBS sent an unexpected handshake");
            }

            var helloData = helloRoot.GetProperty("d");
            var auth = "";
            if (helloData.TryGetProperty("authentication", out var authData))
            {
                if (string.IsNullOrEmpty(settings.Password))
                {
                    throw new ObsAuthException("OBS password is required");
                }

                auth = ObsProtocol.ComputeAuthentication(
                    settings.Password,
                    ReadString(authData, "salt"),
                    ReadString(authData, "challenge"));
            }

            await SendIdentifyAsync(socket, auth, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                using var identified = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false);
                var op = ReadInt(identified.RootElement, "op");
                if (op == 2)
                {
                    _socket = socket;
                    return socket;
                }
                if (op == 9)
                {
                    throw new ObsAuthException("OBS rejected the connection");
                }
            }
        }
        catch
        {
            socket.Abort();
            socket.Dispose();
            throw;
        }
    }

    private static async Task SendIdentifyAsync(ClientWebSocket socket, string authentication, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("op", 1);
            writer.WriteStartObject("d");
            writer.WriteNumber("rpcVersion", 1);
            if (authentication.Length > 0)
            {
                writer.WriteString("authentication", authentication);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        await socket.SendAsync(stream.ToArray(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendObsRequestAsync(
        ClientWebSocket socket,
        string requestType,
        string requestId,
        Action<Utf8JsonWriter>? writeRequestData,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("op", 6);
            writer.WriteStartObject("d");
            writer.WriteString("requestType", requestType);
            writer.WriteString("requestId", requestId);
            if (writeRequestData is not null)
            {
                writer.WritePropertyName("requestData");
                writer.WriteStartObject();
                writeRequestData(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        await socket.SendAsync(stream.ToArray(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // OBS rejects a bad password by closing with 4009
                // (AuthenticationFailed) rather than by sending a reject op,
                // so the close code is the only signal that this is auth and
                // not a transport fault. 4011 is the same class (identify
                // rejected for an unsupported RPC version).
                var code = (int?)socket.CloseStatus ?? 0;
                if (code is 4009 or 4011)
                {
                    throw new ObsAuthException("OBS rejected the password");
                }
                throw new InvalidOperationException("OBS closed the connection");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return JsonDocument.Parse(stream.ToArray());
            }
        }
    }

    private void ResetSocketLocked()
    {
        try
        {
            _socket?.Abort();
            _socket?.Dispose();
        }
        catch
        {
            // Best-effort cleanup only.
        }
        _socket = null;
    }

    private ObsStatusResponse Disconnected(string message, string reason)
    {
        var settings = _store.Load().Obs;
        return new ObsStatusResponse
        {
            Error = true,
            Msg = message,
            Reason = reason,
            Connected = false,
            Host = NormalizeHostForDisplay(settings.Host),
            Port = NormalizePort(settings.Port),
        };
    }

    /// <summary>Raised where OBS wants credentials we cannot satisfy, so the failure is not retryable without user input.</summary>
    private sealed class ObsAuthException : Exception
    {
        public ObsAuthException(string message) : base(message) { }
    }

    /// <summary>
    /// Maps a connect failure to the wire reason. A refused or reset socket
    /// means nothing is listening, which on this port is OBS being closed or
    /// its WebSocket server switched off - both fixed by the user, not a retry.
    /// </summary>
    private static string ClassifyReason(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is ObsAuthException)
            {
                return "auth";
            }
            if (e is System.Net.Sockets.SocketException or WebSocketException)
            {
                return "offline";
            }
        }
        return "error";
    }

    private static List<ObsScene> ReadScenes(JsonElement data)
    {
        var scenes = new List<ObsScene>();
        if (!data.TryGetProperty("scenes", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return scenes;
        }

        foreach (var item in array.EnumerateArray())
        {
            var name = ReadString(item, "sceneName");
            if (name.Length == 0)
            {
                continue;
            }

            scenes.Add(new ObsScene
            {
                Name = name,
                Uuid = ReadString(item, "sceneUuid"),
            });
        }
        return scenes;
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
    }

    /// <summary>
    /// OBS flips the output a moment after acknowledging a toggle, so the
    /// status re-read that follows can still report the previous value. The
    /// toggle's own <c>outputActive</c> is the new state, so it wins.
    /// </summary>
    private static bool? ApplyToggledOutput(JsonElement toggled, ObsStatusResponse status, bool recording)
    {
        if (toggled.ValueKind != JsonValueKind.Object ||
            !toggled.TryGetProperty("outputActive", out var property) ||
            (property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False))
        {
            return null;
        }

        var active = property.ValueKind == JsonValueKind.True;
        if (recording)
        {
            status.Recording = active;
            if (!active)
            {
                status.RecordingDurationMs = 0;
            }
        }
        else
        {
            status.Streaming = active;
            if (!active)
            {
                status.StreamingDurationMs = 0;
            }
        }
        return active;
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.True;
    }

    private static int ReadInt(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static long ReadLong(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
        {
            return 0;
        }

        if (property.TryGetInt64(out var value))
        {
            return value;
        }
        if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out value))
        {
            return value;
        }
        return 0;
    }

    private static int NormalizePort(int port) => port > 0 && port < 65536 ? port : 4455;

    private static string NormalizeHostForDisplay(string host)
    {
        var endpoint = NormalizeEndpoint(host, 4455);
        return endpoint.Host;
    }

    private static (Uri Uri, string Host) NormalizeEndpoint(string host, int port)
    {
        var trimmed = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == "ws" || absolute.Scheme == "wss" || absolute.Scheme == "http" || absolute.Scheme == "https"))
        {
            var scheme = absolute.Scheme is "wss" or "https" ? "wss" : "ws";
            var uriPort = absolute.IsDefaultPort ? NormalizePort(port) : absolute.Port;
            return (new UriBuilder(scheme, absolute.Host, uriPort).Uri, absolute.Host);
        }

        var slash = trimmed.IndexOf('/');
        if (slash >= 0)
        {
            trimmed = trimmed[..slash];
        }

        var hostPart = trimmed;
        var portPart = NormalizePort(port);
        var colon = trimmed.LastIndexOf(':');
        if (colon > 0 && int.TryParse(trimmed[(colon + 1)..], out var parsedPort))
        {
            hostPart = trimmed[..colon];
            portPart = NormalizePort(parsedPort);
        }

        hostPart = string.IsNullOrWhiteSpace(hostPart) ? "127.0.0.1" : hostPart;
        return (new UriBuilder("ws", hostPart, portPart).Uri, hostPart);
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            ResetSocketLocked();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
