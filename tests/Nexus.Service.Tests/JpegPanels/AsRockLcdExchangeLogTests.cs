using System.Text;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

public class AsRockLcdExchangeLogTests
{
    private static byte[] Reply(string text) => Encoding.ASCII.GetBytes(text);

    [Fact]
    public void Ok_reply_is_classified_ok()
    {
        var outcome = AsRockLcdExchangeLog.Classify(true, 64, Reply("POST 200 OK\r\n\r\n"), out var status);
        Assert.Equal(AsRockExchangeOutcome.Ok, outcome);
        Assert.Equal("200", status);
    }

    [Fact]
    public void Non_200_reply_keeps_its_status()
    {
        var outcome = AsRockLcdExchangeLog.Classify(true, 64, Reply("POST 503 Busy\r\n\r\n"), out var status);
        Assert.Equal(AsRockExchangeOutcome.Non200, outcome);
        Assert.Equal("503", status);
    }

    [Theory]
    [InlineData(true, 0, AsRockExchangeOutcome.ReadTimeout)]
    [InlineData(true, -1, AsRockExchangeOutcome.ReadFailed)]
    [InlineData(false, 0, AsRockExchangeOutcome.WriteFailed)]
    public void No_reply_is_told_apart(bool wrote, int read, AsRockExchangeOutcome expected)
    {
        Assert.Equal(expected, AsRockLcdExchangeLog.Classify(wrote, read, null, out var status));
        Assert.Null(status);
    }

    [Fact]
    public void Empty_decode_is_undecodable()
    {
        Assert.Equal(AsRockExchangeOutcome.UndecodableReply,
            AsRockLcdExchangeLog.Classify(true, 64, new byte[0], out _));
    }

    [Fact]
    public void Sanitize_replaces_line_breaks_and_control_bytes_and_truncates()
    {
        Assert.Equal("a b.c", AsRockLcdExchangeLog.Sanitize(new byte[] { 0x61, 0x0D, 0x0A, 0x62, 0x01, 0x63 }).Replace("  ", " "));
        var longText = AsRockLcdExchangeLog.Sanitize(Reply(new string('x', 500)));
        Assert.Equal(AsRockLcdExchangeLog.MaxReplyChars + 3, longText.Length);
        Assert.EndsWith("...", longText);
    }

    [Fact]
    public void Format_states_timeout_and_non_200_distinctly()
    {
        var timeout = AsRockLcdExchangeLog.Format(
            "realtimeDisplay", AsRockExchangeOutcome.ReadTimeout, null, 301, default, 0, 300);
        Assert.Contains("realtimeDisplay: read timeout, no reply within 300 ms", timeout);

        var non200 = AsRockLcdExchangeLog.Format(
            "conn", AsRockExchangeOutcome.Non200, "503", 12, Reply("POST 503 Busy"), 0, 300);
        Assert.Contains("non-200 reply, status 503, in 12 ms: POST 503 Busy", non200);

        var write = AsRockLcdExchangeLog.Format(
            "conn", AsRockExchangeOutcome.WriteFailed, null, 1000, default, 1460, 300);
        Assert.Contains("write failed, Win32 error 1460", write);
    }

    [Fact]
    public void Write_failure_line_carries_chunk_latency_and_error()
    {
        var line = JpegPanelDiagnostics.FormatWriteFailure(4, 1024, 4096, 20000, 1001, 1460);
        Assert.Contains("chunk 4 (1024 byte chunk, 4096 of 20000 bytes sent)", line);
        Assert.Contains("write took 1001 ms", line);
        Assert.Contains("Win32 error 1460", line);
    }

    [Fact]
    public void Limiter_passes_the_burst_then_one_summary_per_interval()
    {
        var limiter = new RepeatLogLimiter(burst: 2, intervalMs: 1000);
        Assert.True(limiter.TryAcquire(0, out _));
        Assert.True(limiter.TryAcquire(10, out _));
        Assert.False(limiter.TryAcquire(500, out _));
        Assert.False(limiter.TryAcquire(900, out _));
        Assert.True(limiter.TryAcquire(1010, out var suppressed));
        Assert.Equal(2, suppressed);
        Assert.False(limiter.TryAcquire(1500, out _));
    }
}
