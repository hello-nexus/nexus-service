using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Nexus.Service.Cloud;
using Xunit;

namespace Nexus.Service.Tests.Cloud;

/// <summary>
/// Exercises CloudApiClient's real HTTP path (not the fake) against a local
/// TCP listener that accepts the connection but never responds, so
/// HttpClient.Timeout fires. That produces a TaskCanceledException whose
/// CancellationToken is HttpClient's own internal linked token, distinct from
/// the caller's ct - the exact case the catch filters across the Cloud
/// subsystem must classify as Offline rather than rethrow (a rethrow escapes
/// CloudProfileSyncService's RunGuardedAsync/CloudDeviceReporter's guard and
/// faults the BackgroundService, which the default
/// BackgroundServiceExceptionBehavior=StopHost turns into the whole service
/// exiting on a single slow api.hellonexus.com call).
///
/// Uses the internal (baseUrl, requestTimeout) constructor rather than the
/// NEXUS_API_BASE environment variable - that variable is process-wide state
/// and mutating it would race any other test that constructs a real
/// CloudApiClient (NexusAppFactory's DI container does, for one) under
/// parallel test execution.
/// </summary>
public sealed class CloudApiClientTests
{
    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Fact]
    public async Task Timeout_shaped_cancellation_is_classified_offline_not_rethrown()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // Accept and hold the connection open; never write a response, so the
        // client's own short Timeout below is what ends the request.
        var acceptTask = listener.AcceptTcpClientAsync();

        try
        {
            var client = new CloudApiClient(new SingleClientFactory(), $"http://127.0.0.1:{port}", TimeSpan.FromMilliseconds(200));

            var result = await client.LogoutAsync("some-refresh-token", CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(result.Offline);
        }
        finally
        {
            listener.Stop();
            try
            {
                using var accepted = await acceptTask;
            }
            catch { }
        }
    }

    /// <summary>Reads one HTTP/1.1 request off <paramref name="stream"/> (headers + Content-Length body) and returns it as raw text.</summary>
    private static async Task<string> ReadHttpRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[16384];
        var request = new System.Text.StringBuilder();
        while (!request.ToString().Contains("\r\n\r\n"))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }
            request.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        }

        var text = request.ToString();
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headerBlock = text.Substring(0, headerEnd);
        var bodySoFar = text.Length - (headerEnd + 4);
        var clMatch = System.Text.RegularExpressions.Regex.Match(headerBlock, @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var contentLength = clMatch.Success ? int.Parse(clMatch.Groups[1].Value) : 0;
        while (bodySoFar < contentLength)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }
            text += System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            bodySoFar += read;
        }
        return text;
    }

    [Fact]
    public async Task PostRawAsync_forwards_body_and_bearer_returns_upstream_status_and_body_verbatim()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            using var stream = accepted.GetStream();
            var request = await ReadHttpRequestAsync(stream);
            // A non-2xx upstream status must still come back as Success - the
            // forwarder relays whatever the server actually said.
            const string body = "{\"code\":\"validation_error\",\"message\":\"bad score\"}";
            var bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
            var response = $"HTTP/1.1 400 Bad Request\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response));
            return request;
        });

        try
        {
            var client = new CloudApiClient(new SingleClientFactory(), $"http://127.0.0.1:{port}", TimeSpan.FromSeconds(5));

            var result = await client.PostRawAsync("/benchmarks/submit", "{\"composite\":950}", "access-tok-1", CancellationToken.None);

            var request = await requestTask;
            Assert.Contains("POST /benchmarks/submit", request);
            Assert.Contains("Authorization: Bearer access-tok-1", request);
            Assert.Contains("{\"composite\":950}", request);

            Assert.True(result.Success);
            Assert.Equal(400, result.StatusCode);
            Assert.Equal("{\"code\":\"validation_error\",\"message\":\"bad score\"}", result.Value!.Body);
            Assert.Equal("application/json", result.Value.ContentType);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task PostRawAsync_withoutAccessToken_omitsAuthorizationHeader()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            using var stream = accepted.GetStream();
            var request = await ReadHttpRequestAsync(stream);
            const string body = "{\"ok\":true}";
            var bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
            var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response));
            return request;
        });

        try
        {
            var client = new CloudApiClient(new SingleClientFactory(), $"http://127.0.0.1:{port}", TimeSpan.FromSeconds(5));

            var result = await client.PostRawAsync("/benchmarks/submit", "{}", null, CancellationToken.None);

            var request = await requestTask;
            Assert.DoesNotContain("Authorization:", request);
            Assert.True(result.Success);
            Assert.Equal(200, result.StatusCode);
            Assert.Equal("{\"ok\":true}", result.Value!.Body);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task SendRawAsync_get_omits_body_and_forwards_method_and_bearer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            using var stream = accepted.GetStream();
            var request = await ReadHttpRequestAsync(stream);
            const string body = "[{\"installId\":\"a\"}]";
            var bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
            var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response));
            return request;
        });

        try
        {
            var client = new CloudApiClient(new SingleClientFactory(), $"http://127.0.0.1:{port}", TimeSpan.FromSeconds(5));

            var result = await client.SendRawAsync(HttpMethod.Get, "/account/devices", null, "access-tok-1", CancellationToken.None);

            var request = await requestTask;
            Assert.Contains("GET /account/devices", request);
            Assert.Contains("Authorization: Bearer access-tok-1", request);
            Assert.DoesNotContain("Content-Length:", request);

            Assert.True(result.Success);
            Assert.Equal(200, result.StatusCode);
            Assert.Equal("[{\"installId\":\"a\"}]", result.Value!.Body);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task SendRawAsync_put_forwards_method_path_and_body()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            using var stream = accepted.GetStream();
            var request = await ReadHttpRequestAsync(stream);
            const string body = "{\"ok\":true}";
            var bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
            var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response));
            return request;
        });

        try
        {
            var client = new CloudApiClient(new SingleClientFactory(), $"http://127.0.0.1:{port}", TimeSpan.FromSeconds(5));

            var result = await client.SendRawAsync(HttpMethod.Put, "/account/devices/install-1", "{\"hostname\":\"pc\"}", "access-tok-2", CancellationToken.None);

            var request = await requestTask;
            Assert.Contains("PUT /account/devices/install-1", request);
            Assert.Contains("Authorization: Bearer access-tok-2", request);
            Assert.Contains("{\"hostname\":\"pc\"}", request);

            Assert.True(result.Success);
            Assert.Equal(200, result.StatusCode);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task SendRawAsync_delete_returns_upstream_204_with_empty_body()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            using var stream = accepted.GetStream();
            var request = await ReadHttpRequestAsync(stream);
            var response = "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response));
            return request;
        });

        try
        {
            var client = new CloudApiClient(new SingleClientFactory(), $"http://127.0.0.1:{port}", TimeSpan.FromSeconds(5));

            var result = await client.SendRawAsync(HttpMethod.Delete, "/account/devices/install-1", null, "access-tok-3", CancellationToken.None);

            var request = await requestTask;
            Assert.Contains("DELETE /account/devices/install-1", request);

            Assert.True(result.Success);
            Assert.Equal(204, result.StatusCode);
            Assert.Equal("", result.Value!.Body);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Avatar_upload_posts_multipart_field_named_file()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            using var stream = accepted.GetStream();
            var buffer = new byte[16384];
            var request = new System.Text.StringBuilder();
            // Read until the terminal multipart boundary; a single read is not
            // guaranteed to capture headers and body in one segment.
            while (!request.ToString().Contains("--\r\n"))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }
                request.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
            }
            var response = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}";
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response));
            return request.ToString();
        });

        try
        {
            var client = new CloudApiClient(new SingleClientFactory(), $"http://127.0.0.1:{port}", TimeSpan.FromSeconds(5));

            await client.UploadAvatarAsync("token", new byte[] { 1, 2, 3 }, "image/png", CancellationToken.None);

            var request = await requestTask;
            // nexus-api's FileInterceptor('file') 400s any other field name.
            // .NET quotes the disposition name only when it is not a simple
            // token, so accept both forms; the trailing delimiter keeps a
            // filename=... parameter from ever matching.
            Assert.Matches("name=\"?file\"?;", request);
        }
        finally
        {
            listener.Stop();
        }
    }
}
