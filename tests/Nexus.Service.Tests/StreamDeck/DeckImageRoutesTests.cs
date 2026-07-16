using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Deck;
using Nexus.Service.Tests.Integration;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>POST/GET /deck/images against a temp-rooted DeckImageStore.</summary>
[Collection("NexusHost")]
public sealed class DeckImageRoutesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-deck-image-route-" + Guid.NewGuid().ToString("N")[..8]);

    private static readonly byte[] TinyPng =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private (WebApplicationFactory<Program> factory, HttpClient client) Boot()
    {
        var factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<DeckImageStore>();
                s.AddSingleton(new DeckImageStore(_root));
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return (factory, client);
    }

    private static MultipartFormDataContent FileUpload(byte[] bytes, string fileName)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", fileName);
        return content;
    }

    [Fact]
    public async Task Upload_ReturnsTheContentAddressedId()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/deck/images", FileUpload(TinyPng, "icon.png"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var id = doc.RootElement.GetProperty("id").GetString();
            Assert.True(DeckImageStore.IsValidId(id));
            Assert.True(File.Exists(Path.Combine(_root, id! + ".png")));
        }
    }

    [Fact]
    public async Task Upload_RejectsANonImagePayload()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/deck/images", FileUpload(new byte[] { 1, 2, 3, 4 }, "not-an-image.bin"));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task Upload_RejectsAFormWithNoFile()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var form = new MultipartFormDataContent { { new StringContent("no file here"), "note" } };
            var res = await client.PostAsync("/deck/images", form);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task Get_ServesTheBytesWithImmutableCacheControlAndMatchingEtag()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var uploadRes = await client.PostAsync("/deck/images", FileUpload(TinyPng, "icon.png"));
            using var doc = System.Text.Json.JsonDocument.Parse(await uploadRes.Content.ReadAsStringAsync());
            var id = doc.RootElement.GetProperty("id").GetString();

            var res = await client.GetAsync($"/deck/images/{id}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("image/png", res.Content.Headers.ContentType!.MediaType);
            Assert.Equal(TinyPng, await res.Content.ReadAsByteArrayAsync());
            Assert.Contains("immutable", res.Headers.CacheControl!.ToString());
            Assert.Equal($"\"{id}\"", res.Headers.ETag!.Tag);
        }
    }

    [Fact]
    public async Task Get_Returns404ForAnUnknownId()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync($"/deck/images/{new string('a', 64)}");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }

    [Fact]
    public async Task Get_Returns404ForAnInvalidId()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/deck/images/not-a-valid-id");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }
}
