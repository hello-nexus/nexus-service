namespace Nexus.Service.Peripherals.Tryx.Panorama;

public sealed class TryxPanoramaState
{
    public string Serial { get; set; } = "";
    public string AdbSerial { get; set; } = "";
    public string PortName { get; set; } = "";
    public string ModelName { get; set; } = "";
    public int ProductId { get; set; }
    public bool ScreenEnabled { get; set; } = true;
    public int Brightness { get; set; } = 100;
    /// <summary>Clockwise degrees (0, 90, 180, 270) Nexus turns the overlay and new uploads for a rotated mount.</summary>
    public int Rotation { get; set; }
    /// <summary>Bare filename on the device under /sdcard/pcMedia/, or a preset id.</summary>
    public string CurrentMedia { get; set; } = "";
    public bool CurrentMediaIsCustom { get; set; }
    public long LastConnectedMs { get; set; }
    public long LastFrameMs { get; set; }
    /// <summary>Small fan on the pump head, rpm, from the panel's last cooler_status reply; null until one arrives this connection.</summary>
    public int? CoolerFanRpm { get; set; }
    /// <summary>Pump, rpm, from the same reply; read-only, the pump is not software-controllable.</summary>
    public int? CoolerPumpRpm { get; set; }
    /// <summary>Fixed power (percent) the small fan runs at as far as Nexus knows: what it last wrote, else
    /// the panel's own FixMode power read at connect; null when unknown.</summary>
    public int? CoolerFanPower { get; set; }
}
