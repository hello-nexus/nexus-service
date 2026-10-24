using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// One bulk-pipe cooler LCD. Unlike the HID-JPEG family these have little in common past
/// "pixels ride a bulk endpoint" - one wants raw BGR888 and a HID commit, one negotiates
/// its own resolution, one encrypts its headers - so each brings its own driver rather
/// than a row in a table.
/// </summary>
public interface IBulkPanelDriver
{
    /// <summary>Device id, Nexus Control gate key, and streamed-panel profile kind.</summary>
    string HandlerId { get; }

    string Name { get; }

    int VendorId { get; }

    IReadOnlyList<int> ProductIds { get; }

    /// <summary>PanelSurfaces value for the record.</summary>
    string Surface { get; }

    int Fps { get; }

    /// <summary>Bulk OUT endpoint the panel takes frames on.</summary>
    byte WritePipeId { get; }

    /// <summary>Bulk IN endpoint, or 0 when the panel never answers.</summary>
    byte ReadPipeId { get; }

    /// <summary>True when the panel also needs its HID control channel open.</summary>
    bool NeedsHidChannel { get; }

    /// <summary>
    /// True when the driver reads the HID channel, so the handle must be opened for overlapped
    /// input: a plain handle ignores read timeouts on Windows and would block the caller.
    /// </summary>
    bool HidOpensForInput => false;

    /// <summary>A Nexus Panel stream started on this panel; for a driver that shares the device with another owner. <paramref name="stream"/> identifies the stream, so a late close cannot end a newer one.</summary>
    void StreamOpened(object stream)
    {
    }

    /// <summary>The Nexus Panel stream on this panel ended.</summary>
    void StreamClosed(object stream)
    {
    }

    /// <summary>The panel never shares a Nexus Panel record with another device's screen.</summary>
    bool MultiInstance => false;

    /// <summary>Writes any state the driver keeps in memory to settings; called on service shutdown, which skips the hub's disconnect.</summary>
    void PersistState()
    {
    }

    /// <summary>
    /// One such panel per PC, whose USB serial is not reliable early in boot: the stream is keyed
    /// on the handler id instead, and a record an earlier build stored under a serial is adopted.
    /// </summary>
    bool SingleUnitPerPc => false;

    /// <summary>
    /// Runs the opening handshake and reports the panel geometry to drive. Null means the
    /// panel is not usable, which the worker treats as "try again later" rather than a
    /// hard failure. A panel that reports its own resolution returns what it reported, not
    /// what a table guessed.
    /// </summary>
    (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid);

    /// <summary>Pushes one captured BGRA frame at the geometry <see cref="Connect"/> reported.</summary>
    bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra);

    /// <summary>Best effort; the device is often already gone.</summary>
    void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid);

    /// <summary>
    /// Idle time after which the stream re-sends the last frame, for a panel that drops back
    /// to its own screen when frames stop. 0 means the panel holds a frame on its own.
    /// </summary>
    int KeepaliveMs => 0;

    /// <summary>Re-sends the last frame <see cref="SendFrame"/> pushed. False means the link is gone.</summary>
    bool Resend(IBulkUsbPipe pipe, IHidDevice? hid) => true;

    /// <summary>Which half of the last failed push broke, for the rejection log; null when none or not tracked.</summary>
    string? LastFailure => null;

    /// <summary>
    /// Asks the connection worker to drop the session and reopen the panel. Returns the delay
    /// in ms to wait before the reopen, or -1 when the link is fine. Each true answer counts as
    /// one recovery attempt.
    /// </summary>
    int TakeReopenDelayMs() => -1;

    bool SupportsBrightness => false;

    /// <summary>Sets the backlight, 0-100.</summary>
    bool SetBrightness(IBulkUsbPipe pipe, IHidDevice? hid, int percent) => false;

    /// <summary>The glass can show a Windows virtual monitor.</summary>
    bool SupportsSecondaryMonitor => false;

    /// <summary>The glass reports touch on <see cref="ReadPipeId"/>; elsewhere that pipe carries command replies.</summary>
    bool HasTouch => false;

    /// <summary>The glass mounts either way up; <see cref="Connect"/> reports tall geometry when its record asks for portrait.</summary>
    bool SupportsPortrait => false;

    /// <summary>Physical px/inch of the glass, for panels whose surface estimate is far off; null keeps the estimate.</summary>
    double? Dpi => null;

    /// <summary>The geometry <see cref="Connect"/> negotiated no longer matches the panel's settings, so the handshake must run again.</summary>
    bool GeometryStale => false;
}
