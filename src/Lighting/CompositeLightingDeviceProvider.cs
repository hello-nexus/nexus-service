using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Aggregates the OpenRGB-backed lighting devices (motherboard, RAM, AIO, etc.) with NP50 + MiniHub hub devices behind a
/// single <see cref="ILightingDeviceProvider"/> so the /devices/lighting-devices/* routes and the React lighting page don't
/// have to know there's more than one source. Routes by id prefix: anything starting with <c>np50:</c> or <c>minihub:</c>
/// goes to the matching hub provider; everything else stays on the OpenRGB provider. Also the layout authority - applies
/// <see cref="CanvasGridLayout"/> to every device without a persisted layout and mirrors the result into engine frames so
/// running effects sample from the same rectangles the SPA shows. Mirrors <see cref="Cooling.CompositeFanControlProvider"/>.
/// </summary>
public sealed class CompositeLightingDeviceProvider : ILightingDeviceProvider
{
    private readonly ILightingDeviceProvider _openRgb;
    private readonly Np50LightingDeviceProvider _np50;
    private readonly MiniHubLightingDeviceProvider _miniHub;
    private readonly SmartHubLightingDeviceProvider _smartHub;
    private readonly CnvsLightingDeviceProvider _cnvs;
    private readonly QSeriesLightingDeviceProvider _qseries;
    private readonly KeebLightingDeviceProvider _keeb;
    private readonly LianLiLightingDeviceProvider _lianLi;
    private readonly Slv3LightingDeviceProvider _lianLiWireless;
    private readonly CorsairLinkLightingDeviceProvider _corsair;
    private readonly StrimerLightingDeviceProvider _strimer;
    private readonly Galahad2LightingDeviceProvider _galahad2;
    private readonly Nexus.Service.Lighting.Smart.SmartLightProvider _smart;
    private readonly IConfigStore _store;
    private readonly LightingEngine _engine;

    public CompositeLightingDeviceProvider(
        ILightingDeviceProvider openRgb,
        Np50LightingDeviceProvider np50,
        MiniHubLightingDeviceProvider miniHub,
        SmartHubLightingDeviceProvider smartHub,
        CnvsLightingDeviceProvider cnvs,
        QSeriesLightingDeviceProvider qseries,
        KeebLightingDeviceProvider keeb,
        LianLiLightingDeviceProvider lianLi,
        Slv3LightingDeviceProvider lianLiWireless,
        CorsairLinkLightingDeviceProvider corsair,
        StrimerLightingDeviceProvider strimer,
        Galahad2LightingDeviceProvider galahad2,
        Nexus.Service.Lighting.Smart.SmartLightProvider smart,
        IConfigStore store,
        LightingEngine engine)
    {
        _openRgb  = openRgb;
        _np50     = np50;
        _miniHub  = miniHub;
        _smartHub = smartHub;
        _cnvs     = cnvs;
        _qseries  = qseries;
        _keeb     = keeb;
        _lianLi   = lianLi;
        _lianLiWireless = lianLiWireless;
        _corsair  = corsair;
        _strimer  = strimer;
        _galahad2 = galahad2;
        _smart    = smart;
        _store    = store;
        _engine   = engine;
    }

    public bool IsConnected => _openRgb.IsConnected || _np50.IsConnected || _miniHub.IsConnected || _smartHub.IsConnected || _cnvs.IsConnected || _qseries.IsConnected || _keeb.IsConnected || _lianLi.IsConnected || _lianLiWireless.IsConnected || _corsair.IsConnected || _strimer.IsConnected || _galahad2.IsConnected || _smart.IsConnected;

    public GetLightingDevicesResponse GetAll()
    {
        var rgb = _openRgb.GetAll();

        // Filter out OpenRGB's NP50/MiniHub/CNVS entries when our own
        // providers are live. nexus-service now opens those COM ports
        // exclusively for hub control; OpenRGB's entries become zombies
        // the animation system can't push frames to.
        if (rgb.Devices.Count > 0)
        {
            if (_np50.IsConnected)
            {
                rgb.Devices.RemoveAll(d =>
                    d.Name.Contains("Nexus Portal NP50", StringComparison.OrdinalIgnoreCase) ||
                    d.Name.Contains("HYTE NP50", StringComparison.OrdinalIgnoreCase));
            }
            if (_miniHub.IsConnected)
            {
                rgb.Devices.RemoveAll(d =>
                    d.Name.Contains("MiniHub", StringComparison.OrdinalIgnoreCase) ||
                    d.Name.Contains("HYTE Mini", StringComparison.OrdinalIgnoreCase));
            }
            if (_smartHub.IsConnected)
            {
                rgb.Devices.RemoveAll(d =>
                    d.Name.Contains("Smart Hub", StringComparison.OrdinalIgnoreCase) ||
                    d.Name.Contains("SmartHub", StringComparison.OrdinalIgnoreCase));
            }
            if (_cnvs.IsConnected)
            {
                // OpenRGB exposes CNVS as "HYTE CNVS" (mousemat). Strip it so
                // the lighting page doesn't show two CNVS cards (zombie OpenRGB
                // entry + our live one).
                rgb.Devices.RemoveAll(d =>
                    d.Name.Contains("HYTE CNVS", StringComparison.OrdinalIgnoreCase) ||
                    d.Name.Contains("HYTE Mousemat", StringComparison.OrdinalIgnoreCase));
            }
            // Strip OpenRGB's inert Q-series zombie by the COM PORT the hub holds, not by
            // device name. OpenRGB reaches the HYTE coolers over serial and reports the port
            // as its location (StableId "openrgb-l-COM4"); matching that id survives OpenRGB
            // renaming the device, where a name substring match ("HYTE Q60" vs the actual
            // "HYTE THICC Q60") breaks.
            var qseriesZombieId = _qseries.OwnedOpenRgbDeviceId;
            if (qseriesZombieId is not null)
            {
                rgb.Devices.RemoveAll(d => string.Equals(d.Id, qseriesZombieId, StringComparison.OrdinalIgnoreCase));
            }
            if (_keeb.IsConnected)
            {
                // We disable the "HYTE Keeb TKL" detector in openrgb-headless so
                // OpenRGB never claims the board, but strip by name too so a stale
                // entry can never shadow our direct card.
                rgb.Devices.RemoveAll(d =>
                    d.Name.Contains("HYTE Keeb", StringComparison.OrdinalIgnoreCase) ||
                    d.Name.Contains("Keeb TKL", StringComparison.OrdinalIgnoreCase));
            }
            if (_lianLi.IsConnected)
            {
                // The "Lian Li Uni Hub - SL Infinity" detector is disabled in
                // openrgb-headless; strip by name too so a stale OpenRGB entry
                // (the "detected but not drivable" zombie) can't shadow our cards.
                rgb.Devices.RemoveAll(d =>
                    d.Name.Contains("Lian Li Uni Hub", StringComparison.OrdinalIgnoreCase));
            }
            if (_corsair.IsConnected)
            {
                // The iCUE Link System Hub detector is disabled in openrgb-headless;
                // strip by name too so a stale OpenRGB zombie can't shadow our cards.
                rgb.Devices.RemoveAll(d =>
                    d.Name.Contains("iCUE Link", StringComparison.OrdinalIgnoreCase));
            }
            if (_strimer.IsConnected)
            {
                rgb.Devices.RemoveAll(d =>
                    d.Name.Contains("Lian Li Strimer", StringComparison.OrdinalIgnoreCase));
            }
            if (_galahad2.IsConnected)
            {
                // OpenRGB names this device "Lian Li GAII Trinity".
                rgb.Devices.RemoveAll(d =>
                    d.Name.Contains("GAII", StringComparison.OrdinalIgnoreCase));
            }
        }

        var hub = _np50.GetAll();
        if (hub.Devices.Count > 0)
        {
            rgb.IsInit = rgb.IsInit || hub.IsInit;
            rgb.Devices.AddRange(hub.Devices);
        }
        var mini = _miniHub.GetAll();
        if (mini.Devices.Count > 0)
        {
            rgb.IsInit = rgb.IsInit || mini.IsInit;
            rgb.Devices.AddRange(mini.Devices);
        }
        var smart = _smartHub.GetAll();
        if (smart.Devices.Count > 0)
        {
            rgb.IsInit = rgb.IsInit || smart.IsInit;
            rgb.Devices.AddRange(smart.Devices);
        }
        var cnvs = _cnvs.GetAll();
        if (cnvs.Devices.Count > 0)
        {
            rgb.IsInit = rgb.IsInit || cnvs.IsInit;
            rgb.Devices.AddRange(cnvs.Devices);
        }
        var qseries = _qseries.GetAll();
        if (qseries.Devices.Count > 0)
        {
            rgb.IsInit = rgb.IsInit || qseries.IsInit;
            rgb.Devices.AddRange(qseries.Devices);
        }
        var keeb = _keeb.GetAll();
        if (keeb.Devices.Count > 0)
        {
            rgb.IsInit = rgb.IsInit || keeb.IsInit;
            rgb.Devices.AddRange(keeb.Devices);
        }
        var lianLi = _lianLi.GetAll();
        if (lianLi.Devices.Count > 0)
        {
            rgb.IsInit = rgb.IsInit || lianLi.IsInit;
            rgb.Devices.AddRange(lianLi.Devices);
        }
        var lianLiWireless = _lianLiWireless.GetAll();
        if (lianLiWireless.Devices.Count > 0)
        {
            rgb.IsInit = rgb.IsInit || lianLiWireless.IsInit;
            rgb.Devices.AddRange(lianLiWireless.Devices);
        }
        var corsair = _corsair.GetAll();
        if (corsair.Devices.Count > 0)
        {
            rgb.IsInit = rgb.IsInit || corsair.IsInit;
            rgb.Devices.AddRange(corsair.Devices);
        }
        var strimer = _strimer.GetAll();
        if (strimer.Devices.Count > 0)
        {
            rgb.IsInit = rgb.IsInit || strimer.IsInit;
            rgb.Devices.AddRange(strimer.Devices);
        }
        var galahad2 = _galahad2.GetAll();
        if (galahad2.Devices.Count > 0)
        {
            rgb.IsInit = rgb.IsInit || galahad2.IsInit;
            rgb.Devices.AddRange(galahad2.Devices);
        }
        var smartLights = _smart.GetAll();
        if (smartLights.Devices.Count > 0)
        {
            rgb.IsInit = rgb.IsInit || smartLights.IsInit;
            rgb.Devices.AddRange(smartLights.Devices);
        }

        // Non-partitionable cards (hub ports, smart lights, CNVS, Q-series)
        // are their own single-zone devices: the modal routing target is the
        // card itself and zone management stays hidden (ZoneCustomizable
        // keeps its default false). Partition-aware providers set DeviceId
        // and EnabledLedCount themselves; everything else resolves disables
        // here through the identity context, on the one settings snapshot
        // this call already loads.
        var settings = _store.Load();
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        foreach (var dev in rgb.Devices)
        {
            if (dev.DeviceId.Length == 0)
            {
                dev.DeviceId = dev.Id;
                dev.EnabledLedCount = Nexus.Service.Lighting.Zones.ZoneResolution.CountEnabled(
                    structure: null, zone: null, dev.Id, dev.LedCount, zoneHint: 0, settings);
            }
            dev.Controlled = uncontrolled.Count == 0 || !uncontrolled.Contains(dev.Id);
        }

        // Spread every device without a persisted layout across the grid. totalCount counts persisted devices too so the
        // slot for any one device is stable across calls (resetting one card doesn't shuffle the others). Must run
        // AFTER the CNVS / NP50 / MiniHub merges so newly-added hub devices also pick up a default slot.
        var layouts = settings.Lighting.DeviceLayouts;
        var total = rgb.Devices.Count;
        for (var i = 0; i < total; i++)
        {
            var dev = rgb.Devices[i];
            if (layouts.ContainsKey(dev.Id)) continue;
            var (x, y, w, h) = CanvasGridLayout.Slot(i, total);
            dev.CanvasX = x; dev.CanvasY = y; dev.CanvasW = w; dev.CanvasH = h;
        }

        // Mirror into engine frames so running effects sample from the same rectangles. Without this, RgbBridge's
        // existing-frame-wins rule would pin pre-grid coordinates in memory. O(N²), trivial for typical N (<30).
        foreach (var frame in _engine.Devices)
        {
            for (var i = 0; i < rgb.Devices.Count; i++)
            {
                var dev = rgb.Devices[i];
                if (dev.Id != frame.Id) continue;
                frame.X = dev.CanvasX; frame.Y = dev.CanvasY; frame.W = dev.CanvasW; frame.H = dev.CanvasH;
                break;
            }
        }
        return rgb;
    }

    public void SetDisabled(IReadOnlyList<string> ids)
    {
        // Per-id routing: split the ids and dispatch each batch to its owner.
        // Keeps each provider's "I own these ids" invariants intact.
        var rgbIds        = new List<string>(ids.Count);
        var np50Ids       = new List<string>(ids.Count);
        var miniIds       = new List<string>(ids.Count);
        var smartIds      = new List<string>(ids.Count);
        var cnvsIds       = new List<string>(ids.Count);
        var qseriesIds    = new List<string>(ids.Count);
        var keebIds       = new List<string>(ids.Count);
        var lianLiIds     = new List<string>(ids.Count);
        var lianLiWirelessIds = new List<string>(ids.Count);
        var corsairIds    = new List<string>(ids.Count);
        var strimerIds    = new List<string>(ids.Count);
        var galahad2Ids   = new List<string>(ids.Count);
        var smartLightIds = new List<string>(ids.Count);
        foreach (var id in ids)
        {
            if (IsNp50Id(id))           np50Ids.Add(id);
            else if (IsMiniHubId(id))   miniIds.Add(id);
            else if (IsSmartHubId(id))  smartIds.Add(id);
            else if (IsCnvsId(id))      cnvsIds.Add(id);
            else if (IsQSeriesId(id))   qseriesIds.Add(id);
            else if (IsKeebId(id))      keebIds.Add(id);
            else if (IsLianLiId(id))    lianLiIds.Add(id);
            else if (IsLianLiWirelessId(id)) lianLiWirelessIds.Add(id);
            else if (IsCorsairId(id))   corsairIds.Add(id);
            else if (IsStrimerId(id))   strimerIds.Add(id);
            else if (IsGalahad2Id(id))  galahad2Ids.Add(id);
            else if (_smart.Owns(id))   smartLightIds.Add(id);
            else                        rgbIds.Add(id);
        }
        if (rgbIds.Count > 0)        _openRgb.SetDisabled(rgbIds);
        if (np50Ids.Count > 0)       _np50.SetDisabled(np50Ids);
        if (miniIds.Count > 0)       _miniHub.SetDisabled(miniIds);
        if (smartIds.Count > 0)      _smartHub.SetDisabled(smartIds);
        if (cnvsIds.Count > 0)       _cnvs.SetDisabled(cnvsIds);
        if (qseriesIds.Count > 0)    _qseries.SetDisabled(qseriesIds);
        if (keebIds.Count > 0)       _keeb.SetDisabled(keebIds);
        if (lianLiIds.Count > 0)     _lianLi.SetDisabled(lianLiIds);
        if (lianLiWirelessIds.Count > 0) _lianLiWireless.SetDisabled(lianLiWirelessIds);
        if (corsairIds.Count > 0)    _corsair.SetDisabled(corsairIds);
        if (strimerIds.Count > 0)    _strimer.SetDisabled(strimerIds);
        if (galahad2Ids.Count > 0)   _galahad2.SetDisabled(galahad2Ids);
        if (smartLightIds.Count > 0) _smart.SetDisabled(smartLightIds);
    }

    public void SetPower(string id, bool on) { Pick(id).SetPower(id, on); }
    public void SetBrightness(string id, int brightness) { Pick(id).SetBrightness(id, brightness); }
    public void SetHue(string id, float hue) { Pick(id).SetHue(id, hue); }
    public void SetSaturation(string id, float saturation) { Pick(id).SetSaturation(id, saturation); }
    public void SetZoneLedCount(string id, int count) { Pick(id).SetZoneLedCount(id, count); }
    public void Identify(string id, int durationMs) { Pick(id).Identify(id, durationMs); }

    private ILightingDeviceProvider Pick(string id)
        => IsNp50Id(id)      ? _np50
        : IsMiniHubId(id)    ? _miniHub
        : IsSmartHubId(id)   ? _smartHub
        : IsCnvsId(id)       ? _cnvs
        : IsQSeriesId(id)    ? _qseries
        : IsKeebId(id)       ? _keeb
        : IsLianLiId(id)     ? _lianLi
        : IsLianLiWirelessId(id) ? _lianLiWireless
        : IsCorsairId(id)    ? _corsair
        : IsStrimerId(id)    ? _strimer
        : IsGalahad2Id(id)   ? _galahad2
        : _smart.Owns(id)    ? _smart
        : _openRgb;

    private static bool IsNp50Id(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("np50:", StringComparison.Ordinal);

    private static bool IsMiniHubId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("minihub:", StringComparison.Ordinal);

    private static bool IsSmartHubId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("smarthub:", StringComparison.Ordinal);

    private static bool IsCnvsId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("cnvs:", StringComparison.Ordinal);

    private static bool IsQSeriesId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("qseries:", StringComparison.Ordinal);

    private static bool IsKeebId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("keeb:", StringComparison.Ordinal);

    private static bool IsLianLiId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("lianli:", StringComparison.Ordinal);

    private static bool IsLianLiWirelessId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("lianli-wireless:", StringComparison.Ordinal);

    private static bool IsCorsairId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("corsair:", StringComparison.Ordinal);

    private static bool IsStrimerId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("strimer:", StringComparison.Ordinal);

    private static bool IsGalahad2Id(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("lianli-aio:", StringComparison.Ordinal);
}
