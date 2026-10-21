using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;
using Nexus.Service.Tests.StreamDeck;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

/// <summary>Report orders are the MB Manager sequences in the customer's captures.</summary>
public class GlacialLcdScreenTests
{
    private const string StateReply = "EC 5C 00 00 00 32 00 00 00 32 14 00 00 00 00 32";

    private static byte[] Report(string hex)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", string.Empty));
        var report = new byte[GlacialLcdProtocol.ReportBytes];
        bytes.CopyTo(report, 0);
        return report;
    }

    private sealed class Clock
    {
        public long Now = 1_000_000;
    }

    private sealed class Rig
    {
        public GlacialLcdSettings Settings { get; set; } = new();
        public Clock Clock { get; } = new();
        public FakeScreenHid Hid { get; } = new();
        public List<(int Brightness, bool DisplayOn, bool KeepOn)> Seeds { get; } = new();
        public List<string> Warnings { get; } = new();
        public int Reads;
        public GlacialLcdConfirmed Confirmed { get; } = new();
        public int DefaultModeChosen;
        public bool Streaming;
        public int Disturbed;
        public GlacialLcdScreen Screen { get; private set; }

        public Rig(byte[]? stateReply)
        {
            Hid.StateReply = stateReply;
            Screen = NewScreen();
        }

        /// <summary>A fresh session over the same panel, as after a reconnect: the confirmed state carries over.</summary>
        public GlacialLcdScreen NewScreen() => Screen = new GlacialLcdScreen(
                Hid,
                () => Settings.Copy(),
                (slot, index) =>
                {
                    Reads++;
                    return index switch
                    {
                        0 => ("CPU", "43.0\u2103"),
                        1 => ("CPU Package", "57.0\u2103"),
                        _ => ("GPU", "30.7\u2103"),
                    };
                },
                Confirmed,
                (b, d, k) => Seeds.Add((b, d, k)),
                Warnings.Add,
                () => DefaultModeChosen++,
                Landed.Add,
                () => Streaming,
                () => Disturbed++,
                () => Clock.Now,
                _ => { });

        public void SetBrightness(int value)
        {
            Settings.UserEdited = true;
            Settings.BrightnessEdited = true;
            Settings.Brightness = value;
        }

        public void SetDisplay(bool value)
        {
            Settings.UserEdited = true;
            Settings.DisplayEdited = true;
            Settings.DisplayOn = value;
        }

        public void SetSleep(bool value)
        {
            Settings.UserEdited = true;
            Settings.SleepEdited = true;
            Settings.StayOnWhileAsleep = value;
        }

        public List<string> Landed { get; } = new();

        /// <summary>The user has changed something and chosen the mode, which is when Nexus starts writing.</summary>
        public Rig Owned(bool modeChosen = true)
        {
            Settings.UserEdited = true;
            Settings.ModeChosen = modeChosen;
            return this;
        }

        public List<byte[]> Take()
        {
            var writes = Hid.Writes.ToList();
            Hid.Writes.Clear();
            return writes;
        }
    }

    private static Rig NewRig(bool answering = true) => new(answering ? Report(StateReply) : null);

    private static void AssertSequence(IEnumerable<byte[]> actual, params string[] expected) =>
        Assert.Equal(expected.Select(Report), actual);

    private const string Slot0 = "EC 53 00 43 50 55 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 34 33 2E 30 E2 84 83";
    private const string Slot1 = "EC 53 01 43 50 55 20 50 61 63 6B 61 67 65 00 00 00 00 00 00 00 35 37 2E 30 E2 84 83";
    // With three widgets the first two are written swapped: wire index 0 carries the second widget.
    private const string Wire0 = "EC 53 00 43 50 55 20 50 61 63 6B 61 67 65 00 00 00 00 00 00 00 35 37 2E 30 E2 84 83";
    private const string Wire1 = "EC 53 01 43 50 55 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 34 33 2E 30 E2 84 83";
    private const string Slot2 = "EC 53 02 47 50 55 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 33 30 2E 37 E2 84 83";

    [Fact]
    public void Open_sends_only_the_app_start_pair_and_seeds_from_the_first_reply()
    {
        var rig = new Rig(Report("EC 5C 00 00 00 28 00 00 00 28 14 00 00 00 00 28 14"));

        Assert.True(rig.Screen.Open());

        AssertSequence(rig.Take(), "EC 82", "EC DC");
        Assert.Equal((40, true, true), Assert.Single(rig.Seeds));
    }

    [Fact]
    public void A_panel_the_user_has_not_touched_is_never_written_to()
    {
        var rig = NewRig();
        rig.Screen.Open();
        rig.Take();

        rig.Settings.Brightness = 70;
        rig.Settings.Mode = GlacialLcdSettings.ModeMonitor;
        Assert.True(rig.Screen.Reconcile());
        rig.Clock.Now += 60_000;
        Assert.True(rig.Screen.Tick());

        Assert.Empty(rig.Take());
    }

    [Fact]
    public void Without_a_first_reply_the_seed_is_retried_later_and_the_gap_is_reported()
    {
        var rig = NewRig(answering: false);

        Assert.True(rig.Screen.Open());
        Assert.Empty(rig.Seeds);

        rig.Clock.Now += GlacialLcdScreen.NoReplyRetryMs;
        rig.Screen.Tick();
        Assert.Single(rig.Warnings);

        rig.Hid.StateReply = Report(StateReply);
        rig.Clock.Now += GlacialLcdScreen.NoReplyRetryMs;
        rig.Screen.Tick();
        Assert.Single(rig.Seeds);
        Assert.All(rig.Hid.ReadTimeouts, t => Assert.True(t >= 0));
    }

    [Fact]
    public void A_saved_monitor_the_user_chose_is_resumed_in_the_captured_order_on_connect()
    {
        var rig = NewRig().Owned();
        rig.Settings.Mode = GlacialLcdSettings.ModeMonitor;

        Assert.True(rig.Screen.Open());

        AssertSequence(rig.Take(), "EC 82", "EC DC", "EC DC", Wire0, Wire1, Slot2, "EC 52 00 02", "EC 51 21", Wire0, Wire1, Slot2);
    }

    [Fact]
    public void Other_saved_modes_are_left_to_the_panel_on_connect()
    {
        var rig = NewRig().Owned();
        rig.Settings.Mode = GlacialLcdSettings.ModeStatic;

        rig.Screen.Open();

        AssertSequence(rig.Take(), "EC 82", "EC DC");
    }

    [Fact]
    public void With_fewer_than_three_widgets_the_slots_go_out_in_their_own_order()
    {
        var rig = NewRig().Owned();
        rig.Settings.Mode = GlacialLcdSettings.ModeMonitor;
        rig.Settings.SlotCount = 2;

        rig.Screen.Open();

        AssertSequence(rig.Take().Skip(3), Slot0, Slot1, "EC 52 00 01", "EC 51 21", Slot0, Slot1);
    }

    [Theory]
    [InlineData(1, "EC 52")]
    [InlineData(2, "EC 52 00 01")]
    public void The_widget_count_is_announced_before_the_monitor_mode(int count, string announce)
    {
        var rig = NewRig().Owned();
        rig.Settings.Mode = GlacialLcdSettings.ModeMonitor;
        rig.Settings.SlotCount = count;

        rig.Screen.Open();

        var writes = rig.Take().Select(w => Convert.ToHexString(w)).ToList();
        int announceAt = writes.IndexOf(Convert.ToHexString(Report(announce)));
        Assert.True(announceAt > 0);
        Assert.Equal(Convert.ToHexString(Report("EC 51 21")), writes[announceAt + 1]);
        Assert.Equal(count, writes.Take(announceAt).Count(w => w.StartsWith("EC53", StringComparison.Ordinal)));
    }

    [Fact]
    public void Monitor_widgets_are_resent_on_the_vendor_cadence_with_fresh_readings()
    {
        var rig = NewRig().Owned();
        rig.Settings.Mode = GlacialLcdSettings.ModeMonitor;
        rig.Screen.Open();
        rig.Take();
        int readsAfterOpen = rig.Reads;

        rig.Clock.Now += GlacialLcdScreen.SlotResendMs - 1;
        Assert.True(rig.Screen.Tick());
        Assert.Empty(rig.Take());

        rig.Clock.Now += 1;
        Assert.True(rig.Screen.Tick());
        var writes = rig.Take();
        Assert.Equal(3, writes.Count);
        Assert.All(writes, w => Assert.Equal(0x53, w[1]));
        Assert.Equal(readsAfterOpen + 3, rig.Reads);
    }

    [Fact]
    public void Nothing_is_resent_outside_monitor_mode_or_while_the_display_is_off()
    {
        var rig = NewRig().Owned();
        rig.Screen.Open();
        rig.Take();
        rig.Clock.Now += 60_000;
        Assert.True(rig.Screen.Tick());
        Assert.Empty(rig.Take());

        rig.Settings.Mode = GlacialLcdSettings.ModeMonitor;
        rig.SetDisplay(false);
        rig.Screen.Reconcile();
        rig.Take();
        rig.Clock.Now += 60_000;
        Assert.True(rig.Screen.Tick());
        Assert.Empty(rig.Take());
    }

    [Fact]
    public void A_brightness_change_is_the_captured_settings_write_and_two_mode_repeats()
    {
        var rig = NewRig().Owned();
        rig.Screen.Open();
        rig.Take();
        rig.SetBrightness(70);

        Assert.True(rig.Screen.Reconcile());

        AssertSequence(
            rig.Take(), "EC DC", "EC 5C 01 00 00 46 00 00 00 46 14 00 00 00 00 46", "EC DC", "EC 51 14 00 01", "EC DC", "EC 51 14 00 01");
    }

    [Fact]
    public void Before_a_mode_is_chosen_a_brightness_change_does_not_reselect_one()
    {
        var rig = NewRig().Owned(modeChosen: false);
        rig.Screen.Open();
        rig.Take();
        rig.SetBrightness(70);

        rig.Screen.Reconcile();

        AssertSequence(rig.Take(), "EC DC", "EC 5C 01 00 00 46 00 00 00 46 14 00 00 00 00 46");
    }

    [Fact]
    public void The_sleep_toggle_sets_the_sleep_field_and_repeats_the_mode()
    {
        var rig = NewRig().Owned();
        rig.Screen.Open();
        rig.Take();
        rig.SetSleep(true);

        rig.Screen.Reconcile();

        var writes = rig.Take();
        Assert.Equal(Report("EC 5C 01 00 00 32 00 00 00 32 14 00 00 00 00 32 14"), writes[1]);
        Assert.Equal(Report("EC 51 14 00 01"), writes[3]);
    }

    [Fact]
    public void Display_off_is_the_settings_write_then_a_bare_mode_off_and_on_restores_the_mode()
    {
        var rig = NewRig().Owned();
        rig.Screen.Open();
        rig.Take();

        rig.SetDisplay(false);
        rig.Screen.Reconcile();
        AssertSequence(rig.Take(), "EC DC", "EC 5C 01 00 00 32 00 00 00 32 00 00 00 00 00 32", "EC 51");

        rig.SetDisplay(true);
        rig.Screen.Reconcile();
        AssertSequence(rig.Take(), "EC DC", "EC 5C 01 00 00 32 00 00 00 32 14 00 00 00 00 32", "EC DC", "EC 51 14 00 01");
    }

    [Theory]
    [InlineData(GlacialLcdSettings.ModeStatic, 4, "EC 51 11 00 04")]
    [InlineData(GlacialLcdSettings.ModeStatic, 3, "EC 51 11 00 03")]
    [InlineData(GlacialLcdSettings.ModeAnimated, 0, "EC 51 14")]
    public void A_preset_pick_is_a_query_and_the_captured_mode_select(string mode, int asset, string expected)
    {
        var rig = NewRig().Owned(modeChosen: false);
        rig.Screen.Open();
        rig.Take();
        rig.Settings.ModeChosen = true;
        rig.Settings.Mode = mode;
        rig.Settings.StaticAsset = mode == GlacialLcdSettings.ModeStatic ? asset : 3;
        rig.Settings.AnimatedAsset = mode == GlacialLcdSettings.ModeAnimated ? asset : 1;

        rig.Screen.Reconcile();

        AssertSequence(rig.Take(), "EC DC", expected);
    }

    [Fact]
    public void A_mode_picked_while_the_display_is_off_is_held_until_it_comes_on()
    {
        var rig = new Rig(Report("EC 5C 00 00 00 32 00 00 00 32 00 00 00 00 00 32")).Owned();
        rig.Settings.DisplayOn = false;
        rig.Screen.Open();
        rig.Take();

        rig.Settings.Mode = GlacialLcdSettings.ModeStatic;
        rig.Settings.StaticAsset = 4;
        Assert.True(rig.Screen.Reconcile());
        Assert.Empty(rig.Take());

        rig.SetDisplay(true);
        rig.Screen.Reconcile();
        Assert.Contains(Report("EC 51 11 00 04"), rig.Take());
    }

    [Fact]
    public void A_brightness_and_a_monitor_switch_in_one_pass_send_the_widgets_before_the_monitor_mode()
    {
        var rig = NewRig().Owned();
        rig.Screen.Open();
        rig.Take();
        rig.SetBrightness(70);
        rig.Settings.Mode = GlacialLcdSettings.ModeMonitor;

        Assert.True(rig.Screen.Reconcile());

        AssertSequence(
            rig.Take(), "EC DC", "EC 5C 01 00 00 46 00 00 00 46 14 00 00 00 00 46", "EC DC", Wire0, Wire1, Slot2, "EC 52 00 02", "EC 51 21", Wire0, Wire1, Slot2);
    }

    [Fact]
    public void Without_a_fresh_state_reply_no_settings_are_written_and_the_write_is_retried_later()
    {
        var rig = NewRig(answering: false).Owned();
        rig.Screen.Open();
        rig.Take();
        rig.SetBrightness(30);

        Assert.True(rig.Screen.Reconcile());
        AssertSequence(rig.Take(), "EC DC");
        Assert.Single(rig.Warnings);

        // Inside the retry pause the panel is left alone.
        Assert.True(rig.Screen.Reconcile());
        Assert.Empty(rig.Take());

        rig.Hid.StateReply = Report(StateReply);
        rig.Clock.Now += GlacialLcdScreen.NoReplyRetryMs;
        Assert.True(rig.Screen.Reconcile());
        Assert.Contains(Report("EC 5C 01 00 00 1E 00 00 00 1E 14 00 00 00 00 1E"), rig.Take());
        Assert.All(rig.Hid.ReadTimeouts, t => Assert.True(t >= 0));
    }

    [Fact]
    public void Stale_echoes_and_old_state_replies_are_drained_before_the_query()
    {
        var rig = new Rig(Report("EC 5C 00 00 00 32 00 00 00 32 14 AA BB 00 00 32")).Owned();
        rig.Screen.Open();
        rig.Take();
        // The panel echoed every earlier report, and an old state reply is still queued.
        rig.Hid.Input.Enqueue(Report("EC 51"));
        rig.Hid.Input.Enqueue(Report("EC 5C 00 00 00 0A 00 00 00 0A 11 00 00 00 00 0A"));
        rig.Hid.Input.Enqueue(Report("EC 53"));
        rig.SetBrightness(70);

        rig.Screen.Reconcile();

        var settings = rig.Take().First(w => w[1] == 0x5C);
        Assert.Equal(70, settings[5]);
        Assert.Equal(0xAA, settings[11]);
        Assert.Equal(0xBB, settings[12]);
        Assert.Equal(0x14, settings[10]);
    }

    [Fact]
    public void Turning_the_display_on_before_any_mode_was_chosen_selects_the_first_run_animation()
    {
        var rig = new Rig(Report("EC 5C 00 00 00 32 00 00 00 32 00 00 00 00 00 32")).Owned(modeChosen: false);
        rig.Settings.DisplayOn = false;
        rig.Screen.Open();
        rig.Take();

        rig.SetDisplay(true);
        Assert.True(rig.Screen.Reconcile());

        AssertSequence(rig.Take(), "EC DC", "EC 5C 01 00 00 32 00 00 00 32 14 00 00 00 00 32", "EC DC", "EC 51 14 00 01");
        Assert.Equal(1, rig.DefaultModeChosen);
    }

    [Fact]
    public void An_edit_that_never_landed_is_sent_after_a_reconnect_and_nothing_else_is()
    {
        var rig = NewRig(answering: false).Owned();
        rig.Settings.Mode = GlacialLcdSettings.ModeStatic;
        rig.Screen.Open();
        rig.SetBrightness(70);
        rig.Screen.Reconcile();
        rig.Take();

        rig.Hid.StateReply = Report(StateReply);
        var second = rig.NewScreen();
        second.Open();
        rig.Take();
        rig.Clock.Now += GlacialLcdScreen.NoReplyRetryMs;
        Assert.True(second.Reconcile());

        // Brightness only: the saved mode is not reselected and the monitor is not restarted.
        AssertSequence(rig.Take(), "EC DC", "EC 5C 01 00 00 46 00 00 00 46 14 00 00 00 00 46", "EC DC", "EC 51 11 00 03", "EC DC", "EC 51 11 00 03");
    }

    [Fact]
    public void The_settings_write_changes_only_the_edited_field_and_keeps_the_rest_of_the_reply()
    {
        var rig = new Rig(Report("EC 5C 00 00 00 28 00 00 00 29 14 00 00 00 00 2A 14")).Owned(modeChosen: false);
        rig.Settings.Brightness = 40;
        rig.Settings.StayOnWhileAsleep = true;
        rig.Screen.Open();
        rig.Take();

        rig.SetDisplay(false);
        rig.Screen.Reconcile();

        var settings = rig.Take().First(w => w[1] == 0x5C);
        Assert.Equal(Report("EC 5C 01 00 00 28 00 00 00 29 00 00 00 00 00 2A 14"), settings);
    }

    [Fact]
    public void A_field_the_user_never_set_keeps_the_panels_value_even_when_the_first_reply_comes_late()
    {
        // The panel holds brightness 30; the saved default of 50 was never seeded because the first reply was lost.
        var rig = NewRig(answering: false);
        rig.Screen.Open();
        rig.SetDisplay(false);
        rig.Hid.StateReply = Report("EC 5C 00 00 00 1E 00 00 00 1E 14 00 00 00 00 1E");

        Assert.True(rig.Screen.Reconcile());

        var settings = rig.Take().First(w => w[1] == 0x5C);
        Assert.Equal(Report("EC 5C 01 00 00 1E 00 00 00 1E 00 00 00 00 00 1E"), settings);
        Assert.Contains((30, true, false), rig.Seeds);
    }

    [Fact]
    public void A_mode_change_that_was_pending_at_restart_is_sent_on_connect()
    {
        var rig = NewRig().Owned();
        rig.Settings.Mode = GlacialLcdSettings.ModeStatic;
        rig.Settings.StaticAsset = 4;
        rig.Settings.ModePending = true;
        rig.Screen.Open();
        rig.Take();

        Assert.True(rig.Screen.Reconcile());

        AssertSequence(rig.Take(), "EC DC", "EC 51 11 00 04");
        Assert.Equal(new[] { "static:4" }, rig.Landed);
    }

    [Fact]
    public void A_pending_mode_stays_pending_while_the_display_is_off_and_lands_when_it_comes_on()
    {
        var rig = new Rig(Report("EC 5C 00 00 00 32 00 00 00 32 00 00 00 00 00 32")).Owned();
        rig.Settings.DisplayOn = false;
        rig.Settings.Mode = GlacialLcdSettings.ModeStatic;
        rig.Settings.StaticAsset = 4;
        rig.Settings.ModePending = true;
        rig.Screen.Open();
        rig.Take();

        Assert.True(rig.Screen.Reconcile());
        Assert.Empty(rig.Take());
        Assert.Empty(rig.Landed);

        rig.SetDisplay(true);
        rig.Screen.Reconcile();
        Assert.Contains(Report("EC 51 11 00 04"), rig.Take());
        Assert.Equal(new[] { "static:4" }, rig.Landed);
    }

    [Fact]
    public void A_preset_pick_is_not_delayed_by_a_seed_that_is_waiting_out_a_silent_panel()
    {
        var rig = NewRig(answering: false).Owned(modeChosen: false);
        rig.Screen.Open();
        rig.Screen.Tick();
        Assert.Single(rig.Warnings);
        rig.Take();

        rig.Settings.ModeChosen = true;
        rig.Settings.Mode = GlacialLcdSettings.ModeStatic;
        rig.Settings.StaticAsset = 4;
        Assert.True(rig.Screen.Reconcile());

        AssertSequence(rig.Take(), "EC DC", "EC 51 11 00 04");
    }

    [Fact]
    public void A_settings_edit_asks_the_panel_itself_instead_of_waiting_for_the_seed_retry()
    {
        var rig = NewRig(answering: false).Owned();
        rig.Screen.Open();
        rig.Screen.Tick();
        rig.Take();
        rig.Hid.StateReply = Report(StateReply);

        rig.SetBrightness(30);
        Assert.True(rig.Screen.Reconcile());

        Assert.Contains(Report("EC 5C 01 00 00 1E 00 00 00 1E 14 00 00 00 00 1E"), rig.Take());
    }

    [Fact]
    public void While_a_panel_streams_the_mode_is_left_alone_and_a_chosen_mode_returns_afterwards()
    {
        var rig = NewRig().Owned();
        rig.Settings.Mode = GlacialLcdSettings.ModeStatic;
        rig.Screen.Open();
        rig.Take();
        rig.Streaming = true;

        rig.Settings.Mode = GlacialLcdSettings.ModeAnimated;
        rig.SetBrightness(70);
        Assert.True(rig.Screen.Reconcile());
        // Brightness still goes through, without the mode repeats; the mode change waits.
        AssertSequence(rig.Take(), "EC DC", "EC 5C 01 00 00 46 00 00 00 46 14 00 00 00 00 46");
        rig.Clock.Now += 60_000;
        Assert.True(rig.Screen.Tick());
        Assert.Empty(rig.Take());

        rig.Streaming = false;
        Assert.True(rig.Screen.Reconcile());
        AssertSequence(rig.Take(), "EC DC", "EC 51 14 00 01");
    }

    [Fact]
    public void When_streaming_stops_no_mode_is_selected_if_the_user_never_chose_one()
    {
        var rig = NewRig().Owned(modeChosen: false);
        rig.Screen.Open();
        rig.Take();
        rig.Streaming = true;
        rig.Screen.Reconcile();
        rig.Streaming = false;

        Assert.True(rig.Screen.Reconcile());

        Assert.Empty(rig.Take());
    }

    [Fact]
    public void An_unknown_display_state_does_not_hold_back_a_preset_pick()
    {
        var rig = NewRig(answering: false).Owned(modeChosen: false);
        rig.Screen.Open();
        rig.Take();
        rig.SetDisplay(true);
        rig.Settings.ModeChosen = true;
        rig.Settings.Mode = GlacialLcdSettings.ModeStatic;
        rig.Settings.StaticAsset = 4;

        Assert.True(rig.Screen.Reconcile());

        Assert.Contains(Report("EC 51 11 00 04"), rig.Take());
    }

    [Fact]
    public void A_link_failure_during_the_seed_retry_fails_the_session()
    {
        var rig = NewRig(answering: false);
        rig.Screen.Open();
        rig.Hid.FailWrites = true;

        Assert.False(rig.Screen.Tick());
    }

    private static GlacialLcdSettings Chosen()
    {
        var g = new GlacialLcdSettings { UserEdited = true, ModeChosen = true };
        return g;
    }

    [Fact]
    public void A_content_change_that_leaves_the_mode_key_alone_does_not_set_the_pending_flag()
    {
        var g = Chosen();
        Nexus.Service.Routes.DevicesRoutes.ApplyGlacialRequest(g, new Nexus.Service.Routes.GlacialLcdSettingsRequest { AnimatedAsset = g.AnimatedAsset });
        Assert.False(g.ModePending);

        Nexus.Service.Routes.DevicesRoutes.ApplyGlacialRequest(g, new Nexus.Service.Routes.GlacialLcdSettingsRequest { AnimatedAsset = 0 });
        Assert.True(g.ModePending);
    }

    [Fact]
    public void Slots_alone_never_mark_a_mode_as_chosen()
    {
        var g = new GlacialLcdSettings();

        Nexus.Service.Routes.DevicesRoutes.ApplyGlacialRequest(
            g,
            new Nexus.Service.Routes.GlacialLcdSettingsRequest
            {
                SlotCount = 2,
                Slots = new[] { new Nexus.Service.Routes.GlacialLcdSlotDto { Category = "gpu" } },
            });

        Assert.False(g.ModeChosen);
        Assert.False(g.ModePending);
        Assert.Equal("gpu", g.Slots[0].Category);
    }

    [Fact]
    public void Display_on_with_no_mode_chosen_sets_the_first_run_animation_in_the_route_itself()
    {
        var g = new GlacialLcdSettings();

        Nexus.Service.Routes.DevicesRoutes.ApplyGlacialRequest(g, new Nexus.Service.Routes.GlacialLcdSettingsRequest { DisplayOn = true });

        Assert.True(g.ModeChosen);
        Assert.True(g.ModePending);
        Assert.Equal((GlacialLcdSettings.ModeAnimated, GlacialLcdSettings.FirstRunAnimation), (g.Mode, g.AnimatedAsset));
        Assert.True(g.DisplayEdited);
    }

    [Fact]
    public void The_nexus_panel_mode_is_never_selected_natively()
    {
        var rig = NewRig().Owned(modeChosen: false);
        rig.Screen.Open();
        rig.Take();

        rig.Settings.ModeChosen = true;
        rig.Settings.Mode = GlacialLcdSettings.ModePanel;
        Assert.True(rig.Screen.Reconcile());

        Assert.Empty(rig.Take());
    }

    [Fact]
    public void A_display_edit_while_streaming_marks_the_panel_as_possibly_off_the_stored_picture()
    {
        var rig = NewRig().Owned();
        rig.Settings.Mode = GlacialLcdSettings.ModePanel;
        rig.Screen.Open();
        rig.Take();
        rig.Streaming = true;

        rig.SetDisplay(false);
        rig.Screen.Reconcile();
        rig.SetDisplay(true);
        rig.Screen.Reconcile();

        Assert.Equal(2, rig.Disturbed);
        Assert.DoesNotContain(rig.Take(), w => w[1] == 0x51 && w[2] == 0x14);
    }

    [Fact]
    public void Pending_flag_edits_are_reported_per_kind()
    {
        var rig = NewRig().Owned();
        rig.Screen.Open();
        rig.Take();
        Assert.Equal((false, false, false), rig.Screen.PendingFlagEdits());

        rig.SetBrightness(0);
        rig.SetSleep(true);

        Assert.Equal((true, false, true), rig.Screen.PendingFlagEdits());
    }

    [Fact]
    public void The_stream_owns_the_display_only_when_the_user_picked_the_nexus_panel_mode()
    {
        var g = new GlacialLcdSettings();
        Assert.False(GlacialLcdSettings.StreamChosen(g));

        Nexus.Service.Routes.DevicesRoutes.ApplyGlacialRequest(g, new Nexus.Service.Routes.GlacialLcdSettingsRequest { Mode = GlacialLcdSettings.ModePanel });
        Assert.True(GlacialLcdSettings.StreamChosen(g));
        // The upload owns this mode, so nothing is left pending for the native side.
        Assert.False(g.ModePending);

        Nexus.Service.Routes.DevicesRoutes.ApplyGlacialRequest(g, new Nexus.Service.Routes.GlacialLcdSettingsRequest { Mode = GlacialLcdSettings.ModeStatic });
        Assert.False(GlacialLcdSettings.StreamChosen(g));
    }

    [Fact]
    public void A_refused_report_fails_the_pass_so_the_worker_reopens()
    {
        var rig = NewRig().Owned();
        rig.Screen.Open();
        rig.Hid.FailWrites = true;
        rig.SetBrightness(10);

        Assert.False(rig.Screen.Reconcile());
    }

    [Fact]
    public void Saved_state_round_trips_through_the_settings_file()
    {
        var settings = new NexusSettings();
        var g = settings.Devices.GlacialLcd;
        g.UserEdited = true;
        g.ModeChosen = true;
        g.DisplayOn = false;
        g.Mode = GlacialLcdSettings.ModeMonitor;
        g.StaticAsset = 4;
        g.AnimatedAsset = 0;
        g.SlotCount = 2;
        g.Slots[1] = new GlacialLcdSlot { Category = "motherboard", Sensor = "mobo/voltage/vddio", Label = "VDDIO" };
        g.Brightness = 30;
        g.StayOnWhileAsleep = true;

        var json = System.Text.Json.JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings)!.Devices.GlacialLcd;

        Assert.True(back.UserEdited);
        Assert.True(back.ModeChosen);
        Assert.False(back.DisplayOn);
        Assert.Equal(GlacialLcdSettings.ModeMonitor, back.Mode);
        Assert.Equal((4, 0, 2, 30, true), (back.StaticAsset, back.AnimatedAsset, back.SlotCount, back.Brightness, back.StayOnWhileAsleep));
        Assert.Equal("mobo/voltage/vddio", back.Slots[1].Sensor);
        Assert.Equal("VDDIO", back.Slots[1].Label);
        Assert.Equal(3, back.Slots.Count);
    }

    [Fact]
    public void Copy_replaces_persisted_nulls_and_fills_the_widget_list()
    {
        var g = new GlacialLcdSettings { Mode = null!, Slots = new List<GlacialLcdSlot> { null!, new() { Category = null!, Sensor = null!, Label = null! } } };

        var copy = g.Copy();

        Assert.Equal(GlacialLcdSettings.ModeAnimated, copy.Mode);
        Assert.Equal(3, copy.Slots.Count);
        Assert.All(copy.Slots, slot => Assert.True(slot.Category is not null && slot.Sensor is not null && slot.Label is not null));
        Assert.Equal("CPU", copy.Slots[0].Label);
    }

    [Fact]
    public void Defaults_are_the_vendor_first_run_widgets()
    {
        var g = new GlacialLcdSettings();
        Assert.Equal(new[] { "CPU", "CPU Package", "GPU" }, g.Slots.Select(s => s.Label));
        Assert.Equal(3, g.SlotCount);
        Assert.Equal(50, g.Brightness);
    }

    [Fact]
    public void Values_use_the_vendor_formats()
    {
        Assert.Equal("43.0℃", GlacialLcdScreen.FormatValue("Temperature", 43f));
        Assert.Equal("1.447↊", GlacialLcdScreen.FormatValue("Voltage", 1.4473f));
        Assert.Equal("--", GlacialLcdScreen.FormatValue("Temperature", float.NaN));
        Assert.Equal("--", GlacialLcdScreen.FormatValue("Text", 40f));
    }

    [Theory]
    // Glyph forms the vendor app sent in the captures.
    [InlineData("Fan", 630f, "630↌")]
    [InlineData("Clock", 3600f, "3.600㎓")]
    // ASCII forms, not seen in any capture.
    [InlineData("Load", 40.04f, "40.0%")]
    [InlineData("Control", 55f, "55.0%")]
    [InlineData("Level", 80f, "80.0%")]
    [InlineData("Power", 125.46f, "125.5W")]
    [InlineData("Current", 1.5f, "1.500A")]
    [InlineData("Frequency", 60f, "60Hz")]
    [InlineData("Throughput", 1536f, "1.5KB/s")]
    [InlineData("Rate", 5242880f, "5.00MB/s")]
    [InlineData("Data", 12.345f, "12.35GB")]
    [InlineData("SmallData", 8192f, "8192MB")]
    [InlineData("Framerate", 143.6f, "144FPS")]
    [InlineData("TimeSpan", 5400f, "1.5h")]
    [InlineData("Energy", 500f, "500mWh")]
    public void Other_types_have_a_short_format(string type, float value, string expected)
    {
        Assert.Equal(expected, GlacialLcdScreen.FormatValue(type, value));
    }

    [Fact]
    public void The_longest_values_still_fit_the_value_field()
    {
        int room = GlacialLcdProtocol.ReportBytes - GlacialLcdProtocol.SlotValueOffset;
        foreach (var type in new[] { "Temperature", "Voltage", "Fan", "Clock", "Load", "Power", "Current", "Data", "Throughput", "TimeSpan" })
        {
            var text = GlacialLcdScreen.FormatValue(type, 999999f);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= room, text);
        }
        // Past the room, the cut falls on a character boundary.
        var report = GlacialLcdProtocol.EncodeSlot(0, "x", new string('㎓', 20));
        // 14 whole characters fit in 44 bytes; the two bytes left over stay zero rather than holding half a character.
        Assert.All(report.Skip(GlacialLcdProtocol.SlotValueOffset + 42), b => Assert.Equal(0, b));
        Assert.Equal(0xE3, report[GlacialLcdProtocol.SlotValueOffset + 41 - 2]);
    }

    [Fact]
    public void A_quick_summary_sensor_resolves_end_to_end()
    {
        var sensors = new FakeSensorProvider
        {
            CpuSensors = new[] { Sensor("cpu/load", "CPU Total", "Load", 12.34f) },
        };
        var slot = new GlacialLcdSlot { Category = "quick", Sensor = "summary/cpu-usage", Label = "Usage" };

        Assert.Equal(("Usage", "12.3%"), GlacialLcdSensors.Read(sensors, slot, 0));
    }

    [Fact]
    public void Network_and_fps_widgets_resolve_with_the_gathered_sources()
    {
        var nic = new HardwareComponent { Id = "nic0", Name = "Ethernet" };
        nic.Sensors.Add(Sensor("nic0/down", "Download Speed", "Throughput", 2048f));
        var sensors = new FakeSensorProvider { Extras = new SensorExtras { Nics = { nic } } };
        var slot = new GlacialLcdSlot { Category = "network", Sensor = "network-in", Label = "Net" };

        var sources = GlacialLcdSensors.Gather(sensors, "network", null, null);
        Assert.Equal(("Net", "2.0KB/s"), GlacialLcdSensors.Read(sensors, slot, 0, sources));

        var byName = new GlacialLcdSlot { Category = "network", Sensor = "Network Total", Label = "Net" };
        Assert.Equal(("Net", "2.0KB/s"), GlacialLcdSensors.Read(sensors, byName, 0, sources));

        var fps = new SensorSnapshotSources(FpsSensors: new[] { Sensor("fps/current", "FPS", "Framerate", 90f) });
        var fpsSlot = new GlacialLcdSlot { Category = "fps", Sensor = "FPS", Label = "FPS" };
        Assert.Equal(("FPS", "90FPS"), GlacialLcdSensors.Read(sensors, fpsSlot, 0, fps));
    }

    private static HardwareSensor Sensor(string id, string name, string type, float value) =>
        new() { Id = id, Name = name, Type = type, Value = value };

    [Fact]
    public void Unpicked_widgets_default_to_cpu_temperature_package_and_gpu_temperature()
    {
        var sensors = new FakeSensorProvider
        {
            CpuSensors = new[]
            {
                Sensor("cpu/load", "CPU Total", "Load", 10f),
                Sensor("cpu/core", "Core Max", "Temperature", 43f),
                Sensor("cpu/pkg", "CPU Package", "Temperature", 57f),
            },
        };
        var defaults = GlacialLcdSettings.DefaultSlots();

        Assert.Equal(("CPU", "43.0℃"), GlacialLcdSensors.Read(sensors, defaults[0], 0));
        Assert.Equal(("CPU Package", "57.0℃"), GlacialLcdSensors.Read(sensors, defaults[1], 1));
        Assert.Equal(("GPU", GlacialLcdScreen.NoReading), GlacialLcdSensors.Read(sensors, defaults[2], 2));
    }

    [Fact]
    public void A_picked_sensor_uses_its_own_name_unless_a_label_is_set()
    {
        var sensors = new FakeSensorProvider
        {
            MotherboardSensors = new[] { Sensor("mobo/vddio", "CPU VDDIO / MC", "Voltage", 1.447f) },
        };
        var slot = new GlacialLcdSlot { Category = "motherboard", Sensor = "mobo/vddio" };

        Assert.Equal(("CPU VDDIO / MC", "1.447↊"), GlacialLcdSensors.Read(sensors, slot, 2));
        slot.Label = "VDDIO";
        Assert.Equal("VDDIO", GlacialLcdSensors.Read(sensors, slot, 2).Label);
    }

    private sealed class FakeScreenHid : IHidDevice
    {
        private readonly object _lock = new();
        private readonly Queue<byte[]> _input = new();

        public Queue<byte[]> Input => _input;
        private readonly List<byte[]> _writes = new();

        public List<byte[]> Writes => _writes;
        public List<int> ReadTimeouts { get; } = new();
        public byte[]? StateReply { get; set; }
        public bool FailWrites { get; set; }

        public int VendorId => 0x0B05;
        public int ProductId => GlacialLcdProtocol.ProductId;
        public string Path => "/dev/fake-glacial";
        public string? Serial => "FAKE";
        public int UsagePage => 0xFF72;
        public int Usage => 1;

        public bool Write(ReadOnlySpan<byte> report)
        {
            if (FailWrites)
            {
                return false;
            }
            lock (_lock)
            {
                _writes.Add(report.ToArray());
                if (report[1] == 0x5C && StateReply is not null)
                {
                    // The panel holds what it was just given and reports it back.
                    StateReply = report.ToArray();
                    StateReply[2] = 0;
                }
                if (report[1] == 0xDC)
                {
                    if (StateReply is { } reply)
                    {
                        _input.Enqueue(reply);
                    }
                }
                else
                {
                    // The panel echoes every other report's opcode.
                    var echo = new byte[GlacialLcdProtocol.ReportBytes];
                    echo[0] = report[0];
                    echo[1] = report[1];
                    _input.Enqueue(echo);
                }
            }
            return true;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            lock (_lock)
            {
                ReadTimeouts.Add(timeoutMs);
                if (_input.Count == 0)
                {
                    return 0;
                }
                var reply = _input.Dequeue();
                reply.CopyTo(buffer);
                return reply.Length;
            }
        }

        public void Dispose() { }
    }
}
