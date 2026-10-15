#if WINDOWS
using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Service.Helper;
using Nexus.Service.Lighting.Capture;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

public class HelperScreenFrameSourceTests
{
    private static (HelperScreenFrameSource Source, List<(string Monitor, int W, int H)> Sent) Create()
    {
        var sent = new List<(string, int, int)>();
        var source = new HelperScreenFrameSource(new HelperRegistry(), (m, w, h) =>
        {
            lock (sent) { sent.Add((m, w, h)); }
            return Task.CompletedTask;
        });
        return (source, sent);
    }

    [Fact]
    public async Task HelperConnectsAfterStart_ResendsTheActiveRequest()
    {
        var (source, sent) = Create();

        source.Start("0", 64, 36);
        sent.Clear();
        await source.ResendStartAsync();

        Assert.Equal(new[] { ("0", 64, 36) }, sent);
    }

    [Fact]
    public async Task HelperConnectsWithoutAStart_SendsNothing()
    {
        var (source, sent) = Create();

        await source.ResendStartAsync();

        Assert.Empty(sent);
    }

    [Fact]
    public async Task HelperConnectsAfterStop_SendsNothing()
    {
        var (source, sent) = Create();

        source.Start("0", 64, 36);
        source.Stop();
        sent.Clear();
        await source.ResendStartAsync();

        Assert.Empty(sent);
    }
}
#endif
