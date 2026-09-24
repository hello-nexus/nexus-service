using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Panel;
using Nexus.Service.QSeries;
using Xunit;

namespace Nexus.Service.Tests.QSeries;

public class PanelTunnelMonitorTests
{
    [Fact]
    public void Inactive_when_port_null()
    {
        var monitor = new PanelTunnelMonitor(null);
        Assert.False(monitor.IsActive);
        Assert.Null(monitor.Port);
    }

    [Fact]
    public void Active_with_port_and_zero_activity_until_marked()
    {
        var monitor = new PanelTunnelMonitor(9401);
        Assert.True(monitor.IsActive);
        Assert.Equal(9401, monitor.Port);
        Assert.Equal(0, monitor.LastInboundActivityUnixMs);

        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        monitor.MarkInboundActivity();
        Assert.InRange(monitor.LastInboundActivityUnixMs, before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}

public class PanelTunnelAuthTests
{
    private static DefaultHttpContext ContextOnPort(int port)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.LocalPort = port;
        return ctx;
    }

    [Fact]
    public void Only_tunnel_port_requests_mark_authorized()
    {
        var monitor = new PanelTunnelMonitor(9401);
        Assert.False(monitor.IsTunnelRequest(ContextOnPort(9400)));
        monitor.MarkAuthorized(ContextOnPort(9400));
        Assert.Equal(0, monitor.LastAuthorizedUnixMs);

        Assert.True(monitor.IsTunnelRequest(ContextOnPort(9401)));
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        monitor.MarkAuthorized(ContextOnPort(9401));
        Assert.InRange(monitor.LastAuthorizedUnixMs, before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Unbound_monitor_never_matches()
    {
        var monitor = new PanelTunnelMonitor(null);
        Assert.False(monitor.IsTunnelRequest(ContextOnPort(0)));
    }

    [Fact]
    public void Socket_count_balances_and_close_restamps()
    {
        var monitor = new PanelTunnelMonitor(9401);
        monitor.SocketOpened();
        monitor.SocketOpened();
        Assert.Equal(2, monitor.AuthenticatedSockets);
        var opened = monitor.LastAuthorizedUnixMs;
        Assert.NotEqual(0, opened);

        monitor.SocketClosed();
        monitor.SocketClosed();
        Assert.Equal(0, monitor.AuthenticatedSockets);
        Assert.True(monitor.LastAuthorizedUnixMs >= opened);
    }
}

public class PanelContactedSinceTests
{
    private const long FirstSeen = 1_000_000;

    [Fact]
    public void Tunnel_active_with_activity_since_first_seen_is_alive()
    {
        Assert.True(QSeriesPortWatcher.PanelContactedSince(FirstSeen + 1, null, FirstSeen));
        Assert.True(QSeriesPortWatcher.PanelContactedSince(FirstSeen, null, FirstSeen));
    }

    [Fact]
    public void Tunnel_active_with_stale_activity_is_dead()
    {
        Assert.False(QSeriesPortWatcher.PanelContactedSince(FirstSeen - 1, null, FirstSeen));
        Assert.False(QSeriesPortWatcher.PanelContactedSince(0, null, FirstSeen));
    }

    [Fact]
    public void Tunnel_active_ignores_record_last_seen()
    {
        // The masking bug this replaces: a fresh record LastSeenAt (desktop
        // dashboard GETs) must not count as panel liveness when the tunnel
        // signal is available.
        Assert.False(QSeriesPortWatcher.PanelContactedSince(0, FirstSeen + 500, FirstSeen));
    }

    [Fact]
    public void Legacy_fallback_uses_record_last_seen()
    {
        Assert.True(QSeriesPortWatcher.PanelContactedSince(null, FirstSeen + 1, FirstSeen));
        Assert.False(QSeriesPortWatcher.PanelContactedSince(null, FirstSeen - 1, FirstSeen));
        Assert.False(QSeriesPortWatcher.PanelContactedSince(null, null, FirstSeen));
    }
}

public class EscalationRebootPermittedTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_attempt_with_no_prior_reboots_is_permitted()
    {
        Assert.True(QSeriesPortWatcher.EscalationRebootPermitted(0, null, null, Now));
    }

    [Fact]
    public void Budget_exhausted_after_three_attempts()
    {
        Assert.True(QSeriesPortWatcher.EscalationRebootPermitted(2, null, null, Now));
        Assert.False(QSeriesPortWatcher.EscalationRebootPermitted(3, null, null, Now));
    }

    [Fact]
    public void Recent_reseat_reboot_blocks_escalation()
    {
        // Any reboot (reseat path included) within its 2-min cooldown must
        // block; past it, escalation is allowed again.
        Assert.False(QSeriesPortWatcher.EscalationRebootPermitted(0, Now.AddMinutes(-1), null, Now));
        Assert.True(QSeriesPortWatcher.EscalationRebootPermitted(0, Now.AddMinutes(-3), null, Now));
    }

    [Fact]
    public void Escalation_retries_are_spaced_thirty_minutes()
    {
        Assert.False(QSeriesPortWatcher.EscalationRebootPermitted(1, Now.AddMinutes(-29), Now.AddMinutes(-29), Now));
        Assert.True(QSeriesPortWatcher.EscalationRebootPermitted(1, Now.AddMinutes(-31), Now.AddMinutes(-31), Now));
    }
}

public class StaleSessionDetectedTests
{
    private const long FirstSeen = 1_000_000;
    private static readonly long Grace = (long)QSeriesPortWatcher.StaleSessionGrace.TotalMilliseconds;

    [Fact]
    public void Contacted_but_never_authenticated_past_the_grace_is_stale()
    {
        Assert.True(QSeriesPortWatcher.StaleSessionDetected(FirstSeen + 2_000, 0, 0, FirstSeen, FirstSeen + Grace));
        Assert.True(QSeriesPortWatcher.StaleSessionDetected(FirstSeen + 2_000, FirstSeen - 1, 0, FirstSeen, FirstSeen + Grace + 1));
    }

    [Fact]
    public void Silent_panel_is_the_escalation_reboot_s_case_not_this_one()
    {
        Assert.False(QSeriesPortWatcher.StaleSessionDetected(FirstSeen - 1, 0, 0, FirstSeen, FirstSeen + Grace));
        Assert.False(QSeriesPortWatcher.StaleSessionDetected(0, 0, 0, FirstSeen, FirstSeen + Grace));
    }

    [Fact]
    public void Bootstrap_allocate_or_socket_upgrade_counts_as_authenticated()
    {
        Assert.False(QSeriesPortWatcher.StaleSessionDetected(FirstSeen + 2_000, FirstSeen + 3_000, 0, FirstSeen, FirstSeen + Grace));
        Assert.False(QSeriesPortWatcher.StaleSessionDetected(FirstSeen + 2_000, FirstSeen, 0, FirstSeen, FirstSeen + Grace));
    }

    [Fact]
    public void Open_authenticated_socket_is_never_stale()
    {
        // A socket that survived a re-enumeration re-anchor authenticated before the new first sighting.
        Assert.False(QSeriesPortWatcher.StaleSessionDetected(FirstSeen + 2_000, FirstSeen - 5_000, 1, FirstSeen, FirstSeen + Grace));
    }

    [Fact]
    public void Grace_covers_a_reconnect_backoff()
    {
        Assert.False(QSeriesPortWatcher.StaleSessionDetected(FirstSeen + 2_000, 0, 0, FirstSeen, FirstSeen + Grace - 1));
        // nexus-web useMultiplexSocket RECONNECT_MAX_MS: a healthy page's next upgrade is at most this far away.
        Assert.True(QSeriesPortWatcher.StaleSessionGrace > TimeSpan.FromSeconds(60));
    }
}

public class TunnelActivityPipeReaderTests
{
    [Fact]
    public async Task Marks_activity_when_read_yields_bytes()
    {
        var monitor = new PanelTunnelMonitor(9401);
        var pipe = new Pipe();
        var reader = new TunnelActivityPipeReader(pipe.Reader, monitor);

        await pipe.Writer.WriteAsync(new byte[] { 1, 2, 3 });

        var result = await reader.ReadAsync();
        Assert.Equal(3, result.Buffer.Length);
        Assert.NotEqual(0, monitor.LastInboundActivityUnixMs);

        reader.AdvanceTo(result.Buffer.End);
        await reader.CompleteAsync();
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task Does_not_mark_on_empty_completion()
    {
        var monitor = new PanelTunnelMonitor(9401);
        var pipe = new Pipe();
        var reader = new TunnelActivityPipeReader(pipe.Reader, monitor);

        await pipe.Writer.CompleteAsync();

        var result = await reader.ReadAsync();
        Assert.True(result.IsCompleted);
        Assert.Equal(0, monitor.LastInboundActivityUnixMs);

        await reader.CompleteAsync();
    }

    [Fact]
    public async Task TryRead_marks_activity_on_buffered_bytes()
    {
        var monitor = new PanelTunnelMonitor(9401);
        var pipe = new Pipe();
        var reader = new TunnelActivityPipeReader(pipe.Reader, monitor);

        await pipe.Writer.WriteAsync(new byte[] { 7 });

        Assert.True(reader.TryRead(out var result));
        Assert.Equal(1, result.Buffer.Length);
        Assert.NotEqual(0, monitor.LastInboundActivityUnixMs);

        reader.AdvanceTo(result.Buffer.End);
        await reader.CompleteAsync();
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task Canceled_read_does_not_mark_even_with_buffered_bytes()
    {
        var monitor = new PanelTunnelMonitor(9401);
        var pipe = new Pipe();
        var reader = new TunnelActivityPipeReader(pipe.Reader, monitor);

        // Buffer a byte but leave it unexamined, then cancel: the canceled
        // result surfaces the old bytes and must not count as fresh activity.
        await pipe.Writer.WriteAsync(new byte[] { 7 });
        var first = await reader.ReadAsync();
        reader.AdvanceTo(first.Buffer.Start, first.Buffer.Start);
        var marked = monitor.LastInboundActivityUnixMs;

        reader.CancelPendingRead();
        var canceled = await reader.ReadAsync();
        Assert.True(canceled.IsCanceled);
        Assert.Equal(marked, monitor.LastInboundActivityUnixMs);

        reader.AdvanceTo(canceled.Buffer.Start, canceled.Buffer.Start);
        await reader.CompleteAsync();
        await pipe.Writer.CompleteAsync();
    }
}
