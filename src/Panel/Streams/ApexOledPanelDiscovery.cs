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
    private readonly Dictionary<string, Tracked> _tracked = new(StringComparer.Ordinal);
    private readonly object _backoffLock = new();
    private readonly Func<long> _clock;
    private readonly bool? _enforceLength;

    /// <summary>A keyboard missing from passes for this long starts over when it returns; the coordinator's own detach linger.</summary>
    internal const long AbsentResetMs = 60_000;

    // One backoff per serial for the life of the discovery, shared with its transport; it is only ever reset in place.
    private sealed class Tracked
    {
        public readonly AttachBackoff Backoff;
        public long AbsentSinceMs = -1;

        public Tracked(Func<long> clock) { Backoff = new AttachBackoff(clock); }
    }

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
        var screens = _presence is not null && !_presence.UsbPresent(ApexOledProtocol.VendorId, ApexOledProtocol.ProductIds)
            ? new List<ApexOledScreen>()
            : ApexOledScreens.Find(_hid, _enforceLength);
        NotePresence(screens);
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

    // A single missed pass changes nothing; only an absence past the linger resets a keyboard's backoff.
    private void NotePresence(List<ApexOledScreen> screens)
    {
        var now = _clock();
        var present = new HashSet<string>(screens.ConvertAll(PanelSerial), StringComparer.Ordinal);
        lock (_backoffLock)
        {
            foreach (var (serial, tracked) in _tracked)
            {
                if (present.Contains(serial))
                {
                    tracked.AbsentSinceMs = -1;
                    continue;
                }
                if (tracked.AbsentSinceMs < 0)
                {
                    tracked.AbsentSinceMs = now;
                }
                else if (now - tracked.AbsentSinceMs >= AbsentResetMs)
                {
                    tracked.Backoff.Clear();
                }
            }
        }
    }

    private AttachBackoff BackoffFor(string serial)
    {
        lock (_backoffLock)
        {
            if (!_tracked.TryGetValue(serial, out var tracked))
            {
                _tracked[serial] = tracked = new Tracked(_clock);
            }
            return tracked.Backoff;
        }
    }
}
