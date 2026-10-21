using System;
using System.Collections.Generic;
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
            DeviceEdited = true, ModeEdited = true, ReadoutEdited = true, RecordingEdited = true,
            Mode = DeepCoolMystiqueSettings.ModeRecording,
        };

        Assert.True(new MystiqueSession(pipe).Open(settings, Now));

        Assert.Equal(
            new byte[]
            {
                MystiqueProtocol.CmdInfo, MystiqueProtocol.CmdSystem, MystiqueProtocol.CmdMode, MystiqueProtocol.CmdReadout,
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
    public void Tick_polls_then_pushes_the_reading_on_the_live_channel()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();

        Assert.True(session.Tick(new MystiqueReading(55, 3, 24, 5380, 1700, 3341), Now.AddSeconds(1)));

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

        public bool Write(byte pipeId, ReadOnlySpan<byte> data)
        {
            if (FailWrites)
            {
                return false;
            }
            var frame = data.ToArray();
            Writes.Add((pipeId, frame));
            if (Answer)
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
