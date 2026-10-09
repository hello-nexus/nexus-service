using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class HydroShift2CurveTests
{
    // ── protocol, pinned to bytes L-Connect sent the Y70's unit (USBPcap 2026-10-08) ──

    [Fact]
    public void Tilt_from_0_to_10_degrees_is_the_captured_move()
    {
        var move = HydroShift2CurveProtocol.TiltMove(0, 10);
        Assert.NotNull(move);
        Assert.Equal("5001020000003C01",
            Convert.ToHexString(HydroShift2CurveProtocol.EncodeMove(1, move.Value.Direction, move.Value.Steps, 1)));
    }

    [Fact]
    public void Slide_steps_come_from_absolute_positions_so_rounding_never_accumulates()
    {
        // L-Connect truncates each relative delta (0 -> 2 is 197 steps there), which drifts.
        var up = HydroShift2CurveProtocol.SlideMove(0, 3)!.Value.Steps + HydroShift2CurveProtocol.SlideMove(3, 5)!.Value.Steps;
        Assert.Equal(HydroShift2CurveProtocol.SlideMove(5, 0)!.Value.Steps, up);
        Assert.Equal("500201000000C601",
            Convert.ToHexString(HydroShift2CurveProtocol.EncodeMove(2, 1, HydroShift2CurveProtocol.SlideMove(0, 2)!.Value.Steps, 1)));
    }

    [Theory]
    [InlineData(10, 5, 1, 30)]
    [InlineData(0, 45, 2, 270)]
    [InlineData(0, 90, 2, 270)]
    [InlineData(45, -5, 1, 270)]
    public void Tilt_moves_are_clamped_to_the_range(int from, int to, byte direction, int steps)
    {
        Assert.Equal((direction, steps), HydroShift2CurveProtocol.TiltMove(from, to));
    }

    [Theory]
    [InlineData(2, 0, 2, 198)]
    [InlineData(0, -10, 2, 989)]
    [InlineData(-10, 8, 1, 1780)]
    [InlineData(0, 20, 1, 791)]
    public void Slide_moves_are_clamped_to_the_range(int from, int to, byte direction, int steps)
    {
        Assert.Equal((direction, steps), HydroShift2CurveProtocol.SlideMove(from, to));
    }

    [Fact]
    public void No_move_when_already_there()
    {
        Assert.Null(HydroShift2CurveProtocol.TiltMove(15, 15));
        Assert.Null(HydroShift2CurveProtocol.SlideMove(-4, -4));
    }

    [Fact]
    public void Decodes_the_captured_replies()
    {
        Assert.Equal(23, HydroShift2CurveProtocol.DecodeCoolant(Hex("6017013032363034313830310000000000")));
        Assert.Equal(1719, HydroShift2CurveProtocol.DecodePumpRpm(Hex("6206B73032363034313830310000000000")));
        Assert.Equal("2026041801", HydroShift2CurveProtocol.DecodeVersion(Hex("10053230323630343138303100000000")));
        Assert.Equal(0, HydroShift2CurveProtocol.DecodeMoveStatus(Hex("500001")));
        Assert.Equal(2, HydroShift2CurveProtocol.DecodeMoveStatus(Hex("500201")));
        Assert.Null(HydroShift2CurveProtocol.DecodeCoolant(Hex("6206B7")));
    }

    [Fact]
    public void Led_frame_goes_as_three_chunks_of_15_15_and_5_leds()
    {
        var rgb = Enumerable.Range(0, HydroShift2CurveProtocol.LedCount * 3).Select(i => (byte)(i + 1)).ToArray();

        var chunks = HydroShift2CurveProtocol.EncodeLedFrame(rgb);

        Assert.Equal(3, chunks.Length);
        Assert.All(chunks, c => Assert.Equal(64, c.Length));
        Assert.Equal(new byte[] { 0x11, 0 }, chunks[0][..2]);
        Assert.Equal(new byte[] { 0x11, 15 }, chunks[1][..2]);
        Assert.Equal(new byte[] { 0x11, 30 }, chunks[2][..2]);
        Assert.Equal(rgb[..45], chunks[0][4..49]);
        Assert.Equal(rgb[45..90], chunks[1][4..49]);
        Assert.Equal(rgb[90..105], chunks[2][4..19]);
        Assert.All(chunks[2][19..], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(0, 250)]
    [InlineData(50, 1275)]
    [InlineData(100, 2300)]
    [InlineData(150, 2300)]
    public void Pump_duty_maps_onto_the_output_range(int duty, int output)
    {
        Assert.Equal(output, HydroShift2CurveProtocol.PumpOutputForDuty(duty));
    }

    [Fact]
    public void Edge_leds_run_clockwise_with_the_left_edge_at_15_to_19()
    {
        var left = Enumerable.Range(0, 35).Where(i => HydroShift2CurveLightingDeviceProvider.EdgePosition(i).U == 0f).ToArray();
        Assert.Equal(new[] { 15, 16, 17, 18, 19 }, left);
        var right = Enumerable.Range(0, 35).Where(i => HydroShift2CurveLightingDeviceProvider.EdgePosition(i).U == 1f).ToArray();
        Assert.Equal(new[] { 0, 1, 2, 32, 33, 34 }, right);
        Assert.Equal(1f, HydroShift2CurveLightingDeviceProvider.EdgePosition(3).V);
        Assert.Equal(0f, HydroShift2CurveLightingDeviceProvider.EdgePosition(20).V);
        // Clockwise: down the right edge, then leftwards along the bottom.
        Assert.True(HydroShift2CurveLightingDeviceProvider.EdgePosition(0).V > HydroShift2CurveLightingDeviceProvider.EdgePosition(34).V);
        Assert.True(HydroShift2CurveLightingDeviceProvider.EdgePosition(4).U < HydroShift2CurveLightingDeviceProvider.EdgePosition(3).U);
    }

    // ── board ──

    [Fact]
    public void Connect_reads_the_version_and_ticks_poll_coolant_and_pump()
    {
        var (board, pipe, _) = Connected();

        board.Tick(1_000);

        Assert.Equal("2026041801", board.Firmware);
        Assert.Equal(23, board.CoolantC);
        Assert.Equal(1719, board.PumpRpm);
        Assert.Contains(pipe.Writes, w => w[0] == HydroShift2CurveProtocol.BoardStatus);
        Assert.Contains(pipe.Writes, w => w[0] == HydroShift2CurveProtocol.BoardPumpRpm);
    }

    [Fact]
    public void A_target_moves_both_motors_and_persists_the_position()
    {
        var (board, pipe, store) = Connected();

        board.SetHeadTarget(10, 2);
        board.Tick(1_000);

        var moves = pipe.Moves;
        Assert.Contains("5001020000003C01", moves);
        Assert.Contains("500201000000C601", moves);
        Assert.Equal((10, 2), (board.Head.Tilt, board.Head.Slide));
        Assert.Equal(10, store.Load().Devices.HydroShift2Curve.Tilt);
        Assert.Equal(2, store.Load().Devices.HydroShift2Curve.Slide);
        Assert.True(board.Head.Moving);

        pipe.Idle();
        board.Tick(1_200);
        Assert.False(board.Head.Moving);
    }

    [Fact]
    public void A_busy_motor_takes_no_new_move_until_it_reports_idle()
    {
        var (board, pipe, _) = Connected();
        board.SetHeadTarget(10, null);
        board.Tick(1_000);

        board.SetHeadTarget(20, null);
        board.Tick(1_200);
        Assert.Equal(10, board.Head.Tilt);
        Assert.Single(pipe.Moves, m => m.StartsWith("5001", StringComparison.Ordinal));

        pipe.Idle();
        board.Tick(1_400);
        board.Tick(1_600);
        Assert.Equal(20, board.Head.Tilt);
    }

    [Fact]
    public void Recalibrate_homes_like_l_connect_then_returns_to_the_target()
    {
        var (board, pipe, store) = Connected(tilt: 30, slide: 4);

        board.Recalibrate();
        Assert.True(board.Head.Calibrating);
        for (long t = 1_000; t < 3_000; t += 200)
        {
            board.Tick(t);
            pipe.Idle();
        }

        var moves = pipe.Moves;
        int tilt = moves.IndexOf("5001010000013B02");
        int bottom = moves.IndexOf("5002020000070802");
        int middle = moves.IndexOf("500201000003D402");
        Assert.True(tilt >= 0 && bottom > tilt && middle > bottom, string.Join(",", moves));
        Assert.False(board.Head.Calibrating);
        // Homed to (0, 0), then driven back to the target it had.
        Assert.Contains("500102000000B401", moves.Skip(middle));
        Assert.Equal((30, 4), (board.Head.Tilt, board.Head.Slide));
        Assert.Equal(30, store.Load().Devices.HydroShift2Curve.Tilt);
    }

    [Fact]
    public void Pump_is_driven_resent_and_handed_back()
    {
        var (board, pipe, _) = Connected();

        board.SetPumpDuty(100);
        board.Tick(1_000);
        board.Tick(1_500);
        board.Tick(3_100);
        Assert.Equal(new[] { 2300, 2300 }, pipe.PumpOutputs);

        board.SetPumpDuty(null);
        board.Tick(3_200);
        board.Tick(9_000);
        Assert.Equal(new[] { 2300, 2300, HydroShift2CurveProtocol.DefaultPumpOutput }, pipe.PumpOutputs);
    }

    [Fact]
    public void An_unchanged_led_frame_is_not_resent()
    {
        var (board, pipe, _) = Connected();
        var rgb = new byte[HydroShift2CurveProtocol.LedCount * 3];
        rgb[0] = 255;

        board.SetLeds(rgb);
        board.Tick(1_000);
        board.SetLeds(rgb);
        board.Tick(1_100);

        Assert.Equal(3, pipe.Writes.Count(w => w[0] == HydroShift2CurveProtocol.BoardLedFrame));
    }

    [Fact]
    public void A_move_whose_reply_is_lost_counts_as_started_and_is_not_resent()
    {
        var (board, pipe, store) = Connected();
        pipe.DropNextMoveReply = true;

        board.SetHeadTarget(10, null);
        board.Tick(1_000);
        board.Tick(1_200);
        board.Tick(1_400);

        Assert.Single(pipe.Moves);
        Assert.Equal(10, board.Head.Tilt);
        Assert.Equal(10, store.Load().Devices.HydroShift2Curve.Tilt);
    }

    [Fact]
    public void A_pump_driven_before_a_glitch_is_still_handed_back_after_reconnecting()
    {
        var (board, pipe, _) = Connected();
        board.SetPumpDuty(100);
        board.Tick(1_000);

        pipe.Silent = true;
        board.Tick(2_100);
        Assert.False(board.IsAvailable);

        pipe.Silent = false;
        Assert.True(board.TryConnect());
        board.SetPumpDuty(null);
        board.Tick(2_200);

        Assert.Equal(HydroShift2CurveProtocol.DefaultPumpOutput, pipe.PumpOutputs[^1]);
    }

    [Fact]
    public void A_pump_following_the_motherboard_header_is_taken_off_it_and_given_back()
    {
        var (board, pipe, _) = Connected();
        pipe.FollowsHeader = true;
        board.Tick(1_000);

        board.SetPumpDuty(50);
        board.Tick(1_100);
        var headerOff = pipe.Writes.FindIndex(w => w[0] == HydroShift2CurveProtocol.BoardHeaderFollow && w[1] == 1);
        var output = pipe.Writes.FindIndex(w => w[0] == HydroShift2CurveProtocol.BoardPumpOutput);
        Assert.True(headerOff >= 0 && output > headerOff);

        board.SetPumpDuty(null);
        board.Tick(1_200);
        Assert.Equal(new byte[] { HydroShift2CurveProtocol.BoardHeaderFollow, 0 }, pipe.Writes[^1][..2]);
        Assert.Equal(new[] { 1275 }, pipe.PumpOutputs);
    }

    [Fact]
    public async Task Shutdown_hands_the_pump_back_from_the_board_loop()
    {
        var store = new MemoryStore();
        store.Update(s => s.Devices.NexusControlEnabled.Add(HydroShift2CurveLcdDriver.Id));
        var pipe = new BoardPipe();
        var board = new HydroShift2CurveBoard(
            new PipeFactory(pipe), new DeviceControlGate(store), new HardwarePresence(new BoardUsb()), store);
        board.SetPumpDuty(100);

        await board.StartAsync(CancellationToken.None);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref pipe.PumpWriteCount) == 0 && sw.ElapsedMilliseconds < 4000)
            {
                await Task.Delay(20);
            }
            Assert.True(pipe.PumpWriteCount > 0);

            board.ReleaseForShutdown(TimeSpan.FromSeconds(2));

            Assert.Equal(HydroShift2CurveProtocol.DefaultPumpOutput, pipe.PumpOutputs[^1]);
        }
        finally
        {
            await board.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void A_recalibration_cut_short_by_a_restart_runs_again()
    {
        var (board, pipe, store) = Connected(tilt: 20, slide: 2, recalibrating: true);

        Assert.True(board.Head.Calibrating);
        board.Tick(1_000);

        Assert.Equal("5001010000013B02", pipe.Moves[0]);
        Assert.True(store.Load().Devices.HydroShift2Curve.Recalibrating);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void An_idle_pump_takes_the_follow_setting(bool follow, byte wire)
    {
        var (board, pipe, store) = Connected();
        pipe.FollowsHeader = !follow;
        board.Tick(1_000);
        Assert.DoesNotContain(pipe.Writes, w => w[0] == HydroShift2CurveProtocol.BoardHeaderFollow);

        store.Update(s => s.Devices.HydroShift2Curve.PumpFollowsMotherboard = follow);
        board.Tick(2_100);

        Assert.Contains(pipe.Writes, w => w[0] == HydroShift2CurveProtocol.BoardHeaderFollow && w[1] == wire);
        Assert.Equal(follow ? Array.Empty<int>() : new[] { HydroShift2CurveProtocol.DefaultPumpOutput }, pipe.PumpOutputs);
        Assert.Equal(follow, board.FollowsMotherboardWhenIdle);
    }

    [Fact]
    public void The_follow_setting_overrides_how_the_pump_was_found_on_hand_back()
    {
        var (board, pipe, store) = Connected();
        pipe.FollowsHeader = true;
        store.Update(s => s.Devices.HydroShift2Curve.PumpFollowsMotherboard = false);
        board.SetPumpDuty(50);
        board.Tick(1_000);

        board.SetPumpDuty(null);
        board.Tick(1_100);

        Assert.Equal(HydroShift2CurveProtocol.DefaultPumpOutput, pipe.PumpOutputs[^1]);
        Assert.False(pipe.FollowsHeader);
    }

    [Theory]
    [InlineData(true, new byte[] { HydroShift2Protocol.CommandStopPlay, HydroShift2CurveProtocol.CommandReboot })]
    [InlineData(false, new byte[] { HydroShift2Protocol.CommandStopPlay })]
    public void Release_reboots_the_glass_into_its_own_screen_only_after_a_session(bool answers, byte[] releaseCommands)
    {
        var pipe = new GlassPipe { Answers = answers };
        var driver = new HydroShift2CurveLcdDriver();
        Assert.Equal(answers, driver.Connect(pipe, null) is not null);
        pipe.Commands.Clear();

        driver.Disconnect(pipe, null);

        Assert.Equal(releaseCommands, pipe.Commands);
    }

    [Fact]
    public void Retiring_releases_the_live_session_then_leaves_the_glass_alone()
    {
        var pipe = new GlassPipe { Answers = true };
        var driver = new HydroShift2CurveLcdDriver();
        Assert.NotNull(driver.Connect(pipe, null));
        pipe.Commands.Clear();

        driver.Retire();
        driver.Disconnect(pipe, null);
        Assert.Equal(new[] { HydroShift2Protocol.CommandStopPlay, HydroShift2CurveProtocol.CommandReboot }, pipe.Commands);

        pipe.Commands.Clear();
        Assert.Null(driver.Connect(pipe, null));
        driver.Disconnect(pipe, null);
        Assert.Empty(pipe.Commands);
    }

    // ── native video ──

    [Fact]
    public void A_video_chunk_carries_length_last_flag_play_count_and_session_tick_then_the_bytes()
    {
        var chunk = new byte[] { 0, 0, 0, 1, 0x67, 0x42 };

        var packet = HydroShift2CurveProtocol.EncodeVideoChunk(chunk, last: true, sessionTick: 0x01020304, timestampMs: 77);

        var header = HydroShift2Protocol.EncodeCommand(
            HydroShift2CurveProtocol.CommandStartPlay, new byte[] { 0, 0, 0, 6, 1, 1, 1, 2, 3, 4 }, 77);
        Assert.Equal(header, packet[..header.Length]);
        Assert.Equal(chunk, packet[header.Length..]);
    }

    [Fact]
    public void Decodes_the_block_size_and_the_buffered_count()
    {
        var block = new byte[16];
        block[0] = HydroShift2CurveProtocol.CommandGetH264Block;
        block[9] = 0x10;
        Assert.Equal(1_048_576, HydroShift2CurveProtocol.DecodeH264Block(block));
        Assert.Equal(HydroShift2CurveProtocol.DefaultH264Block, HydroShift2CurveProtocol.DecodeH264Block(new byte[16]));

        var play = new byte[16];
        play[0] = HydroShift2CurveProtocol.CommandStartPlay;
        play[8] = 4;
        Assert.Equal(4, HydroShift2CurveProtocol.DecodeBufferedBlocks(play));
        Assert.Null(HydroShift2CurveProtocol.DecodeBufferedBlocks(new byte[] { 0x65, 0xC8 }));
    }

    [Theory]
    [InlineData(false, false, "scale=2288:1080:flags=lanczos,transpose=1")]
    [InlineData(true, false, "scale=2288:1080:flags=lanczos,transpose=2")]
    [InlineData(false, true, "scale=2288:1080:flags=lanczos,hflip,transpose=1")]
    public void Videos_turn_into_the_portrait_framebuffer_for_the_mount(bool flip, bool mirror, string filter) =>
        Assert.Equal(filter, HydroShift2CurveProtocol.MountFilter(flip, mirror));

    [Fact]
    public void Reads_the_duration_ffmpeg_reports()
    {
        Assert.Equal(83.42, HydroShift2CurveMedia.ParseDuration("  Duration: 00:01:23.42, start: 0.000000, bitrate: 2 kb/s"));
        Assert.Null(HydroShift2CurveMedia.ParseDuration("Duration: N/A"));
    }

    [Fact]
    public void The_library_lists_imported_videos_and_deletes_only_safe_names()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-curve-media-" + Guid.NewGuid().ToString("N"));
        try
        {
            var item = Path.Combine(root, "clip-1");
            Directory.CreateDirectory(item);
            File.WriteAllBytes(Path.Combine(item, "source.mp4"), new byte[] { 1 });
            File.WriteAllLines(Path.Combine(item, "meta.txt"), new[] { "My clip", "0,0,1,1,0,0", "12.5" });
            Directory.CreateDirectory(Path.Combine(root, "no-source"));
            var media = new HydroShift2CurveMedia(root);

            Assert.Equal(new HydroShift2CurveMediaItem("clip-1", "My clip", 12.5, Ready: true), Assert.Single(media.List()));

            Assert.False(media.Exists("../clip-1"));
            Assert.Equal(HydroShift2CurveMediaDelete.Missing, media.Delete("../clip-1"));
            Assert.Equal(HydroShift2CurveMediaDelete.Deleted, media.Delete("clip-1"));
            Assert.Empty(media.List());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("clip.MP4", ".mp4")]
    [InlineData("loop.webm", ".webm")]
    [InlineData("list.m3u8", null)]
    [InlineData("clip.mp4:stream", null)]
    [InlineData("noext", null)]
    public void Only_video_containers_are_imported(string fileName, string? ext) =>
        Assert.Equal(ext, HydroShift2CurveMedia.VideoExtension(fileName));

    private static (HydroShift2CurveBoard Board, BoardPipe Pipe, MemoryStore Store) Connected(
        int tilt = 0, int slide = 0, bool recalibrating = false)
    {
        var store = new MemoryStore();
        store.Update(s =>
        {
            s.Devices.HydroShift2Curve.Tilt = tilt;
            s.Devices.HydroShift2Curve.Slide = slide;
            s.Devices.HydroShift2Curve.Recalibrating = recalibrating;
        });
        var pipe = new BoardPipe();
        var board = new HydroShift2CurveBoard(
            new PipeFactory(pipe), new DeviceControlGate(store), new HardwarePresence(new NoUsb()), store);
        Assert.True(board.TryConnect());
        return (board, pipe, store);
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    /// <summary>Stands in for the board: answers like its firmware and runs each accepted move until <see cref="Idle"/>.</summary>
    private sealed class BoardPipe : IBulkUsbPipe
    {
        private readonly Queue<byte[]> _pending = new();
        private readonly bool[] _busy = new bool[3];

        public List<byte[]> Writes { get; } = new();

        public List<string> Moves => Writes
            .Where(w => w[0] == HydroShift2CurveProtocol.BoardMove && (w[3] | w[4] | w[5] | w[6]) != 0)
            .Select(Convert.ToHexString)
            .ToList();

        public int[] PumpOutputs => Writes
            .Where(w => w[0] == HydroShift2CurveProtocol.BoardPumpOutput)
            .Select(w => (w[1] << 8) | w[2])
            .ToArray();

        public bool Silent { get; set; }
        public bool FollowsHeader { get; set; }
        public int PumpWriteCount;
        public bool DropNextMoveReply { get; set; }

        public void Idle() => Array.Clear(_busy);

        public bool Write(ReadOnlySpan<byte> data)
        {
            var packet = data.ToArray();
            Writes.Add(packet);
            if (packet[0] == HydroShift2CurveProtocol.BoardPumpOutput)
            {
                Interlocked.Increment(ref PumpWriteCount);
            }
            if (packet[0] == HydroShift2CurveProtocol.BoardHeaderFollow)
            {
                FollowsHeader = packet[1] == 0;
            }
            var reply = packet[0] switch
            {
                HydroShift2CurveProtocol.BoardVersion => Hex("10053230323630343138303100000000"),
                HydroShift2CurveProtocol.BoardStatus => new byte[] { 0x60, 0x17, (byte)(FollowsHeader ? 0 : 1) },
                HydroShift2CurveProtocol.BoardPumpRpm => Hex("6206B730323630343138303100000000"),
                HydroShift2CurveProtocol.BoardMove => MoveReply(packet),
                _ => new byte[] { packet[0], 0x17, 0x01 },
            };
            bool isRealMove = packet[0] == HydroShift2CurveProtocol.BoardMove && (packet[3] | packet[4] | packet[5] | packet[6]) != 0;
            if (Silent || (isRealMove && DropNextMoveReply))
            {
                DropNextMoveReply &= !isRealMove;
                return true;
            }
            _pending.Enqueue(reply);
            return true;
        }

        private byte[] MoveReply(byte[] packet)
        {
            var motor = packet[1];
            if (_busy[motor])
            {
                return new byte[] { HydroShift2CurveProtocol.BoardMove, motor, 1 };
            }
            if ((packet[3] | packet[4] | packet[5] | packet[6]) != 0)
            {
                _busy[motor] = true;
            }
            return new byte[] { HydroShift2CurveProtocol.BoardMove, 0, 1 };
        }

        public bool Write(byte pipeId, ReadOnlySpan<byte> data) => Write(data);

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (!_pending.TryDequeue(out var reply))
            {
                return 0;
            }
            reply.CopyTo(buffer);
            return reply.Length;
        }

        public void Dispose() { }
    }

    /// <summary>Stands in for the glass: decrypts each command header and, when it answers, replies [cmd, C8].</summary>
    private sealed class GlassPipe : IBulkUsbPipe
    {
        private readonly Queue<byte[]> _pending = new();
        public bool Answers { get; set; }
        public List<byte> Commands { get; } = new();

        public bool Write(ReadOnlySpan<byte> data)
        {
#pragma warning disable CA5351 // The glass's own framing.
            using var des = System.Security.Cryptography.DES.Create();
#pragma warning restore CA5351
            des.Key = des.IV = System.Text.Encoding.ASCII.GetBytes("slv3tuzx");
            des.Mode = System.Security.Cryptography.CipherMode.CBC;
            des.Padding = System.Security.Cryptography.PaddingMode.None;
            using var decryptor = des.CreateDecryptor();
            var command = decryptor.TransformFinalBlock(data[..8].ToArray(), 0, 8)[0];
            Commands.Add(command);
            if (Answers)
            {
                var reply = new byte[512];
                reply[0] = command;
                reply[1] = 0xC8;
                _pending.Enqueue(reply);
            }
            return true;
        }

        public bool Write(byte pipeId, ReadOnlySpan<byte> data) => Write(data);

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (!_pending.TryDequeue(out var reply))
            {
                Thread.Sleep(Math.Min(timeoutMs, 5));
                return 0;
            }
            reply.CopyTo(buffer);
            return reply.Length;
        }

        public void Dispose() { }
    }

    private sealed class PipeFactory : IBulkUsbPipeFactory
    {
        private readonly IBulkUsbPipe _pipe;
        public PipeFactory(IBulkUsbPipe pipe) => _pipe = pipe;
        public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId) => _pipe;
    }

    private sealed class NoUsb : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Enumerate() => new();
    }

    private sealed class BoardUsb : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Enumerate() => new()
        {
            new() { VendorId = HydroShift2CurveProtocol.BoardVendorId, ProductId = HydroShift2CurveProtocol.BoardProductId },
        };
    }

    private sealed class MemoryStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }
}
