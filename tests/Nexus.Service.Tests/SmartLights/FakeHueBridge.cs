using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Nexus.Service.Tests.SmartLights;

/// <summary>
/// Real (socket-bound) HTTPS Hue bridge stand-in for HueDriver tests.
/// HueBridgeClient always opens a genuine TCP/TLS connection, so an in-memory
/// TestServer can't stand in - this runs a real Kestrel listener on a loopback
/// ephemeral port with a throwaway self-signed cert (HueBridgeClient accepts any
/// server cert). Serves only the light PUT endpoint the dedup test needs.
/// </summary>
internal sealed class FakeHueBridge : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly X509Certificate2 _cert;

    public ConcurrentQueue<(string Rid, string Body)> Puts { get; } = new();
    public int Port { get; private set; }
    public string Host => $"127.0.0.1:{Port}";

    public FakeHueBridge()
    {
        _cert = CreateSelfSignedCert();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(opts => opts.Listen(IPAddress.Loopback, 0, lo => lo.UseHttps(_cert)));
        _app = builder.Build();

        _app.MapPut("/clip/v2/resource/light/{rid}", async (string rid, HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            Puts.Enqueue((rid, body));
            return Results.Ok();
        });
    }

    public async Task StartAsync()
    {
        await _app.StartAsync();
        Port = new Uri(_app.Urls.First()).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _cert.Dispose();
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // SChannel cannot use an ephemeral in-memory key for a TLS server; a PFX round trip gives it a usable one.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }
}
