using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>The Nexus write-health packet: wire format and the controller round trip against a scripted daemon.</summary>
public class OpenRgbControllerWriteHealthTests
{
    private static byte[] Reply(uint failures, uint successes, uint msSince)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(b, failures);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), successes);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), msSince);
        return b;
    }

    [Fact]
    public void The_packet_id_is_the_agreed_extension_id()
    {
        Assert.Equal(40002u, (uint)OpenRgbProtocol.PacketId.NexusWriteHealth);
    }

    [Fact]
    public void Parses_three_little_endian_counters()
    {
        Assert.True(NexusWriteHealthProtocol.TryParse(Reply(7, 1_000_000, 250), out var health));

        Assert.Equal(new OpenRgbWriteHealth(7, 1_000_000, 250), health);
    }

    [Fact]
    public void All_ones_means_the_controller_never_failed()
    {
        Assert.True(NexusWriteHealthProtocol.TryParse(Reply(0, 5, 0xFFFFFFFFu), out var health));

        Assert.Equal(NexusWriteHealthProtocol.NeverFailed, health.MsSinceLastFailure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void A_short_body_does_not_parse(int length)
    {
        Assert.False(NexusWriteHealthProtocol.TryParse(new byte[length], out _));
    }

    [Fact]
    public void Request_header_is_addressed_to_the_controller_with_an_empty_body()
    {
        var packet = new byte[OpenRgbProtocol.HeaderSize];
        OpenRgbProtocol.WriteHeader(packet, 9, OpenRgbProtocol.PacketId.NexusWriteHealth, 0);

        var (device, id, size) = OpenRgbProtocol.ReadHeader(packet);

        Assert.Equal(9u, device);
        Assert.Equal(OpenRgbProtocol.PacketId.NexusWriteHealth, id);
        Assert.Equal(0u, size);
    }

    private static async Task<(OpenRgbWriteHealthResult Result, uint SeenAddress, uint SeenSize)> RoundTrip(Func<NetworkStream, uint, Task<uint>> onRequest, int address)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        uint seenAddress = 0;
        uint seenSize = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cts.Token);
            var stream = client.GetStream();
            while (!cts.IsCancellationRequested)
            {
                var header = new byte[OpenRgbProtocol.HeaderSize];
                await stream.ReadExactlyAsync(header, cts.Token);
                var (dev, id, size) = OpenRgbProtocol.ReadHeader(header);
                var body = new byte[size];
                if (size > 0)
                {
                    await stream.ReadExactlyAsync(body, cts.Token);
                }
                uint status = OpenRgbProtocol.StatusOk;
                if (id == OpenRgbProtocol.PacketId.RequestProtocolVersion)
                {
                    await Send(stream, 0, id, BitConverter.GetBytes(6u));
                }
                else if (id == OpenRgbProtocol.PacketId.SetClientFlags)
                {
                    await Send(stream, 0, OpenRgbProtocol.PacketId.SetServerFlags, BitConverter.GetBytes(OpenRgbProtocol.ServerFlagLocalClient));
                }
                else if (id == OpenRgbProtocol.PacketId.NexusWriteHealth)
                {
                    seenAddress = dev;
                    seenSize = size;
                    status = await onRequest(stream, dev);
                }
                var ack = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(ack, (uint)id);
                BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(4), status);
                await Send(stream, dev, OpenRgbProtocol.PacketId.Ack, ack);
            }
        });
        try
        {
            await using var controller = new OpenRgbController(port: port);
            Assert.True(await controller.TryConnectAsync());
            var result = await controller.GetWriteHealthAsync(address, cts.Token);
            return (result, seenAddress, seenSize);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await server; } catch { }
        }
    }

    private static async Task Send(NetworkStream stream, uint dev, OpenRgbProtocol.PacketId id, byte[] body)
    {
        var packet = new byte[OpenRgbProtocol.HeaderSize + body.Length];
        OpenRgbProtocol.WriteHeader(packet, dev, id, (uint)body.Length);
        body.CopyTo(packet, OpenRgbProtocol.HeaderSize);
        await stream.WriteAsync(packet);
    }

    [Fact]
    public async Task Reads_the_counters_the_daemon_replies_with_before_its_ack()
    {
        var (result, address, size) = await RoundTrip(async (stream, dev) =>
        {
            await Send(stream, dev, OpenRgbProtocol.PacketId.NexusWriteHealth, Reply(4, 90, 1200));
            return OpenRgbProtocol.StatusOk;
        }, address: 42);

        Assert.Equal(OpenRgbWriteHealthOutcome.Ok, result.Outcome);
        Assert.Equal(new OpenRgbWriteHealth(4, 90, 1200), result.Health);
        Assert.Equal(42u, address);
        Assert.Equal(0u, size);
    }

    [Fact]
    public async Task A_daemon_that_does_not_know_the_packet_is_reported_unsupported()
    {
        var (result, _, _) = await RoundTrip((_, _) => Task.FromResult(OpenRgbProtocol.StatusUnsupported), address: 1);

        Assert.Equal(OpenRgbWriteHealthOutcome.Unsupported, result.Outcome);
    }

    [Fact]
    public async Task A_daemon_that_does_not_know_the_controller_reports_it_unknown_not_unsupported()
    {
        var (result, _, _) = await RoundTrip((_, _) => Task.FromResult(OpenRgbProtocol.StatusInvalidId), address: 1);

        Assert.Equal(OpenRgbWriteHealthOutcome.UnknownController, result.Outcome);
    }

    [Fact]
    public async Task An_unreadable_reply_is_a_failure()
    {
        var (result, _, _) = await RoundTrip(async (stream, dev) =>
        {
            await Send(stream, dev, OpenRgbProtocol.PacketId.NexusWriteHealth, new byte[] { 1, 2, 3 });
            return OpenRgbProtocol.StatusOk;
        }, address: 1);

        Assert.Equal(OpenRgbWriteHealthOutcome.Failed, result.Outcome);
    }
}
