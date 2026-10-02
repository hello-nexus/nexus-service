using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>Protocol-6 client behaviour against a scripted daemon on real loopback sockets.</summary>
public class OpenRgbControllerV6Tests
{
    /// <summary>Answers the v6 handshake, ACKs every packet like the real server, and hands each request to a script.</summary>
    private sealed class ScriptedDaemon : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<NetworkStream, OpenRgbProtocol.PacketId, uint, Task> _onPacket;
        private TcpClient? _client;

        public int Port { get; }

        public ScriptedDaemon(Func<NetworkStream, OpenRgbProtocol.PacketId, uint, Task> onPacket)
        {
            _onPacket = onPacket;
            _listener = new TcpListener(IPAddress.Loopback, 0);
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
                    if (size > 0)
                    {
                        await stream.ReadExactlyAsync(new byte[size], _cts.Token);
                    }
                    switch (id)
                    {
                        case OpenRgbProtocol.PacketId.RequestProtocolVersion:
                            await SendAsync(stream, 0, id, U32(6));
                            break;
                        case OpenRgbProtocol.PacketId.SetClientFlags:
                            await SendAsync(stream, 0, OpenRgbProtocol.PacketId.SetServerFlags, U32(OpenRgbProtocol.ServerFlagLocalClient));
                            break;
                        default:
                            await _onPacket(stream, id, dev);
                            break;
                    }
                    await AckAsync(stream, dev, id, OpenRgbProtocol.StatusOk);
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

        public static Task AckAsync(NetworkStream stream, uint dev, OpenRgbProtocol.PacketId acked, uint status)
        {
            var body = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)acked);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), status);
            return SendAsync(stream, dev, OpenRgbProtocol.PacketId.Ack, body);
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

    private static byte[] MouseV6()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine("Fixtures", "OpenRgb", "controllers-v6.json")));
        return Convert.FromHexString(doc.RootElement.GetProperty("controllers")[0].GetString()!);
    }

    private static byte[] CountWithIds(params uint[] ids)
    {
        var body = new byte[4 + 4 * ids.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4 + 4 * i), ids[i]);
        }
        return body;
    }

    [Fact]
    public async Task A_controller_that_vanished_after_the_count_is_skipped_on_its_ack()
    {
        var mouse = MouseV6();
        await using var daemon = new ScriptedDaemon(async (stream, id, dev) =>
        {
            if (id == OpenRgbProtocol.PacketId.RequestControllerCount)
            {
                await ScriptedDaemon.SendAsync(stream, 0, id, CountWithIds(5, 6));
            }
            else if (id == OpenRgbProtocol.PacketId.RequestControllerData && dev == 5)
            {
                await ScriptedDaemon.SendAsync(stream, 5, id, mouse);
            }
            // id 6: the real server sends only the ACK for an unknown controller.
        });
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var devices = await controller.GetDevicesAsync();

        var device = Assert.Single(devices);
        Assert.Equal(5, device.Address);
        Assert.Equal("Corsair M65 PRO", device.Name);
        Assert.True(sw.Elapsed < OpenRgbController.IoTimeout, $"enumeration waited {sw.Elapsed} on the vanished controller");
    }

    [Fact]
    public async Task A_refused_write_raises_WriteRejected_with_its_address_and_status()
    {
        await using var daemon = new ScriptedDaemon(async (stream, id, dev) =>
        {
            if (id == OpenRgbProtocol.PacketId.RgbControllerUpdateLeds)
            {
                // The server ACKs every packet; this one carries the refusal ahead of the generic OK.
                await ScriptedDaemon.AckAsync(stream, dev, id, OpenRgbProtocol.StatusInvalidData);
            }
        });
        await using var controller = new OpenRgbController(port: daemon.Port);
        var rejected = new TaskCompletionSource<(int address, uint packet, uint status)>();
        controller.WriteRejected += (a, p, s) => rejected.TrySetResult((a, p, s));
        Assert.True(await controller.TryConnectAsync());

        await controller.PushFrameAsync(7, new RgbColor[121]);

        var (address, packet, status) = await rejected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(7, address);
        Assert.Equal((uint)OpenRgbProtocol.PacketId.RgbControllerUpdateLeds, packet);
        Assert.Equal(OpenRgbProtocol.StatusInvalidData, status);
    }

    [Fact]
    public async Task Update_broadcasts_ahead_of_a_reply_do_not_disturb_the_request()
    {
        await using var daemon = new ScriptedDaemon(async (stream, id, dev) =>
        {
            if (id == OpenRgbProtocol.PacketId.RequestControllerCount)
            {
                for (var i = 0; i < 50; i++)
                {
                    await ScriptedDaemon.SendAsync(stream, 3, OpenRgbProtocol.PacketId.RgbControllerSignalUpdate, new byte[512]);
                }
                await ScriptedDaemon.SendAsync(stream, 0, id, CountWithIds(3));
            }
        });
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());

        Assert.Equal(new[] { 3 }, await controller.GetControllerAddressesAsync());
        Assert.True(controller.IsConnected);
    }

    [Fact]
    public async Task Detection_messages_raise_DetectionStateChanged_in_order()
    {
        await using var daemon = new ScriptedDaemon(async (stream, id, dev) =>
        {
            if (id == OpenRgbProtocol.PacketId.RequestRescanDevices)
            {
                await ScriptedDaemon.SendAsync(stream, 0, OpenRgbProtocol.PacketId.DetectionStarted, Array.Empty<byte>());
                // u32 size, u32 percent, bstring detector name.
                await ScriptedDaemon.SendAsync(stream, 0, OpenRgbProtocol.PacketId.DetectionProgressChanged,
                    Convert.FromHexString("0F000000320000000300" + "414200"));
                await ScriptedDaemon.SendAsync(stream, 0, OpenRgbProtocol.PacketId.DetectionComplete, Array.Empty<byte>());
            }
        });
        await using var controller = new OpenRgbController(port: daemon.Port);
        var states = new List<string>();
        var completed = new TaskCompletionSource();
        controller.DetectionStateChanged += s =>
        {
            lock (states) { states.Add(s ? "started" : "complete"); }
            if (!s) completed.TrySetResult();
        };
        controller.DetectionProgress += () => { lock (states) { states.Add("progress"); } };
        Assert.True(await controller.TryConnectAsync());

        Assert.True(await controller.RescanAsync());

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lock (states) { Assert.Equal(new[] { "started", "progress", "complete" }, states); }
    }
}
