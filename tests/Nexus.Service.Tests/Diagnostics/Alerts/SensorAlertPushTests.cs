using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Nexus.Service.Cloud;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.Alerts;
using Nexus.Service.Models.Sentry;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Nexus.Service.Tests.Cloud;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Alerts;

public partial class SensorAlertEngineTests
{
    private sealed class PushRig
    {
        public Rig Rig { get; } = new();
        public FakeCloudApiClient Cloud { get; } = new();
        public PanelPhonePairingService Pairing { get; }
        public SensorAlertPushSender Sender { get; }
        public List<(string Path, AlertPushRequest Request)> Posts { get; } = new();
        public List<Task> Pending { get; } = new();
        public string Status { get; set; } = "sent";

        public PushRig()
        {
            Pairing = new PanelPhonePairingService(Rig.Store, new MultiplexHub());
            Cloud.OnPostRaw = (path, body, token) =>
            {
                Assert.Null(token);
                var request = JsonSerializer.Deserialize(body, AppJsonContext.Default.AlertPushRequest)!;
                lock (Posts)
                {
                    Posts.Add((path, request));
                }
                var response = new SentryPushResponse { Results = request.Targets.Select(t => new SentryPushResult { Token = t.Token, Status = Status }).ToList() };
                return CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = JsonSerializer.Serialize(response, AppJsonContext.Default.SentryPushResponse) });
            };
            Sender = new SensorAlertPushSender(Rig.Store, Pairing, Cloud, Rig.Time);
            Rig.Engine.MobileSink = (notice, key) => Pending.Add(Sender.Raise(key, notice));
            AddPhone("p1", "tok-1");
        }

        public void AddPhone(string id, string token) => Rig.Store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.PanelPhoneSessions ??= new List<PanelPhoneSessionToken>();
            s.Auth.PanelPhoneSessions.Add(new PanelPhoneSessionToken
            {
                Id = id,
                PushTarget = new PanelPhonePushTarget { Platform = "ios", Token = token, Environment = "sandbox", Title = "t", Body = "b" },
            });
        });

        public async Task<List<(string Path, AlertPushRequest Request)>> Settle()
        {
            await Task.WhenAll(Pending.ToArray());
            lock (Posts)
            {
                return Posts.ToList();
            }
        }
    }

    private static DiagnosticsAlertNotice PushNotice(bool urgent, string text = "CPU above 80") =>
        new("Sensor alert", text, "sensor-alert", Urgent: urgent);

    private static string Hash(string ruleId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ruleId))).ToLowerInvariant()[..32];

    [Fact]
    public async Task Mobile_raise_posts_one_alert_with_hashed_entry_title_body_and_targets()
    {
        var rig = new PushRig();
        var rule = TempRule(raise: 0, id: "r1");
        rule.Severity = "act";
        rule.NotifyMobile = true;
        rig.Rig.AddRule(rule);
        rig.Rig.SetTemp(90);

        rig.Rig.Seconds(1);

        var (path, request) = Assert.Single(await rig.Settle());
        Assert.Equal("/push/alert", path);
        Assert.Equal(Hash("r1"), request.Entry);
        Assert.Matches("^[A-Za-z0-9._:-]+$", request.Entry);
        Assert.True(request.Urgent);
        Assert.Equal("Sensor alert on " + rig.Pairing.MachineName, request.Title);
        Assert.Equal(Assert.Single(rig.Rig.Notices).Notice.Text, request.Body);
        var target = Assert.Single(request.Targets);
        Assert.Equal(("ios", "tok-1", "sandbox"), (target.Platform, target.Token, target.Environment));
    }

    [Fact]
    public async Task Notify_desktop_off_with_notify_mobile_on_still_pushes_and_desktop_stays_silent()
    {
        var rig = new PushRig();
        var rule = TempRule(raise: 0);
        rule.NotifyDesktop = false;
        rule.NotifyMobile = true;
        rig.Rig.AddRule(rule);
        rig.Rig.SetTemp(90);

        rig.Rig.Seconds(1);

        Assert.Single(await rig.Settle());
        Assert.Empty(rig.Rig.Notices);
    }

    [Fact]
    public async Task Notify_mobile_off_sends_nothing()
    {
        var rig = new PushRig();
        rig.Rig.AddRule(TempRule(raise: 0));
        rig.Rig.SetTemp(90);

        rig.Rig.Seconds(1);

        Assert.Empty(await rig.Settle());
        Assert.Single(rig.Rig.Notices);
    }

    [Fact]
    public async Task A_disabled_rule_never_pushes()
    {
        var rig = new PushRig();
        var rule = TempRule(raise: 0);
        rule.NotifyMobile = true;
        rule.Enabled = false;
        rig.Rig.AddRule(rule);
        rig.Rig.SetTemp(90);

        rig.Rig.Seconds(10);

        Assert.Empty(await rig.Settle());
    }

    [Fact]
    public async Task The_desktop_master_switch_does_not_gate_mobile()
    {
        var rig = new PushRig();
        rig.Rig.Store.Update(s => s.Diagnostics.Notifications.Enabled = false);
        var rule = TempRule(raise: 0);
        rule.NotifyMobile = true;
        rig.Rig.AddRule(rule);
        rig.Rig.SetTemp(90);

        rig.Rig.Seconds(1);

        Assert.Single(await rig.Settle());
    }

    [Fact]
    public async Task Urgent_repeats_after_five_minutes_but_not_before()
    {
        var rig = new PushRig();
        rig.Rig.Store.Update(s => s.Diagnostics.Notifications.CooldownMinutes = 60);

        await rig.Sender.Raise("alert:r1", PushNotice(urgent: true));
        rig.Rig.Time.Advance(TimeSpan.FromMinutes(4));
        await rig.Sender.Raise("alert:r1", PushNotice(urgent: true));
        Assert.Single(await rig.Settle());

        rig.Rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.Sender.Raise("alert:r1", PushNotice(urgent: true));
        Assert.Equal(2, (await rig.Settle()).Count);
    }

    [Fact]
    public async Task Non_urgent_follows_the_configured_cooldown_per_rule()
    {
        var rig = new PushRig();
        rig.Rig.Store.Update(s => s.Diagnostics.Notifications.CooldownMinutes = 15);

        await rig.Sender.Raise("alert:r1", PushNotice(urgent: false));
        await rig.Sender.Raise("alert:r2", PushNotice(urgent: false));
        rig.Rig.Time.Advance(TimeSpan.FromMinutes(14));
        await rig.Sender.Raise("alert:r1", PushNotice(urgent: false));
        Assert.Equal(2, (await rig.Settle()).Count);

        rig.Rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.Sender.Raise("alert:r1", PushNotice(urgent: false));
        Assert.Equal(3, (await rig.Settle()).Count);
    }

    [Fact]
    public async Task No_paired_phones_sends_no_request()
    {
        var rig = new PushRig();
        rig.Rig.Store.Update(s => s.Auth!.PanelPhoneSessions!.Clear());

        await rig.Sender.Raise("alert:r1", PushNotice(urgent: true));

        Assert.Empty(await rig.Settle());
        Assert.Equal(0, rig.Cloud.PostRawCalls);
    }

    [Fact]
    public async Task At_most_ten_targets_are_sent_and_invalid_tokens_are_pruned()
    {
        var rig = new PushRig();
        for (var i = 2; i <= 12; i++)
        {
            rig.AddPhone("p" + i, "tok-" + i);
        }
        rig.Status = "invalid";

        await rig.Sender.Raise("alert:r1", PushNotice(urgent: true));

        var (_, request) = Assert.Single(await rig.Settle());
        Assert.Equal(10, request.Targets.Count);
        Assert.Equal(2, rig.Pairing.GetPushTargets().Count);
    }

    [Fact]
    public async Task Long_text_is_truncated_without_splitting_a_surrogate_pair()
    {
        var rig = new PushRig();

        await rig.Sender.Raise("alert:r1", PushNotice(urgent: true, text: new string('a', 199) + "\U0001F600"));

        var (_, request) = Assert.Single(await rig.Settle());
        Assert.Equal(199, request.Body.Length);
    }

    [Fact]
    public async Task A_failing_post_never_throws_into_the_caller()
    {
        var rig = new PushRig();
        rig.Cloud.OnPostRaw = (_, _, _) => throw new InvalidOperationException("boom");

        await rig.Sender.Raise("alert:r1", PushNotice(urgent: true));
    }

    [Fact]
    public void Notify_mobile_defaults_off_round_trips_through_json_and_counts_as_a_config_change()
    {
        var rule = TempRule();
        Assert.False(rule.NotifyMobile);
        var other = rule.Clone();
        Assert.True(rule.SameConfig(other));
        other.NotifyMobile = true;
        Assert.False(rule.SameConfig(other));

        var json = JsonSerializer.Serialize(other, PersistenceJsonContext.Default.SensorAlertRule);
        Assert.True(JsonDocument.Parse(json).RootElement.GetProperty("notifyMobile").GetBoolean());
        Assert.True(JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.SensorAlertRule)!.NotifyMobile);
        Assert.False(JsonSerializer.Deserialize("{}", PersistenceJsonContext.Default.SensorAlertRule)!.NotifyMobile);
    }
}
