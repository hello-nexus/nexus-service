using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Store;
using Xunit;

namespace Nexus.Service.Tests.Store;

/// <summary>
/// The store media proxy relays bytes onto the dashboard origin, so only image
/// types under the size cap come through; anything else the bucket hands back
/// (an HTML page, an oversized object) is refused.
/// </summary>
public sealed class StoreCatalogProxyMediaTests
{
    private sealed class TypedHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly string _contentType;
        private readonly long? _declaredLength;

        public TypedHandler(byte[] body, string contentType, long? declaredLength = null)
        {
            _body = body;
            _contentType = contentType;
            _declaredLength = declaredLength;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent(_body);
            content.Headers.ContentType = new MediaTypeHeaderValue(_contentType);
            if (_declaredLength is { } length)
            {
                content.Headers.ContentLength = length;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static StoreCatalogProxy Proxy(byte[] body, string contentType, long? declaredLength = null)
        => new(new HttpClient(new TypedHandler(body, contentType, declaredLength)));

    [Fact]
    public async Task Image_types_are_relayed_with_a_normalized_content_type()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var media = await Proxy(png, "image/PNG").MediaAsync("com.example.app/media/icon.png", CancellationToken.None);

        Assert.NotNull(media);
        Assert.Equal(png, media!.Value.bytes);
        Assert.Equal("image/png", media.Value.contentType);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/javascript")]
    [InlineData("application/octet-stream")]
    public async Task Non_image_types_are_refused(string contentType)
    {
        var media = await Proxy(new byte[] { 1, 2, 3 }, contentType).MediaAsync("com.example.app/media/x", CancellationToken.None);
        Assert.Null(media);
    }

    [Fact]
    public async Task Objects_over_the_cap_are_refused()
    {
        var declared = await Proxy(new byte[] { 1 }, "image/png", declaredLength: StoreCatalogProxy.MaxMediaBytes + 1L)
            .MediaAsync("com.example.app/media/big.png", CancellationToken.None);
        Assert.Null(declared);

        var oversized = new byte[StoreCatalogProxy.MaxMediaBytes + 1];
        var streamed = await Proxy(oversized, "image/png").MediaAsync("com.example.app/media/big.png", CancellationToken.None);
        Assert.Null(streamed);
    }

    [Fact]
    public async Task Path_traversal_is_refused_before_any_request()
    {
        var media = await Proxy(new byte[] { 1 }, "image/png").MediaAsync("../other/x.png", CancellationToken.None);
        Assert.Null(media);
    }
}
