using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Nexus.Service.Activity;
using Nexus.Service.Deck;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Serialization;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nexus.Service.Rendering;

/// <summary>
/// Device-neutral render of one deck key's face to a model's wire bytes: a C#
/// port of nexus-web's renderDeckKeyBitmap.ts. Background is the slot color
/// or the category default (DeckIconDefaults), then an icon (lucide PNG
/// resource, emoji glyph, app icon, or an uploaded image cover-fit), then the
/// label with its resolved title style. Every visible key on a physical deck
/// - not just monitoring/weather - renders through here.
/// </summary>
public sealed class DeckKeyRenderer
{
    private const float IconFraction = 0.62f;

    private static readonly string[] EmojiFontNames =
    {
        "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", "Noto Emoji",
    };

    private static readonly Regex ExecutablePathRegex = new(@"\.(exe|lnk|app)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly ConcurrentDictionary<string, Image<Rgba32>?> LucideCache = new(StringComparer.Ordinal);

    private const int CacheCapacity = 256;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _cacheLru = new();

    private readonly DeckImageStore _imageStore;
    private readonly IShortcutsProvider _shortcuts;
    private readonly IProcessIconProvider _processIcons;

    public DeckKeyRenderer(DeckImageStore imageStore, IShortcutsProvider shortcuts, IProcessIconProvider processIcons)
    {
        _imageStore = imageStore;
        _shortcuts = shortcuts;
        _processIcons = processIcons;
    }

    private static readonly DeckSlot BackKeySlot = new() { Icon = new DeckIcon { Kind = "lucide", Value = "Undo2" }, Color = "#23262e" };

    /// <summary>The reserved folder-Back key's bitmap, matching nexus-web's renderDeckBackKeyBitmap.</summary>
    public byte[]? RenderBackKey(StreamDeckModel model, int orientation) => Render(BackKeySlot, isToggleOn: false, model, orientation);

    /// <summary>Accent used for the Recent Apps focused-key ring, matching MonitoringTileRenderer's default accent.</summary>
    private static readonly Color SelectedAccent = Color.ParseHex("4da3ff");

    /// <summary>
    /// Renders one slot's wire bytes at model/orientation. isToggleOn selects
    /// the on/off branch when the slot's action is a toggle; ignored
    /// otherwise. selected paints the Recent Apps focused-key treatment
    /// (accent ring plus a brighter fill) over the resolved background.
    /// Null when the encode fails or the model's wire format rejects the
    /// length. A miss caused by a transient app-icon lookup
    /// (IProcessIconProvider returning null - the helper is not connected
    /// yet) is never cached, so the next tick retries the icon instead of
    /// pinning a blank key.
    /// </summary>
    public byte[]? Render(DeckSlot slot, bool isToggleOn, StreamDeckModel model, int orientation, bool selected = false)
    {
        var cacheKey = $"{ComputeContentHash(slot, isToggleOn)}|{model.ProductId}|{orientation}|{selected}";
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                Touch(cacheKey);
                return cached;
            }
        }

        var display = ResolveDisplay(slot, isToggleOn);
        var transient = false;
        using var image = RenderImage(display, model.KeyPixelSize, ref transient, selected);
        var bytes = DeckWireImageEncoder.Encode(image, model, orientation);
        if (bytes is not null && !transient)
        {
            lock (_cacheLock)
            {
                Insert(cacheKey, bytes);
            }
        }
        return bytes;
    }

    private void Touch(string key)
    {
        _cacheLru.Remove(key);
        _cacheLru.AddLast(key);
    }

    private void Insert(string key, byte[] bytes)
    {
        _cache[key] = bytes;
        Touch(key);
        if (_cache.Count > CacheCapacity)
        {
            var oldest = _cacheLru.First!.Value;
            _cacheLru.RemoveFirst();
            _cache.Remove(oldest);
        }
    }

    /// <summary>Content identity of a slot's rendered face (independent of model/orientation), for a caller that needs its own cache keyed alongside Render's output - e.g. the worker's pressed-key-variant cache.</summary>
    public static string ComputeContentHash(DeckSlot slot, bool isToggleOn)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(slot, AppJsonContext.Default.DeckSlot);
        var combined = new byte[json.Length + 1];
        json.CopyTo(combined, 0);
        combined[^1] = (byte)(isToggleOn ? 1 : 0);
        return Convert.ToHexString(SHA256.HashData(combined));
    }

    private readonly record struct DisplaySlot(DeckIcon? Icon, string? Label, string ColorHex, DeckTitleStyle? Title, bool IsFolder, bool IsBlankOff, DeckAction? EffectiveAction);

    /// <summary>Resolves the toggle branch (if any) and the background color, matching renderDeckKeyBitmap.ts's paintKey.</summary>
    private static DisplaySlot ResolveDisplay(DeckSlot slot, bool isToggleOn)
    {
        var isFolder = slot.Folder is not null;
        var isBlankOff = slot.Action is null && slot.Folder is null && slot.Icon is null && string.IsNullOrEmpty(slot.Color);

        var icon = slot.Icon;
        var colorHex = slot.Color;
        var effectiveAction = slot.Action;

        if (slot.Action is { Type: "toggle" })
        {
            var branch = isToggleOn ? slot.Action.On : slot.Action.Off;
            var (branchIcon, branchColor) = DeckIconDefaults.ResolveToggleBranch(slot, branch);
            icon = branchIcon;
            colorHex = branchColor;
            effectiveAction = branch;
        }

        if (!isBlankOff && colorHex is null)
        {
            colorHex = DeckIconDefaults.CategoryColorHex(isFolder ? DeckCategory.Folder : DeckIconDefaults.Category(effectiveAction));
        }

        return new DisplaySlot(icon, slot.Label, isBlankOff ? "#000000" : colorHex!, slot.Title, isFolder, isBlankOff, effectiveAction);
    }

    private Image<Rgba32> RenderImage(DisplaySlot display, int size, ref bool transient, bool selected = false)
    {
        var image = new Image<Rgba32>(size, size);
        var background = RenderKit.ParseColor(display.ColorHex, Color.Black);
        if (selected)
        {
            background = Brighten(background, 0.25f);
        }
        image.Mutate(ctx => ctx.Fill(background));

        if (display.IsBlankOff)
        {
            return image;
        }

        var shouldPaintIcon = display.EffectiveAction is not null || display.IsFolder || display.Icon is not null;
        if (shouldPaintIcon)
        {
            PaintIcon(image, display, size, ref transient);
        }

        if (!string.IsNullOrEmpty(display.Label))
        {
            var titleStyle = ResolveTitleStyle(display.Title);
            if (titleStyle.Show)
            {
                PaintLabel(image, display.Label!, size, titleStyle);
            }
        }

        if (selected)
        {
            var ringWidth = MathF.Max(2f, size * 0.06f);
            var rect = new RectangleF(ringWidth / 2f, ringWidth / 2f, size - ringWidth, size - ringWidth);
            image.Mutate(ctx => ctx.Draw(SelectedAccent, ringWidth, rect));
        }
        return image;
    }

    /// <summary>Lerps each channel toward white by amount (0..1), for the Recent Apps focused-key fill.</summary>
    private static Color Brighten(Color color, float amount)
    {
        var rgba = color.ToPixel<Rgba32>();
        byte Lerp(byte c) => (byte)MathF.Round(c + (255 - c) * amount);
        return Color.FromRgba(Lerp(rgba.R), Lerp(rgba.G), Lerp(rgba.B), rgba.A);
    }

    private void PaintIcon(Image<Rgba32> image, DisplaySlot display, int size, ref bool transient)
    {
        var target = (int)MathF.Round(size * IconFraction);
        if (target <= 0)
        {
            return;
        }
        var cx = size / 2f;
        var cy = size / 2f;
        var icon = display.Icon;
        var action = display.EffectiveAction;

        if (icon is { Kind: "emoji" })
        {
            PaintEmoji(image, icon.Value, size, target);
            return;
        }

        if (icon is { Kind: "image" } && _imageStore.TryLoad(icon.Value) is { } loaded)
        {
            using var src = Image.Load<Rgba32>(loaded.Bytes);
            DrawCover(image, src, size);
            return;
        }

        var appId = ResolveAppId(icon, action);
        if (appId is not null)
        {
            var appIcon = _shortcuts.GetIcon(appId);
            if (appIcon.Length > 0)
            {
                using var src = Image.Load<Rgba32>(appIcon);
                DrawCentered(image, src, cx, cy, target);
                return;
            }
            var exePath = ResolveExePath(action);
            if (exePath is not null)
            {
                var processIcon = _processIcons.GetIcon(exePath);
                if (processIcon is null)
                {
                    transient = true;
                    return;
                }
                if (processIcon.Length > 0)
                {
                    using var src = Image.Load<Rgba32>(processIcon);
                    DrawCentered(image, src, cx, cy, target);
                    return;
                }
            }
        }

        var name = icon is { Kind: "lucide" } ? icon.Value : DeckIconDefaults.AutoIconName(action, display.IsFolder);
        var lucide = LoadLucide(name);
        if (lucide is not null)
        {
            DrawCentered(image, lucide, cx, cy, target);
        }
    }

    /// <summary>The /shortcuts/icon target a slot's icon comes from, matching nexus-web's deckIcons.ts slotAppId.</summary>
    private static string? ResolveAppId(DeckIcon? icon, DeckAction? action)
    {
        if (action is { Type: "launchApp" } && !string.IsNullOrEmpty(action.AppId))
        {
            return action.AppId;
        }
        if (icon is { Kind: "app" })
        {
            return icon.Value;
        }
        return ResolveExePath(action);
    }

    private static string? ResolveExePath(DeckAction? action) =>
        action is { Type: "openFile" } && action.Path is not null && ExecutablePathRegex.IsMatch(action.Path) ? action.Path : null;

    private static void PaintEmoji(Image<Rgba32> image, string emoji, int size, int target)
    {
        var font = ResolveEmojiFont().CreateFont(target * 0.85f, FontStyle.Regular);
        image.Mutate(ctx => RenderKit.DrawCentered(ctx, emoji, font, Color.White, new PointF(size / 2f, size / 2f)));
    }

    private static FontFamily ResolveEmojiFont()
    {
        foreach (var name in EmojiFontNames)
        {
            if (SystemFonts.TryGet(name, out var family))
            {
                return family;
            }
        }
        return RenderKit.ResolveFont();
    }

    private static Image<Rgba32>? LoadLucide(string name) => LucideCache.GetOrAdd(name, static key =>
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream($"deck-icon-{key}.png");
        return stream is null ? null : Image.Load<Rgba32>(stream);
    });

    /// <summary>Glyph-sized centered fit, matching renderDeckKeyBitmap.ts's drawCentered.</summary>
    private static void DrawCentered(Image<Rgba32> image, Image<Rgba32> src, float cx, float cy, int targetSize)
    {
        var scale = targetSize / (float)Math.Max(Math.Max(src.Width, src.Height), 1);
        var w = Math.Max(1, (int)MathF.Round(src.Width * scale));
        var h = Math.Max(1, (int)MathF.Round(src.Height * scale));
        using var resized = src.Clone(c => c.Resize(w, h));
        var x = (int)MathF.Round(cx - w / 2f);
        var y = (int)MathF.Round(cy - h / 2f);
        image.Mutate(ctx => ctx.DrawImage(resized, new Point(x, y), 1f));
    }

    /// <summary>Cover-fit whole-key-face fill, matching coverFitRect.ts.</summary>
    private static void DrawCover(Image<Rgba32> image, Image<Rgba32> src, int size)
    {
        var scale = Math.Max((float)size / src.Width, (float)size / src.Height);
        var w = Math.Max(1, (int)MathF.Round(src.Width * scale));
        var h = Math.Max(1, (int)MathF.Round(src.Height * scale));
        using var resized = src.Clone(c => c.Resize(w, h));
        var x = (int)MathF.Round((size - w) / 2f);
        var y = (int)MathF.Round((size - h) / 2f);
        image.Mutate(ctx => ctx.DrawImage(resized, new Point(x, y), 1f));
    }

    private readonly record struct ResolvedTitleStyle(bool Show, string Align, FontFamily Font, float SizePercent, bool Bold, bool Italic, bool Underline, string ColorHex);

    /// <summary>Defaults matching nexus-web's deckTitleStyle.ts resolveDeckTitleStyle: show defaults OFF, everything else has a concrete fallback.</summary>
    private static ResolvedTitleStyle ResolveTitleStyle(DeckTitleStyle? title) => new(
        Show: title?.Show ?? false,
        Align: title?.Align ?? "middle",
        Font: ResolveFontFamily(title?.Font),
        SizePercent: Math.Clamp(title?.Size ?? 16, 8, 30),
        Bold: title?.Bold ?? false,
        Italic: title?.Italic ?? false,
        Underline: title?.Underline ?? false,
        ColorHex: title?.Color ?? "#ffffff");

    private static FontFamily ResolveFontFamily(string? fontId) => fontId switch
    {
        "arial" => TryFamily("Arial"),
        "georgia" => TryFamily("Georgia"),
        "courierNew" => TryFamily("Courier New"),
        _ => RenderKit.ResolveFont(),
    };

    private static FontFamily TryFamily(string name) => SystemFonts.TryGet(name, out var family) ? family : RenderKit.ResolveFont();

    /// <summary>
    /// Truncates with an ellipsis and draws centered at the style's alignment.
    /// Unlike the web renderer, this skips the dark stroke halo behind the
    /// fill - a visual-polish gap, not a functional one.
    /// </summary>
    private static void PaintLabel(Image<Rgba32> image, string label, int size, ResolvedTitleStyle style)
    {
        var fontPx = Math.Max(8f, size * style.SizePercent / 100f);
        var fontStyle = (style.Bold, style.Italic) switch
        {
            (true, true) => FontStyle.BoldItalic,
            (true, false) => FontStyle.Bold,
            (false, true) => FontStyle.Italic,
            _ => FontStyle.Regular,
        };
        var font = style.Font.CreateFont(fontPx, fontStyle);
        var color = RenderKit.ParseColor(style.ColorHex, Color.White);

        var maxWidth = size * 0.92f;
        var text = label;
        while (text.Length > 1 && TextMeasurer.MeasureSize(text, new TextOptions(font)).Width > maxWidth)
        {
            text = text[..^1];
        }
        if (text != label && text.Length > 1)
        {
            text = text[..^1] + "…";
        }

        var pad = size * 0.06f;
        var y = style.Align switch
        {
            "top" => pad + fontPx / 2f,
            "bottom" => size - pad - fontPx / 2f,
            _ => size / 2f,
        };

        image.Mutate(ctx =>
        {
            RenderKit.DrawCentered(ctx, text, font, color, new PointF(size / 2f, y));
            if (style.Underline)
            {
                var textWidth = TextMeasurer.MeasureSize(text, new TextOptions(font)).Width;
                var thickness = Math.Max(1f, fontPx * 0.06f);
                var rect = new RectangleF(size / 2f - textWidth / 2f, y + fontPx * 0.42f - thickness / 2f, textWidth, thickness);
                ctx.Fill(color, rect);
            }
        });
    }
}
