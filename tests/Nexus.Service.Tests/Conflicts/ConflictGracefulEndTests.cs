using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Conflicts;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

public class ConflictGracefulEndTests
{
    private static readonly ConflictAppDefinition Graceful = new()
    {
        Id = "app",
        DisplayName = "App",
        ProcessNames = new[] { "App", "AppService" },
        GracefulProcessNames = new[] { "App" },
    };

    private static readonly ConflictAppDefinition Plain = new()
    {
        Id = "plain",
        DisplayName = "Plain",
        ProcessNames = new[] { "Plain" },
    };

    private sealed class Rig
    {
        public long Now;
        /// <summary>When the app's own process exits; null means it never does.</summary>
        public long? ExitsAt;
        /// <summary>A service or helper process that outlives the app until a kill.</summary>
        public bool Leftover;
        public bool BackOffRunning;
        public SessionEndOutcome Outcome = SessionEndOutcome.Delivered;
        public long NoWindowsUntil;
        public readonly List<long> Sends = new();
        public readonly List<long> Kills = new();
        public readonly List<string> Log = new();
        public readonly ConflictGracefulEnd End;

        public Rig(long? exitsAt, bool canSend = true)
        {
            ExitsAt = exitsAt;
            End = new ConflictGracefulEnd(
                _ => ExitsAt is { } e && Now >= e ? Array.Empty<int>() : new[] { 4242 },
                _ =>
                {
                    Sends.Add(Now);
                    return Now < NoWindowsUntil ? SessionEndOutcome.NoWindows : Outcome;
                },
                _ => (ExitsAt is not { } e || Now < e) || Leftover,
                _ => BackOffRunning,
                _ =>
                {
                    Kills.Add(Now);
                    ExitsAt = Now;
                    Leftover = false;
                },
                () => Now,
                ms => Now += ms,
                Log.Add,
                canSend);
        }
    }

    private static long Ms(TimeSpan t) => (long)t.TotalMilliseconds;

    [Fact]
    public void An_app_that_closes_on_the_request_is_never_killed()
    {
        var rig = new Rig(exitsAt: 3000);

        rig.End.End(Graceful);

        Assert.Single(rig.Sends);
        Assert.Empty(rig.Kills);
        Assert.Contains(rig.Log, l => l.Contains("closed after"));
    }

    [Fact]
    public void An_app_that_stays_is_killed_only_after_the_wait_from_the_delivery()
    {
        var rig = new Rig(exitsAt: null);

        rig.End.End(Graceful);

        Assert.InRange(Assert.Single(rig.Kills), Ms(ConflictGracefulEnd.Wait), Ms(ConflictGracefulEnd.Wait) + 1000);
    }

    [Fact]
    public void The_request_is_repeated_while_the_app_stays_but_the_deadline_is_set_by_the_first_delivery()
    {
        var rig = new Rig(exitsAt: null);

        rig.End.End(Graceful);

        Assert.True(rig.Sends.Count >= 3);
        Assert.All(rig.Sends.Zip(rig.Sends.Skip(1)), p => Assert.True(p.Second - p.First >= Ms(ConflictGracefulEnd.Resend)));
        Assert.InRange(Assert.Single(rig.Kills), Ms(ConflictGracefulEnd.Wait), Ms(ConflictGracefulEnd.Wait) + 1000);
    }

    [Fact]
    public void An_app_with_no_windows_yet_is_asked_again_and_not_killed_early()
    {
        var rig = new Rig(exitsAt: null) { NoWindowsUntil = 7000 };
        rig.Outcome = SessionEndOutcome.Delivered;
        var end = new ConflictGracefulEnd(
            _ => rig.ExitsAt is { } e && rig.Now >= e ? Array.Empty<int>() : new[] { 7 },
            _ =>
            {
                rig.Sends.Add(rig.Now);
                if (rig.Now < rig.NoWindowsUntil) return SessionEndOutcome.NoWindows;
                rig.ExitsAt ??= rig.Now + 2500;
                return SessionEndOutcome.Delivered;
            },
            _ => rig.ExitsAt is not { } e || rig.Now < e,
            _ => false,
            _ => rig.Kills.Add(rig.Now),
            () => rig.Now,
            ms => rig.Now += ms);

        end.End(Graceful);

        Assert.Empty(rig.Kills);
        Assert.True(rig.Sends.Count >= 3);
    }

    [Fact]
    public void A_helper_that_never_answers_gets_the_long_wait_before_the_kill()
    {
        var rig = new Rig(exitsAt: null) { Outcome = SessionEndOutcome.HelperUnavailable };

        rig.End.End(Graceful);

        Assert.InRange(Assert.Single(rig.Kills), Ms(ConflictGracefulEnd.NotReachableWait), Ms(ConflictGracefulEnd.NotReachableWait) + 1000);
        Assert.Contains(rig.Log, l => l.Contains("HelperUnavailable"));
    }

    [Fact]
    public void A_helper_that_comes_up_late_still_gets_its_request_delivered()
    {
        var rig = new Rig(exitsAt: null);
        var end = new ConflictGracefulEnd(
            _ => rig.ExitsAt is { } e && rig.Now >= e ? Array.Empty<int>() : new[] { 7 },
            _ =>
            {
                rig.Sends.Add(rig.Now);
                if (rig.Now < 12_000) return SessionEndOutcome.HelperUnavailable;
                rig.ExitsAt ??= rig.Now + 2000;
                return SessionEndOutcome.Delivered;
            },
            _ => rig.ExitsAt is not { } e || rig.Now < e,
            _ => false,
            _ => rig.Kills.Add(rig.Now),
            () => rig.Now,
            ms => rig.Now += ms);

        end.End(Graceful);

        Assert.Empty(rig.Kills);
    }

    [Fact]
    public void A_refused_request_means_a_higher_integrity_app_and_is_killed_at_once()
    {
        var rig = new Rig(exitsAt: null) { Outcome = SessionEndOutcome.Refused };

        rig.End.End(Graceful);

        Assert.Equal(new long[] { 0 }, rig.Kills);
    }

    [Fact]
    public void Leftover_services_are_ended_the_moment_the_app_itself_is_gone()
    {
        var rig = new Rig(exitsAt: 2000) { Leftover = true };

        rig.End.End(Graceful);

        // The app exits at 2000 ms; the leftover is ended right then, not after the wait.
        Assert.InRange(Assert.Single(rig.Kills), 2000, 2500);
    }

    [Fact]
    public void While_an_installer_runs_nothing_is_sent_and_nothing_is_ended()
    {
        var rig = new Rig(exitsAt: null) { BackOffRunning = true };

        rig.End.End(Graceful);
        rig.End.End(Plain);

        Assert.Empty(rig.Sends);
        Assert.Empty(rig.Kills);
    }

    [Fact]
    public void Without_a_way_to_send_the_app_is_killed_at_once()
    {
        var rig = new Rig(exitsAt: null, canSend: false);

        rig.End.End(Graceful);

        Assert.Empty(rig.Sends);
        Assert.Equal(new long[] { 0 }, rig.Kills);
    }

    [Fact]
    public void An_app_with_no_graceful_names_is_killed_at_once()
    {
        var rig = new Rig(exitsAt: null);

        rig.End.End(Plain);

        Assert.Empty(rig.Sends);
        Assert.Equal(new long[] { 0 }, rig.Kills);
    }

    [Fact]
    public void An_app_already_gone_is_left_alone()
    {
        var rig = new Rig(exitsAt: 0);

        rig.End.End(Graceful);

        Assert.Empty(rig.Sends);
        Assert.Empty(rig.Kills);
    }

    [Fact]
    public void The_outcome_reports_running_before_and_after()
    {
        var rig = new Rig(exitsAt: 3000);

        var outcome = rig.End.EndWithOutcome(Graceful);

        Assert.True(outcome.RunningBefore);
        Assert.False(outcome.RunningAfter);
        Assert.True(outcome.Killed);
    }

    [Fact]
    public void The_catalog_ends_the_icue_main_process_gracefully()
    {
        var icue = ConflictAppCatalog.All.Single(a => a.Id == "icue");

        Assert.Contains("iCUE", icue.GracefulProcessNames);
        Assert.All(icue.GracefulProcessNames, n => Assert.Contains(n, icue.ProcessNames));
    }
}
