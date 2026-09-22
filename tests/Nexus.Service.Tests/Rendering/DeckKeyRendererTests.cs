using System;
using System.IO;
using System.Linq;
using Nexus.Service.Activity;
using Nexus.Service.Deck;
using Nexus.Service.Models.Activity;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace Nexus.Service.Tests.Rendering;

internal sealed class NullShortcutsProvider : IShortcutsProvider
{
    public System.Collections.Generic.IReadOnlyList<Shortcut> GetAll() => Array.Empty<Shortcut>();
    public Shortcut? GetById(string targetId) => null;
    public byte[] GetIcon(string targetId) => Array.Empty<byte>();
    public bool Launch(string targetId) => false;
    public string ResolveProcessName(string targetId) => "";
}

internal sealed class NullProcessIconProvider : IProcessIconProvider
{
    public byte[]? GetIcon(string exePath) => Array.Empty<byte>();
}

public sealed class DeckKeyRendererTests : IDisposable
{
    private readonly string _imagesDir = Path.Combine(Path.GetTempPath(), "nexus-deck-key-renderer-test-" + Guid.NewGuid().ToString("N"));
    private readonly DeckKeyRenderer _renderer;
    private static readonly StreamDeckModel Mk2 = StreamDeckModels.ByProductId(0x0080)!;

    public DeckKeyRendererTests()
    {
        _renderer = new DeckKeyRenderer(new DeckImageStore(_imagesDir), new NullShortcutsProvider(), new NullProcessIconProvider());
    }

    public void Dispose()
    {
        try { Directory.Delete(_imagesDir, recursive: true); } catch { /* best effort */ }
    }

    private static Image<Rgba32> Decode(byte[] wireBytes) => Image.Load<Rgba32>(wireBytes);

    private static int LitPixelCount(Image<Rgba32> image, int threshold = 40)
    {
        var lit = 0;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                foreach (ref var px in accessor.GetRowSpan(y))
                {
                    if (px.R > threshold || px.G > threshold || px.B > threshold)
                    {
                        lit++;
                    }
                }
            }
        });
        return lit;
    }

    [Fact]
    public void Render_BlankOffSlot_ProducesAnAllBlackImage()
    {
        var bytes = _renderer.Render(new DeckSlot(), isToggleOn: false, Mk2, orientation: 0);
        Assert.NotNull(bytes);

        using var image = Decode(bytes!);
        Assert.Equal(Mk2.KeyPixelSize, image.Width);
        Assert.Equal(0, LitPixelCount(image, threshold: 5));
    }

    [Fact]
    public void Render_ColorOnlySlot_FillsWithThatColor()
    {
        var slot = new DeckSlot { Color = "#ff0000" };
        var bytes = _renderer.Render(slot, isToggleOn: false, Mk2, orientation: 0);
        Assert.NotNull(bytes);

        using var image = Decode(bytes!);
        var center = image[Mk2.KeyPixelSize / 2, 2];
        Assert.True(center.R > 150, $"expected a red-dominant pixel near the top edge, got {center}");
    }

    [Fact]
    public void Render_ActionWithNoExplicitColor_UsesTheCategoryDefault()
    {
        var slot = new DeckSlot { Action = new DeckAction { Type = "power", PowerAction = "lock" } };
        var bytes = _renderer.Render(slot, isToggleOn: false, Mk2, orientation: 0);
        Assert.NotNull(bytes);

        using var image = Decode(bytes!);
        // DeckIconDefaults.CategoryColorHex(Power) = #ef4444 (red-dominant).
        var corner = image[1, 1];
        Assert.True(corner.R > corner.B, $"expected the power category's red-leaning fill, got {corner}");
    }

    [Fact]
    public void Render_Label_ChangesTheOutputVersusNoLabel()
    {
        var withoutLabel = new DeckSlot { Color = "#222222", Action = new DeckAction { Type = "openUrl", Url = "https://a" } };
        var withLabel = new DeckSlot
        {
            Color = "#222222",
            Action = new DeckAction { Type = "openUrl", Url = "https://a" },
            Label = "Hello",
            Title = new DeckTitleStyle { Show = true },
        };

        var a = _renderer.Render(withoutLabel, false, Mk2, 0);
        var b = _renderer.Render(withLabel, false, Mk2, 0);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Render_LabelWithShowFalse_IsNotPainted()
    {
        var noShow = new DeckSlot { Color = "#222222", Label = "Hello", Title = new DeckTitleStyle { Show = false } };
        var noLabelAtAll = new DeckSlot { Color = "#222222" };

        var a = _renderer.Render(noShow, false, Mk2, 0);
        var b = _renderer.Render(noLabelAtAll, false, Mk2, 0);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Render_ToggleBranches_ProduceDifferentBytes()
    {
        var slot = new DeckSlot
        {
            Action = new DeckAction
            {
                Type = "toggle",
                State = new DeckToggleState { Kind = "mute" },
                On = new DeckAction { Type = "audioOutput" },
                Off = new DeckAction { Type = "power", PowerAction = "lock" },
            },
        };

        var on = _renderer.Render(slot, isToggleOn: true, Mk2, orientation: 0);
        var off = _renderer.Render(slot, isToggleOn: false, Mk2, orientation: 0);

        Assert.NotNull(on);
        Assert.NotNull(off);
        Assert.NotEqual(on, off);
    }

    [Fact]
    public void Render_SameSlotTwice_IsServedFromCache()
    {
        var slot = new DeckSlot { Color = "#334455", Label = "X" };

        var first = _renderer.Render(slot, false, Mk2, 0);
        var second = _renderer.Render(slot, false, Mk2, 0);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    /// <summary>
    /// An empty shortcut icon is what the Windows helper proxy answers while no
    /// helper is connected (boot), so the generic fallback drawn for a launchApp
    /// key must not be cached: once the helper connects, the next render has
    /// to pick up the real icon.
    /// </summary>
    [Fact]
    public void Render_LaunchAppWithNoShortcutIconYet_IsNotCached()
    {
        var shortcuts = new SwitchableShortcutsProvider();
        var renderer = new DeckKeyRenderer(new DeckImageStore(_imagesDir), shortcuts, new NullProcessIconProvider());
        var slot = new DeckSlot { Action = new DeckAction { Type = "launchApp", AppId = "app-1" } };

        var fallback = renderer.Render(slot, false, Mk2, 0);
        Assert.NotNull(fallback);

        using (var icon = new Image<Rgba32>(16, 16))
        {
            icon.Mutate(ctx => ctx.Fill(Color.Lime));
            using var ms = new MemoryStream();
            icon.SaveAsPng(ms);
            shortcuts.Icon = ms.ToArray();
        }
        var real = renderer.Render(slot, false, Mk2, 0);

        Assert.NotNull(real);
        Assert.NotSame(fallback, real);
        Assert.NotEqual(fallback, real);
        // With the icon available the face IS cached.
        Assert.Same(real, renderer.Render(slot, false, Mk2, 0));
    }

    private sealed class SwitchableShortcutsProvider : IShortcutsProvider
    {
        public byte[] Icon = Array.Empty<byte>();
        public System.Collections.Generic.IReadOnlyList<Shortcut> GetAll() => Array.Empty<Shortcut>();
        public Shortcut? GetById(string targetId) => null;
        public byte[] GetIcon(string targetId) => Icon;
        public bool Launch(string targetId) => false;
        public string ResolveProcessName(string targetId) => "";
    }

    [Fact]
    public void Render_AppIcon_FillsTheKeyFaceOnBlack()
    {
        var shortcuts = new SwitchableShortcutsProvider { Icon = SolidPng(Color.Red, 4, 4) };
        var renderer = new DeckKeyRenderer(new DeckImageStore(_imagesDir), shortcuts, new NullProcessIconProvider());
        var slot = new DeckSlot { Action = new DeckAction { Type = "launchApp", AppId = "app-1" } };

        using var image = Decode(renderer.Render(slot, false, Mk2, 0)!);
        // Contain-fit of a square icon covers the key edge to edge: no accent left in the corner.
        var corner = image[1, 1];
        Assert.True(corner.R > 150 && corner.G < 100 && corner.B < 100, $"expected the app icon at the key edge, got {corner}");
    }

    [Fact]
    public void Render_SelectedAppIcon_KeepsBlackBehindTheIcon()
    {
        var shortcuts = new SwitchableShortcutsProvider { Icon = SolidPng(Color.Red, 4, 2) };
        var renderer = new DeckKeyRenderer(new DeckImageStore(_imagesDir), shortcuts, new NullProcessIconProvider());
        var slot = new DeckSlot { Action = new DeckAction { Type = "launchApp", AppId = "app-1" } };

        using var image = Decode(renderer.Render(slot, false, Mk2, 0, selected: true)!);
        // Inside the ring, above the 2:1 icon's letterbox: still black, no brightened fill.
        var letterbox = image[Mk2.KeyPixelSize / 2, (int)(Mk2.KeyPixelSize * 0.12f)];
        Assert.True(letterbox.R < 24 && letterbox.G < 24 && letterbox.B < 24, $"expected black in the letterbox of a selected app key, got {letterbox}");
    }

    [Fact]
    public void Render_AppIcon_LetterboxesOnTheSlotsOwnColor()
    {
        var shortcuts = new SwitchableShortcutsProvider { Icon = SolidPng(Color.Red, 4, 2) };
        var renderer = new DeckKeyRenderer(new DeckImageStore(_imagesDir), shortcuts, new NullProcessIconProvider());
        var slot = new DeckSlot { Color = "#00ff00", Action = new DeckAction { Type = "launchApp", AppId = "app-1" } };

        using var image = Decode(renderer.Render(slot, false, Mk2, 0)!);
        var top = image[Mk2.KeyPixelSize / 2, 2];
        var center = image[Mk2.KeyPixelSize / 2, Mk2.KeyPixelSize / 2];
        Assert.True(top.G > 150 && top.R < 100, $"expected the slot color above a 2:1 icon, got {top}");
        Assert.True(center.R > 150 && center.G < 100, $"expected the icon across the middle, got {center}");
    }

    /// <summary>
    /// A null process icon means extraction is still running: the face stays
    /// blank and uncached, one provider call per render, and the next render
    /// after the icon lands paints it full-face and caches.
    /// </summary>
    [Fact]
    public void Render_ExeWithProcessIconPending_IsBlankUncachedAndQueriesOnce()
    {
        var processIcons = new CountingProcessIconProvider { Icon = null };
        var renderer = new DeckKeyRenderer(new DeckImageStore(_imagesDir), new SwitchableShortcutsProvider(), processIcons);
        var slot = new DeckSlot { Action = new DeckAction { Type = "openFile", Path = @"C:\Games\Hades\Hades.exe" } };

        var pending = renderer.Render(slot, false, Mk2, 0);
        Assert.NotNull(pending);
        Assert.Equal(1, processIcons.Calls);
        // Blank while pending: the bare category accent, no glyph.
        Assert.Equal(renderer.Render(new DeckSlot { Color = DeckIconDefaults.CategoryColorHex(DeckCategory.Launch) }, false, Mk2, 0), pending);

        processIcons.Icon = SolidPng(Color.Red, 4, 4);
        var real = renderer.Render(slot, false, Mk2, 0);
        Assert.Equal(2, processIcons.Calls);
        Assert.NotEqual(pending, real);
        Assert.Same(real, renderer.Render(slot, false, Mk2, 0));
    }

    private sealed class CountingProcessIconProvider : IProcessIconProvider
    {
        public byte[]? Icon;
        public int Calls;
        public byte[]? GetIcon(string exePath) { Calls++; return Icon; }
    }

    private static byte[] SolidPng(Color color, int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx => ctx.Fill(color));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Render_ImageIconFromDeckImageStore_CoverFillsTheKey()
    {
        byte[] redPng;
        using (var redImage = new Image<Rgba32>(4, 4))
        {
            redImage.Mutate(ctx => ctx.Fill(Color.Red));
            using var ms = new MemoryStream();
            redImage.SaveAsPng(ms);
            redPng = ms.ToArray();
        }
        var store = new DeckImageStore(_imagesDir);
        var id = store.Store(redPng);
        Assert.NotNull(id);

        var slot = new DeckSlot { Icon = new DeckIcon { Kind = "image", Value = id! } };
        var bytes = _renderer.Render(slot, false, Mk2, 0);
        Assert.NotNull(bytes);

        using var image = Decode(bytes!);
        var center = image[Mk2.KeyPixelSize / 2, Mk2.KeyPixelSize / 2];
        Assert.True(center.R > 150 && center.G < 100 && center.B < 100, $"expected the cover-filled red source image, got {center}");
    }

    /// <summary>
    /// Lucide PNGs (data/deck-icons/*.png) are generated by a separate
    /// nexus-web script and are not committed yet in this phase - the
    /// renderer must degrade to a blank icon rather than throwing. Once the
    /// resources land, this also proves the icon actually paints.
    /// </summary>
    [Fact]
    public void Render_LucideIcon_DoesNotThrowRegardlessOfWhetherTheResourceExists()
    {
        var hasAnyLucideResource = typeof(DeckKeyRenderer).Assembly.GetManifestResourceNames()
            .Any(n => n.StartsWith("deck-icon-", StringComparison.Ordinal));

        var slot = new DeckSlot { Icon = new DeckIcon { Kind = "lucide", Value = "Globe" } };
        var withIcon = _renderer.Render(slot, false, Mk2, 0);
        var withoutIcon = _renderer.Render(new DeckSlot { Color = slot.Color }, false, Mk2, 0);

        Assert.NotNull(withIcon);
        if (hasAnyLucideResource)
        {
            Assert.NotEqual(withIcon, withoutIcon);
        }
    }
}
