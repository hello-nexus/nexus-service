using System;
using System.Linq;
using Nexus.Service.Deck;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

public sealed class DeckStripRendererTests
{
    private static readonly StreamDeckModel Plus = StreamDeckModels.ByProductId(0x0084)!;
    private static readonly StreamDeckModel PlusXl = StreamDeckModels.ByProductId(0x00c6)!;
    private static readonly StreamDeckModel Neo = StreamDeckModels.ByProductId(0x009a)!;
    private static readonly StreamDeckModel Studio = StreamDeckModels.ByProductId(0x00aa)!;
    private static readonly StreamDeckModel Galleon = StreamDeckModels.ByProductId(0x2b18)!;

    private readonly DeckStripRenderer _renderer = new(DeckTestHelpers.NewTestKeyRenderer());

    private static readonly Rgba32 Accent = new(0x22, 0xd3, 0xee, 255);

    private static DialSegmentInput Value(double fraction, bool muted = false, int stack = 0, int stackIndex = 0) => new()
    {
        Kind = DialSegmentKind.Value,
        Title = "Volume",
        IconName = "Volume2",
        AccentHex = "#22d3ee",
        ValueText = muted ? "Muted" : $"{Math.Round(fraction * 100)}%",
        Fraction = fraction,
        Muted = muted,
        StackCount = stack,
        StackIndex = stackIndex,
    };

    private static bool IsAccent(Rgba32 p) => Math.Abs(p.R - Accent.R) < 24 && Math.Abs(p.G - Accent.G) < 24 && Math.Abs(p.B - Accent.B) < 24;

    private static int AccentPixels(Image<Rgba32> image, Rectangle area)
    {
        var count = 0;
        for (var y = area.Top; y < area.Bottom; y++)
        {
            for (var x = area.Left; x < area.Right; x++)
            {
                if (IsAccent(image[x, y]))
                {
                    count++;
                }
            }
        }
        return count;
    }

    [Theory]
    [InlineData(200, 100)]
    [InlineData(360, 384)]
    [InlineData(150, 60)]
    [InlineData(100, 200)]
    public void Segment_RendersAtAnySize(int width, int height)
    {
        using var image = _renderer.RenderSegment(Value(0.5), width, height);

        Assert.Equal((width, height), (image.Width, image.Height));
    }

    [Fact]
    public void Segment_BarFillTracksTheFraction()
    {
        using var full = _renderer.RenderSegment(Value(1.0), 200, 100);
        using var half = _renderer.RenderSegment(Value(0.5), 200, 100);
        using var none = _renderer.RenderSegment(Value(0.0), 200, 100);
        var bar = new Rectangle(76, 74, 108, 12);

        Assert.True(AccentPixels(full, bar) > AccentPixels(half, bar));
        Assert.True(AccentPixels(half, bar) > AccentPixels(none, bar));
        Assert.True(AccentPixels(full, bar) > 600);
    }

    [Fact]
    public void Segment_MutedDrawsNoBarFill()
    {
        using var muted = _renderer.RenderSegment(Value(0.8, muted: true), 200, 100);

        Assert.Equal(0, AccentPixels(muted, new Rectangle(76, 74, 108, 12)));
    }

    [Fact]
    public void Segment_EmptyIsPlainBackground()
    {
        using var empty = _renderer.RenderSegment(new DialSegmentInput(), 200, 100);

        var first = empty[0, 0];
        for (var y = 0; y < empty.Height; y += 7)
        {
            for (var x = 0; x < empty.Width; x += 7)
            {
                Assert.Equal(first, empty[x, y]);
            }
        }
    }

    [Fact]
    public void Segment_StackDotsMarkThePosition()
    {
        using var first = _renderer.RenderSegment(Value(0.5, stack: 3, stackIndex: 0), 200, 100);
        using var last = _renderer.RenderSegment(Value(0.5, stack: 3, stackIndex: 2), 200, 100);
        var leftDot = new Rectangle(80, 91, 10, 6);
        var rightDot = new Rectangle(110, 91, 10, 6);

        Assert.True(AccentPixels(first, leftDot) > 0);
        Assert.Equal(0, AccentPixels(first, rightDot));
        Assert.True(AccentPixels(last, rightDot) > 0);
    }

    [Fact]
    public void Segment_FeedbackAddsAnAccentBorder_ThatFadesOut()
    {
        using var plain = _renderer.RenderSegment(Value(0.5), 200, 100);
        using var lit = _renderer.RenderSegment(Value(0.5), 200, 100, feedback: 1f);

        Assert.True(AccentPixels(lit, new Rectangle(0, 0, 200, 3)) > 100);
        Assert.Equal(0, AccentPixels(plain, new Rectangle(0, 0, 200, 3)));
    }

    [Fact]
    public void MonitoringSegment_PlotsTheHistory()
    {
        var input = new DialSegmentInput
        {
            Kind = DialSegmentKind.Monitoring,
            Title = "CPU",
            AccentHex = "#22d3ee",
            ValueText = "63%",
            History = new float[] { 10, 40, 20, 80, 60 },
        };
        using var withHistory = _renderer.RenderSegment(input, 200, 100);
        using var without = _renderer.RenderSegment(new DialSegmentInput { Kind = DialSegmentKind.Monitoring, Title = "CPU", ValueText = "63%" }, 200, 100);
        var band = new Rectangle(16, 62, 168, 28);

        var tinted = 0;
        for (var y = band.Top; y < band.Bottom; y++)
        {
            for (var x = band.Left; x < band.Right; x++)
            {
                if (withHistory[x, y].B > withHistory[x, y].R + 20)
                {
                    tinted++;
                }
            }
        }
        Assert.True(tinted > 500);
        Assert.NotEqual(withHistory[100, 85], without[100, 85]);
    }

    [Fact]
    public void Strip_ComposesSegmentsSideBySide()
    {
        var inputs = new[] { Value(1.0), new DialSegmentInput(), Value(1.0), new DialSegmentInput() };

        using var strip = _renderer.RenderStrip(inputs, 800, 100);

        Assert.Equal((800, 100), (strip.Width, strip.Height));
        Assert.True(AccentPixels(strip, new Rectangle(76, 74, 108, 12)) > 600);
        Assert.Equal(0, AccentPixels(strip, new Rectangle(276, 74, 108, 12)));
        Assert.True(AccentPixels(strip, new Rectangle(476, 74, 108, 12)) > 600);
    }

    [Fact]
    public void InfoScreen_OffIsBlack_ClockAndPageDrawText()
    {
        static bool AnyLit(Image<Rgba32> image)
        {
            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    if (image[x, y].R > 128)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        var time = new DateTime(2026, 10, 8, 9, 30, 0);
        using var off = _renderer.RenderInfoScreen(new InfoScreenInput { Mode = "off", LocalTime = time }, 248, 58);
        using var clock = _renderer.RenderInfoScreen(new InfoScreenInput { Mode = "clock", LocalTime = time }, 248, 58);
        using var page = _renderer.RenderInfoScreen(new InfoScreenInput { Mode = "page", Page = 1, PageCount = 4 }, 248, 58);

        Assert.False(AnyLit(off));
        Assert.True(AnyLit(clock));
        Assert.True(AnyLit(page));
    }

    [Fact]
    public void StateKeys_ChangeWithContent_NotWithIdentity()
    {
        Assert.Equal(Value(0.5).StateKey(), Value(0.5).StateKey());
        Assert.NotEqual(Value(0.5).StateKey(), Value(0.51).StateKey());
        Assert.NotEqual(Value(0.5).StateKey(), Value(0.5, muted: true).StateKey());
        Assert.NotEqual(Value(0.5, stack: 2, stackIndex: 0).StateKey(), Value(0.5, stack: 2, stackIndex: 1).StateKey());

        var t = new DateTime(2026, 10, 8, 9, 30, 0);
        Assert.Equal(new InfoScreenInput { Mode = "clock", LocalTime = t }.StateKey(), new InfoScreenInput { Mode = "clock", LocalTime = t.AddSeconds(40) }.StateKey());
        Assert.NotEqual(new InfoScreenInput { Mode = "clock", LocalTime = t }.StateKey(), new InfoScreenInput { Mode = "clock", LocalTime = t.AddMinutes(1) }.StateKey());
    }

    // ── Wire encoding ──

    [Theory]
    [InlineData(0x0084, 200, 100, 200, 100)]
    [InlineData(0x00c6, 200, 100, 100, 200)]
    [InlineData(0x009a, 248, 58, 248, 58)]
    [InlineData(0x2b18, 360, 384, 360, 384)]
    public void EncodeScreen_AppliesTheModelsScreenTransform(int pid, int w, int h, int wireW, int wireH)
    {
        var model = StreamDeckModels.ByProductId(pid)!;
        using var image = new Image<Rgba32>(w, h, new Rgba32(10, 20, 30, 255));

        var wire = DeckWireImageEncoder.EncodeScreen(image, model);

        var info = Image.Identify(wire);
        Assert.Equal((wireW, wireH), (info.Width, info.Height));
    }

    [Fact]
    public void EncodeScreen_Rot90Ccw_PutsTheTopLeftCornerAtTheBottomLeft()
    {
        using var image = new Image<Rgba32>(200, 100, new Rgba32(0, 0, 0, 255));
        for (var y = 0; y < 30; y++)
        {
            for (var x = 0; x < 30; x++)
            {
                image[x, y] = new Rgba32(255, 255, 255, 255);
            }
        }

        using var decoded = Image.Load<Rgba32>(DeckWireImageEncoder.EncodeScreen(image, PlusXl));

        Assert.True(decoded[10, 190].R > 200);
        Assert.True(decoded[10, 10].R < 60);
    }

    [Fact]
    public void KeyEncode_PlusXl_RotatesTheSquareNinetyCcw()
    {
        using var square = new Image<Rgba32>(112, 112, new Rgba32(0, 0, 0, 255));
        for (var y = 0; y < 30; y++)
        {
            for (var x = 0; x < 30; x++)
            {
                square[x, y] = new Rgba32(255, 255, 255, 255);
            }
        }

        var wire = DeckWireImageEncoder.Encode(square, PlusXl, orientation: 0)!;
        using var decoded = Image.Load<Rgba32>(wire);

        Assert.Equal((112, 112), (decoded.Width, decoded.Height));
        Assert.True(decoded[10, 100].R > 200);
        Assert.True(decoded[10, 10].R < 60);
    }

    [Fact]
    public void KeyEncode_Studio_LetterboxesTheSquareOntoA144x112Key()
    {
        using var square = new Image<Rgba32>(112, 112, new Rgba32(200, 40, 40, 255));

        var wire = DeckWireImageEncoder.Encode(square, Studio, orientation: 0)!;
        using var decoded = Image.Load<Rgba32>(wire);

        Assert.Equal((144, 112), (decoded.Width, decoded.Height));
        Assert.True(decoded[4, 56].R < 40, "left bar is black");
        Assert.True(decoded[140, 56].R < 40, "right bar is black");
        Assert.True(decoded[72, 56].R > 150, "the square sits in the middle");
    }

    [Fact]
    public void KeyEncode_Plus_IsUnchangedByTheTransform()
    {
        using var square = new Image<Rgba32>(120, 120, new Rgba32(0, 0, 0, 255));
        square[5, 5] = new Rgba32(255, 255, 255, 255);
        for (var y = 0; y < 20; y++)
        {
            for (var x = 0; x < 20; x++)
            {
                square[x, y] = new Rgba32(255, 255, 255, 255);
            }
        }

        using var decoded = Image.Load<Rgba32>(DeckWireImageEncoder.Encode(square, Plus, orientation: 0)!);

        Assert.True(decoded[5, 5].R > 200);
        Assert.True(decoded[110, 110].R < 60);
    }

    [Fact]
    public void ClearKey_OnAJpegModel_PushesABlackKeyImage()
    {
        var dev = new MockStreamDeckHidDevice { ProductId = Studio.ProductId };
        var surface = new HidStreamDeckSurface(new FakeStreamDeckHidEnumerator { DeviceToOpen = dev }, Studio);
        surface.Connect(new HidDeviceInfo { VendorId = Studio.VendorId, ProductId = Studio.ProductId, Path = "p", Serial = "S" });
        dev.OutputWrites.Clear();

        Assert.True(surface.ClearKey(3));

        var page = dev.OutputWrites.First();
        Assert.Equal(new byte[] { 0x02, 0x07, 3 }, page[..3]);
        var length = page[4] | (page[5] << 8);
        using var decoded = Image.Load<Rgba32>(page.AsSpan(8, length).ToArray());
        Assert.Equal((144, 112), (decoded.Width, decoded.Height));
        Assert.True(decoded[72, 56].R < 10);
    }
}
