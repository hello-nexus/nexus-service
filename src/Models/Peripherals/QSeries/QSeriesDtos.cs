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

    /// <summary>Null on a POST body leaves the stored value unchanged; GET
    /// always returns the stored value.</summary>
    public bool? SleepWhenLocked { get; set; }
}

public class QSeriesLinkStatus : ApiResponse
{
    /// <summary>A Q-series panel devnode is enumerated on USB. True with
    /// <see cref="AdbOnline"/> false is the wedged-adbd case: the cable is
    /// fine, the daemon is not answering.</summary>
    public bool UsbPresent { get; set; }

    /// <summary>A Q-series panel is online via adb, so the panel surfaces are
    /// configurable.</summary>
    public bool AdbOnline { get; set; }

    /// <summary>How long the panel has been enumerated without an online adb
    /// link; 0 while online.</summary>
    public int OfflineSeconds { get; set; }

    /// <summary>Windows deferred the USB reset that recovers a wedged panel, so
    /// every further reset is refused until the host restarts. Nothing the
    /// service can do clears this.</summary>
    public bool HostRebootPending { get; set; }

    /// <summary>The panel is being held asleep because the desktop session is
    /// locked, so a screen-on request will not light it until the unlock.</summary>
    public bool SleepingForSessionLock { get; set; }

    /// <summary>The panel's adb serial when one is known, else null.</summary>
    public string? Serial { get; set; }
}
