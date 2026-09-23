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

    bool SupportsBrightness => false;

    /// <summary>Sets the backlight, 0-100.</summary>
    bool SetBrightness(IBulkUsbPipe pipe, IHidDevice? hid, int percent) => false;
}
