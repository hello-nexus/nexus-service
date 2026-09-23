namespace Nexus.Service.Models.Peripherals.Y70;

public class Y70RotationParams : ApiResponse
{
    /// <summary>One of: Landscape, Portrait, LandscapeFlipped, PortraitFlipped.
    /// Null on a POST body leaves the stored orientation preference unchanged;
    /// GET always returns the stored value.</summary>
    public string? Orientation { get; set; }

    /// <summary>When true, the effective orientation applied to hardware is
    /// forced to PortraitFlipped regardless of Orientation. Null on a POST
    /// body leaves the stored flag unchanged; GET always returns the stored
    /// value.</summary>
    public bool? ForceOrientation { get; set; }
}

public class Y70CompatibilityRenderingParams : ApiResponse
{
    public bool Enabled { get; set; }
    /// <summary>GET only: the panel kiosk honours the setting (Windows, nexus-overlay).</summary>
    public bool Supported { get; set; }
}

public class Y70BrightnessResponse : ApiResponse
{
    public int Brightness { get; set; }
}

public class Y70BrightnessParams { public int Brightness { get; set; } }

public class Y70ToggleScreenResponse : ApiResponse
{
    public bool Toggle { get; set; }
}

public class Y70ToggleScreenParams { public bool Toggle { get; set; } }
