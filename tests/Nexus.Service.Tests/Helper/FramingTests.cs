using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Helper;
using Xunit;

namespace Nexus.Service.Tests.Helper;

public class FramingTests
{
    /// <summary>Cancels the supplied source as soon as the first write lands, like a client dropping mid-frame.</summary>
    private sealed class CancelOnFirstWriteStream(CancellationTokenSource cts) : MemoryStream
    {
        private bool _first = true;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            if (_first)
            {
                _first = false;
                cts.Cancel();
            }
        }
    }

    [Fact]
    public async Task WriteFrame_CancelledAfterTheHeader_StillWritesTheWholeFrame()
    {
        var body = Encoding.UTF8.GetBytes("{\"type\":\"x\"}");
        using var cts = new CancellationTokenSource();
        await using var pipe = new CancelOnFirstWriteStream(cts);

        await Framing.WriteFrameAsync(pipe, body, cts.Token);

        Assert.True(cts.IsCancellationRequested);
        pipe.Position = 0;
        var read = await Framing.ReadFrameAsync(pipe, CancellationToken.None);
        Assert.Equal(body, read);
        Assert.Equal(pipe.Length, 4 + body.Length);
    }

    [Fact]
    public async Task WriteFrame_CancelledBeforeAnyByte_WritesNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await using var pipe = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Framing.WriteFrameAsync(pipe, new byte[] { 1, 2, 3 }, cts.Token));

        Assert.Equal(0, pipe.Length);
    }
}
