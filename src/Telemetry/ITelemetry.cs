namespace Nexus.Service.Telemetry;

/// <summary>
/// Fire-and-forget product telemetry. Inject this anywhere and emit one
/// self-explanatory line:
///
///     _telemetry.Capture(TelemetryEvents.DeviceConnected, ("kind", "np50"));
///
/// Capture() never blocks and never throws; events are dropped when the user
/// has opted out of anonymous data. The batched send (to nexus-api) runs
/// off-thread in <see cref="TelemetryFlushService"/>. Swap the destination by
/// adding an <see cref="ITelemetrySink"/> - call sites don't change.
/// </summary>
public interface ITelemetry
{
    /// <summary>Record one event with optional flat properties.</summary>
    /// <param name="event">A name from <see cref="TelemetryEvents"/> - never a raw literal.</param>
    /// <param name="properties">Flat key/value pairs: string, bool, number, or string[] values.</param>
    void Capture(string @event, params (string Key, object? Value)[] properties);

    /// <summary>
    /// Attach persistent properties to the anonymous person (sent as a profile event) -
    /// e.g. the hardware/system profile. Persisted on the install id, not on a
    /// single event. Same opt-out applies.
    /// </summary>
    /// <param name="properties">Flat key/value pairs: string, bool, number, or string[] values.</param>
    void Identify(params (string Key, object? Value)[] properties);
}
