using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.StreamDeck;

/// <summary>
/// In-memory Stream Deck surface: no HID hardware needed. Used by Mac dev, the
/// unit-test suite, and the localhost-only <c>/streamdeck/dev/*</c> simulator
/// routes on boxes with no physical deck attached. Constructed only on demand
/// (a simulate route call), never at startup.
/// </summary>
public sealed class SimulatedStreamDeckSurface : IStreamDeckSurface
{
    private readonly object _io = new();
    private readonly byte[]?[] _keyImages;
    private readonly bool[] _pressed;
    private readonly bool[] _touchPressed;
    private readonly bool[] _dialDown;
    private readonly byte[]?[] _rings;
    private readonly (byte R, byte G, byte B)[] _centerLeds;
    /// <summary>
    /// One queued event per input transition, drained in order by ReadInput -
    /// mirrors a real HID input report per state change, so a press and
    /// release within one tick are both delivered instead of only the latest
    /// sampled state.
    /// </summary>
    private readonly Queue<StreamDeckInput> _pendingReports = new();
    private bool _connected = true;

    public StreamDeckModel Model { get; }
    public string Serial { get; }
    public string FirmwareVersion => "sim-1.0";
    public bool IsConnected => _connected;
    public bool IsReady => _connected;
    public int Brightness { get; private set; } = 100;
    public int ResetCount { get; private set; }
    /// <summary>Test hook: total successful SetKeyImage calls, so a test can prove a hash-unchanged push was skipped rather than merely re-rendering the same bytes.</summary>
    public int SetKeyImageCallCount { get; private set; }
    /// <summary>Test hook: key index of every successful SetKeyImage call, in call order, so a test can prove a multi-pass repaint's push ordering (e.g. every key's placeholder before any key's real content).</summary>
    public List<int> SetKeyImageOrder { get; } = new();

    public SimulatedStreamDeckSurface(StreamDeckModel model, string serial)
    {
        Model = model;
        Serial = serial;
        _keyImages = new byte[model.KeyCount][];
        _pressed = new bool[model.KeyCount];
        _touchPressed = new bool[model.TouchKeys];
        _dialDown = new bool[model.Encoders];
        _rings = new byte[model.Encoders][];
        _centerLeds = new (byte, byte, byte)[model.Encoders];
    }

    private const int MaxRecordedScreenRegions = 256;

    /// <summary>Test hook: the most recent successful screen region pushes, in call order.</summary>
    public List<(int X, int Y, int Width, int Height, byte[] Bytes)> ScreenRegions { get; } = new();

    /// <summary>Test hook: the last whole info-screen image, or null.</summary>
    public byte[]? InfoScreenImage { get; private set; }

    /// <summary>Test hook: the last FillScreen colour, or null.</summary>
    public (byte R, byte G, byte B)? ScreenFill { get; private set; }

    /// <summary>Test hook: the last FillKey colour per key index (touch keys at KeyCount and up).</summary>
    public Dictionary<int, (byte R, byte G, byte B)> KeyFills { get; } = new();

    /// <summary>Test hook: the last firmware sleep duration pushed, or null.</summary>
    public int? SleepDuration { get; private set; }

    public bool SetBrightness(int percent)
    {
        lock (_io)
        {
            if (!_connected)
            {
                return false;
            }
            Brightness = Math.Clamp(percent, 0, 100);
            return true;
        }
    }

    public bool SetKeyImage(int keyIndex, ReadOnlyMemory<byte> wireBytes)
    {
        lock (_io)
        {
            if (!_connected || keyIndex < 0 || keyIndex >= Model.KeyCount)
            {
                return false;
            }
            _keyImages[keyIndex] = wireBytes.ToArray();
            SetKeyImageCallCount++;
            SetKeyImageOrder.Add(keyIndex);
            return true;
        }
    }

    public bool ClearKey(int keyIndex)
    {
        lock (_io)
        {
            if (!_connected || keyIndex < 0 || keyIndex >= Model.KeyCount)
            {
                return false;
            }
            _keyImages[keyIndex] = null;
            return true;
        }
    }

    public bool Reset()
    {
        lock (_io)
        {
            if (!_connected)
            {
                return false;
            }
            Array.Clear(_keyImages);
            Brightness = 100;
            ResetCount++;
            return true;
        }
    }

    public StreamDeckInput? ReadInput(int timeoutMs)
    {
        lock (_io)
        {
            if (!_connected || _pendingReports.Count == 0)
            {
                return null;
            }
            return _pendingReports.Dequeue();
        }
    }

    /// <summary>Test/dev hook: sets one key's pressed state, queuing this transition's full snapshot for ReadInput to deliver in order.</summary>
    public void Poke(int keyIndex, bool pressed)
    {
        lock (_io)
        {
            if (keyIndex < 0 || keyIndex >= Model.KeyCount)
            {
                return;
            }
            _pressed[keyIndex] = pressed;
            _pendingReports.Enqueue(StreamDeckInput.ForKeys((bool[])_pressed.Clone(), (bool[])_touchPressed.Clone()));
        }
    }

    /// <summary>Test/dev hook: sets one Neo touch key's state, queuing a key report.</summary>
    public void PokeTouchKey(int index, bool pressed)
    {
        lock (_io)
        {
            if (index < 0 || index >= _touchPressed.Length)
            {
                return;
            }
            _touchPressed[index] = pressed;
            _pendingReports.Enqueue(StreamDeckInput.ForKeys((bool[])_pressed.Clone(), (bool[])_touchPressed.Clone()));
        }
    }

    /// <summary>Test/dev hook: sets one dial's pressed state, queuing a dial-press report with every dial's state.</summary>
    public void PokeDialPress(int dial, bool down)
    {
        lock (_io)
        {
            if (dial < 0 || dial >= _dialDown.Length)
            {
                return;
            }
            _dialDown[dial] = down;
            _pendingReports.Enqueue(StreamDeckInput.ForDialPress((bool[])_dialDown.Clone()));
        }
    }

    /// <summary>Test/dev hook: queues a rotation report with signed ticks on one dial (positive is clockwise).</summary>
    public void PokeRotate(int dial, int ticks)
    {
        lock (_io)
        {
            if (dial < 0 || dial >= _dialDown.Length || ticks == 0)
            {
                return;
            }
            var all = new int[_dialDown.Length];
            all[dial] = ticks;
            _pendingReports.Enqueue(StreamDeckInput.ForDialRotate(all));
        }
    }

    /// <summary>Test/dev hook: queues a touch report. x2/y2 matter only for a flick.</summary>
    public void PokeTouch(StreamDeckTouchKind kind, int x, int y, int x2 = 0, int y2 = 0)
    {
        lock (_io)
        {
            if (Model.Screen is null)
            {
                return;
            }
            _pendingReports.Enqueue(StreamDeckInput.ForTouch(kind, x, y, x2, y2));
        }
    }

    public bool SetScreenRegion(int x, int y, int width, int height, ReadOnlyMemory<byte> wireBytes)
    {
        lock (_io)
        {
            var screen = Model.Screen;
            if (!_connected || screen is null || wireBytes.IsEmpty
                || x < 0 || y < 0 || width <= 0 || height <= 0
                || x + width > screen.Width || y + height > screen.Height)
            {
                return false;
            }
            ScreenRegions.Add((x, y, width, height, wireBytes.ToArray()));
            if (ScreenRegions.Count > MaxRecordedScreenRegions)
            {
                ScreenRegions.RemoveAt(0);
            }
            return true;
        }
    }

    public bool SetInfoScreen(ReadOnlyMemory<byte> wireBytes)
    {
        lock (_io)
        {
            if (!_connected || Model.Screen?.Kind != StreamDeckScreenKind.InfoScreen || wireBytes.IsEmpty)
            {
                return false;
            }
            InfoScreenImage = wireBytes.ToArray();
            return true;
        }
    }

    public bool FillScreen(byte r, byte g, byte b)
    {
        lock (_io)
        {
            if (!_connected || !Model.HasExpandedInput)
            {
                return false;
            }
            ScreenFill = (r, g, b);
            return true;
        }
    }

    public bool FillKey(int keyIndex, byte r, byte g, byte b)
    {
        lock (_io)
        {
            if (!_connected || !Model.HasExpandedInput || keyIndex < 0 || keyIndex >= Model.KeyCount + Model.TouchKeys)
            {
                return false;
            }
            KeyFills[keyIndex] = (r, g, b);
            return true;
        }
    }

    public bool SetSleepDuration(int seconds)
    {
        lock (_io)
        {
            if (!_connected || !Model.HasExpandedInput)
            {
                return false;
            }
            SleepDuration = seconds;
            return true;
        }
    }

    public bool SetRing(int dial, ReadOnlySpan<byte> rgbTriplets)
    {
        lock (_io)
        {
            if (!_connected || Model.RingKind == StreamDeckRingKind.None || dial < 0 || dial >= Model.Encoders
                || rgbTriplets.Length < Model.EncoderRingLeds * 3)
            {
                return false;
            }
            _rings[dial] = rgbTriplets[..(Model.EncoderRingLeds * 3)].ToArray();
            return true;
        }
    }

    public bool SetCenterLed(int dial, byte r, byte g, byte b)
    {
        lock (_io)
        {
            if (!_connected || Model.RingKind != StreamDeckRingKind.StudioReport || dial < 0 || dial >= Model.Encoders)
            {
                return false;
            }
            _centerLeds[dial] = (r, g, b);
            return true;
        }
    }

    /// <summary>Test hook: the last ring colours pushed for a dial (visual order), or null.</summary>
    public byte[]? PeekRing(int dial) => dial >= 0 && dial < _rings.Length ? _rings[dial] : null;

    /// <summary>Test hook: the last centre LED colour for a dial.</summary>
    public (byte R, byte G, byte B) PeekCenterLed(int dial) => dial >= 0 && dial < _centerLeds.Length ? _centerLeds[dial] : default;

    /// <summary>Test/dev hook: the last bytes pushed to a key, or null if never set / cleared.</summary>
    public byte[]? PeekKeyImage(int keyIndex) =>
        keyIndex >= 0 && keyIndex < Model.KeyCount ? _keyImages[keyIndex] : null;

    /// <summary>Test hook: simulates an unplug.</summary>
    public void SimulateDisconnect()
    {
        lock (_io)
        {
            _connected = false;
        }
    }

    public void Dispose() { }
}
