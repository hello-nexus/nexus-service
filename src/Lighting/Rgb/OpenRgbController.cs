using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// TCP client for the bundled OpenRGB daemon, SDK protocol 6 only. One reader
/// task per connection drains everything the daemon sends (replies, an ACK for
/// every packet, controller update broadcasts for every write, device-list and
/// detection notifications); a daemon blocked sending to a client that stops
/// reading stalls its own threads, so nothing here may leave the socket unread.
///
/// Requests run one at a time and complete on their ACK, which the daemon sends
/// after the reply: a request whose reply never comes (an id that vanished)
/// still completes, and an ACK can never leak into the next request. Frame
/// pushes are writes only; a refused one surfaces as <see cref="WriteRejected"/>.
///
/// AOT: pure System.Net.Sockets + ArrayPool, zero reflection.
/// </summary>
public sealed class OpenRgbController : IRgbController
{
    private const string ClientName = "nexus";
    private const uint ClientFlags = OpenRgbProtocol.ClientFlagSupportsRgbController
        | OpenRgbProtocol.ClientFlagSupportsSettingsManager
        | OpenRgbProtocol.ClientFlagRequestLocalClient;
    private const uint MaxPacketSize = 8 * 1024 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    // A request's reply + ACK. The daemon answers from memory, so missing this means its listen thread is wedged.
    internal static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(3);
    // Each holder is bounded by IoTimeout; waiting longer means a queue behind a wedge.
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    // Handlers run off the reader, so a slow subscriber can never stop the socket
    // draining, and in arrival order, so a STARTED is never handled after its COMPLETE.
    private readonly Channel<Action> _events = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });

    private Session? _session;
    private bool _disposed;
    private bool _oldDaemonLogged;
    // Pushes already queued when the socket drops all fail; only the first is logged.
    private int _pushFailureBurst;

    private sealed class Session
    {
        public required TcpClient Tcp { get; init; }
        public required NetworkStream Stream { get; init; }
        public readonly CancellationTokenSource ReaderCts = new();
        public volatile Pending? Pending;
        public volatile bool Closed;
        // Set once the version is negotiated: the daemon reads a packet sent earlier at protocol 0, where the address is a list index.
        public volatile bool Ready;
        public bool LocalClient;
    }

    private sealed class Pending
    {
        public required OpenRgbProtocol.PacketId Request { get; init; }
        public required uint Address { get; init; }
        /// <summary>Reply packet expected before the ACK; null for an ACK-only request.</summary>
        public OpenRgbProtocol.PacketId? Reply { get; init; }
        /// <summary>Complete on the reply alone (the version handshake, before the daemon is known to ACK).</summary>
        public bool ReplyOnly { get; init; }
        public byte[]? Body;
        public readonly TaskCompletionSource<(byte[]? body, uint status)> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public OpenRgbController(string host = "127.0.0.1", int port = 6742)
    {
        _host = host;
        _port = port;
        _ = Task.Run(DispatchEventsAsync);
    }

    private async Task DispatchEventsAsync()
    {
        await foreach (var raise in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            { raise(); }
            catch (Exception ex) { ServiceLog.Warn($"[openrgb] event handler threw: {ex.Message}"); }
        }
    }

    public bool IsConnected => Volatile.Read(ref _session) is { Closed: false, Tcp.Connected: true };

    public event Action? DeviceListChanged;
    public event Action<bool>? DetectionStateChanged;
    public event Action<int, uint, uint>? WriteRejected;

    public async Task<bool> TryConnectAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return false;
        }
        if (IsConnected)
        {
            return true;
        }
        if (!await _requestLock.WaitAsync(LockWait, ct).ConfigureAwait(false))
        {
            return false;
        }
        try
        {
            CloseSession();

            var tcp = new TcpClient { NoDelay = true };
            NetworkStream stream;
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(ConnectTimeout);
                await tcp.ConnectAsync(_host, _port, connectCts.Token).ConfigureAwait(false);
                stream = tcp.GetStream();
            }
            catch
            {
                tcp.Dispose();
                return false;
            }

            var session = new Session { Tcp = tcp, Stream = stream };
            Volatile.Write(ref _session, session);
            _ = Task.Run(() => ReadLoopAsync(session));

            try
            {
                var (versionBody, _) = await RequestLockedAsync(session, OpenRgbProtocol.PacketId.RequestProtocolVersion, 0,
                    OpenRgbProtocol.BuildProtocolVersionBody(OpenRgbProtocol.CurrentProtocolVersion),
                    OpenRgbProtocol.PacketId.RequestProtocolVersion, replyOnly: true, ct).ConfigureAwait(false);
                var serverVersion = versionBody is { Length: >= 4 } ? BinaryPrimitives.ReadUInt32LittleEndian(versionBody) : 0;
                if (serverVersion < OpenRgbProtocol.CurrentProtocolVersion)
                {
                    if (!_oldDaemonLogged)
                    {
                        _oldDaemonLogged = true;
                        ServiceLog.Error($"[openrgb] daemon speaks SDK protocol {serverVersion}; Nexus needs {OpenRgbProtocol.CurrentProtocolVersion}. The bundled OpenRGB is out of date.");
                    }
                    CloseSession();
                    return false;
                }

                await RequestLockedAsync(session, OpenRgbProtocol.PacketId.SetClientName, 0,
                    OpenRgbProtocol.BuildSetClientNameBody(ClientName), reply: null, replyOnly: false, ct).ConfigureAwait(false);
                var (flagsBody, _) = await RequestLockedAsync(session, OpenRgbProtocol.PacketId.SetClientFlags, 0,
                    OpenRgbProtocol.BuildClientFlagsBody(ClientFlags), OpenRgbProtocol.PacketId.SetServerFlags, replyOnly: false, ct).ConfigureAwait(false);
                session.LocalClient = flagsBody is { Length: >= 4 }
                    && (BinaryPrimitives.ReadUInt32LittleEndian(flagsBody) & OpenRgbProtocol.ServerFlagLocalClient) != 0;
                session.Ready = true;
            }
            catch
            {
                CloseSession();
                return false;
            }

            var suppressed = _pushFailureBurst;
            _pushFailureBurst = 0;
            if (suppressed > 1)
            {
                ServiceLog.Warn($"[openrgb] reconnected; {suppressed - 1} further push-frame failure(s) were not logged");
            }
        }
        finally
        {
            _requestLock.Release();
        }

        Raise(() => DeviceListChanged?.Invoke());
        return true;
    }

    public Task DisconnectAsync()
    {
        CloseSession();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default)
    {
        var session = Volatile.Read(ref _session);
        if (session is null || session.Closed)
        {
            return Array.Empty<int>();
        }
        await WaitRequestLockAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadAddressesLockedAsync(session, ct).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        var session = Volatile.Read(ref _session);
        if (session is null || session.Closed)
        {
            return Array.Empty<RgbDevice>();
        }
        await WaitRequestLockAsync(ct).ConfigureAwait(false);
        try
        {
            var addresses = await ReadAddressesLockedAsync(session, ct).ConfigureAwait(false);
            var devices = new List<RgbDevice>(addresses.Count);
            var dataBody = OpenRgbProtocol.BuildRequestControllerDataBody(OpenRgbProtocol.CurrentProtocolVersion);
            for (var i = 0; i < addresses.Count; i++)
            {
                var (body, _) = await RequestLockedAsync(session, OpenRgbProtocol.PacketId.RequestControllerData, (uint)addresses[i],
                    dataBody, OpenRgbProtocol.PacketId.RequestControllerData, replyOnly: false, ct).ConfigureAwait(false);
                if (body is null)
                {
                    // Removed between the count and this request.
                    continue;
                }
                try
                {
                    var dev = OpenRgbProtocol.ParseControllerData(i, body, OpenRgbProtocol.CurrentProtocolVersion);
                    dev.Address = addresses[i];
                    devices.Add(dev);
                }
                catch (Exception ex)
                {
                    ServiceLog.Warn($"[openrgb] failed to parse controller id={addresses[i]} ({body.Length}B): {ex.GetType().Name}: {ex.Message}");
                }
            }
            return devices;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default)
    {
        var mode = device.FindCustomMode();
        if (mode is not null)
        {
            // UPDATE_MODE runs DeviceUpdateMode on the controller, which ENE-style
            // controllers need before they honour per-LED writes.
            await SendAsync((uint)device.Address, OpenRgbProtocol.PacketId.RgbControllerUpdateMode,
                OpenRgbProtocol.BuildUpdateModeBody(mode.Index, mode.Bytes), ct).ConfigureAwait(false);
        }
        else
        {
            await SendAsync((uint)device.Address, OpenRgbProtocol.PacketId.SetCustomMode, null, ct).ConfigureAwait(false);
        }
    }

    public async Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default)
    {
        if (colors.Length == 0)
        {
            return;
        }
        try
        {
            await SendAsync((uint)address, OpenRgbProtocol.PacketId.RgbControllerUpdateLeds,
                OpenRgbProtocol.BuildUpdateLedsBody(colors.Span), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            if (++_pushFailureBurst == 1)
            {
                ServiceLog.Warn($"[openrgb] push frame to controller {address} failed: {ex.Message}");
            }
        }
    }

    public async Task SetOffAsync(int address, int ledCount, CancellationToken ct = default)
    {
        if (ledCount <= 0)
        {
            return;
        }
        var pool = ArrayPool<RgbColor>.Shared.Rent(ledCount);
        try
        {
            Array.Clear(pool, 0, ledCount);
            await PushFrameAsync(address, pool.AsMemory(0, ledCount), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<RgbColor>.Shared.Return(pool);
        }
    }

    public async Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default)
    {
        if (colors.Length == 0 || zoneIndex < 0)
        {
            return;
        }
        try
        {
            await SendAsync((uint)address, OpenRgbProtocol.PacketId.RgbControllerUpdateZoneLeds,
                OpenRgbProtocol.BuildUpdateZoneLedsBody((uint)zoneIndex, colors.Span), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            ServiceLog.Warn($"[openrgb] push zone frame to controller {address} zone {zoneIndex} failed: {ex.Message}");
        }
    }

    public async Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default)
    {
        if (zoneIndex < 0 || newSize < 0)
        {
            return;
        }
        var session = Volatile.Read(ref _session);
        if (session is null || session.Closed)
        {
            return;
        }
        await WaitRequestLockAsync(ct).ConfigureAwait(false);
        try
        {
            // Awaited so the caller's re-fetch sees the new size.
            var (_, status) = await RequestLockedAsync(session, OpenRgbProtocol.PacketId.RgbControllerResizeZone, (uint)address,
                OpenRgbProtocol.BuildResizeZoneBody(zoneIndex, (uint)newSize), reply: null, replyOnly: false, ct).ConfigureAwait(false);
            if (status != OpenRgbProtocol.StatusOk)
            {
                ServiceLog.Warn($"[openrgb] resize controller {address} zone {zoneIndex} -> {newSize} refused (status {status})");
            }
        }
        catch (IOException ex)
        {
            ServiceLog.Warn($"[openrgb] resize controller {address} zone {zoneIndex} -> {newSize} failed: {ex.Message}");
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task<bool> RescanAsync(CancellationToken ct = default)
    {
        var session = Volatile.Read(ref _session);
        if (session is null || session.Closed)
        {
            return false;
        }
        await WaitRequestLockAsync(ct).ConfigureAwait(false);
        try
        {
            var (_, status) = await RequestLockedAsync(session, OpenRgbProtocol.PacketId.RequestRescanDevices, 0,
                null, reply: null, replyOnly: false, ct).ConfigureAwait(false);
            return status == OpenRgbProtocol.StatusOk;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task<bool> SetSettingsAsync(string key, string valueJson, CancellationToken ct = default)
    {
        var session = Volatile.Read(ref _session);
        if (session is null || session.Closed || !session.LocalClient)
        {
            return false;
        }
        // {"<key>": <value>}: the daemon replaces each top-level key it is sent.
        var body = Encoding.UTF8.GetBytes("{\"" + System.Text.Json.JsonEncodedText.Encode(key) + "\":" + valueJson + "}");
        await WaitRequestLockAsync(ct).ConfigureAwait(false);
        try
        {
            var (_, status) = await RequestLockedAsync(session, OpenRgbProtocol.PacketId.SettingsManagerSetSettings, 0,
                body, reply: null, replyOnly: false, ct).ConfigureAwait(false);
            return status == OpenRgbProtocol.StatusOk;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    // ── request / send plumbing ───────────────────────────────────────────

    private async Task WaitRequestLockAsync(CancellationToken ct)
    {
        if (!await _requestLock.WaitAsync(LockWait, ct).ConfigureAwait(false))
        {
            throw new IOException($"OpenRGB socket busy for {LockWait.TotalSeconds:F0}s");
        }
    }

    private async Task<IReadOnlyList<int>> ReadAddressesLockedAsync(Session session, CancellationToken ct)
    {
        var (body, _) = await RequestLockedAsync(session, OpenRgbProtocol.PacketId.RequestControllerCount, 0, null,
            OpenRgbProtocol.PacketId.RequestControllerCount, replyOnly: false, ct).ConfigureAwait(false);
        return body is null ? Array.Empty<int>() : OpenRgbProtocol.ParseControllerAddresses(body, OpenRgbProtocol.CurrentProtocolVersion);
    }

    /// <summary>Send one request and wait for its completion (caller holds <see cref="_requestLock"/>). A timeout drops the connection.</summary>
    private async Task<(byte[]? body, uint status)> RequestLockedAsync(Session session, OpenRgbProtocol.PacketId request, uint address,
        byte[]? body, OpenRgbProtocol.PacketId? reply, bool replyOnly, CancellationToken ct)
    {
        var pending = new Pending { Request = request, Address = address, Reply = reply, ReplyOnly = replyOnly };
        session.Pending = pending;
        try
        {
            await SendOnSessionAsync(session, address, request, body, ct).ConfigureAwait(false);
            try
            {
                return await pending.Done.Task.WaitAsync(IoTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                CloseSession(session);
                throw new IOException($"OpenRGB read timed out after {IoTimeout.TotalSeconds:F0}s");
            }
        }
        finally
        {
            session.Pending = null;
        }
    }

    private async Task SendAsync(uint address, OpenRgbProtocol.PacketId packetId, byte[]? body, CancellationToken ct)
    {
        var session = Volatile.Read(ref _session);
        if (session is null || session.Closed || !session.Ready)
        {
            return;
        }
        await SendOnSessionAsync(session, address, packetId, body, ct).ConfigureAwait(false);
    }

    private async Task SendOnSessionAsync(Session session, uint address, OpenRgbProtocol.PacketId packetId, byte[]? body, CancellationToken ct)
    {
        // A frame that misses the bound is dropped rather than queued behind a wedged peer.
        if (!await _sendLock.WaitAsync(LockWait, ct).ConfigureAwait(false))
        {
            throw new IOException($"OpenRGB socket busy for {LockWait.TotalSeconds:F0}s");
        }
        var bodyLen = body?.Length ?? 0;
        var total = OpenRgbProtocol.HeaderSize + bodyLen;
        var buffer = ArrayPool<byte>.Shared.Rent(total);
        var timedOut = false;
        try
        {
            if (session.Closed)
            {
                throw new IOException("not connected");
            }
            OpenRgbProtocol.WriteHeader(buffer.AsSpan(0, OpenRgbProtocol.HeaderSize), address, packetId, (uint)bodyLen);
            if (bodyLen > 0)
            {
                Buffer.BlockCopy(body!, 0, buffer, OpenRgbProtocol.HeaderSize, bodyLen);
            }
            var write = session.Stream.WriteAsync(buffer.AsMemory(0, total), ct);
            if (write.IsCompleted)
            {
                await write.ConfigureAwait(false);
            }
            else
            {
                // A write parks only once the peer stops draining and the send buffer is full.
                try
                { await write.AsTask().WaitAsync(IoTimeout).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    timedOut = true;
                    CloseSession(session);
                    throw new IOException($"OpenRGB write timed out after {IoTimeout.TotalSeconds:F0}s");
                }
            }
        }
        catch (Exception ex) when (ex is not IOException && ex is not OperationCanceledException)
        {
            CloseSession(session);
            throw new IOException(ex.Message, ex);
        }
        finally
        {
            // An abandoned write still references the buffer until the socket is torn down.
            if (!timedOut)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            _sendLock.Release();
        }
    }

    // ── reader ────────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(Session session)
    {
        var header = new byte[OpenRgbProtocol.HeaderSize];
        try
        {
            while (!session.Closed)
            {
                await ReadExactAsync(session, header, header.Length).ConfigureAwait(false);
                var (address, packetId, size) = OpenRgbProtocol.ReadHeader(header);
                if (size > MaxPacketSize)
                {
                    throw new IOException($"OpenRGB packet {(uint)packetId} claims {size} bytes; stream out of sync");
                }

                if (packetId == OpenRgbProtocol.PacketId.RgbControllerSignalUpdate)
                {
                    // Broadcast after every write, ours included; nothing here needs it.
                    var scratch = ArrayPool<byte>.Shared.Rent((int)Math.Max(size, 1));
                    try
                    { await ReadExactAsync(session, scratch, (int)size).ConfigureAwait(false); }
                    finally { ArrayPool<byte>.Shared.Return(scratch); }
                    continue;
                }

                var body = size == 0 ? Array.Empty<byte>() : new byte[size];
                if (size > 0)
                {
                    await ReadExactAsync(session, body, body.Length).ConfigureAwait(false);
                }
                Dispatch(session, address, packetId, body);
            }
        }
        catch (Exception ex)
        {
            // Info: a stop or restart kills the daemon under the reader, and a
            // crash is already reported by the process manager.
            if (!session.Closed)
            {
                ServiceLog.Info($"[openrgb] connection lost: {ex.Message}");
            }
        }
        finally
        {
            CloseSession(session);
        }
    }

    private void Dispatch(Session session, uint address, OpenRgbProtocol.PacketId packetId, byte[] body)
    {
        var pending = session.Pending;
        if (pending is not null && pending.Reply == packetId && pending.Address == address)
        {
            pending.Body = body;
            if (pending.ReplyOnly)
            {
                pending.Done.TrySetResult((body, OpenRgbProtocol.StatusOk));
            }
            return;
        }

        switch (packetId)
        {
            case OpenRgbProtocol.PacketId.Ack:
            {
                var (acked, status) = OpenRgbProtocol.ParseAck(body);
                if (pending is not null && !pending.ReplyOnly && pending.Request == acked && pending.Address == address)
                {
                    pending.Done.TrySetResult((pending.Body, status));
                    return;
                }
                if (status != OpenRgbProtocol.StatusOk && IsWrite(acked))
                {
                    Raise(() => WriteRejected?.Invoke((int)address, (uint)acked, status));
                }
                return;
            }
            case OpenRgbProtocol.PacketId.DeviceListUpdated:
                Raise(() => DeviceListChanged?.Invoke());
                return;
            case OpenRgbProtocol.PacketId.DetectionStarted:
            case OpenRgbProtocol.PacketId.DetectionProgressChanged:
                Raise(() => DetectionStateChanged?.Invoke(true));
                return;
            case OpenRgbProtocol.PacketId.DetectionComplete:
                Raise(() => DetectionStateChanged?.Invoke(false));
                return;
            default:
                // Server name, progress, profile/log broadcasts: nothing to act on.
                return;
        }
    }

    private static bool IsWrite(OpenRgbProtocol.PacketId id) => id is OpenRgbProtocol.PacketId.RgbControllerUpdateLeds
        or OpenRgbProtocol.PacketId.RgbControllerUpdateZoneLeds
        or OpenRgbProtocol.PacketId.RgbControllerUpdateMode
        or OpenRgbProtocol.PacketId.SetCustomMode
        or OpenRgbProtocol.PacketId.RgbControllerResizeZone;

    private void Raise(Action raise) => _events.Writer.TryWrite(raise);

    private static async Task ReadExactAsync(Session session, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = await session.Stream.ReadAsync(buffer.AsMemory(read, count - read), session.ReaderCts.Token).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("OpenRGB connection closed");
            }
            read += n;
        }
    }

    private void CloseSession() => CloseSession(Volatile.Read(ref _session));

    private void CloseSession(Session? session)
    {
        if (session is null)
        {
            return;
        }
        Interlocked.CompareExchange(ref _session, null, session);
        if (session.Closed)
        {
            return;
        }
        session.Closed = true;
        session.Pending?.Done.TrySetException(new IOException("OpenRGB connection closed"));
        try
        { session.ReaderCts.Cancel(); }
        catch { }
        try
        { session.Stream.Dispose(); }
        catch { }
        try
        { session.Tcp.Dispose(); }
        catch { }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        CloseSession();
        _events.Writer.TryComplete();
        return default;
    }
}
