using Nexus.Service.Models.Panel;

namespace Nexus.Service.Panel;

/// <summary>Whether a display-bound record hosts a kiosk.</summary>
public static class PanelHosting
{
    /// <summary>
    /// <see cref="PanelDeviceRecord.Hosted"/> when set, else <see cref="PanelDeviceRecord.Enabled"/> (null = on).
    /// A record with <c>Hosted</c> stores <c>Enabled=false</c>, so a build without this guard hosts no kiosk for it.
    /// </summary>
    public static bool IsOn(PanelDeviceRecord r) => r.Hosted ?? (r.Enabled != false);

    /// <summary>Turns hosting on or off through whichever field the record uses.</summary>
    public static void Set(PanelDeviceRecord r, bool on)
    {
        if (r.Hosted is not null) r.Hosted = on;
        else r.Enabled = on;
    }
}
