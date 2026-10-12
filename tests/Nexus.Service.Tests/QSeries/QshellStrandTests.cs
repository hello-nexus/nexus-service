using System;
using Nexus.Service.QSeries;
using Xunit;

namespace Nexus.Service.Tests.QSeries;

public class ParseProcessUptimeTests
{
    // starttime (field 22) = 3000 ticks = 30 s after boot.
    private const string Stat =
        "4321 (llonexus.qshell) S 312 312 0 0 -1 1077952832 25810 0 13 0 263 146 0 0 10 -10 37 0 3000 1610612736 21000 18446744073709551615";

    [Fact]
    public void Age_is_uptime_minus_start()
    {
        Assert.Equal(TimeSpan.FromSeconds(470.5), QSeriesPortWatcher.ParseProcessUptime($"500.50 1900.10\n{Stat}\n"));
    }

    [Fact]
    public void Comm_with_spaces_and_parens_does_not_shift_fields()
    {
        var stat = Stat.Replace("(llonexus.qshell)", "(a (b) c)");
        Assert.Equal(TimeSpan.FromSeconds(470.5), QSeriesPortWatcher.ParseProcessUptime($"500.50 1900.10\r\n{stat}\r\n"));
    }

    [Fact]
    public void No_qshell_process_reads_as_unknown()
    {
        // pidof prints nothing, so cat is handed /proc//stat.
        Assert.Null(QSeriesPortWatcher.ParseProcessUptime("500.50 1900.10\ncat: /proc//stat: No such file or directory\n"));
    }

    [Fact]
    public void Garbage_reads_as_unknown()
    {
        Assert.Null(QSeriesPortWatcher.ParseProcessUptime(""));
        Assert.Null(QSeriesPortWatcher.ParseProcessUptime($"/system/bin/sh: pidof: not found\n{Stat}"));
        Assert.Null(QSeriesPortWatcher.ParseProcessUptime("500.50 1900.10\n4321 (x) S 312"));
    }

    [Fact]
    public void Start_after_uptime_reads_as_unknown()
    {
        // A start beyond uptime means a wrong tick unit; never report an age from it.
        Assert.Null(QSeriesPortWatcher.ParseProcessUptime($"20.00 40.00\n{Stat}"));
    }
}

public class StrandedBeforeGraceTests
{
    private static readonly TimeSpan Quiet = QSeriesPortWatcher.TunnelSilenceGrace;
    private static readonly TimeSpan Old = QSeriesPortWatcher.QshellBootstrapStartBound;

    [Fact]
    public void Old_qshell_behind_a_quiet_open_tunnel_is_stranded()
    {
        Assert.True(QSeriesPortWatcher.StrandedBeforeGrace(Quiet, Old, 0));
        // A panel first seen long after qshell launched: its bootstrap is spent.
        Assert.True(QSeriesPortWatcher.StrandedBeforeGrace(TimeSpan.FromSeconds(40), TimeSpan.FromMinutes(31), 0));
    }

    [Fact]
    public void Open_socket_is_a_connected_page_not_a_strand()
    {
        // An idle connected page sends only a keepalive pong per interval.
        Assert.False(QSeriesPortWatcher.StrandedBeforeGrace(TimeSpan.FromMinutes(2), TimeSpan.FromHours(1), 1));
    }

    [Fact]
    public void Freshly_launched_qshell_is_left_to_bootstrap()
    {
        Assert.False(QSeriesPortWatcher.StrandedBeforeGrace(TimeSpan.FromSeconds(170), Old - TimeSpan.FromSeconds(1), 0));
    }

    [Fact]
    public void Tunnel_open_too_briefly_proves_nothing()
    {
        Assert.False(QSeriesPortWatcher.StrandedBeforeGrace(Quiet - TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), 0));
    }

    [Fact]
    public void Unknowns_never_prove_a_strand()
    {
        Assert.False(QSeriesPortWatcher.StrandedBeforeGrace(null, TimeSpan.FromHours(1), 0));
        Assert.False(QSeriesPortWatcher.StrandedBeforeGrace(TimeSpan.FromMinutes(2), null, 0));
    }
}
