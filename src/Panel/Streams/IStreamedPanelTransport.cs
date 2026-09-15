using System;
using System.Collections.Generic;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// A display device a discovery implementation found attached and ready to
/// receive a panel stream.
/// </summary>
public sealed class StreamedPanelDeviceInfo
{
    public required string Serial { get; init; }
    public required StreamedPanelProfile Profile { get; init; }
}

/// <summary>
/// Per-device-family discovery for streamed panel displays. Implementations
/// poll their own bus (adb, serial, WinUSB, ...) and hand back attached
/// devices plus a transport factory; the coordinator owns sessions and
/// pacing and never touches the bus directly.
/// </summary>
public interface IStreamedPanelDiscovery
{
    /// <summary>DeviceControlGate handler id gating this family.</summary>
    string HandlerId { get; }

    IReadOnlyList<StreamedPanelDeviceInfo> Discover();

    IStreamedPanelTransport CreateTransport(StreamedPanelDeviceInfo info);
}

/// <summary>
/// Transport that may be able to dim its panel. Implementing it is not the capability test -
/// whether the attached model has a backlight command is, and an implementation that finds
/// it does not ignore the binding.
/// </summary>
public interface IBrightnessPanelTransport
{
    /// <summary>
    /// Supplies the panel record's backlight percent, or null when the record has none and
    /// the panel keeps whatever it powered up with. Polled while frames flow: unlike
    /// orientation this is a device command rather than a frame filter, so the transport
    /// writes only when the polled value changes. A caller may also force an immediate apply
    /// after a settings edit, even when no frames are currently flowing.
    /// </summary>
    void BindBrightness(Func<int?> source);

    /// <summary>Applies the currently bound backlight value immediately.</summary>
    bool ApplyBrightness();
}

/// <summary>
/// Byte sink for one streamed panel device: encoded H.264 Annex-B access
/// units in, glass on the other end. Implementations own device prep and
/// playback start so the coordinator stays device-agnostic.
/// </summary>
public interface IStreamedPanelTransport : IDisposable
{
    bool IsOpen { get; }
    string Serial { get; }

    /// <summary>Device prep + open the byte pipe. Throws on failure.</summary>
    void Open();

    /// <summary>Start device-side playback once the pipe is up. Throws on failure.</summary>
    void StartPlayer();

    /// <summary>
    /// Blocking write of one or more concatenated Annex-B access units (the
    /// paced writer batches per <see cref="StreamedPanelProfile.WriteBatchFrames"/>,
    /// so implementations must treat the payload as a byte stream, never as
    /// one framed unit). Must fail (throw) within a bounded time when the
    /// device stalls rather than block indefinitely; the paced writer treats
    /// any throw as transport loss.
    /// </summary>
    void Write(ReadOnlySpan<byte> annexBPayload);
}
