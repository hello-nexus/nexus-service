using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// TCP client for the OpenRGB SDK server. Connects to 127.0.0.1:6742 by default,
/// performs the handshake (SET_CLIENT_NAME + REQUEST_PROTOCOL_VERSION), and exposes
/// device-list / push-frame operations.
///
/// Thread-safety: every public operation acquires <see cref="_writeLock"/> for the
/// duration of the request/response round-trip. The OpenRGB protocol is not multiplexed,
/// so we serialize all access to the single TCP socket.
///
/// AOT: pure System.Net.Sockets + ArrayPool, zero reflection, zero JSON.
/// </summary>
public sealed class OpenRgbController : IRgbController
{
    private const string ClientName = "nexus";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    // Per socket read/write. The daemon replies from memory under a shared lock even
    // mid-detection, so a reply this slow means its listen thread is wedged.
    internal static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(3);
    // Each lock holder is bounded by IoTimeout; waiting longer means a queue behind a wedge.
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisconnectLockWait = TimeSpan.FromSeconds(1);

    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private uint _protocolVersion;
    private bool _disposed;
    // Pushes already queued on _writeLock when the socket drops all reach the
    // catch below; only the first is logged and the rest counted.
    private int _pushFailureBurst;

    public OpenRgbController(string host = "127.0.0.1", int port = 6742)
    {
        _host = host;
        _port = port;
    }

    public bool IsConnected
    {
        get
        {
            // Volatile reads to avoid torn observation; the cleanup path
            // sets both fields to null while holding _writeLock, so other
            // threads might briefly observe a half-disposed state otherwise.
            var tcp = Volatile.Read(ref _tcp);
            var stream = Volatile.Read(ref _stream);
            return tcp is { Connected: true } && stream is not null;
        }
    }

    public event Action? DeviceListChanged;

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

        try
        { await WaitLockAsync(ct).ConfigureAwait(false); }
        catch (IOException)
        {
            return false;
        }
        try
        {
            CleanupSocketLocked();

            var tcp = new TcpClient { NoDelay = true };
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(ConnectTimeout);
                await tcp.ConnectAsync(_host, _port, connectCts.Token).ConfigureAwait(false);
            }
            catch
            {
                tcp.Dispose();
                return false;
            }

            // NetworkStream.ReadTimeout/WriteTimeout govern synchronous I/O only; the
            // async bound lives in ReadExactLockedAsync / SendPacketLockedAsync.
            NetworkStream stream;
            try
            { stream = tcp.GetStream(); }
            catch
            {
                tcp.Dispose();
                return false;
            }
            _tcp = tcp;
            _stream = stream;

            try
            {
                await SendPacketLockedAsync(deviceIndex: 0,
                    OpenRgbProtocol.PacketId.SetClientName,
                    OpenRgbProtocol.BuildSetClientNameBody(ClientName),
                    ct).ConfigureAwait(false);

                // Negotiate protocol version
                await SendPacketLockedAsync(deviceIndex: 0,
                    OpenRgbProtocol.PacketId.RequestProtocolVersion,
                    OpenRgbProtocol.BuildProtocolVersionBody(OpenRgbProtocol.CurrentProtocolVersion),
                    ct).ConfigureAwait(false);

                var (replyId, replyBody) = await ReadPacketLockedAsync(ct).ConfigureAwait(false);
                if (replyId != OpenRgbProtocol.PacketId.RequestProtocolVersion || replyBody.Length < 4)
                {
                    CleanupSocketLocked();
                    return false;
                }
                var serverVersion = BinaryPrimitives.ReadUInt32LittleEndian(replyBody.AsSpan(0, 4));
                _protocolVersion = Math.Min(OpenRgbProtocol.CurrentProtocolVersion, serverVersion);
            }
            catch
            {
                CleanupSocketLocked();
                return false;
            }

            var suppressed = _pushFailureBurst;
            _pushFailureBurst = 0;
            if (suppressed > 1)
            {
                ServiceLog.Warn($"[openrgb] reconnected; {suppressed - 1} further push-frame failure(s) were not logged");
            }

            // Surface a "list refreshed" tick to subscribers.
            try
            { DeviceListChanged?.Invoke(); }
            catch { }
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        if (await _writeLock.WaitAsync(DisconnectLockWait).ConfigureAwait(false))
        {
            try
            { CleanupSocketLocked(); }
            finally { _writeLock.Release(); }
            return;
        }
        // The holder is parked on a silent peer; closing the socket faults it, which releases the lock.
        ServiceLog.Warn($"[openrgb] disconnect: socket busy for {DisconnectLockWait.TotalSeconds:F0}s, closing it under the pending request");
        CleanupSocketLocked();
    }

    public async Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            return Array.Empty<RgbDevice>();
        }

        await WaitLockAsync(ct).ConfigureAwait(false);
        try
        {
            // Get controller count
            await SendPacketLockedAsync(0, OpenRgbProtocol.PacketId.RequestControllerCount, body: null, ct)
                .ConfigureAwait(false);
            var (countId, countBody) = await ReadExpectedLockedAsync(
                OpenRgbProtocol.PacketId.RequestControllerCount, expectedDeviceIndex: 0, ct).ConfigureAwait(false);
            if (countId != OpenRgbProtocol.PacketId.RequestControllerCount || countBody.Length < 4)
            {
                return Array.Empty<RgbDevice>();
            }

            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(countBody.AsSpan(0, 4));
            if (count <= 0)
            {
                return Array.Empty<RgbDevice>();
            }

            var devices = new List<RgbDevice>(count);
            for (int i = 0; i < count; i++)
            {
                await SendPacketLockedAsync((uint)i, OpenRgbProtocol.PacketId.RequestControllerData,
                    OpenRgbProtocol.BuildRequestControllerDataBody(_protocolVersion), ct).ConfigureAwait(false);
                var (replyId, replyBody) = await ReadExpectedLockedAsync(
                    OpenRgbProtocol.PacketId.RequestControllerData, expectedDeviceIndex: (uint)i, ct).ConfigureAwait(false);
                if (replyId != OpenRgbProtocol.PacketId.RequestControllerData)
                {
                    continue;
                }

                try
                {
                    var dev = OpenRgbProtocol.ParseControllerData(i, replyBody, _protocolVersion);
                    devices.Add(dev);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[openrgb] failed to parse controller {i} (protocol={_protocolVersion}, body={replyBody.Length}B): {ex.GetType().Name}: {ex.Message}");
                }
            }
            return devices;
        }
        catch (Exception) when (DropSocketLocked())
        {
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Exception filter (always false): a failed request leaves the stream mid-packet, so drop the socket and let the bridge reconnect.</summary>
    private bool DropSocketLocked()
    {
        CleanupSocketLocked();
        return false;
    }

    private async Task WaitLockAsync(CancellationToken ct)
    {
        if (!await _writeLock.WaitAsync(LockWait, ct).ConfigureAwait(false))
        {
            throw new IOException($"OpenRGB socket busy for {LockWait.TotalSeconds:F0}s");
        }
    }

    public async Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            return;
        }

        await WaitLockAsync(ct).ConfigureAwait(false);
        try
        {
            var mode = device.FindCustomMode();
            if (mode is not null)
            {
                // UPDATE_MODE: server runs SetModeDescription + UpdateMode, which
                // calls DeviceUpdateMode on the controller. Required for ENE-style
                // controllers that gate per-LED writes on the hardware mode register.
                var body = OpenRgbProtocol.BuildUpdateModeBody(mode.Index, mode.Bytes);
                await SendPacketLockedAsync((uint)device.Index,
                    OpenRgbProtocol.PacketId.RgbControllerUpdateMode, body, ct).ConfigureAwait(false);
            }
            else
            {
                // Defensive fallback for controllers that didn't expose a recognized
                // per-LED mode in their mode list. SET_CUSTOM_MODE has the right
                // semantics for those (most peripherals' UpdateLEDs path writes
                // per-LED registers directly without honoring active_mode).
                await SendPacketLockedAsync((uint)device.Index,
                    OpenRgbProtocol.PacketId.SetCustomMode, body: null, ct).ConfigureAwait(false);
            }
        }
        catch (Exception) when (DropSocketLocked())
        {
            throw;
        }
        finally { _writeLock.Release(); }
    }

    public async Task PushFrameAsync(int deviceIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default)
    {
        if (!IsConnected || colors.Length == 0)
        {
            return;
        }

        // Acquire BEFORE the try block - if WaitAsync throws (canceled/disposed)
        // we must NOT call Release on a semaphore we never acquired. A frame that
        // misses the bound is dropped rather than queued behind a wedged peer.
        if (!await _writeLock.WaitAsync(LockWait, ct).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            try
            {
                var body = OpenRgbProtocol.BuildUpdateLedsBody(colors.Span);
                await SendPacketLockedAsync((uint)deviceIndex,
                    OpenRgbProtocol.PacketId.RgbControllerUpdateLeds, body, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (++_pushFailureBurst == 1)
                {
                    ServiceLog.Warn($"[openrgb] push frame to device {deviceIndex} failed: {ex.Message}");
                }
                CleanupSocketLocked();
            }
        }
        finally { _writeLock.Release(); }
    }

    public async Task SetOffAsync(int deviceIndex, int ledCount, CancellationToken ct = default)
    {
        if (ledCount <= 0)
        {
            return;
        }

        var pool = ArrayPool<RgbColor>.Shared.Rent(ledCount);
        try
        {
            Array.Clear(pool, 0, ledCount);
            await PushFrameAsync(deviceIndex, pool.AsMemory(0, ledCount), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<RgbColor>.Shared.Return(pool);
        }
    }

    public async Task PushZoneFrameAsync(int deviceIndex, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default)
    {
        if (!IsConnected || colors.Length == 0 || zoneIndex < 0)
        {
            return;
        }

        await WaitLockAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                var body = OpenRgbProtocol.BuildUpdateZoneLedsBody((uint)zoneIndex, colors.Span);
                await SendPacketLockedAsync((uint)deviceIndex,
                    OpenRgbProtocol.PacketId.RgbControllerUpdateZoneLeds, body, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[openrgb] push zone frame to device {deviceIndex} zone {zoneIndex} failed: {ex.Message}");
                CleanupSocketLocked();
            }
        }
        finally { _writeLock.Release(); }
    }

    public async Task ResizeZoneAsync(int deviceIndex, int zoneIndex, int newSize, CancellationToken ct = default)
    {
        if (!IsConnected || zoneIndex < 0 || newSize < 0)
        {
            return;
        }

        await WaitLockAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                var body = OpenRgbProtocol.BuildResizeZoneBody(zoneIndex, (uint)newSize);
                await SendPacketLockedAsync((uint)deviceIndex,
                    OpenRgbProtocol.PacketId.RgbControllerResizeZone, body, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[openrgb] resize device {deviceIndex} zone {zoneIndex} -> {newSize} failed: {ex.Message}");
                CleanupSocketLocked();
            }
        }
        finally { _writeLock.Release(); }
    }

    // ── private helpers (called with _writeLock held) ─────────────────────

    private async Task SendPacketLockedAsync(uint deviceIndex, OpenRgbProtocol.PacketId packetId, byte[]? body, CancellationToken ct)
    {
        // Local copy: a bounded disconnect may null the field under us.
        var stream = _stream ?? throw new IOException("not connected");

        var bodyLen = body?.Length ?? 0;
        var total = OpenRgbProtocol.HeaderSize + bodyLen;
        var buffer = ArrayPool<byte>.Shared.Rent(total);
        var timedOut = false;
        try
        {
            OpenRgbProtocol.WriteHeader(buffer.AsSpan(0, OpenRgbProtocol.HeaderSize), deviceIndex, packetId, (uint)bodyLen);
            if (body is { Length: > 0 })
            {
                Buffer.BlockCopy(body, 0, buffer, OpenRgbProtocol.HeaderSize, bodyLen);
            }

            var write = stream.WriteAsync(buffer.AsMemory(0, total), ct);
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
                    throw new IOException($"OpenRGB write timed out after {IoTimeout.TotalSeconds:F0}s");
                }
            }
        }
        finally
        {
            // An abandoned write still references the buffer until the socket is torn down.
            if (!timedOut)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private async Task<(OpenRgbProtocol.PacketId id, byte[] body)> ReadPacketLockedAsync(CancellationToken ct)
    {
        var headerBuf = new byte[OpenRgbProtocol.HeaderSize];
        await ReadExactLockedAsync(headerBuf, ct).ConfigureAwait(false);
        var (_, packetId, dataSize) = OpenRgbProtocol.ReadHeader(headerBuf);

        var body = dataSize == 0 ? Array.Empty<byte>() : new byte[dataSize];
        if (dataSize > 0)
        {
            await ReadExactLockedAsync(body, ct).ConfigureAwait(false);
        }

        return (packetId, body);
    }

    /// <summary>
    /// Read packets from the stream, filtering out unsolicited notifications
    /// (DEVICE_LIST_UPDATED) until we get one matching <paramref name="expected"/>
    /// at <paramref name="expectedDeviceIndex"/>.
    ///
    /// OpenRGB sends DEVICE_LIST_UPDATED (id 100) at any time when devices are
    /// added/removed during runtime detection. If we treat that packet as a
    /// reply to our REQUEST_CONTROLLER_DATA we get desynced. We must filter it.
    /// </summary>
    private async Task<(OpenRgbProtocol.PacketId id, byte[] body)> ReadExpectedLockedAsync(
        OpenRgbProtocol.PacketId expected, uint expectedDeviceIndex, CancellationToken ct)
    {
        // Bound the loop so we never spin forever on a desync.
        for (int attempt = 0; attempt < 16; attempt++)
        {
            var headerBuf = new byte[OpenRgbProtocol.HeaderSize];
            await ReadExactLockedAsync(headerBuf, ct).ConfigureAwait(false);
            var (devIdx, packetId, dataSize) = OpenRgbProtocol.ReadHeader(headerBuf);

            var body = dataSize == 0 ? Array.Empty<byte>() : new byte[dataSize];
            if (dataSize > 0)
            {
                await ReadExactLockedAsync(body, ct).ConfigureAwait(false);
            }

            if (packetId == OpenRgbProtocol.PacketId.DeviceListUpdated)
            {
                // Async device-list change notification - fire event and try again.
                try
                { DeviceListChanged?.Invoke(); }
                catch { }
                continue;
            }

            if (packetId == expected && devIdx == expectedDeviceIndex)
            {
                return (packetId, body);
            }

            // Some other unexpected packet - log once and try the next one.
            // Don't tear down the connection: a single stale packet from a previous
            // request shouldn't kill the session.
            Console.Error.WriteLine(
                $"[openrgb] unexpected packet id={(uint)packetId} devIdx={devIdx} (waiting for {(uint)expected} idx {expectedDeviceIndex})");
        }
        throw new IOException($"OpenRGB: gave up waiting for {expected} after 16 unexpected packets");
    }

    private async Task ReadExactLockedAsync(byte[] buffer, CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("not connected");

        var read = 0;
        while (read < buffer.Length)
        {
            var pending = stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            int n;
            if (pending.IsCompleted)
            {
                n = await pending.ConfigureAwait(false);
            }
            else
            {
                // WaitAsync does not cancel the read; the caller's socket drop does.
                try
                { n = await pending.AsTask().WaitAsync(IoTimeout).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    throw new IOException($"OpenRGB read timed out after {IoTimeout.TotalSeconds:F0}s");
                }
            }
            if (n == 0)
            {
                throw new EndOfStreamException("OpenRGB connection closed");
            }

            read += n;
        }
    }

    private void CleanupSocketLocked()
    {
        // Also reached without the lock from a bounded disconnect: swap the fields
        // out first so a concurrent holder fails with "not connected".
        var stream = Interlocked.Exchange(ref _stream, null);
        var tcp = Interlocked.Exchange(ref _tcp, null);
        _protocolVersion = 0;
        try
        { stream?.Dispose(); }
        catch { }
        try
        { tcp?.Close(); }
        catch { }
        try
        { tcp?.Dispose(); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // The lock is left undisposed: a holder still unwinding from a bounded
        // disconnect releases it afterwards, and SemaphoreSlim owns no handle.
        await DisconnectAsync().ConfigureAwait(false);
    }
}
