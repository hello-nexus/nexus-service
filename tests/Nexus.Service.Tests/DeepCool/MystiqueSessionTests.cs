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
        var session = new MystiqueSession(pipe);

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

        Assert.True(new MystiqueSession(pipe).Open(settings, Now));

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
        var session = new MystiqueSession(pipe);
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
        var session = new MystiqueSession(pipe, monotonicMs: () => 0);
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
        foreach (var frame in package.Frames)
        {
            expected.AddRange(MystiqueMedia.Packets(frame));
        }
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
    public void Nothing_streams_unless_the_screen_answers_the_announce()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe);
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
        var session = new MystiqueSession(pipe);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();
        var package = MystiqueMedia.BuildPicture(new byte[1000], "2101b6e6277841daa821e8d79a5c528b");
        pipe.ZeroAttempts.Add(pipe.Attempts + 5);

        Assert.True(session.Upload(package));

        var streamed = pipe.Writes.Skip(1).TakeWhile(w => w.Frame.Length != MystiqueMedia.FinishLength).SelectMany(w => w.Frame);
        Assert.Equal(package.Frames[0], streamed);
    }

    [Fact]
    public void An_unanswered_source_command_after_an_upload_is_sent_again_by_the_next_apply()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe);
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
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_packet_ends_the_frame_with_zeros_then_the_finish_packet(bool knownZero)
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();
        var package = MystiqueMedia.BuildPicture(new byte[1000], "2101b6e6277841daa821e8d79a5c528b");
        int failing = pipe.Attempts + 3;
        if (knownZero)
        {
            // Both the packet and its one resend take nothing.
            pipe.ZeroAttempts.Add(failing);
            pipe.ZeroAttempts.Add(failing + 1);
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
        Assert.Equal(frame.Length, body.Length);
        Assert.Equal(frame[..(2 * MystiqueMedia.PacketLength)], body[..(2 * MystiqueMedia.PacketLength)]);
        Assert.All(body[(2 * MystiqueMedia.PacketLength)..], b => Assert.Equal(0, b));
        Assert.DoesNotContain(pipe.Writes, w => w.Frame.Length == MystiqueProtocol.FrameLength && w.Frame[2] == MystiqueProtocol.CmdMediaSource);
    }

    [Fact]
    public void A_pipe_that_stops_taking_writes_gets_no_fill_and_no_finish()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();
        var package = MystiqueMedia.BuildPicture(new byte[1000], "2101b6e6277841daa821e8d79a5c528b");
        pipe.FailAfter = 3;

        Assert.False(session.Upload(package));

        Assert.Equal(3, pipe.Writes.Count);
        Assert.DoesNotContain(pipe.Writes, w => w.Frame.Length == MystiqueMedia.FinishLength);
    }

    [Fact]
    public void Recovery_sends_nothing_once_the_failed_packet_is_past_its_budget()
    {
        var pipe = new FakePipe();
        long now = 0;
        var session = new MystiqueSession(pipe, monotonicMs: () => now);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();
        var package = MystiqueMedia.BuildPicture(new byte[1000], "2101b6e6277841daa821e8d79a5c528b");
        int failing = pipe.Attempts + 3;
        pipe.ZeroAttempts.Add(failing);
        pipe.BeforeWrite = attempt => { if (attempt == failing) { now += 5000; } };

        Assert.False(session.Upload(package));

        Assert.Equal(3, pipe.Writes.Count);
    }

    [Fact]
    public void Tick_polls_then_pushes_the_reading_on_the_live_channel()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe);
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
        var session = new MystiqueSession(pipe);

        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));

        Assert.Contains(MystiqueProtocol.SettingsWritePipe, pipe.Halts);
        Assert.Contains(MystiqueProtocol.SettingsReadPipe, pipe.Halts);
        Assert.Equal("03FF021004E0058494069B0700080009", session.Info);
    }

    [Fact]
    public void A_reply_that_does_not_match_the_write_is_discarded_and_does_not_count_as_a_miss()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe);
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

        Assert.False(new MystiqueSession(pipe).Open(new DeepCoolMystiqueSettings(), Now));
    }

    [Fact]
    public void A_screen_that_stops_answering_fails_the_session_after_a_run_of_misses()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe);
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
        var session = new MystiqueSession(pipe);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.ReadResult = -1;

        Assert.False(session.Tick(default, Now));
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
        public List<int> ReadTimeouts { get; } = new();
        public Action<int>? BeforeWrite { get; set; }
        public bool Answer { get; set; } = true;
        public int? ReadResult { get; set; }

        public IEnumerable<(byte Cmd, byte Pipe)> Sent()
        {
            foreach (var (pipe, frame) in Writes)
            {
                yield return (frame[2], pipe);
            }
        }

        public byte[] Commands() => Writes.ConvertAll(w => w.Frame[2]).ToArray();

        public byte[] Last(byte cmd) => Writes.FindLast(w => w.Frame[2] == cmd).Frame;

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
