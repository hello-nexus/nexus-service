using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.DeepCool;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.DeepCool;

public class MystiqueSessionTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 16, 26, 59);

    [Fact]
    public void Open_on_untouched_settings_only_queries_and_sets_the_clock()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);

        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));

        Assert.Equal(new[] { (MystiqueProtocol.CmdInfo, MystiqueProtocol.SettingsWritePipe), (MystiqueProtocol.CmdClock, MystiqueProtocol.LiveWritePipe) },
            pipe.Sent());
        Assert.Equal("03FF021004E0058494069B0700080009", session.Info);
    }

    [Fact]
    public void Open_writes_each_edited_group_in_the_order_the_screens_own_software_does()
    {
        var pipe = new FakePipe();
        var settings = new DeepCoolMystiqueSettings
        {
            DeviceEdited = true, ModeEdited = true, ReadoutEdited = true, RecordingEdited = true, MediaEdited = true, SourceEdited = true,
            Mode = DeepCoolMystiqueSettings.ModeRecording,
        };

        Assert.True(new MystiqueSession(pipe, sleep: NoSleep).Open(settings, Now));

        Assert.Equal(
            new byte[]
            {
                MystiqueProtocol.CmdInfo, MystiqueProtocol.CmdSystem, MystiqueProtocol.CmdMode, MystiqueProtocol.CmdReadout,
                MystiqueProtocol.CmdMediaPlayback, MystiqueProtocol.CmdMediaSource,
                MystiqueProtocol.CmdRecording, MystiqueProtocol.CmdRecordingApply, MystiqueProtocol.CmdClock,
            },
            pipe.Commands());
        Assert.Equal(3, pipe.Last(MystiqueProtocol.CmdMode)[3]);
    }

    [Fact]
    public void Apply_sends_only_the_group_that_changed()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        var settings = new DeepCoolMystiqueSettings { DeviceEdited = true, ModeEdited = true };
        Assert.True(session.Open(settings, Now));
        pipe.Writes.Clear();

        Assert.True(session.Apply(settings.Copy()));
        Assert.Empty(pipe.Writes);

        settings.Brightness = 30;
        Assert.True(session.Apply(settings.Copy()));
        Assert.Equal(new[] { MystiqueProtocol.CmdSystem }, pipe.Commands());
        Assert.Equal(30, pipe.Last(MystiqueProtocol.CmdSystem)[7]);
    }

    [Fact]
    public void Upload_announces_streams_finishes_then_selects_the_kind_like_the_screens_own_software()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, monotonicMs: () => 0, sleep: NoSleep);
        var settings = new DeepCoolMystiqueSettings();
        Assert.True(session.Open(settings, Now));
        pipe.Writes.Clear();
        var jpegs = new List<byte[]>();
        for (int i = 0; i < MystiqueMedia.GifFrames; i++)
        {
            jpegs.Add(new byte[100 + i]);
        }
        var package = MystiqueMedia.BuildGif(jpegs, "acd9325521f3493c86bfc0b0aa85658d");

        Assert.True(session.Upload(package));

        var expected = new List<byte[]> { MystiqueProtocol.BuildUploadStart() };
        expected.AddRange(package.Frames.SelectMany(MystiqueMedia.Packets));
        expected.Add(MystiqueMedia.FinishPacket());
        expected.Add(MystiqueProtocol.BuildMediaSource(DeepCoolMystiqueSettings.SourceGif));
        Assert.All(pipe.Writes, w => Assert.Equal(MystiqueProtocol.SettingsWritePipe, w.Pipe));
        Assert.Equal(expected.ConvertAll(Convert.ToHexString), pipe.Writes.ConvertAll(w => Convert.ToHexString(w.Frame)));
        Assert.Equal(2000, pipe.ReadTimeouts[^1]);

        pipe.Writes.Clear();
        settings.SourceEdited = true;
        settings.MediaSource = DeepCoolMystiqueSettings.SourceGif;
        Assert.True(session.Apply(settings.Copy()));
        Assert.Empty(pipe.Writes);
    }

    [Fact]
    public void A_frame_streams_one_packet_per_write_header_first_and_reports_the_bytes_after_each()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();
        var package = MystiqueMedia.BuildPicture(new byte[40_010], "2101b6e6277841daa821e8d79a5c528b");
        var reported = new List<long>();

        Assert.True(session.Upload(package, progress: reported.Add));

        var stream = pipe.Writes.Skip(1).TakeWhile(w => w.Frame.Length != MystiqueMedia.FinishLength).ToList();
        var frame = package.Frames[0];
        Assert.Equal(frame.Length / 64 + 1, stream.Count);
        Assert.All(stream.SkipLast(1), w => Assert.Equal(64, w.Frame.Length));
        Assert.Equal(frame.Length % 64, stream[^1].Frame.Length);
        Assert.Equal(frame[..MystiqueMedia.HeaderLength], stream[0].Frame);
        Assert.Equal(frame, stream.SelectMany(w => w.Frame));
        Assert.Equal(stream.Count, reported.Count);
        Assert.Equal(frame.Length, reported[^1]);
    }

    [Fact]
    public void A_write_that_took_nothing_is_not_resent_once_the_stall_time_has_passed()
    {
        var pipe = new FakePipe();
        long now = 0;
        var session = new MystiqueSession(pipe, monotonicMs: () => now, sleep: NoSleep);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();
        var package = MystiqueMedia.BuildPicture(new byte[40_000], "2101b6e6277841daa821e8d79a5c528b");
        int failing = pipe.Attempts + 3;
        pipe.ZeroAttempts.Add(failing);
        pipe.BeforeWrite = attempt => { if (attempt == failing) { now += MystiqueSession.StreamStallMs; } };

        Assert.False(session.Upload(package));

        var body = pipe.Writes.Skip(1).SkipLast(1).SelectMany(w => w.Frame).ToArray();
        Assert.Equal(package.Frames[0][..128], body[..128]);
        Assert.All(body[128..], b => Assert.Equal(0, b));
        Assert.Equal(MystiqueMedia.FinishLength, pipe.Writes[^1].Frame.Length);
    }

    [Fact]
    public void A_picture_waits_the_recorded_gap_before_the_next_command_and_a_gif_does_not()
    {
        var pipe = new FakePipe();
        var slept = new List<int>();
        var session = new MystiqueSession(pipe, sleep: slept.Add);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));

        Assert.True(session.Upload(MystiqueMedia.BuildPicture(new byte[1000], "2101b6e6277841daa821e8d79a5c528b")));
        Assert.Equal(new[] { 410 }, slept);
        Assert.True(session.Upload(MystiqueMedia.BuildPicture(new byte[1000], "2101b6e6277841daa821e8d79a5c528b"), () => null));
        Assert.True(session.Upload(MystiqueMedia.BuildGif(Enumerable.Range(0, MystiqueMedia.GifFrames).Select(_ => new byte[100]).ToList(), "acd9325521f3493c86bfc0b0aa85658d")));

        Assert.Equal(new[] { 410 }, slept);
        var picture = MystiqueMedia.BuildPicture(new byte[1000], "2101b6e6277841daa821e8d79a5c528b");
        Assert.Equal(2, session.Upload(new[] { picture, picture }));
        Assert.Equal(new[] { 410, 410, 410 }, slept);
    }

    [Fact]
    public void Packages_stream_in_turn_with_one_source_command_after_the_last()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();
        var first = MystiqueMedia.BuildPicture(new byte[1000], "2101b6e6277841daa821e8d79a5c528b");
        var second = MystiqueMedia.BuildPicture(new byte[2000], "90b8cf628a5440908e6ad8ce51452ef8");
        var reported = new List<long>();

        Assert.Equal(2, session.Upload(new[] { first, second }, progress: reported.Add));

        var expected = new List<byte[]> { MystiqueProtocol.BuildUploadStart() };
        expected.AddRange(MystiqueMedia.Packets(first.Frames[0]));
        expected.Add(MystiqueMedia.FinishPacket());
        expected.Add(MystiqueProtocol.BuildUploadStart());
        expected.AddRange(MystiqueMedia.Packets(second.Frames[0]));
        expected.Add(MystiqueMedia.FinishPacket());
        expected.Add(MystiqueProtocol.BuildMediaSource(DeepCoolMystiqueSettings.SourceImages));
        Assert.Equal(expected.ConvertAll(Convert.ToHexString), pipe.Writes.ConvertAll(w => Convert.ToHexString(w.Frame)));
        Assert.Equal(first.Frames[0].Length + second.Frames[0].Length, reported[^1]);
    }

    [Fact]
    public void A_package_that_does_not_stream_stops_the_rest_and_the_source_command()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        var first = MystiqueMedia.BuildPicture(new byte[1000], "2101b6e6277841daa821e8d79a5c528b");
        var second = MystiqueMedia.BuildPicture(new byte[2000], "90b8cf628a5440908e6ad8ce51452ef8");
        pipe.Writes.Clear();
        pipe.Silent.Add(MystiqueProtocol.CmdUploadStart);

        Assert.Equal(0, session.Upload(new[] { first, second }));
        pipe.Silent.Clear();
        pipe.SilentAfter(MystiqueProtocol.CmdUploadStart, 1);
        pipe.Writes.Clear();

        Assert.Equal(1, session.Upload(new[] { first, second }));
        Assert.DoesNotContain(pipe.Writes, w => w.Frame.Length == MystiqueProtocol.FrameLength && w.Frame[2] == MystiqueProtocol.CmdMediaSource);
    }

    [Fact]
    public void The_source_command_goes_on_its_own_even_when_the_screen_already_has_it()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        var settings = new DeepCoolMystiqueSettings { SourceEdited = true, MediaSource = DeepCoolMystiqueSettings.SourceImages };
        Assert.True(session.Open(settings, Now));
        pipe.Writes.Clear();

        Assert.True(session.SendSource(DeepCoolMystiqueSettings.SourceImages));

        Assert.Equal(new[] { MystiqueProtocol.CmdMediaSource }, pipe.Commands());
        pipe.Writes.Clear();
        Assert.True(session.Apply(settings.Copy()));
        Assert.Empty(pipe.Writes);
    }

    [Fact]
    public void A_null_source_streams_without_the_source_command()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        var settings = new DeepCoolMystiqueSettings { SourceEdited = true, MediaSource = DeepCoolMystiqueSettings.SourceImages };
        Assert.True(session.Open(settings, Now));
        pipe.Writes.Clear();

        Assert.True(session.Upload(MystiqueMedia.BuildGif(Enumerable.Repeat(new byte[100], MystiqueMedia.GifFrames).ToList(), "acd9325521f3493c86bfc0b0aa85658d"), source: () => null));
        Assert.Equal(MystiqueMedia.FinishLength, pipe.Writes[^1].Frame.Length);
        Assert.DoesNotContain(pipe.Writes, w => w.Frame.Length == MystiqueProtocol.FrameLength && w.Frame[2] == MystiqueProtocol.CmdMediaSource);

        pipe.Writes.Clear();
        Assert.True(session.Apply(settings.Copy()));
        Assert.Empty(pipe.Writes);
    }

    [Fact]
    public void A_source_kept_unsent_counts_as_applied()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        var settings = new DeepCoolMystiqueSettings { SourceEdited = true, MediaSource = DeepCoolMystiqueSettings.SourceGif };
        Assert.True(session.Open(settings, Now));
        pipe.Writes.Clear();

        Assert.True(session.Upload(MystiqueMedia.BuildPicture(new byte[1000], "2101b6e6277841daa821e8d79a5c528b"),
            () => new MystiqueSourceAfter(DeepCoolMystiqueSettings.SourceImages, false)));

        Assert.DoesNotContain(pipe.Writes, w => w.Frame.Length == MystiqueProtocol.FrameLength && w.Frame[2] == MystiqueProtocol.CmdMediaSource);
        pipe.Writes.Clear();
        settings.MediaSource = DeepCoolMystiqueSettings.SourceImages;
        Assert.True(session.Apply(settings.Copy()));
        Assert.Empty(pipe.Writes);
    }

    [Fact]
    public void The_source_is_chosen_after_the_finish_packet_and_sent_as_chosen()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        var settings = new DeepCoolMystiqueSettings { SourceEdited = true, MediaSource = DeepCoolMystiqueSettings.SourceImages };
        Assert.True(session.Open(settings, Now));
        pipe.Writes.Clear();
        int writesWhenChosen = -1;

        Assert.True(session.Upload(MystiqueMedia.BuildGif(Enumerable.Repeat(new byte[100], MystiqueMedia.GifFrames).ToList(), "acd9325521f3493c86bfc0b0aa85658d"), source: () =>
        {
            writesWhenChosen = pipe.Writes.Count;
            return new MystiqueSourceAfter(DeepCoolMystiqueSettings.SourceImages, true);
        }));

        Assert.Equal(MystiqueMedia.FinishLength, pipe.Writes[writesWhenChosen - 1].Frame.Length);
        Assert.Equal(Convert.ToHexString(MystiqueProtocol.BuildMediaSource(DeepCoolMystiqueSettings.SourceImages)), Convert.ToHexString(pipe.Writes[^1].Frame));
        pipe.Writes.Clear();
        Assert.True(session.Apply(settings.Copy()));
        Assert.Empty(pipe.Writes);
    }

    [Fact]
    public void Nothing_streams_unless_the_screen_answers_the_announce()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();
        pipe.Silent.Add(MystiqueProtocol.CmdUploadStart);

        Assert.False(session.Upload(MystiqueMedia.BuildPicture(new byte[1000], "2101b6e6277841daa821e8d79a5c528b")));

        Assert.Equal(new[] { MystiqueProtocol.CmdUploadStart }, pipe.Commands());
    }

    [Fact]
    public void A_packet_known_to_have_sent_nothing_is_resent_as_is_and_the_stream_completes()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();
        var package = MystiqueMedia.BuildPicture(new byte[40_000], "2101b6e6277841daa821e8d79a5c528b");
        pipe.ZeroAttempts.Add(pipe.Attempts + 2);

        Assert.True(session.Upload(package));

        var streamed = pipe.Writes.Skip(1).TakeWhile(w => w.Frame.Length != MystiqueMedia.FinishLength).SelectMany(w => w.Frame);
        Assert.Equal(package.Frames[0], streamed);
    }

    [Fact]
    public void An_unanswered_source_command_after_an_upload_is_sent_again_by_the_next_apply()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        var settings = new DeepCoolMystiqueSettings { SourceEdited = true };
        Assert.True(session.Open(settings, Now));
        pipe.Silent.Add(MystiqueProtocol.CmdMediaSource);

        Assert.True(session.Upload(MystiqueMedia.BuildPicture(new byte[1000], "2101b6e6277841daa821e8d79a5c528b")));
        pipe.Silent.Clear();
        pipe.Writes.Clear();
        Assert.True(session.Apply(settings.Copy()));

        Assert.Equal(new[] { MystiqueProtocol.CmdMediaSource }, pipe.Commands());
    }

    [Theory]
    [InlineData("unknown", 0)]
    [InlineData("zero", 0)]
    [InlineData("partial", 40)]
    public void A_failed_write_ends_the_frame_with_zeros_after_what_landed_then_the_finish_packet(string failure, int landed)
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();
        var package = MystiqueMedia.BuildPicture(new byte[40_000], "2101b6e6277841daa821e8d79a5c528b");
        int failing = pipe.Attempts + 3;
        if (failure == "zero")
        {
            // Both the write and its one resend take nothing.
            pipe.ZeroAttempts.Add(failing);
            pipe.ZeroAttempts.Add(failing + 1);
        }
        else if (failure == "partial")
        {
            pipe.PartialAttempts[failing] = landed;
        }
        else
        {
            pipe.FailAttempts.Add(failing);
        }

        Assert.False(session.Upload(package));

        var stream = pipe.Writes.Skip(1).ToList();
        Assert.Equal(MystiqueMedia.FinishLength, stream[^1].Frame.Length);
        var body = stream.SkipLast(1).SelectMany(w => w.Frame).ToArray();
        var frame = package.Frames[0];
        int good = 2 * MystiqueMedia.PacketLength + landed;
        Assert.Equal(frame.Length, body.Length);
        Assert.Equal(frame[..good], body[..good]);
        Assert.All(body[good..], b => Assert.Equal(0, b));
        Assert.DoesNotContain(pipe.Writes, w => w.Frame.Length == MystiqueProtocol.FrameLength && w.Frame[2] == MystiqueProtocol.CmdMediaSource);
    }

    [Fact]
    public void A_pipe_that_stops_taking_writes_gets_no_fill_and_no_finish()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();
        var package = MystiqueMedia.BuildPicture(new byte[40_000], "2101b6e6277841daa821e8d79a5c528b");
        pipe.FailAfter = 3;

        Assert.False(session.Upload(package));

        Assert.Equal(3, pipe.Writes.Count);
        Assert.DoesNotContain(pipe.Writes, w => w.Frame.Length == MystiqueMedia.FinishLength);
    }

    [Fact]
    public void Recovery_clears_the_halt_and_its_budget_starts_at_the_failure()
    {
        var pipe = new FakePipe();
        long now = 0;
        var session = new MystiqueSession(pipe, monotonicMs: () => now, sleep: NoSleep);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();
        pipe.Halts.Clear();
        var package = MystiqueMedia.BuildPicture(new byte[40_000], "2101b6e6277841daa821e8d79a5c528b");
        int failing = pipe.Attempts + 3;
        pipe.FailAttempts.Add(failing);
        // The failed write ran into the transport timeout.
        pipe.BeforeWrite = attempt => { if (attempt == failing) { now += 5000; } };

        Assert.False(session.Upload(package));

        Assert.Equal(new[] { MystiqueProtocol.SettingsWritePipe }, pipe.Halts);
        Assert.Equal(MystiqueMedia.FinishLength, pipe.Writes[^1].Frame.Length);
    }

    [Fact]
    public void Recovery_stops_filling_once_its_budget_is_spent()
    {
        var pipe = new FakePipe();
        long now = 0;
        var session = new MystiqueSession(pipe, monotonicMs: () => now, sleep: NoSleep);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();
        var package = MystiqueMedia.BuildPicture(new byte[40_000], "2101b6e6277841daa821e8d79a5c528b");
        int failing = pipe.Attempts + 3;
        pipe.FailAttempts.Add(failing);
        pipe.BeforeWrite = attempt => { if (attempt == failing + 2) { now += 5000; } };

        Assert.False(session.Upload(package));

        Assert.Equal(5, pipe.Writes.Count);
        Assert.DoesNotContain(pipe.Writes, w => w.Frame.Length == MystiqueMedia.FinishLength);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Format_counts_only_when_the_screen_answers(bool answers)
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();
        if (!answers)
        {
            pipe.Silent.Add(MystiqueProtocol.CmdFormat);
        }

        Assert.Equal(answers, session.Format());
        Assert.Equal(new[] { MystiqueProtocol.CmdFormat }, pipe.Commands());
    }

    [Fact]
    public void Tick_polls_then_pushes_the_reading_on_the_live_channel()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();

        Assert.True(session.Tick(new MystiqueReading(55, 3, 240, 5380, 1700, 3341), Now.AddSeconds(1)));

        Assert.Equal(new[] { (MystiqueProtocol.CmdPoll, MystiqueProtocol.LiveWritePipe), (MystiqueProtocol.CmdSensors, MystiqueProtocol.LiveWritePipe) },
            pipe.Sent());
        Assert.Equal(55, pipe.Last(MystiqueProtocol.CmdSensors)[3]);
    }

    [Fact]
    public void Tick_sets_the_clock_again_on_the_monotonic_schedule_whatever_the_wall_clock_says()
    {
        var pipe = new FakePipe();
        long mono = 0;
        var session = new MystiqueSession(pipe, () => mono, _ => TimeSpan.Zero);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();

        mono = 59L * 60 * 1000;
        Assert.True(session.Tick(default, Now.AddDays(30)));
        Assert.DoesNotContain(MystiqueProtocol.CmdClock, pipe.Commands());

        mono = 61L * 60 * 1000;
        Assert.True(session.Tick(default, Now.AddSeconds(-5)));
        Assert.Contains(MystiqueProtocol.CmdClock, pipe.Commands());
    }

    [Fact]
    public void Tick_sets_the_clock_at_once_when_the_utc_offset_changes()
    {
        var pipe = new FakePipe();
        var offset = TimeSpan.FromHours(-3);
        var session = new MystiqueSession(pipe, () => 0, _ => offset);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();

        Assert.True(session.Tick(default, Now.AddSeconds(1)));
        Assert.DoesNotContain(MystiqueProtocol.CmdClock, pipe.Commands());

        offset = TimeSpan.FromHours(-2);
        Assert.True(session.Tick(default, Now.AddSeconds(2)));
        Assert.Contains(MystiqueProtocol.CmdClock, pipe.Commands());
    }

    [Fact]
    public void Open_clears_a_halt_on_the_settings_pipes_and_drains_stale_input()
    {
        var pipe = new FakePipe();
        pipe.Incoming.Enqueue(MystiqueProtocol.BuildFrame(MystiqueProtocol.CmdMode, ReadOnlySpan<byte>.Empty, MystiqueProtocol.DeviceMagic));
        pipe.Incoming.Enqueue(MystiqueProtocol.BuildFrame(MystiqueProtocol.CmdInfo, ReadOnlySpan<byte>.Empty, MystiqueProtocol.DeviceMagic));
        var session = new MystiqueSession(pipe, sleep: NoSleep);

        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));

        Assert.Contains(MystiqueProtocol.SettingsWritePipe, pipe.Halts);
        Assert.Contains(MystiqueProtocol.SettingsReadPipe, pipe.Halts);
        Assert.Equal("03FF021004E0058494069B0700080009", session.Info);
    }

    [Fact]
    public void A_reply_that_does_not_match_the_write_is_discarded_and_does_not_count_as_a_miss()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));

        for (int i = 0; i < 20; i++)
        {
            pipe.Incoming.Enqueue(MystiqueProtocol.BuildFrame(MystiqueProtocol.CmdMode, ReadOnlySpan<byte>.Empty, MystiqueProtocol.DeviceMagic));
            Assert.True(session.Tick(default, Now));
        }
    }

    [Fact]
    public void A_refused_write_fails_the_session()
    {
        var pipe = new FakePipe { FailWrites = true };

        Assert.False(new MystiqueSession(pipe, sleep: NoSleep).Open(new DeepCoolMystiqueSettings(), Now));
    }

    [Fact]
    public void A_screen_that_stops_answering_fails_the_session_after_a_run_of_misses()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Answer = false;

        bool alive = true;
        int ticks = 0;
        while (alive && ticks < 10)
        {
            alive = session.Tick(default, Now);
            ticks++;
        }

        Assert.False(alive);
        Assert.InRange(ticks, 2, 3);
    }

    [Fact]
    public void A_lost_pipe_fails_the_session()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe, sleep: NoSleep);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.ReadResult = -1;

        Assert.False(session.Tick(default, Now));
    }

    private static void NoSleep(int ms)
    {
    }

    private sealed class FakePipe : IBulkUsbPipe
    {
        public Queue<byte[]> Incoming { get; } = new();
        public List<byte> Halts { get; } = new();
        public List<(byte Pipe, byte[] Frame)> Writes { get; } = new();
        public bool FailWrites { get; set; }
        public int FailAfter { get; set; } = int.MaxValue;
        public int Attempts { get; private set; }
        public HashSet<int> FailAttempts { get; } = new();
        public HashSet<byte> Silent { get; } = new();
        public HashSet<int> ZeroAttempts { get; } = new();

        /// <summary>Attempts that take only the given number of leading bytes and report that count.</summary>
        public Dictionary<int, int> PartialAttempts { get; } = new();

        private (byte Cmd, int Answered)? _silentAfter;

        /// <summary>Answers the first <paramref name="answered"/> writes of <paramref name="cmd"/>, then none.</summary>
        public void SilentAfter(byte cmd, int answered) => _silentAfter = (cmd, answered);
        public List<int> ReadTimeouts { get; } = new();
        public Action<int>? BeforeWrite { get; set; }
        public bool Answer { get; set; } = true;
        public int? ReadResult { get; set; }

        public IEnumerable<(byte Cmd, byte Pipe)> Sent()
        {
            foreach (var (pipe, frame) in Writes)
            {
                yield return (Cmd(frame), pipe);
            }
        }

        // A packet's tail write can be shorter than a command's header.
        private static byte Cmd(byte[] w) => w.Length > 2 ? w[2] : (byte)0;

        public byte[] Commands() => Writes.ConvertAll(w => Cmd(w.Frame)).ToArray();

        public byte[] Last(byte cmd) => Writes.FindLast(w => Cmd(w.Frame) == cmd).Frame;

        public bool Write(ReadOnlySpan<byte> data) => Write(MystiqueProtocol.LiveWritePipe, data);

        public int WriteCounted(byte pipeId, ReadOnlySpan<byte> data)
        {
            if (ZeroAttempts.Contains(Attempts))
            {
                // Not BeforeWrite?.Invoke(Attempts++): a null delegate would skip the increment.
                int attempt = Attempts++;
                BeforeWrite?.Invoke(attempt);
                return 0;
            }
            if (PartialAttempts.TryGetValue(Attempts, out int partial))
            {
                int attempt = Attempts++;
                BeforeWrite?.Invoke(attempt);
                Writes.Add((pipeId, data[..partial].ToArray()));
                return partial;
            }
            return Write(pipeId, data) ? data.Length : -1;
        }

        public bool Write(byte pipeId, ReadOnlySpan<byte> data)
        {
            BeforeWrite?.Invoke(Attempts);
            int attempt = Attempts++;
            if (FailWrites || Writes.Count >= FailAfter || FailAttempts.Contains(attempt))
            {
                return false;
            }
            var frame = data.ToArray();
            Writes.Add((pipeId, frame));
            if (_silentAfter is { } after && frame.Length == MystiqueProtocol.FrameLength && frame[2] == after.Cmd)
            {
                _silentAfter = after.Answered > 0 ? (after.Cmd, after.Answered - 1) : after;
                if (after.Answered == 0)
                {
                    return true;
                }
            }
            if (Answer && frame.Length == MystiqueProtocol.FrameLength && !Silent.Contains(frame[2]))
            {
                byte[] reply;
                if (frame[2] == MystiqueProtocol.CmdInfo)
                {
                    var payload = new byte[MystiqueProtocol.MaxPayload];
                    System.Text.Encoding.ASCII.GetBytes("03FF021004E0058494069B0700080009").CopyTo(payload, 1);
                    reply = MystiqueProtocol.BuildFrame(frame[2], payload, MystiqueProtocol.DeviceMagic);
                }
                else
                {
                    reply = MystiqueProtocol.BuildFrame(frame[2], ReadOnlySpan<byte>.Empty, MystiqueProtocol.DeviceMagic);
                }
                Incoming.Enqueue(reply);
            }
            return true;
        }

        public int Read(Span<byte> buffer, int timeoutMs) => Read(MystiqueProtocol.LiveReadPipe, buffer, timeoutMs);

        public int Read(byte pipeId, Span<byte> buffer, int timeoutMs)
        {
            ReadTimeouts.Add(timeoutMs);
            if (ReadResult is { } forced)
            {
                return forced;
            }
            if (Incoming.Count == 0)
            {
                return 0;
            }
            var next = Incoming.Dequeue();
            next.CopyTo(buffer);
            return next.Length;
        }

        public bool ClearHalt(byte pipeId)
        {
            Halts.Add(pipeId);
            return true;
        }

        public void Dispose()
        {
        }
    }
}
