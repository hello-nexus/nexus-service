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

        Assert.True(session.Tick(new MystiqueReading(55, 3, 5380, 1700, 3341), Now.AddSeconds(1)));

        Assert.Equal(new[] { (MystiqueProtocol.CmdPoll, MystiqueProtocol.LiveWritePipe), (MystiqueProtocol.CmdSensors, MystiqueProtocol.LiveWritePipe) },
            pipe.Sent());
        Assert.Equal(55, pipe.Last(MystiqueProtocol.CmdSensors)[3]);
    }

    [Fact]
    public void Tick_sets_the_clock_again_once_an_hour()
    {
        var pipe = new FakePipe();
        var session = new MystiqueSession(pipe);
        Assert.True(session.Open(new DeepCoolMystiqueSettings(), Now));
        pipe.Writes.Clear();

        Assert.True(session.Tick(default, Now.AddMinutes(59)));
        Assert.DoesNotContain(MystiqueProtocol.CmdClock, pipe.Commands());

        Assert.True(session.Tick(default, Now.AddMinutes(61)));
        Assert.Contains(MystiqueProtocol.CmdClock, pipe.Commands());
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
        private byte[]? _pending;

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
                _pending = reply;
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
            if (_pending is null)
            {
                return 0;
            }
            _pending.CopyTo(buffer);
            int n = _pending.Length;
            _pending = null;
            return n;
        }

        public void Dispose()
        {
        }
    }
}
