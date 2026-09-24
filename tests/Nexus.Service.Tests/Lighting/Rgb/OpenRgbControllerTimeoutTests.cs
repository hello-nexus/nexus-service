using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>Controller against a daemon that accepts the socket and then goes silent, on real loopback sockets: every request must fail within its bound, drop the socket, and free the lock.</summary>
public class OpenRgbControllerTimeoutTests
{
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Speaks just enough of the SDK protocol to get a client connected, then
    /// either answers or parks on the next request, keeping the socket open.
    /// </summary>
    private sealed class FakeDaemon : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<TcpClient> _clients = new();
        private readonly bool _answerHandshake;
        private readonly bool _answerRequests;
        private readonly bool _drainWrites;

        public int Port { get; }
        public int Accepted;

        public FakeDaemon(bool answerHandshake, bool answerRequests, bool drainWrites = true)
        {
            _answerHandshake = answerHandshake;
            _answerRequests = answerRequests;
            _drainWrites = drainWrites;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    lock (_clients) { _clients.Add(client); }
                    Interlocked.Increment(ref Accepted);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                // SET_CLIENT_NAME then REQUEST_PROTOCOL_VERSION.
                await ReadPacketAsync(stream);
                await ReadPacketAsync(stream);
                if (!_answerHandshake)
                {
                    await Task.Delay(Timeout.Infinite, _cts.Token);
                    return;
                }
                await WriteReplyAsync(stream, OpenRgbProtocol.PacketId.RequestProtocolVersion, Body(OpenRgbProtocol.CurrentProtocolVersion));

                while (!_cts.IsCancellationRequested)
                {
                    if (!_drainWrites)
                    {
                        // A daemon whose listen thread stopped: nothing is read,
                        // so the client's send buffer eventually fills.
                        await Task.Delay(Timeout.Infinite, _cts.Token);
                        return;
                    }
                    var (id, _) = await ReadPacketAsync(stream);
                    if (!_answerRequests)
                    {
                        await Task.Delay(Timeout.Infinite, _cts.Token);
                        return;
                    }
                    if (id == OpenRgbProtocol.PacketId.RequestControllerCount)
                    {
                        await WriteReplyAsync(stream, id, Body(0));
                    }
                }
            }
            catch { }
        }

        private static byte[] Body(uint value)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            return b;
        }

        private static async Task<(OpenRgbProtocol.PacketId id, byte[] body)> ReadPacketAsync(NetworkStream stream)
        {
            var header = new byte[OpenRgbProtocol.HeaderSize];
            await stream.ReadExactlyAsync(header);
            var (_, id, size) = OpenRgbProtocol.ReadHeader(header);
            var body = new byte[size];
            if (size > 0)
            {
                await stream.ReadExactlyAsync(body);
            }
            return (id, body);
        }

        private static async Task WriteReplyAsync(NetworkStream stream, OpenRgbProtocol.PacketId id, byte[] body)
        {
            var packet = new byte[OpenRgbProtocol.HeaderSize + body.Length];
            OpenRgbProtocol.WriteHeader(packet, 0, id, (uint)body.Length);
            body.CopyTo(packet, OpenRgbProtocol.HeaderSize);
            await stream.WriteAsync(packet);
        }

        public ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            lock (_clients)
            {
                foreach (var c in _clients)
                {
                    try { c.Dispose(); } catch { }
                }
            }
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<TimeSpan> TimeAsync(Func<Task> op)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await op();
        return sw.Elapsed;
    }

    [Fact]
    public async Task Connect_to_a_daemon_that_never_answers_the_handshake_fails_within_the_bound()
    {
        await using var daemon = new FakeDaemon(answerHandshake: false, answerRequests: false);
        await using var controller = new OpenRgbController(port: daemon.Port);

        var connected = false;
        var elapsed = await TimeAsync(async () => connected = await controller.TryConnectAsync());

        Assert.False(connected);
        Assert.False(controller.IsConnected);
        Assert.True(elapsed < OpenRgbController.IoTimeout + Slack, $"handshake wait took {elapsed}");
    }

    [Fact]
    public async Task Request_to_a_silent_daemon_times_out_drops_the_socket_and_reconnects_cleanly()
    {
        await using var daemon = new FakeDaemon(answerHandshake: true, answerRequests: false);
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());

        var elapsed = await TimeAsync(async () =>
            await Assert.ThrowsAsync<IOException>(() => controller.GetDevicesAsync()));

        Assert.True(elapsed < OpenRgbController.IoTimeout + Slack, $"request wait took {elapsed}");
        Assert.False(controller.IsConnected);

        // The lock is free and the socket is gone: a disconnect is immediate
        // and a reconnect opens a fresh session.
        var disconnect = await TimeAsync(controller.DisconnectAsync);
        Assert.True(disconnect < TimeSpan.FromMilliseconds(500), $"disconnect took {disconnect}");
        Assert.True(await controller.TryConnectAsync());
        Assert.Equal(2, daemon.Accepted);
    }

    [Fact]
    public async Task Disconnect_while_a_request_is_parked_completes_within_its_bound_and_faults_the_request()
    {
        await using var daemon = new FakeDaemon(answerHandshake: true, answerRequests: false);
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());

        var parked = controller.GetDevicesAsync();
        await Task.Delay(200);
        Assert.False(parked.IsCompleted);

        // Bound checked inside the request's own read timeout, so this proves
        // the disconnect path and not the read bound.
        var elapsed = await TimeAsync(controller.DisconnectAsync);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"disconnect took {elapsed}");
        Assert.False(controller.IsConnected);

        var fault = await Assert.ThrowsAnyAsync<Exception>(() => parked.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.IsNotType<TimeoutException>(fault);
    }

    [Fact]
    public async Task Frame_pushes_to_a_daemon_that_stopped_draining_stay_bounded()
    {
        await using var daemon = new FakeDaemon(answerHandshake: true, answerRequests: false, drainWrites: false);
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());

        // Push until the send path blocks: the write bound must then fire,
        // drop the socket, and every later push returns at once.
        var frame = new RgbColor[4096];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 4000 && sw.Elapsed < TimeSpan.FromSeconds(20); i++)
        {
            await controller.PushFrameAsync(0, frame);
            if (!controller.IsConnected)
            {
                break;
            }
        }

        Assert.False(controller.IsConnected);
        Assert.True(sw.Elapsed < OpenRgbController.IoTimeout + TimeSpan.FromSeconds(10), $"pushes ran for {sw.Elapsed}");
    }
}
