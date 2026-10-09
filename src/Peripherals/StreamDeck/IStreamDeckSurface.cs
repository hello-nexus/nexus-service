using System;

namespace Nexus.Service.Peripherals.StreamDeck;

/// <summary>
/// Hardware-agnostic contract for one physical (or simulated) Stream Deck.
/// <see cref="HidStreamDeckSurface"/> and <see cref="SimulatedStreamDeckSurface"/>
/// both implement this so <see cref="StreamDeckConnectionWorker"/> never branches
/// on transport.
/// </summary>
public interface IStreamDeckSurface : IDisposable
{
    StreamDeckModel Model { get; }
    string Serial { get; }
    string FirmwareVersion { get; }
    bool IsConnected { get; }

    /// <summary>True once the surface accepts commands; a connected surface can be briefly not ready after open.</summary>
    bool IsReady { get; }

    /// <summary>Consecutive refused writes not yet cleared by a success; the surface drops its handle at its own threshold.</summary>
    int ConsecutiveWriteFailures { get; }

    /// <summary>Sets display brightness, 0-100 (clamped).</summary>
    bool SetBrightness(int percent);

    /// <summary>
    /// Pushes already-encoded wire bytes (BMP for gen1) to one key. keyIndex is
    /// the canonical (user-facing, left-to-right/top-to-bottom) index; the
    /// surface applies the model's hardware remap internally.
    /// </summary>
    bool SetKeyImage(int keyIndex, ReadOnlyMemory<byte> wireBytes);

    /// <summary>Blanks one key. No-op (returns false) for models with no known blank image.</summary>
    bool ClearKey(int keyIndex);

    bool Reset();

    /// <summary>
    /// Pushes already-encoded (wire-transformed JPEG) bytes to a screen region
    /// given in logical, pre-rotation pixels. False when the model has no
    /// screen or the region is out of bounds.
    /// </summary>
    bool SetScreenRegion(int x, int y, int width, int height, ReadOnlyMemory<byte> wireBytes);

    /// <summary>Pushes a whole-screen image (Neo info screen, 0x0B). False on models without an info screen.</summary>
    bool SetInfoScreen(ReadOnlyMemory<byte> wireBytes);

    /// <summary>Fills the whole LCD with one colour (0x05).</summary>
    bool FillScreen(byte r, byte g, byte b);

    /// <summary>Fills one key, including the Neo touch-key backlights at indices KeyCount and KeyCount + 1 (0x06).</summary>
    bool FillKey(int keyIndex, byte r, byte g, byte b);

    /// <summary>Sets the firmware idle sleep duration in seconds; 0 disables it (0x0D).</summary>
    bool SetSleepDuration(int seconds);

    /// <summary>
    /// Lights a dial ring from RGB triplets, EncoderRingLeds of them in
    /// visual order from the ring's top. False on models without rings.
    /// </summary>
    bool SetRing(int dial, ReadOnlySpan<byte> rgbTriplets);

    /// <summary>Sets a dial's centre LED (Studio). False on models without one.</summary>
    bool SetCenterLed(int dial, byte r, byte g, byte b);

    /// <summary>
    /// Reads and decodes the next input report, honoring the model's key-index
    /// remap. Returns the typed event, or null on idle/timeout or an ignored
    /// report. Flips IsConnected false when the device is gone.
    /// </summary>
    StreamDeckInput? ReadInput(int timeoutMs);
}
