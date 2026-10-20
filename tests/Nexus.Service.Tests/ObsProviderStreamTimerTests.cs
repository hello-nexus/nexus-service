using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Nexus.Service.Obs;

namespace Nexus.Service.Tests;

public sealed class ObsProviderStreamTimerTests : IAsyncLifetime
{
    private readonly FakeObsServer _server = new();
    private readonly ManualTime _time = new();
    private ObsProvider _provider = null!;

    public Task InitializeAsync()
    {
        var store = new InMemoryConfigStore();
        store.Load().Obs.Host = "127.0.0.1";
        store.Load().Obs.Port = _server.Port;
        _provider = new ObsProvider(store, _time);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _provider.Dispose();
        _server.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ObsDurationRunningFasterThanClock_ReportsWallClock()
    {
        _server.StreamActive = true;
        _server.StreamDurationMs = 0;
        await _provider.GetStatusAsync(default);

        for (var i = 0; i < 4; i++)
        {
            _time.AdvanceMs(2_500);
            _server.StreamDurationMs += 8_750;
            var status = await _provider.GetStatusAsync(default);
            Assert.Equal(2_500 * (i + 1), status.StreamingDurationMs);
        }
    }

    [Fact]
    public async Task FirstObservationMidStream_SeedsFromObsDuration()
    {
        _server.StreamActive = true;
        _server.StreamDurationMs = 90_000;

        var first = await _provider.GetStatusAsync(default);
        _time.AdvanceMs(5_000);
        var second = await _provider.GetStatusAsync(default);

        Assert.Equal(90_000, first.StreamingDurationMs);
        Assert.Equal(95_000, second.StreamingDurationMs);
    }

    [Fact]
    public async Task ToggleStartingStream_StartsAtZeroFromToggleMoment()
    {
        _server.StreamActive = false;
        var started = await _provider.ToggleStreamingAsync(default);
        Assert.True(started.Streaming);
        Assert.Equal(0, started.StreamingDurationMs);

        _time.AdvanceMs(7_000);
        _server.StreamDurationMs = 24_000;
        var status = await _provider.GetStatusAsync(default);

        Assert.Equal(7_000, status.StreamingDurationMs);
    }

    [Fact]
    public async Task StreamStop_ClearsDuration()
    {
        _server.StreamActive = true;
        _server.StreamDurationMs = 30_000;
        await _provider.GetStatusAsync(default);
        _time.AdvanceMs(1_000);

        var stopped = await _provider.ToggleStreamingAsync(default);

        Assert.False(stopped.Streaming);
        Assert.Equal(0, stopped.StreamingDurationMs);
    }

    [Fact]
    public async Task StreamEndedBetweenPolls_ClearsAndNextStreamSeedsFresh()
    {
        _server.StreamActive = true;
        _server.StreamDurationMs = 30_000;
        await _provider.GetStatusAsync(default);

        _server.StreamActive = false;
        var off = await _provider.GetStatusAsync(default);
        Assert.Equal(0, off.StreamingDurationMs);

        _time.AdvanceMs(60_000);
        _server.StreamActive = true;
        _server.StreamDurationMs = 2_000;
        var again = await _provider.GetStatusAsync(default);
        Assert.Equal(2_000, again.StreamingDurationMs);
    }

    [Fact]
    public async Task ObsDurationDrops_ReseedsAsRestart()
    {
        _server.StreamActive = true;
        _server.StreamDurationMs = 600_000;
        await _provider.GetStatusAsync(default);
        _time.AdvanceMs(2_500);

        _server.StreamDurationMs = 1_000;
        var status = await _provider.GetStatusAsync(default);

        Assert.Equal(1_000, status.StreamingDurationMs);
    }

    [Fact]
    public async Task UnobservedRestartWithLongerObsDuration_Reseeds()
    {
        _server.StreamActive = true;
        _server.StreamDurationMs = 30_000;
        await _provider.GetStatusAsync(default);

        _time.AdvanceMs(120_000);
        _server.StreamDurationMs = 60_000;
        var status = await _provider.GetStatusAsync(default);

        Assert.Equal(60_000, status.StreamingDurationMs);
    }

    [Fact]
    public async Task ToggleStartWhileStatusReReadLags_StillStartsAtZero()
    {
        _server.StreamActive = false;
        _server.LagAfterToggle = true;

        var started = await _provider.ToggleStreamingAsync(default);
        Assert.True(started.Streaming);
        Assert.Equal(0, started.StreamingDurationMs);

        _time.AdvanceMs(3_000);
        _server.StreamDurationMs = 3_000;
        var status = await _provider.GetStatusAsync(default);
        Assert.Equal(3_000, status.StreamingDurationMs);
    }

    [Fact]
    public async Task SocketReconnectWhileLive_KeepsStart()
    {
        _server.StreamActive = true;
        _server.StreamDurationMs = 0;
        await _provider.GetStatusAsync(default);
        _time.AdvanceMs(10_000);
        _server.StreamDurationMs = 35_000;

        _server.DropConnections();
        await _provider.GetStatusAsync(default);
        var status = await _provider.GetStatusAsync(default);

        Assert.True(status.Connected);
        Assert.Equal(10_000, status.StreamingDurationMs);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ms;
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => _ms;
        public void AdvanceMs(long ms) => _ms += ms;
    }

    private sealed class FakeObsServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<WebSocket> _sockets = [];

        public int Port { get; }
        public volatile bool StreamActive;
        public long StreamDurationMs;
        public volatile bool LagAfterToggle;
        private int _staleReads;

        public FakeObsServer()
        {
            var random = new Random();
            for (var attempt = 0; ; attempt++)
            {
                var port = random.Next(20_000, 60_000);
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    Port = port;
                    break;
                }
                catch (HttpListenerException) when (attempt < 20)
                {
                    listener.Close();
                }
            }
            _ = Task.Run(AcceptLoop);
        }

        public void DropConnections()
        {
            lock (_sockets)
            {
                foreach (var socket in _sockets)
                {
                    socket.Abort();
                }
                _sockets.Clear();
            }
        }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }
                try
                {
                    var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                    lock (_sockets)
                    {
                        _sockets.Add(ws);
                    }
                    _ = Task.Run(() => Serve(ws));
                }
                catch
                {
                    ctx.Response.Abort();
                }
            }
        }

        private async Task Serve(WebSocket ws)
        {
            try
            {
                await Send(ws, "{\"op\":0,\"d\":{\"obsWebSocketVersion\":\"5.0.0\",\"rpcVersion\":1}}");
                await Receive(ws);
                await Send(ws, "{\"op\":2,\"d\":{\"negotiatedRpcVersion\":1}}");
                while (ws.State == WebSocketState.Open)
                {
                    using var doc = JsonDocument.Parse(await Receive(ws));
                    var d = doc.RootElement.GetProperty("d");
                    var type = d.GetProperty("requestType").GetString();
                    var id = d.GetProperty("requestId").GetString();
                    var data = type switch
                    {
                        "GetSceneList" => "{\"currentProgramSceneName\":\"Scene\",\"scenes\":[{\"sceneName\":\"Scene\",\"sceneUuid\":\"u\"}]}",
                        "GetRecordStatus" => "{\"outputActive\":false,\"outputDuration\":0}",
                        "GetStreamStatus" => StreamStatus(),
                        "ToggleStream" => ToggleStream(),
                        _ => "{}",
                    };
                    await Send(ws, $"{{\"op\":7,\"d\":{{\"requestType\":\"{type}\",\"requestId\":\"{id}\",\"requestStatus\":{{\"result\":true,\"code\":100}},\"responseData\":{data}}}}}");
                }
            }
            catch
            {
                // Connection dropped by the test or by provider teardown.
            }
        }

        private string StreamStatus()
        {
            var reported = StreamActive;
            if (Interlocked.Exchange(ref _staleReads, 0) > 0)
            {
                reported = !reported;
            }
            return $"{{\"outputActive\":{(reported ? "true" : "false")},\"outputDuration\":{StreamDurationMs}}}";
        }

        private string ToggleStream()
        {
            StreamActive = !StreamActive;
            if (LagAfterToggle)
            {
                _staleReads = 1;
            }
            return $"{{\"outputActive\":{(StreamActive ? "true" : "false")}}}";
        }

        private static Task Send(WebSocket ws, string json) =>
            ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

        private static async Task<byte[]> Receive(WebSocket ws)
        {
            var buffer = new byte[8192];
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            return buffer[..result.Count];
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
            DropConnections();
        }
    }
}
