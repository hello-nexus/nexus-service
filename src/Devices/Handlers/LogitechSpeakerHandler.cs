using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LogitechAudio;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// A Logitech speaker whose output carries Logitech's speaker effect (the G560), listed
/// under Devices with a page of its own: G HUB's Acoustics and Surround sound. It is found
/// through that audio effect, not by a USB id, and named after the model ("Logitech G560").
/// Nexus Control has nothing to switch: the settings live in the effect, and the subwoofer
/// level goes over a HID++ channel G HUB shares too.
/// </summary>
public sealed class LogitechSpeakerHandler : IDeviceHandler
{
    public const string HandlerId = "logitech-speaker";

    private readonly LogitechSpeakerService? _speaker;

    // The speaker service runs on Windows only; elsewhere it is not registered.
    public LogitechSpeakerHandler(LogitechSpeakerService? speaker = null)
    {
        _speaker = speaker;
    }

    public string Id => HandlerId;
    public string Name => $"Logitech {_speaker?.PresentModel() ?? "G560"}";
    public string Category => "speaker";
    public IReadOnlyList<UsbId> Identifiers => Array.Empty<UsbId>();
    public bool SupportsNexusControl => false;

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) => _speaker?.PresentModel() is not null;

    public string GetFirmwareVersion() => "";
}
