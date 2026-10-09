using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.BulkPanels;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class UniversalScreen88Tests
{
    [Fact]
    public void Connect_runs_the_lconnect_apply_sequence_and_comes_up_landscape()
    {
        var driver = new UniversalScreen88Driver();
        var pipe = new GlassPipe();

        var geometry = driver.Connect(pipe, null);

        Assert.Equal((1920, 480), geometry);
        Assert.Equal(
            new byte[]
            {
                UniversalScreen88Protocol.CommandGetVersion,
                UniversalScreen88Protocol.CommandStopPlay,
                UniversalScreen88Protocol.CommandRotate,
                UniversalScreen88Protocol.CommandSetClock,
                UniversalScreen88Protocol.CommandStopClock,
                UniversalScreen88Protocol.CommandPushPng,
            },
            pipe.Commands);
        Assert.Equal(UniversalScreen88Protocol.RotateLandscape, pipe.Params[2][0]);
        Assert.Equal("lianli88_0001_0023", driver.Firmware);
    }

    [Fact]
    public void Portrait_mount_drives_the_native_strip_untouched()
    {
        var driver = new UniversalScreen88Driver();
        driver.BindPortrait(() => true);
        var pipe = new GlassPipe();

        Assert.Equal((480, 1920), driver.Connect(pipe, null));
        Assert.Equal(UniversalScreen88Protocol.RotatePortrait, pipe.Params[2][0]);

        pipe.Clear();
        Assert.True(driver.SendFrame(pipe, null, Quadrants(480, 1920)));
        var shown = DecodeJpeg(pipe.Payloads.Single());
        Assert.Equal((480, 1920), (shown.Width, shown.Height));
        Assert.True(IsRed(shown[10, 10]));
    }

    [Fact]
    public void A_glass_that_never_answers_is_not_connected()
    {
        var driver = new UniversalScreen88Driver();

        Assert.Null(driver.Connect(new GlassPipe { Answers = false }, null));
    }

    [Fact]
    public void Landscape_frames_turn_a_quarter_clockwise_into_the_portrait_framebuffer()
    {
        var driver = new UniversalScreen88Driver();
        var pipe = new GlassPipe();
        driver.Connect(pipe, null);
        pipe.Clear();

        Assert.True(driver.SendFrame(pipe, null, Quadrants(1920, 480)));

        var shown = DecodeJpeg(pipe.Payloads.Single());
        Assert.Equal((480, 1920), (shown.Width, shown.Height));
        // The landscape top-left (red) lands top-right once turned clockwise: upright on the glass (camera-checked).
        Assert.True(IsRed(shown[469, 10]));
        Assert.False(IsRed(shown[10, 10]));
    }

    [Theory]
    [InlineData(100, 0x32)]
    [InlineData(50, 0x19)]
    [InlineData(0, 0)]
    public void Brightness_is_sent_as_half_the_percent(int percent, byte wire)
    {
        var driver = new UniversalScreen88Driver();
        var pipe = new GlassPipe();
        driver.Connect(pipe, null);
        pipe.Clear();

        Assert.True(driver.SetBrightness(pipe, null, percent));

        Assert.Equal(UniversalScreen88Protocol.CommandBrightness, pipe.Commands.Single());
        Assert.Equal(wire, pipe.Params.Single()[0]);
    }

    [Fact]
    public void Release_after_a_session_reboots_the_glass_to_its_own_screen()
    {
        var driver = new UniversalScreen88Driver();
        var pipe = new GlassPipe();
        driver.Connect(pipe, null);
        pipe.Clear();

        driver.Disconnect(pipe, null);

        Assert.Equal(new[] { UniversalScreen88Protocol.CommandStopPlay, UniversalScreen88Protocol.CommandReboot }, pipe.Commands);
    }

    [Fact]
    public void A_retired_glass_without_a_session_is_left_alone_and_never_retaken()
    {
        var driver = new UniversalScreen88Driver();
        driver.Retire();
        var pipe = new GlassPipe();

        Assert.Null(driver.Connect(pipe, null));
        driver.Disconnect(pipe, null);

        Assert.Empty(pipe.Commands);
    }

    [Fact]
    public void A_mounting_change_renegotiates_on_the_open_pipe_without_a_reboot()
    {
        var portrait = false;
        var driver = new UniversalScreen88Driver();
        driver.BindPortrait(() => portrait);
        var pipe = new GlassPipe();
        using var hub = new BulkPanelHub(driver);
        Assert.True(hub.Attach(pipe, null));
        Assert.False(driver.GeometryStale);

        portrait = true;
        Assert.True(driver.GeometryStale);
        pipe.Clear();
        Assert.True(hub.Renegotiate());

        Assert.Equal((480, 1920), (hub.Width, hub.Height));
        Assert.False(driver.GeometryStale);
        Assert.DoesNotContain(UniversalScreen88Protocol.CommandReboot, pipe.Commands);
    }

    [Fact]
    public void Discovery_offers_a_widget_panel_that_can_go_portrait()
    {
        using var hub = new BulkPanelHub(new UniversalScreen88Driver());
        hub.Attach(new GlassPipe(), null);

        var profile = new BulkPanelDiscovery(hub).Discover().Single().Profile;

        Assert.Equal(PanelSurfaces.Monitor, profile.Surface);
        Assert.Equal((1920, 480), (profile.CssWidth, profile.CssHeight));
        Assert.True(profile.SupportsBrightness);
        Assert.True(profile.BuildCapabilities().SupportsPortrait);
        // The real density keeps the grid at four rows; the monitor estimate would give eight.
        Assert.Equal(225, profile.BuildCapabilities().Dpi);
    }

    // ── bezel LEDs (OpenRGB "Lian Li Universal Screen") ──

    [Fact]
    public void Ring_positions_follow_the_camera_map()
    {
        var (u, v) = UniversalScreenRing.Positions();

        Assert.Equal(60, u.Length);
        // LED 0 on the top edge 60% across, running left.
        Assert.Equal(0f, v[0]);
        Assert.InRange(u[0], 0.58f, 0.62f);
        Assert.True(u[1] < u[0]);
        // 15-20 down the left edge, 21-44 along the bottom, 45-50 up the right, 51-59 along the top.
        Assert.All(Enumerable.Range(15, 6), i => Assert.Equal(0f, u[i]));
        Assert.All(Enumerable.Range(21, 24), i => Assert.Equal(1f, v[i]));
        Assert.All(Enumerable.Range(45, 6), i => Assert.Equal(1f, u[i]));
        Assert.All(Enumerable.Range(51, 9), i => Assert.Equal(0f, v[i]));
        Assert.True(v[45] > v[50]);
        Assert.Equal(60, u.Zip(v).Distinct().Count());
    }

    [Fact]
    public void Only_the_screen_bezel_gets_the_frame_layout()
    {
        var bezel = new RgbDevice { Name = "Lian Li Universal Screen", LedCount = 60 };
        var strip = new RgbDevice { Name = "Some Strip", LedCount = 60 };

        Assert.Equal(60, LedUvComputer.ComputeDefaults(bezel).ledU.Length);
        Assert.Empty(LedUvComputer.ComputeDefaults(strip).ledU);
        var (_, _, w, h) = OpenRgbLightingDeviceProvider.DefaultCardLayout(0, bezel);
        Assert.Equal(4f, w / h);
        Assert.Equal(OpenRgbLightingDeviceProvider.DefaultCardLayout(0), OpenRgbLightingDeviceProvider.DefaultCardLayout(0, strip));
    }

    private static byte[] Quadrants(int width, int height)
    {
        var frame = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                var red = x < width / 2 && y < height / 2;
                frame[i] = red ? (byte)0 : (byte)255;
                frame[i + 1] = red ? (byte)0 : (byte)255;
                frame[i + 2] = 255;
                frame[i + 3] = 255;
            }
        }
        return frame;
    }

    private static Image<Rgba32> DecodeJpeg(byte[] jpeg) => Image.Load<Rgba32>(jpeg);

    private static bool IsRed(Rgba32 p) => p.R > 200 && p.G < 60 && p.B < 60;

    /// <summary>Decrypts each command header, answers it like the glass does, and keeps any payload after the header.</summary>
    private sealed class GlassPipe : IBulkUsbPipe
    {
        private readonly Queue<byte[]> _pending = new();
        public bool Answers { get; set; } = true;
        public List<byte> Commands { get; } = new();
        public List<byte[]> Params { get; } = new();
        public List<byte[]> Payloads { get; } = new();

        public void Clear()
        {
            Commands.Clear();
            Params.Clear();
            Payloads.Clear();
        }

        public bool Write(ReadOnlySpan<byte> data)
        {
#pragma warning disable CA5351 // The glass's own framing.
            using var des = System.Security.Cryptography.DES.Create();
#pragma warning restore CA5351
            des.Key = des.IV = System.Text.Encoding.ASCII.GetBytes("slv3tuzx");
            des.Mode = System.Security.Cryptography.CipherMode.CBC;
            des.Padding = System.Security.Cryptography.PaddingMode.None;
            using var decryptor = des.CreateDecryptor();
            var plain = decryptor.TransformFinalBlock(data[..504].ToArray(), 0, 504);
            Commands.Add(plain[0]);
            Params.Add(plain[8..40]);
            if (data.Length > UniversalScreen88Protocol.PacketLength)
            {
                Payloads.Add(data[UniversalScreen88Protocol.PacketLength..].ToArray());
            }
            if (Answers)
            {
                var reply = new byte[512];
                reply[0] = plain[0];
                reply[1] = 0xC8;
                if (plain[0] == UniversalScreen88Protocol.CommandGetVersion)
                {
                    "lianli88_0001_0023"u8.CopyTo(reply.AsSpan(8));
                }
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
}
