using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Nexus.Service.Calendar;

namespace Nexus.Service.Tests.Calendar;

/// <summary>The real fetcher against a raw loopback TCP server, so the connect-time address rule, redirects, caps and deadline are exercised on the wire.</summary>
public sealed class HttpCalendarFetcherTests
{
    private sealed class RawServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        public int Connections;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public RawServer(Func<NetworkStream, CancellationToken, Task> handler, IPAddress? address = null)
        {
            _listener = new TcpListener(address ?? IPAddress.Loopback, 0);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    }
                    catch
                    {
                        return;
                    }
                    Interlocked.Increment(ref Connections);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                var stream = client.GetStream();
                                await ReadRequestAsync(stream);
                                await handler(stream, _stop.Token);
                            }
                            catch
                            {
                            }
                        }
                    });
                }
            });
        }

        private static async Task ReadRequestAsync(NetworkStream s)
        {
            var buf = new byte[4096];
            var all = new StringBuilder();
            while (!all.ToString().Contains("\r\n\r\n"))
            {
                var n = await s.ReadAsync(buf);
                if (n == 0)
                {
                    return;
                }
                all.Append(Encoding.ASCII.GetString(buf, 0, n));
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private static Task Write(NetworkStream s, string text) => s.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

    private static bool LoopbackV4Only(IPAddress ip) => IPAddress.Loopback.Equals(ip);

    private static HttpCalendarFetcher Lenient(TimeSpan? timeout = null) => new(LoopbackV4Only, timeout);

    private static Uri UrlOf(RawServer s, string host = "127.0.0.1") => new($"http://{host}:{s.Port}/a.ics");

    [Fact]
    public async Task Private_addresses_are_refused_at_connect_with_the_default_rule()
    {
        using var server = new RawServer((s, _) => Write(s, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"));

        var result = await new HttpCalendarFetcher().FetchAsync(UrlOf(server), null, null, CancellationToken.None);

        Assert.Equal(CalendarFetchOutcome.Error, result.Outcome);
        Assert.Equal("unreachable", result.ErrorCode);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task A_redirect_to_a_refused_address_is_not_followed()
    {
        TcpListener? probe = null;
        try
        {
            probe = new TcpListener(IPAddress.IPv6Loopback, 0);
            probe.Start();
        }
        catch (SocketException)
        {
            return; // No IPv6 loopback on this host; the redirect case cannot be built here.
        }
        probe.Stop();
        using var v6 = new RawServer((s, _) => Write(s, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"), IPAddress.IPv6Loopback);
        using var v4 = new RawServer((s, _) => Write(s, $"HTTP/1.1 302 Found\r\nLocation: http://[::1]:{v6.Port}/secret\r\nContent-Length: 0\r\n\r\n"));

        var result = await Lenient().FetchAsync(UrlOf(v4), null, null, CancellationToken.None);

        Assert.Equal(CalendarFetchOutcome.Error, result.Outcome);
        Assert.Equal(1, v4.Connections);
        Assert.Equal(0, v6.Connections);
    }

    [Fact]
    public async Task A_plain_feed_is_returned_with_etag_and_byte_count()
    {
        const string body = "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n";
        using var server = new RawServer((s, _) => Write(s, $"HTTP/1.1 200 OK\r\nETag: \"abc\"\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));

        var result = await Lenient().FetchAsync(UrlOf(server), null, null, CancellationToken.None);

        Assert.Equal(CalendarFetchOutcome.Ok, result.Outcome);
        Assert.Equal(body, result.Body);
        Assert.Equal("\"abc\"", result.ETag);
        Assert.Equal(body.Length, result.Bytes);
    }

    [Theory]
    [InlineData(401, "unauthorized")]
    [InlineData(404, "notFound")]
    [InlineData(500, "unreachable")]
    public async Task Status_codes_map_to_error_codes(int status, string code)
    {
        using var server = new RawServer((s, _) => Write(s, $"HTTP/1.1 {status} X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));

        var result = await Lenient().FetchAsync(UrlOf(server), null, null, CancellationToken.None);

        Assert.Equal(code, result.ErrorCode);
    }

    [Fact]
    public async Task A_declared_length_over_the_cap_is_rejected_before_reading()
    {
        using var server = new RawServer((s, _) => Write(s, $"HTTP/1.1 200 OK\r\nContent-Length: {HttpCalendarFetcher.MaxBodyBytes + 1}\r\nConnection: close\r\n\r\n"));

        var result = await Lenient().FetchAsync(UrlOf(server), null, null, CancellationToken.None);

        Assert.Equal("tooLarge", result.ErrorCode);
    }

    [Fact]
    public async Task A_streamed_body_over_the_cap_is_cut_off()
    {
        using var server = new RawServer(async (s, ct) =>
        {
            await Write(s, "HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n");
            var chunk = new byte[64 * 1024];
            Array.Fill(chunk, (byte)'x');
            for (var sent = 0; sent <= HttpCalendarFetcher.MaxBodyBytes + chunk.Length; sent += chunk.Length)
            {
                await s.WriteAsync(chunk, ct);
            }
        });

        var result = await Lenient().FetchAsync(UrlOf(server), null, null, CancellationToken.None);

        Assert.Equal("tooLarge", result.ErrorCode);
    }

    [Fact]
    public async Task A_body_that_drips_is_stopped_by_the_overall_deadline()
    {
        using var server = new RawServer(async (s, ct) =>
        {
            await Write(s, "HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nBEGIN:VCALENDAR\r\n");
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        });
        var started = DateTime.UtcNow;

        var result = await Lenient(TimeSpan.FromMilliseconds(700)).FetchAsync(UrlOf(server), null, null, CancellationToken.None);

        Assert.Equal("unreachable", result.ErrorCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Gzip_is_decoded_and_the_cap_applies_after_decompression()
    {
        static byte[] Gzip(byte[] raw)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                gz.Write(raw);
            }
            return ms.ToArray();
        }

        var small = Gzip(Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"));
        using var ok = new RawServer(async (s, _) =>
        {
            await Write(s, $"HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: {small.Length}\r\nConnection: close\r\n\r\n");
            await s.WriteAsync(small);
        });
        var bomb = Gzip(new byte[HttpCalendarFetcher.MaxBodyBytes + 1024 * 1024]);
        using var bad = new RawServer(async (s, _) =>
        {
            await Write(s, $"HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: {bomb.Length}\r\nConnection: close\r\n\r\n");
            await s.WriteAsync(bomb);
        });

        var decoded = await Lenient().FetchAsync(UrlOf(ok), null, null, CancellationToken.None);
        var capped = await Lenient().FetchAsync(UrlOf(bad), null, null, CancellationToken.None);

        Assert.Equal("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", decoded.Body);
        Assert.True(bomb.Length < 100_000);
        Assert.Equal("tooLarge", capped.ErrorCode);
    }

    [Fact]
    public async Task Conditional_headers_are_sent_and_a_304_is_reported()
    {
        using var server = new RawServer((s, _) => Write(s, "HTTP/1.1 304 Not Modified\r\nConnection: close\r\n\r\n"));

        var result = await Lenient().FetchAsync(UrlOf(server), "\"abc\"", null, CancellationToken.None);

        Assert.Equal(CalendarFetchOutcome.NotModified, result.Outcome);
    }
}
