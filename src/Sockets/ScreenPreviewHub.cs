namespace Nexus.Service.Sockets;

/// <summary>Live preview of the selected mirror monitor, in the lighting output frame layout.</summary>
public sealed class ScreenPreviewHub : WebSocketHub
{
    /// <summary>Close code sent when no capture source exists, so the page falls back to the output stream.</summary>
    public const int UnavailableCloseCode = 4503;

    /// <summary>Set by the lighting provider; null or false means no preview source can deliver frames.</summary>
    public volatile Func<bool>? IsAvailable;
}
