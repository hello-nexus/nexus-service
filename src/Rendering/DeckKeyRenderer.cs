using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Nexus.Service.Activity;
using Nexus.Service.Deck;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Serialization;
using SkiaSharp;

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

    private static readonly ConcurrentDictionary<string, SKBitmap?> LucideCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string Name, int Size), SKBitmap?> ScaledLucideCache = new();

    /// <summary>Bounds the per-size glyph cache; a clear drops at most a few MB that the next renders rebuild.</summary>
    private const int ScaledLucideCapacity = 512;

    private const int CacheCapacity = 256;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, DeckKeyRender> _cache = new(StringComparer.Ordinal);
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
    private static readonly SKColor SelectedAccent = new(0x4d, 0xa3, 0xff);

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
    public byte[]? Render(DeckSlot slot, bool isToggleOn, StreamDeckModel model, int orientation, bool selected = false) =>
        RenderWithPreview(slot, isToggleOn, model, orientation, selected).Wire;

    /// <summary>
    /// Same as <see cref="Render"/>, plus the upright JPEG of the same face
    /// (no orientation or model wire transform) for the streamdeckTiles
    /// editor preview - the wire bytes are already rotated/mirrored for the
    /// panel, so decoding them back would show the editor a transformed key.
    /// </summary>
    public DeckKeyRender RenderWithPreview(DeckSlot slot, bool isToggleOn, StreamDeckModel model, int orientation, bool selected = false)
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
        var render = new DeckKeyRender(bytes, bytes is null ? null : RenderKit.EncodeJpeg(image));
        if (bytes is not null && !transient)
        {
            lock (_cacheLock)
            {
                Insert(cacheKey, render);
            }
        }
        return render;
    }

    /// <summary>
    /// A square icon glyph on a transparent canvas for the dial screens: an
    /// uploaded image cover-fit, an emoji, or the named lucide glyph (also the
    /// fallback for an app icon, which the screens do not resolve). Null when
    /// nothing renders. Caller disposes.
    /// </summary>
    public SKBitmap? RenderIconGlyph(DeckIcon? icon, string fallbackLucide, int size)
    {
        if (size <= 0)
        {
            return null;
        }
        var canvas = RenderKit.NewImage(size, size);
        if (icon is { Kind: "image" } && _imageStore.TryLoad(icon.Value) is { } loaded && RenderKit.Decode(loaded.Bytes) is { } source)
        {
            using (source)
            {
                DrawCover(canvas, source, size);
            }
            return canvas;
        }
        if (icon is { Kind: "emoji" })
        {
            PaintEmoji(canvas, icon.Value, size, size);
            return canvas;
        }
        var lucide = LoadLucide(icon is { Kind: "lucide" } ? icon.Value : fallbackLucide, size);
        if (lucide is null)
        {
            canvas.Dispose();
            return null;
        }
        DrawGlyph(canvas, lucide, size / 2f, size / 2f);
        return canvas;
    }

    private void Touch(string key)
    {
        _cacheLru.Remove(key);
        _cacheLru.AddLast(key);
    }

    private void Insert(string key, DeckKeyRender render)
    {
        _cache[key] = render;
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

    private readonly record struct DisplaySlot(DeckIcon? Icon, string? Label, string ColorHex, bool ExplicitColor, DeckTitleStyle? Title, bool IsFolder, bool IsBlankOff, DeckAction? EffectiveAction);

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

        var explicitColor = colorHex is not null;
        if (!isBlankOff && colorHex is null)
        {
            colorHex = DeckIconDefaults.CategoryColorHex(isFolder ? DeckCategory.Folder : DeckIconDefaults.Category(effectiveAction));
        }

        return new DisplaySlot(icon, slot.Label, isBlankOff ? "#000000" : colorHex!, explicitColor, slot.Title, isFolder, isBlankOff, effectiveAction);
    }

    private SKBitmap RenderImage(DisplaySlot display, int size, ref bool transient, bool selected = false)
    {
        var image = RenderKit.NewImage(size, size);
        var shouldPaintIcon = !display.IsBlankOff && (display.EffectiveAction is not null || display.IsFolder || display.Icon is not null);
        // Face precedence: a custom image, else the app icon, else PaintIcon's
        // emoji / lucide glyph. An image or app icon is the key face (as
        // DeckGrid.tsx faceFills): no accent behind it unless the slot has its
        // own color.
        SKBitmap? customImage = null;
        SKBitmap? appIcon = null;
        var iconPending = false;
        if (shouldPaintIcon && display.Icon is { Kind: "image" } && _imageStore.TryLoad(display.Icon.Value) is { } loaded)
        {
            customImage = RenderKit.Decode(loaded.Bytes);
        }
        else if (shouldPaintIcon && display.Icon is not { Kind: "emoji" })
        {
            appIcon = LoadAppIcon(display, ref transient, out iconPending);
        }
        var iconOnBlack = (customImage is not null || appIcon is not null) && !display.ExplicitColor;
        var background = iconOnBlack ? SKColors.Black : RenderKit.ParseColor(display.ColorHex, SKColors.Black);
        // Selected: brighten a real accent; an icon on black keeps the ring
        // only (RecentAppsGrid.tsx .selected over a transparent face), so no
        // grey square shows through the icon's transparent letterbox.
        if (selected && !iconOnBlack)
        {
            background = Brighten(background, 0.25f);
        }
        image.Erase(background);

        if (display.IsBlankOff)
        {
            return image;
        }

        if (customImage is not null)
        {
            using (customImage)
            {
                DrawCover(image, customImage, size);
            }
        }
        else if (appIcon is not null)
        {
            using (appIcon)
            {
                DrawAppIcon(image, appIcon, size);
            }
        }
        else if (iconPending)
        {
            // Process icon still resolving: blank face, never cached.
            transient = true;
        }
        else if (shouldPaintIcon)
        {
            PaintIcon(image, display, size);
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
            var rect = SKRect.Create(ringWidth / 2f, ringWidth / 2f, size - ringWidth, size - ringWidth);
            using var canvas = new SKCanvas(image);
            using var paint = RenderKit.Stroke(SelectedAccent, ringWidth);
            canvas.DrawRect(rect, paint);
        }
        return image;
    }

    /// <summary>Lerps each channel toward white by amount (0..1), for the Recent Apps focused-key fill.</summary>
    private static SKColor Brighten(SKColor color, float amount)
    {
        byte Lerp(byte c) => (byte)MathF.Round(c + (255 - c) * amount);
        return new SKColor(Lerp(color.Red), Lerp(color.Green), Lerp(color.Blue), color.Alpha);
    }

    private void PaintIcon(SKBitmap image, DisplaySlot display, int size)
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

        var name = icon is { Kind: "lucide" } ? icon.Value : DeckIconDefaults.AutoIconName(action, display.IsFolder);
        var lucide = LoadLucide(name, target);
        if (lucide is not null)
        {
            DrawGlyph(image, lucide, cx, cy);
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
        action is { Type: "openFile" } && action.Path is not null && IsExecutablePath(action.Path) ? action.Path : null;

    // Linux executables carry no extension (a Recent Apps entry's /proc/<pid>/exe target); the icon provider keys on the basename.
    private static bool IsExecutablePath(string path) =>
        ExecutablePathRegex.IsMatch(path) || (OperatingSystem.IsLinux() && !Path.HasExtension(path) && File.Exists(path));

    private static void PaintEmoji(SKBitmap image, string emoji, int size, int target)
    {
        using var font = RenderKit.CreateFont(ResolveEmojiFont(), target * 0.85f);
        using var canvas = new SKCanvas(image);
        RenderKit.DrawCentered(canvas, emoji, font, SKColors.White, new SKPoint(size / 2f, size / 2f));
    }

    private static SKTypeface? _emojiFont;

    private static SKTypeface ResolveEmojiFont()
    {
        if (_emojiFont is { } cached)
        {
            return cached;
        }
        foreach (var name in EmojiFontNames)
        {
            if (RenderKit.TryFamily(name) is { } family)
            {
                return _emojiFont = family;
            }
        }
        return _emojiFont = RenderKit.ResolveFont();
    }

    /// <summary>The named glyph contain-fit into size x size, resized once per size and shared read-only.</summary>
    private static SKBitmap? LoadLucide(string name, int size)
    {
        if (ScaledLucideCache.TryGetValue((name, size), out var cached))
        {
            return cached;
        }
        SKBitmap? scaled = null;
        if (LoadLucide(name) is { } source)
        {
            var scale = size / (float)Math.Max(Math.Max(source.Width, source.Height), 1);
            scaled = RenderKit.Resize(source, Math.Max(1, (int)MathF.Round(source.Width * scale)), Math.Max(1, (int)MathF.Round(source.Height * scale)));
            scaled.SetImmutable();
        }
        if (ScaledLucideCache.Count >= ScaledLucideCapacity)
        {
            // Never disposed here: another render may still be drawing an evicted bitmap.
            ScaledLucideCache.Clear();
        }
        return ScaledLucideCache.GetOrAdd((name, size), scaled);
    }

    private static void DrawGlyph(SKBitmap image, SKBitmap glyph, float cx, float cy)
    {
        using var canvas = new SKCanvas(image);
        RenderKit.DrawImage(canvas, glyph, (int)MathF.Round(cx - glyph.Width / 2f), (int)MathF.Round(cy - glyph.Height / 2f));
    }

    private static SKBitmap? LoadLucide(string name) => LucideCache.GetOrAdd(name, static key =>
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream($"deck-icon-{key}.png");
        return stream is null ? null : RenderKit.DecodeShared(stream);
    });

    /// <summary>
    /// The slot's app icon (shortcut, then the exe's process icon), or null
    /// when the slot has none or it is not available. An empty shortcut icon
    /// is what the Windows helper proxy returns while no helper is connected
    /// (boot, before the user session exists), indistinguishable from a real
    /// miss, so a miss without an exe fallback marks the render transient.
    /// pending is true when the process icon is still being extracted (one
    /// provider call per render; the caller leaves the face blank, uncached).
    /// </summary>
    private SKBitmap? LoadAppIcon(DisplaySlot display, ref bool transient, out bool pending)
    {
        pending = false;
        var action = display.EffectiveAction;
        var appId = ResolveAppId(display.Icon, action);
        if (appId is null)
        {
            return null;
        }
        var appIcon = _shortcuts.GetIcon(appId);
        if (appIcon.Length > 0)
        {
            return RenderKit.Decode(appIcon);
        }
        var exePath = ResolveExePath(action);
        if (exePath is null)
        {
            transient = true;
            return null;
        }
        var processIcon = _processIcons.GetIcon(exePath);
        if (processIcon is null)
        {
            pending = true;
            return null;
        }
        return processIcon.Length > 0 ? RenderKit.Decode(processIcon) : null;
    }

    /// <summary>Alpha below this is the icon's margin or drop shadow (macOS icons keep ~9% clear around the rounded square, shadow alpha peaks near 32), not artwork.</summary>
    private const byte AppIconOpaqueAlpha = 64;

    /// <summary>Artwork size on the key: the Deck widget's look (a macOS icon's rounded square inside its own margin), applied uniformly whatever margin the icon ships with.</summary>
    private const float AppIconFraction = 0.82f;

    /// <summary>
    /// Draws the icon's artwork at AppIconFraction of the key: the opaque
    /// bounding box is cropped out first so a macOS icon's built-in margin
    /// and a Windows icon's edge-to-edge art end up the same size, then
    /// contain-fit centered on the black key.
    /// </summary>
    private static void DrawAppIcon(SKBitmap image, SKBitmap src, int size)
    {
        var target = Math.Max(1, (int)MathF.Round(size * AppIconFraction));
        int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
        var pixels = src.GetPixelSpan();
        for (var y = 0; y < src.Height; y++)
        {
            var row = pixels.Slice(y * src.RowBytes, src.Width * 4);
            for (var x = 0; x < src.Width; x++)
            {
                if (row[x * 4 + 3] < AppIconOpaqueAlpha)
                {
                    continue;
                }
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0)
        {
            return;
        }
        var box = SKRectI.Create(minX, minY, maxX - minX + 1, maxY - minY + 1);
        if (box.Width == src.Width && box.Height == src.Height)
        {
            DrawCentered(image, src, size / 2f, size / 2f, target);
            return;
        }
        using var cropped = RenderKit.Crop(src, box);
        DrawCentered(image, cropped, size / 2f, size / 2f, target);
    }

    /// <summary>Glyph-sized centered fit, matching renderDeckKeyBitmap.ts's drawCentered.</summary>
    private static void DrawCentered(SKBitmap image, SKBitmap src, float cx, float cy, int targetSize)
    {
        var scale = targetSize / (float)Math.Max(Math.Max(src.Width, src.Height), 1);
        var w = Math.Max(1, (int)MathF.Round(src.Width * scale));
        var h = Math.Max(1, (int)MathF.Round(src.Height * scale));
        using var resized = RenderKit.Resize(src, w, h);
        var x = (int)MathF.Round(cx - w / 2f);
        var y = (int)MathF.Round(cy - h / 2f);
        using var canvas = new SKCanvas(image);
        RenderKit.DrawImage(canvas, resized, x, y);
    }

    /// <summary>Cover-fit whole-key-face fill, matching coverFitRect.ts.</summary>
    private static void DrawCover(SKBitmap image, SKBitmap src, int size)
    {
        var scale = Math.Max((float)size / src.Width, (float)size / src.Height);
        var w = Math.Max(1, (int)MathF.Round(src.Width * scale));
        var h = Math.Max(1, (int)MathF.Round(src.Height * scale));
        using var resized = RenderKit.Resize(src, w, h);
        var x = (int)MathF.Round((size - w) / 2f);
        var y = (int)MathF.Round((size - h) / 2f);
        using var canvas = new SKCanvas(image);
        RenderKit.DrawImage(canvas, resized, x, y);
    }

    private readonly record struct ResolvedTitleStyle(bool Show, string Align, SKTypeface Font, float SizePercent, bool Bold, bool Italic, bool Underline, string ColorHex);

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

    private static SKTypeface ResolveFontFamily(string? fontId) => fontId switch
    {
        "arial" => TryFamily("Arial"),
        "georgia" => TryFamily("Georgia"),
        "courierNew" => TryFamily("Courier New"),
        _ => RenderKit.ResolveFont(),
    };

    private static SKTypeface TryFamily(string name) => RenderKit.TryFamily(name) ?? RenderKit.ResolveFont();

    /// <summary>
    /// Truncates with an ellipsis and draws centered at the style's alignment.
    /// Unlike the web renderer, this skips the dark stroke halo behind the
    /// fill - a visual-polish gap, not a functional one.
    /// </summary>
    private static void PaintLabel(SKBitmap image, string label, int size, ResolvedTitleStyle style)
    {
        var fontPx = Math.Max(8f, size * style.SizePercent / 100f);
        using var font = RenderKit.CreateFont(style.Font, fontPx, style.Bold, style.Italic);
        var color = RenderKit.ParseColor(style.ColorHex, SKColors.White);

        var maxWidth = size * 0.92f;
        var text = label;
        while (text.Length > 1 && RenderKit.MeasureWidth(text, font) > maxWidth)
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

        using var canvas = new SKCanvas(image);
        RenderKit.DrawCentered(canvas, text, font, color, new SKPoint(size / 2f, y));
        if (style.Underline)
        {
            var textWidth = RenderKit.MeasureWidth(text, font);
            var thickness = Math.Max(1f, fontPx * 0.06f);
            RenderKit.FillRect(canvas, color, SKRect.Create(size / 2f - textWidth / 2f, y + fontPx * 0.42f - thickness / 2f, textWidth, thickness));
        }
    }
}

/// <summary>One rendered key face: the model's wire bytes for the panel and the upright JPEG preview for the editor. Both null when the encode failed.</summary>
public sealed record DeckKeyRender(byte[]? Wire, byte[]? PreviewJpeg);
