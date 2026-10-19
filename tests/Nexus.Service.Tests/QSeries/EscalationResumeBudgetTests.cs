using System;
using System.Collections.Generic;
using Nexus.Service.QSeries;
using Xunit;

namespace Nexus.Service.Tests.QSeries;

public class EscalationResumeBudgetTests
{
    private const string Serial = "Q60-1";
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<string, long> _applied = new();
    private readonly Dictionary<string, int> _counts = new();
    private readonly Dictionary<string, DateTimeOffset> _lastEscalation = new();

    private bool Permitted() =>
        QSeriesPortWatcher.EscalationRebootPermitted(
            _counts.GetValueOrDefault(Serial),
            null,
            _lastEscalation.TryGetValue(Serial, out var at) ? at : null,
            Now);

    private bool Reset(long epoch) =>
        QSeriesPortWatcher.ResetBudgetForResume(Serial, epoch, _applied, _counts, _lastEscalation);

    [Fact]
    public void Exhausted_budget_is_re_allowed_by_a_resume()
    {
        Reset(0);
        _counts[Serial] = 3;
        _lastEscalation[Serial] = Now - TimeSpan.FromHours(5);
        Assert.False(Permitted());

        Assert.True(Reset(1));
        Assert.True(Permitted());
    }

    [Fact]
    public void Active_cooldown_is_re_allowed_by_a_resume()
    {
        Reset(0);
        _counts[Serial] = 1;
        _lastEscalation[Serial] = Now - TimeSpan.FromMinutes(5);
        Assert.False(Permitted());

        Assert.True(Reset(1));
        Assert.True(Permitted());
    }

    [Fact]
    public void Within_one_epoch_the_cap_and_cooldown_hold()
    {
        Reset(1);
        _counts[Serial] = 3;
        _lastEscalation[Serial] = Now - TimeSpan.FromHours(5);

        Assert.False(Reset(1));
        Assert.False(Permitted());

        _counts[Serial] = 1;
        _lastEscalation[Serial] = Now - TimeSpan.FromMinutes(5);
        Assert.False(Reset(1));
        Assert.False(Permitted());
    }

    [Fact]
    public void Failing_panel_is_capped_again_after_the_resume_reset()
    {
        Reset(1);
        Assert.True(Reset(2));
        _counts[Serial] = 3;
        Assert.False(Reset(2));
        Assert.False(Permitted());
    }

    [Fact]
    public void Reset_leaves_other_serials_alone()
    {
        Reset(0);
        _counts["other"] = 3;
        Reset(1);
        Assert.Equal(3, _counts["other"]);
    }

    [Fact]
    public void Panel_not_re_sighted_since_the_resume_keeps_its_budget()
    {
        Reset(0);
        _counts[Serial] = 3;
        Assert.False(QSeriesPortWatcher.ResetBudgetForResume(Serial, 1, _applied, _counts, _lastEscalation, sightedSinceResume: false));
        Assert.False(Permitted());
        Assert.True(Reset(1));
        Assert.True(Permitted());
    }

    [Fact]
    public void Short_post_reboot_settle_is_not_logged()
    {
        var reason = QSeriesPortWatcher.EscalationSuppressionReason(0, Now - TimeSpan.FromSeconds(30), null, Now);
        Assert.NotNull(reason);
        Assert.Null(QSeriesPortWatcher.EscalationSuppressionMessage(Serial, reason!, new HashSet<string>()));
    }

    [Fact]
    public void Suppression_is_logged_once_per_serial_with_the_reason()
    {
        var logged = new HashSet<string>();
        var reason = QSeriesPortWatcher.EscalationSuppressionReason(3, null, null, Now);
        Assert.NotNull(reason);

        var first = QSeriesPortWatcher.EscalationSuppressionMessage(Serial, reason!, logged);
        Assert.NotNull(first);
        Assert.Contains(Serial, first);
        Assert.Contains("suppressed", first);
        Assert.Null(QSeriesPortWatcher.EscalationSuppressionMessage(Serial, reason!, logged));

        logged.Remove(Serial);
        Assert.NotNull(QSeriesPortWatcher.EscalationSuppressionMessage(Serial, reason!, logged));
    }

    [Fact]
    public void Suppression_reason_is_null_when_permitted_and_set_when_blocked()
    {
        Assert.Null(QSeriesPortWatcher.EscalationSuppressionReason(0, null, null, Now));
        Assert.NotNull(QSeriesPortWatcher.EscalationSuppressionReason(3, null, null, Now));
        Assert.NotNull(QSeriesPortWatcher.EscalationSuppressionReason(0, Now - TimeSpan.FromSeconds(30), null, Now));
        Assert.NotNull(QSeriesPortWatcher.EscalationSuppressionReason(1, null, Now - TimeSpan.FromMinutes(5), Now));
    }
}
