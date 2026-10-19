using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.FanLcd;

/// <summary>
/// A screen family with content types beyond the shared ones. The streaming worker asks it for the
/// frame of such a type, and for how long to wait before the next one.
/// </summary>
public interface IFanLcdExtraContent
{
    bool Handles(string mode);

    /// <summary>The next 400x400 JPEG and the delay before the one after it; null JPEG when there is nothing to draw yet.</summary>
    (byte[]? Jpeg, int DelayMs) Render(string serial, LianLiWirelessScreenSettings settings);

    /// <summary>The screen left this content type or detached; drop what was held for it.</summary>
    void ReleaseExtra(string serial);
}
