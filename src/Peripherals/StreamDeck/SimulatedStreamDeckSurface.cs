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
    /// <summary>
    /// One queued full-state snapshot per Poke transition, drained in order
    /// by ReadInput - mirrors a real HID input report per state change, so a
    /// press and release within one tick are both delivered instead of only
    /// the latest sampled state.
    /// </summary>
    private readonly Queue<bool[]> _pendingReports = new();
    private bool _connected = true;

    public StreamDeckModel Model { get; }
    public string Serial { get; }
    public string FirmwareVersion => "sim-1.0";
    public bool IsConnected => _connected;
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
    }

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

    public bool[]? ReadInput(int timeoutMs)
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
            _pendingReports.Enqueue((bool[])_pressed.Clone());
        }
    }

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
