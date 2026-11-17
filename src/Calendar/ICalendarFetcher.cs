using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Calendar;

namespace Nexus.Service.Calendar;

public enum CalendarFetchOutcome { Ok, NotModified, Error }

public sealed class CalendarFetchResult
{
    public CalendarFetchOutcome Outcome { get; init; }
    public string? Body { get; init; }
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
    public int Bytes { get; init; }
    public int HttpStatus { get; init; }
    /// <summary>One of <see cref="CalendarErrors"/> when <see cref="Outcome"/> is Error.</summary>
    public string? ErrorCode { get; init; }
}

public interface ICalendarFetcher
{
    Task<CalendarFetchResult> FetchAsync(Uri url, string? etag, string? lastModified, CancellationToken ct);
}

/// <summary>
/// One shared client: 20 s timeout, 10 MB body cap, conditional GET, and a
/// connect-time check that every resolved address is public (so a DNS name or a
/// redirect cannot reach the LAN). The system proxy is not used: its address is
/// usually private and would fail that check.
/// </summary>
public sealed class HttpCalendarFetcher : ICalendarFetcher
{
    public const int MaxBodyBytes = 10 * 1024 * 1024;
    private readonly HttpClient _http;

    public HttpCalendarFetcher()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectPublicOnlyAsync,
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<CalendarFetchResult> FetchAsync(Uri url, string? etag, string? lastModified, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Nexus-Calendar");
            req.Headers.TryAddWithoutValidation("Accept", "text/calendar, text/plain, */*");
            if (!string.IsNullOrEmpty(etag))
            {
                req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }
            if (!string.IsNullOrEmpty(lastModified))
            {
                req.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
            }
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            if (status == 304)
            {
                return new CalendarFetchResult { Outcome = CalendarFetchOutcome.NotModified, HttpStatus = status };
            }
            if (!resp.IsSuccessStatusCode)
            {
                var code = status is 401 or 403 ? CalendarErrors.Unauthorized
                    : status is 404 or 410 ? CalendarErrors.NotFound
                    : CalendarErrors.Unreachable;
                return Fail(code, status);
            }
            if (resp.Content.Headers.ContentLength is { } len && len > MaxBodyBytes)
            {
                return Fail(CalendarErrors.TooLarge, status);
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new System.IO.MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + read > MaxBodyBytes)
                {
                    return Fail(CalendarErrors.TooLarge, status);
                }
                ms.Write(buffer, 0, read);
            }
            return new CalendarFetchResult
            {
                Outcome = CalendarFetchOutcome.Ok,
                Body = System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length),
                ETag = resp.Headers.ETag?.ToString(),
                LastModified = resp.Content.Headers.LastModified?.ToString("R"),
                Bytes = (int)ms.Length,
                HttpStatus = status,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // HttpRequestException text can carry the host; only the code leaves this method.
            return Fail(CalendarErrors.Unreachable, 0);
        }
    }

    private static CalendarFetchResult Fail(string code, int status) =>
        new() { Outcome = CalendarFetchOutcome.Error, ErrorCode = code, HttpStatus = status };

    private static async ValueTask<System.IO.Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        Exception? last = null;
        foreach (var address in addresses)
        {
            if (!CalendarUrl.IsPublicAddress(address))
            {
                continue;
            }
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, ctx.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                last = ex;
            }
        }
        throw last ?? new HttpRequestException("No public address.");
    }
}
