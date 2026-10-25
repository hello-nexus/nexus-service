using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nexus.Service.Panel.Streams;

namespace Nexus.Service.Platform.Linux.Render;

public sealed class RawFrameIngestClient : IDisposable
{
    private const int ChannelCapacity = 8;
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(5);

    private readonly Channel<(byte[] Payload, byte Flags)> _channel;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private int _faulted;
    private long _lastSendTicks;

    public event Action? Faulted;

    public RawFrameIngestClient(string serviceOrigin, string sessionId, string token)
        : this(serviceOrigin, sessionId, token, null)
    {
    }

    internal RawFrameIngestClient(string serviceOrigin, string sessionId, string token, HttpMessageHandler? handler)
    {
        _channel = Channel.CreateBounded<(byte[], byte)>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{serviceOrigin}/panel/streams/{Uri.EscapeDataString(sessionId)}/ingest")
        {
            Content = new FrameStreamContent(this),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        _lastSendTicks = Environment.TickCount64;
        _ = RunAsync(request);
        _ = KeepaliveAsync();
    }

    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

    public void SendFrame(byte[] payload)
    {
        Volatile.Write(ref _lastSendTicks, Environment.TickCount64);
        _channel.Writer.TryWrite((payload, StreamFraming.FlagIdr));
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _faulted, 1);
        _cts.Cancel();
        _channel.Writer.TryComplete();
        _http.Dispose();
    }

    private async Task RunAsync(HttpRequestMessage request)
    {
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
            or HttpRequestException or IOException)
        {
        }
        Fault();
    }

    private async Task KeepaliveAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(KeepaliveInterval, _cts.Token).ConfigureAwait(false);
                if (Environment.TickCount64 - Volatile.Read(ref _lastSendTicks) >= (long)KeepaliveInterval.TotalMilliseconds)
                {
                    _channel.Writer.TryWrite((Array.Empty<byte>(), StreamFraming.FlagControl));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Fault()
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0)
        {
            return;
        }
        try
        {
            Faulted?.Invoke();
        }
        catch
        {
        }
    }

    internal static async Task WriteFrameAsync(Stream stream, byte[] payload, byte flags, CancellationToken ct)
    {
        var header = new byte[StreamFraming.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        header[4] = flags;
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private sealed class FrameStreamContent : HttpContent
    {
        private readonly RawFrameIngestClient _owner;

        public FrameStreamContent(RawFrameIngestClient owner)
        {
            _owner = owner;
            Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var ct = _owner._cts.Token;
            try
            {
                await foreach (var (payload, flags) in _owner._channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    await WriteFrameAsync(stream, payload, flags, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
