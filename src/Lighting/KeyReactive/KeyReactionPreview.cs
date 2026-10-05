using System;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.KeyReactive;

/// <summary>
/// Renders a short scripted typing loop through the same renderer the engine
/// uses, on a board's real layout, over a slow hue drift standing in for the
/// running effect. Deterministic: the same config always yields the same frames.
/// </summary>
public static class KeyReactionPreview
{
    public const int Fps = 30;
    private const int LoopMs = 3200;
    private const float BaseLevel = 0.55f;

    private static readonly (string Key, int AtMs)[] Script =
    {
        ("N", 150), ("E", 400), ("X", 650), ("U", 900), ("S", 1150), ("Space", 1600), ("Enter", 2050),
    };

    public static KeyReactivePreviewResponse Render(KeyboardGeometry geo, KeyReaction cfg)
    {
        var clean = KeyReactionCatalog.Sanitize(cfg);
        var renderer = new KeyReactionRenderer(seed: 1);
        var n = geo.LedCount;
        var frameCount = LoopMs * Fps / 1000;
        var frames = new byte[frameCount * n * 3];
        var leds = new int[Script.Length];
        for (var i = 0; i < Script.Length; i++)
        {
            var led = geo.Resolve(Script[i].Key);
            leds[i] = led >= 0 ? led : Fallback(geo, i);
        }

        var next = 0;
        for (var f = 0; f < frameCount; f++)
        {
            var now = f * 1000.0 / Fps;
            while (next < Script.Length && Script[next].AtMs <= now)
            {
                renderer.Press(geo, leds[next], clean, Script[next].AtMs);
                next++;
            }
            var frame = frames.AsSpan(f * n * 3, n * 3);
            PaintBase(geo, frame, now);
            renderer.Render(geo, clean, now);
            renderer.Composite(frame, n, clean.Background);
        }

        var response = new KeyReactivePreviewResponse
        {
            Width = geo.MaxX - geo.MinX,
            Height = geo.MaxY - geo.MinY,
            Fps = Fps,
            FrameCount = frameCount,
            Frames = Convert.ToBase64String(frames),
        };
        for (var i = 0; i < n; i++)
        {
            response.X.Add(geo.X[i] - geo.MinX);
            response.Y.Add(geo.Y[i] - geo.MinY);
        }
        return response;
    }

    private static void PaintBase(KeyboardGeometry geo, Span<byte> frame, double nowMs)
    {
        for (var i = 0; i < geo.LedCount; i++)
        {
            if (!geo.IsActive(i)) continue;
            var (r, g, b) = KeyReactionRenderer.Hsv((float)(geo.X[i] * 14f + nowMs * 0.05), 0.85f, BaseLevel);
            frame[i * 3] = (byte)(r * 255f);
            frame[i * 3 + 1] = (byte)(g * 255f);
            frame[i * 3 + 2] = (byte)(b * 255f);
        }
    }

    // Spread unmatched script keys across the board so the loop still shows several presses.
    private static int Fallback(KeyboardGeometry geo, int step)
    {
        for (var probe = 0; probe < geo.LedCount; probe++)
        {
            var led = (step * 37 + probe) % geo.LedCount;
            if (geo.IsActive(led)) return led;
        }
        return 0;
    }
}
