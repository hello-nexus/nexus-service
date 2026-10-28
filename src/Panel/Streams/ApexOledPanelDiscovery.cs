using System;
using System.Collections.Generic;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Panel;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Peripherals.SteelSeries;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Presents each attached Apex Pro TKL's OLED as a streamed panel. The screen interface is plain
/// HID, so discovery just looks for it each pass; absence is a normal result.
///
/// The profile asks for <see cref="StreamCodec.RawBgra"/> at the screen's own pixel size and the
/// transport reduces each frame to one bit. A keyboard that refuses frames drops out of discovery
/// while a growing delay runs, so a broken screen is retried slowly instead of every pass.
/// </summary>
public sealed class ApexOledPanelDiscovery : IStreamedPanelDiscovery
{
    public const string Id = "steelseries-apex-oled";

    /// <summary>Widgets on a mono screen change slowly and unchanged frames are not sent, so a low rate is plenty and light on the keyboard.</summary>
    internal const int StreamFps = 10;

    private readonly IHidEnumerator _hid;
    private readonly HardwarePresence? _presence;
    private readonly LcdLockState? _lock;
    private readonly Dictionary<string, AttachBackoff> _backoffs = new(StringComparer.Ordinal);
    private readonly object _backoffLock = new();
    private readonly Func<long> _clock;
    private readonly bool? _enforceLength;
    private long _lastPassMs = long.MinValue;

    /// <summary>A gap this long between passes means the gate was off meanwhile (the coordinator polls far more often), so earlier failures no longer apply.</summary>
    private const long GateOffGapMs = 15_000;

    public ApexOledPanelDiscovery(IHidEnumerator hid, HardwarePresence? presence = null, LcdLockState? lockState = null, Func<long>? clock = null, bool? enforceLength = null)
    {
        _enforceLength = enforceLength;
        _hid = hid;
        _presence = presence;
        _lock = lockState;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public string HandlerId => Id;

    /// <summary>The panel serial for a screen: family, model and the key that tells two keyboards apart.</summary>
    public static string PanelSerial(ApexOledScreen screen) => $"{Id}:{screen.Model.ProductId:x4}:{screen.Key}";

    public IReadOnlyList<StreamedPanelDeviceInfo> Discover()
    {
        var now = _clock();
        lock (_backoffLock)
        {
            if (_lastPassMs != long.MinValue && now - _lastPassMs > GateOffGapMs)
            {
                _backoffs.Clear();
            }
            _lastPassMs = now;
        }
        if (_presence is not null && !_presence.UsbPresent(ApexOledProtocol.VendorId, ApexOledProtocol.ProductIds))
        {
            lock (_backoffLock) { _backoffs.Clear(); }
            return Array.Empty<StreamedPanelDeviceInfo>();
        }
        var screens = ApexOledScreens.Find(_hid, _enforceLength);
        lock (_backoffLock)
        {
            // A keyboard that left starts over when it returns.
            var present = new HashSet<string>(screens.ConvertAll(PanelSerial), StringComparer.Ordinal);
            foreach (var gone in new List<string>(_backoffs.Keys))
            {
                if (!present.Contains(gone))
                {
                    _backoffs.Remove(gone);
                }
            }
        }
        var panels = new List<StreamedPanelDeviceInfo>();
        foreach (var screen in screens)
        {
            if (BackoffFor(PanelSerial(screen)).DelayMs > 0)
            {
                continue;
            }
            panels.Add(new StreamedPanelDeviceInfo
            {
                Serial = PanelSerial(screen),
                Profile = new StreamedPanelProfile
                {
                    Kind = Id,
                    DisplayName = screen.Model.Name,
                    Surface = PanelSurfaces.LcdWide,
                    Family = Id,
                    CssWidth = ApexOledProtocol.Width,
                    CssHeight = ApexOledProtocol.Height,
                    Dpr = 1.0,
                    Fps = StreamFps,
                    Codec = StreamCodec.RawBgra,
                    MultiInstance = true,
                },
            });
        }
        return panels;
    }

    public IStreamedPanelTransport CreateTransport(StreamedPanelDeviceInfo info)
    {
        // Serial is "<id>:<product id hex>:<key>".
        var parts = info.Serial.Split(':', 3);
        var model = (parts.Length == 3 && int.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var pid)
            ? ApexOledProtocol.ModelFor(pid)
            : null) ?? throw new System.IO.IOException($"not an Apex OLED panel: {info.Serial}");
        return new ApexOledStreamTransport(_hid, model, info.Serial, _lock, backoff: BackoffFor(info.Serial), enforceLength: _enforceLength);
    }

    private AttachBackoff BackoffFor(string serial)
    {
        lock (_backoffLock)
        {
            if (!_backoffs.TryGetValue(serial, out var backoff))
            {
                _backoffs[serial] = backoff = new AttachBackoff(_clock);
            }
            return backoff;
        }
    }
}
