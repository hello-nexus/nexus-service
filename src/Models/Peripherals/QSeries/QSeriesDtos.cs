namespace Nexus.Service.Models.Peripherals.QSeries;

public class QSeriesRotationParams : ApiResponse
{
    /// <summary>One of: Portrait, PortraitFlipped. Null on a POST body leaves
    /// the stored orientation unchanged; GET always returns the stored value.</summary>
    public string? Orientation { get; set; }
}

public class QSeriesDisplayParams : ApiResponse
{
    /// <summary>0-100. Null on a POST body leaves the stored value unchanged;
    /// GET always returns the stored value.</summary>
    public int? Brightness { get; set; }

    /// <summary>Null on a POST body leaves the stored value unchanged; GET
    /// always returns the stored value.</summary>
    public bool? ScreenOff { get; set; }

    /// <summary>Null on a POST body leaves the stored value unchanged; GET
    /// always returns the stored value.</summary>
    public bool? SleepWithHost { get; set; }
}
