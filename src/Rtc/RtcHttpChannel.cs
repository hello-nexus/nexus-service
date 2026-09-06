using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nexus.Service.Models.Panel;
using Nexus.Service.Relay;
using Nexus.Service.Serialization;
using SIPSorcery.Net;

namespace Nexus.Service.Rtc;

/// <summary>
/// REST-over-P2P tunnel leg driven by the "http" data channel. Same sealed
/// framing, counters, replay guard, and in-flight cap as
/// <c>RelayConnectionService.HttpChannel</c>; sends sealed frames via
/// <see cref="RTCDataChannel.send(byte[], int, int)"/> instead of a
/// WebSocket transport's SendAsync.
/// </summary>
public sealed class RtcHttpChannel : IDisposable
{
    // Matches RelayConnectionService.MaxConcurrentHttpRequests.
    private const int MaxConcurrentHttpRequests = 32;

    // Matches RTCSctpTransport.SCTP_DEFAULT_MAX_MESSAGE_SIZE; RTCDataChannel.send
    // throws past this, so a response must be checked before it reaches send.
    internal const int MaxFrameBytes = 256 * 1024;
    // RelayCrypto.Seal frame = nonce(12) || ciphertext || tag(16).
    internal const int SealOverheadBytes = RelayCrypto.NonceLength + RelayCrypto.TagLength;

    private readonly RelayHttpDispatcher _httpDispatcher;
    private readonly RTCDataChannel _channel;
    // Inbound state is mutated only from OnRequestFrame (SIPSorcery invokes
    // onmessage serially, one SCTP receive thread per association); the rekey's
    // key switch runs under _sendLock so concurrent replies see it.
    private readonly SealedChannelKeys _keys;
    private readonly string _sessionId;
    private readonly ILogger _log;
    private readonly CancellationToken _ct;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _inFlight = new(MaxConcurrentHttpRequests, MaxConcurrentHttpRequests);
    private volatile bool _disposed;

    public RtcHttpChannel(
        RelayHttpDispatcher httpDispatcher, RTCDataChannel channel, byte[] aeadKey,
        string sessionId, ILogger log, CancellationToken ct, Func<byte[], byte[]>? rekeyDerive = null)
    {
        _httpDispatcher = httpDispatcher;
        _channel = channel;
        _keys = new SealedChannelKeys(aeadKey, rekeyDerive);
        _sessionId = sessionId;
        _log = log;
        _ct = ct;
        _channel.onmessage += OnMessage;
    }

    private void OnMessage(RTCDataChannel dc, DataChannelPayloadProtocols proto, byte[] data)
    {
        if (proto != DataChannelPayloadProtocols.WebRTC_Binary)
            return;
        OnRequestFrame(data);
    }

    /// <summary>
    /// Decrypt one inbound sealed request frame (dir=2, non-replayed counter)
    /// and dispatch it on a background task. A bad direction / replayed
    /// counter / tamper is dropped silently, matching the relay HTTP leg.
    /// </summary>
    private void OnRequestFrame(byte[] frame)
    {
        if (_disposed)
            return;

        RelayHttpRequest? request;
        try
        {
            var kind = _keys.Open(frame, RelayCrypto.DirClientToHost, out var plaintext);
            if (kind == SealedChannelKeys.InboundKind.Rejected)
                return;
            if (kind == SealedChannelKeys.InboundKind.RekeyRequest)
            {
                BeginRekey();
                return;
            }
            request = JsonSerializer.Deserialize(plaintext, AppJsonContext.Default.RelayHttpRequest);
        }
        catch (CryptographicException)
        {
            return;
        }
        catch (JsonException)
        {
            return;
        }

        if (request is null)
            return;

        if (!_inFlight.Wait(0))
        {
            _ = SendBusyAsync(request.Id);
            return;
        }

        _ = DispatchAndReplyAsync(request);
    }

    // The send lock spans the key switch and the host-nonce send, so no frame
    // sealed under the rekeyed key can precede it.
    private void BeginRekey()
    {
        try
        {
            _sendLock.Wait(_ct);
        }
        catch (Exception)
        {
            return; // disposed or cancelled during teardown
        }
        try
        {
            var frame = _keys.SealHostNonceAndRekey(RelayCrypto.DirHostToClient);
            if (!_disposed && _channel.readyState == RTCDataChannelState.open)
                _channel.send(frame);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "rtc http rekey reply failed");
        }
        finally
        {
            try { _sendLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task DispatchAndReplyAsync(RelayHttpRequest request)
    {
        try
        {
            var response = await _httpDispatcher.DispatchAsync(request, _sessionId, _ct).ConfigureAwait(false);
            await SendSealedAsync(response).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "rtc http dispatch failed for id {Id}", request.Id);
        }
        finally
        {
            _inFlight.Release();
        }
    }

    private async Task SendBusyAsync(int id)
    {
        var response = new RelayHttpResponse
        {
            Id = id,
            Status = StatusCodes.Status503ServiceUnavailable,
            Body = "{\"error\":true,\"msg\":\"too many concurrent rtc requests\"}",
            ContentType = "application/json",
        };
        try { await SendSealedAsync(response).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "rtc http busy reply failed"); }
    }

    private async Task SendSealedAsync(RelayHttpResponse response)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(response, AppJsonContext.Default.RelayHttpResponse);
        if (ExceedsFrameCap(json.Length))
        {
            response = RejectOversized(response.Id);
            json = JsonSerializer.SerializeToUtf8Bytes(response, AppJsonContext.Default.RelayHttpResponse);
        }

        await _sendLock.WaitAsync(_ct).ConfigureAwait(false);
        try
        {
            if (_disposed || _channel.readyState != RTCDataChannelState.open)
                return;
            var frame = _keys.Seal(RelayCrypto.DirHostToClient, json);
            _channel.send(frame);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>True when a plaintext response of this length would seal past the data channel's max message size.</summary>
    internal static bool ExceedsFrameCap(int jsonByteLength) => jsonByteLength + SealOverheadBytes > MaxFrameBytes;

    internal static RelayHttpResponse RejectOversized(int id) => new()
    {
        Id = id,
        Status = StatusCodes.Status413PayloadTooLarge,
        Body = "{\"error\":true,\"msg\":\"response exceeds direct-channel cap\"}",
        ContentType = "application/json",
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _channel.onmessage -= OnMessage;
        _sendLock.Dispose();
        _inFlight.Dispose();
    }
}
