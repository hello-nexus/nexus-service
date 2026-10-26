using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>The Nexus cooling packets against a scripted daemon on real loopback sockets.</summary>
public class OpenRgbControllerCoolingTests
{
    private const string StatusHex = "01011c0cfa0003" + "0135094b" + "019603ff" + "000000ff" + "03" + "017b00" + "000000" + "01f1ff";

    private sealed class ScriptedDaemon : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<NetworkStream, OpenRgbProtocol.PacketId, uint, byte[], Task<uint>> _onPacket;
        private TcpClient? _client;

        public int Port { get; }

        public ScriptedDaemon(Func<NetworkStream, OpenRgbProtocol.PacketId, uint, byte[], Task<uint>> onPacket)
        {
            _onPacket = onPacket;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try
            {
                _client = await _listener.AcceptTcpClientAsync(_cts.Token);
                var stream = _client.GetStream();
                while (!_cts.IsCancellationRequested)
                {
                    var header = new byte[OpenRgbProtocol.HeaderSize];
                    await stream.ReadExactlyAsync(header, _cts.Token);
                    var (dev, id, size) = OpenRgbProtocol.ReadHeader(header);
                    var body = new byte[size];
                    if (size > 0)
                    {
                        await stream.ReadExactlyAsync(body, _cts.Token);
                    }
                    uint status = OpenRgbProtocol.StatusOk;
                    switch (id)
                    {
                        case OpenRgbProtocol.PacketId.RequestProtocolVersion:
                            await SendAsync(stream, 0, id, U32(6));
                            break;
                        case OpenRgbProtocol.PacketId.SetClientFlags:
                            await SendAsync(stream, 0, OpenRgbProtocol.PacketId.SetServerFlags, U32(OpenRgbProtocol.ServerFlagLocalClient));
                            break;
                        default:
                            status = await _onPacket(stream, id, dev, body);
                            break;
                    }
                    var ack = new byte[8];
                    BinaryPrimitives.WriteUInt32LittleEndian(ack, (uint)id);
                    BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(4), status);
                    await SendAsync(stream, dev, OpenRgbProtocol.PacketId.Ack, ack);
                }
            }
            catch { }
        }

        public static byte[] U32(uint v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            return b;
        }

        public static async Task SendAsync(NetworkStream stream, uint dev, OpenRgbProtocol.PacketId id, byte[] body)
        {
            var packet = new byte[OpenRgbProtocol.HeaderSize + body.Length];
            OpenRgbProtocol.WriteHeader(packet, dev, id, (uint)body.Length);
            body.CopyTo(packet, OpenRgbProtocol.HeaderSize);
            await stream.WriteAsync(packet);
        }

        public ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _client?.Dispose(); } catch { }
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Reads_the_status_the_daemon_replies_with_before_its_ack()
    {
        uint seenAddress = 0;
        await using var daemon = new ScriptedDaemon(async (stream, id, dev, _) =>
        {
            if (id != OpenRgbProtocol.PacketId.NexusCoolingGetStatus)
            {
                return OpenRgbProtocol.StatusUnsupported;
            }
            seenAddress = dev;
            await ScriptedDaemon.SendAsync(stream, dev, id, Convert.FromHexString(StatusHex));
            return OpenRgbProtocol.StatusOk;
        });
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());

        var result = await controller.GetCoolingStatusAsync(42);

        Assert.Equal(NexusCoolingOutcome.Ok, result.Outcome);
        Assert.Equal(42u, seenAddress);
        Assert.Equal(0x0C1C, result.Status!.ProductId);
        Assert.Equal(3, result.Status.Channels.Count);
        Assert.Equal(2357, result.Status.Channels[0].Rpm);
    }

    [Fact]
    public async Task A_daemon_that_does_not_know_the_packet_is_reported_unsupported()
    {
        await using var daemon = new ScriptedDaemon((_, _, _, _) => Task.FromResult(OpenRgbProtocol.StatusUnsupported));
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());

        var status = await controller.GetCoolingStatusAsync(1);
        var set = await controller.SetCoolingDutiesAsync(1, new[] { (0, 50) });

        Assert.Equal(NexusCoolingOutcome.Unsupported, status.Outcome);
        Assert.Null(status.Status);
        Assert.Equal(NexusCoolingOutcome.Unsupported, set);
    }

    [Fact]
    public async Task An_unreadable_status_reply_is_a_failure()
    {
        await using var daemon = new ScriptedDaemon(async (stream, id, dev, _) =>
        {
            await ScriptedDaemon.SendAsync(stream, dev, id, new byte[] { 9, 9, 9 });
            return OpenRgbProtocol.StatusOk;
        });
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());

        var result = await controller.GetCoolingStatusAsync(1);

        Assert.Equal(NexusCoolingOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task Sends_the_duty_table_to_the_addressed_controller()
    {
        byte[]? received = null;
        uint address = 0;
        await using var daemon = new ScriptedDaemon((_, id, dev, body) =>
        {
            if (id == OpenRgbProtocol.PacketId.NexusCoolingSetDuties)
            {
                received = body;
                address = dev;
            }
            return Task.FromResult(OpenRgbProtocol.StatusOk);
        });
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());

        var outcome = await controller.SetCoolingDutiesAsync(7, new[] { (0, 75), (3, 100) });

        Assert.Equal(NexusCoolingOutcome.Ok, outcome);
        Assert.Equal(7u, address);
        Assert.Equal("0102004b0364", Convert.ToHexString(received!).ToLowerInvariant());
    }

    [Fact]
    public async Task A_refused_table_is_a_failure_not_a_dropped_connection()
    {
        await using var daemon = new ScriptedDaemon((_, id, _, _) =>
            Task.FromResult(id == OpenRgbProtocol.PacketId.NexusCoolingSetDuties ? OpenRgbProtocol.StatusInvalidData : OpenRgbProtocol.StatusOk));
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());

        var outcome = await controller.SetCoolingDutiesAsync(7, new[] { (0, 75) });

        Assert.Equal(NexusCoolingOutcome.Failed, outcome);
        Assert.True(controller.IsConnected);
    }

    [Fact]
    public async Task Without_a_connection_both_calls_fail_at_once()
    {
        await using var controller = new OpenRgbController(port: 1);

        Assert.Equal(NexusCoolingOutcome.Failed, (await controller.GetCoolingStatusAsync(1)).Outcome);
        Assert.Equal(NexusCoolingOutcome.Failed, await controller.SetCoolingDutiesAsync(1, new[] { (0, 50) }));
    }
}
