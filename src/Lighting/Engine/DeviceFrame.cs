using System;
using System.Threading;

namespace Nexus.Service.Lighting.Engine;

/// <summary>One LED's draft position in the LED map editor's unsaved layout.</summary>
public readonly struct PreviewLedPosition
{
    public int Index { get; init; }
    public float U { get; init; }
    public float V { get; init; }
    public bool Disabled { get; init; }
}

public sealed class DeviceFrame
{
    // Double buffered. The engine thread paints _back; every other thread reads
    // whatever _front points at, and only ever sees a whole finished frame.
    // The hub writers poll these frames from their own timers with no lock, so
    // a frame published in pieces is a frame they can catch half-written - that
    // is what showed up as flicker on the Keeb.
    private byte[] _back;
    private byte[] _front;
    // Scratch for PublishScaled's undimmed seed. Only ever touched by the
    // engine thread, which is the only caller.
    private byte[]? _scaleSeed;

    public DeviceFrame(int index, string id, int ledCount, float x = 0, float y = 0, float w = 120, float h = 30, int rotation = 0, int physicalIndex = -1, int zoneIndex = -1, int zoneOffset = 0)
    {
        Index = index;
        Id = id;
        LedCount = ledCount;
        X = x;
        Y = y;
        W = w;
        H = h;
        Rotation = rotation;
        PhysicalIndex = physicalIndex >= 0 ? physicalIndex : index;
        ZoneIndex = zoneIndex;
        ZoneOffset = zoneOffset;
        _back = new byte[ledCount * 3];
        _front = new byte[ledCount * 3];
    }

    public int Index { get; }
    public string Id { get; }
    public int LedCount { get; }
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    public int Rotation { get; set; }
    /// <summary>Mirrors the frame left to right about its centre before <see cref="Rotation"/> turns it.</summary>
    public bool Flip { get; set; }
    /// <summary>OpenRGB physical device index. For non-split devices equals <see cref="Index"/>. For motherboard zones, multiple DeviceFrames share the same physical index.</summary>
    public int PhysicalIndex { get; }
    /// <summary>Zone index within the physical OpenRGB device. -1 when the frame represents a whole non-zoned device.</summary>
    public int ZoneIndex { get; }
    /// <summary>LED offset within the physical device's LED array. 0 for non-zoned devices.</summary>
    public int ZoneOffset { get; }
    /// <summary>Per-LED normalised u coordinate in [0..1] when the hardware provides a key-matrix layout (keyboards). Null = fall back to linear interpolation along the rotation axis.</summary>
    public float[]? LedU { get; set; }
    /// <summary>Per-LED normalised v coordinate in [0..1] paired with <see cref="LedU"/>.</summary>
    public float[]? LedV { get; set; }
    /// <summary>Per-LED "off" flag. When true for index i, the render loop writes (0,0,0) to that LED regardless of the effect canvas. Populated from LED-map overrides with Disabled=true.</summary>
    public bool[]? LedDisabled { get; set; }
    /// <summary>LED indices that should be highlighted (overridden with white). Set by the LED map editor. Takes priority over TestPattern.</summary>
    public HashSet<int>? HighlightLeds { get; set; }
    /// <summary>When set, overrides the normal canvas sampling with a repeating directional sweep. Persists until explicitly cleared.</summary>
    public string? TestPattern { get; set; }
    /// <summary>TickCount64 tick recorded when TestPattern was last activated. Subtracted from the current tick to get elapsed time for phase computation, so the sweep starts at phase 0 on activation.</summary>
    public long TestPatternStartMs { get; set; }
    /// <summary>Transient LED count override from the LED map editor draft. Clamped to LedCount on read. Null = use LedCount.</summary>
    public int? PreviewLedCount { get; set; }
    /// <summary>Transient per-LED UV positions from the LED map editor draft. Parallel to preview LEDs by index. Null = use saved LedU/LedV.</summary>
    public PreviewLedPosition[]? PreviewLayout { get; set; }
    /// <summary>Semantic device class for effect routing. Null = no archetype, falls back to canvas sampling. Values: "keyboard", "mouse", "mousepad", "headset", "keypad", "chromalink".</summary>
    public string? Archetype { get; set; }

    public void SetLed(int i, byte r, byte g, byte b)
    {
        if (i < 0 || i >= LedCount)
        {
            return;
        }

        var off = i * 3;
        var back = _back;
        back[off] = r;
        back[off + 1] = g;
        back[off + 2] = b;
    }

    public void Fill(byte r, byte g, byte b)
    {
        var back = _back;
        for (int i = 0; i < back.Length; i += 3)
        { back[i] = r; back[i + 1] = g; back[i + 2] = b; }
    }

    /// <summary>
    /// Publishes the painted frame dimmed to <paramref name="level"/>, leaving
    /// the NEXT frame's seed undimmed. Used by the blackout release ramp, which
    /// scales the live frame rather than a captured one so the ramp ends on
    /// exactly what the effect is publishing.
    ///
    /// The restore at the end is the whole point: <see cref="Publish"/> seeds
    /// the back buffer from what it just published, so scaling in place would
    /// re-scale every LED the next paint pass does not rewrite, and that
    /// compounds to black over a ramp.
    /// </summary>
    public void PublishScaled(float level)
    {
        var painted = _back;
        if (_scaleSeed is null || _scaleSeed.Length != painted.Length)
        {
            _scaleSeed = new byte[painted.Length];
        }
        var seed = _scaleSeed;
        Array.Copy(painted, seed, painted.Length);
        for (int i = 0; i < painted.Length; i++)
        {
            painted[i] = (byte)(painted[i] * level);
        }
        Publish();
        Array.Copy(seed, _back, seed.Length);
    }

    public void Clear()
    {
        Array.Clear(_back, 0, _back.Length);
        Publish();
    }

    /// <summary>
    /// Put the painted buffer in front of readers, then seed the next frame from
    /// what was just published. The seed is what lets a pass write only some of
    /// the LEDs - the rest keep the value they were last published with, rather
    /// than the one from two frames ago.
    /// </summary>
    public void Publish()
    {
        var painted = _back;
        _back = _front;
        Volatile.Write(ref _front, painted);
        Array.Copy(painted, _back, painted.Length);
    }

    /// <summary>The last published frame. Safe to read from any thread.</summary>
    public ReadOnlySpan<byte> LedBytes => Volatile.Read(ref _front);
}
