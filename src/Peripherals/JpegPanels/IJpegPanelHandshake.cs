using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// Per-model control channel for a JPEG panel that needs more than a fixed init report:
/// a request/response handshake, or a keepalive the panel expects while streaming.
/// A model without one streams as soon as its handle opens.
///
/// Implementations are called under the hub's lock and own the device only for the duration
/// of the call. They are cheap by design: <see cref="BeforeFrame"/> runs on every frame.
/// </summary>
public interface IJpegPanelHandshake
{
    /// <summary>
    /// Runs once when a handle opens. Returning false makes the hub drop the handle rather
    /// than report a panel that will silently ignore frames.
    /// </summary>
    bool OnAttach(IHidDevice device, int reportLength);

    /// <summary>Best effort: the device is often already gone by the time this runs.</summary>
    void OnDetach(IHidDevice device, int reportLength);

    /// <summary>
    /// Runs before each frame. Returning false skips the frame, which is how a panel that
    /// has not finished booting stays unwritten without tearing the session down.
    /// </summary>
    bool BeforeFrame(IHidDevice device, int reportLength, long nowMs);
}

/// <summary>
/// A control channel whose panel takes a host-settable backlight. The setting is held here
/// rather than in the hub because it rides the same control packet the handshake already
/// builds, so a re-attach re-asserts it without the hub knowing the layout.
///
/// Called under the hub's lock, like the rest of <see cref="IJpegPanelHandshake"/>.
/// </summary>
public interface IJpegPanelBrightness
{
    /// <summary>Backlight the panel was last told, so a caller can skip a no-op write.</summary>
    int Brightness { get; }

    /// <summary>Records the backlight to send from now on. Does not touch hardware.</summary>
    void SetBrightness(int percent);

    /// <summary>Pushes the recorded backlight to an attached panel.</summary>
    bool ApplyBrightness(IHidDevice device, int reportLength);
}

/// <summary>
/// A control channel whose panel wants a settle period after its handle opens and a
/// periodic exchange while attached, independent of how often frames are pushed (a
/// static page is resent only every few seconds).
///
/// <see cref="Tick"/> is called under the hub's lock, like the rest of the handshake.
/// </summary>
public interface IJpegPanelSession
{
    /// <summary>How long the connection worker idles on the open handle before <see cref="IJpegPanelHandshake.OnAttach"/>.</summary>
    int SettleMs { get; }

    /// <summary>How often <see cref="Tick"/> should run while attached.</summary>
    int TickIntervalMs { get; }

    /// <summary>False when the handle is dead and the hub should drop it so the worker re-attaches.</summary>
    bool Tick(IHidDevice device, int reportLength, long nowMs);

    /// <summary>
    /// <see cref="IJpegPanelHandshake.OnAttach"/> for a session that pauses: <paramref name="gate"/>
    /// is the hub's lock, released across each pause, and <paramref name="cancel"/> ends the pauses on shutdown.
    /// </summary>
    bool Attach(IHidDevice device, int reportLength, object? gate, System.Threading.CancellationToken cancel);
}
