#if WINDOWS
using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Service.Helper;
using Nexus.Service.Lighting.Capture;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

public class HelperScreenFrameSourceTests
{
    // With a gate, the first start send signals `entered` and then blocks until the gate opens.
    private static (HelperScreenFrameSource Source, List<string> Sent) Create(
        Task? firstSendGate = null, TaskCompletionSource? entered = null)
    {
        var sent = new List<string>();
        var gate = firstSendGate;
        var source = new HelperScreenFrameSource(new HelperRegistry(),
            async (m, w, h) =>
            {
                var wait = gate;
                gate = null;
                if (wait is not null)
                {
                    entered?.SetResult();
                    await wait;
                }
                lock (sent) { sent.Add($"start {m} {w}x{h}"); }
            },
            () =>
            {
                lock (sent) { sent.Add("stop"); }
                return Task.CompletedTask;
            });
        return (source, sent);
    }

    [Fact]
    public async Task HelperConnectsAfterStart_ResendsTheActiveRequest()
    {
        var (source, sent) = Create();

        source.Start("0", 64, 36);
        await source.PendingSends;
        sent.Clear();
        await source.ResendStartAsync();

        Assert.Equal(new[] { "start 0 64x36" }, sent);
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
        await source.PendingSends;
        sent.Clear();
        await source.ResendStartAsync();

        Assert.Empty(sent);
    }

    [Fact]
    public async Task StopWhileResendIsQueued_ResendIsDroppedAndStopIsLast()
    {
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var (source, sent) = Create(gate.Task, entered);

        source.Start("0", 64, 36);
        await entered.Task;
        var resend = source.ResendStartAsync();
        source.Stop();
        gate.SetResult();
        await resend;
        await source.PendingSends;

        Assert.Equal(new[] { "start 0 64x36", "stop" }, sent);
    }

    [Fact]
    public async Task MonitorSwitchWhileResendIsQueued_OnlyTheNewMonitorStarts()
    {
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var (source, sent) = Create(gate.Task, entered);

        source.Start("0", 64, 36);
        await entered.Task;
        _ = source.ResendStartAsync();
        source.Stop();
        source.Start("1", 64, 36);
        gate.SetResult();
        await source.PendingSends;

        Assert.Equal(new[] { "start 0 64x36", "stop", "start 1 64x36" }, sent);
    }
}
#endif
