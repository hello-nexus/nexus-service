using Nexus.Service.Relay;

namespace Nexus.Service.Tests;

/// <summary>
/// Unit coverage for the REST-over-relay path gate. Deny wins over allow; only
/// the panel / control REST surface is tunnelable; the socket / high-bandwidth
/// endpoints are rejected even though they sit under an allowed prefix.
/// </summary>
public class RelayHttpAllowlistTests
{
    [Theory]
    [InlineData("GET", "/panel/status")]
    [InlineData("GET", "/panel/phone/sessions")]
    [InlineData("POST", "/panel/host-name")]
    [InlineData("GET", "/cooling/status")]
    [InlineData("GET", "/devices/all")]
    [InlineData("GET", "/lighting/current")]
    [InlineData("POST", "/lighting/global-brightness")]
    [InlineData("GET", "/profiles")]
    [InlineData("GET", "/apps-api/installed")]
    [InlineData("GET", "/api/steam/status")]
    [InlineData("GET", "/ping")]
    [InlineData("GET", "/panel/status?foo=bar")] // query is ignored for matching
    [InlineData("POST", "/system/open-url")]      // deck open-url (relay-allowed)
    [InlineData("POST", "/system/power/lock")]    // deck power keys (relay-allowed)
    [InlineData("POST", "/system/power/sleep")]
    [InlineData("POST", "/system/power/shutdown")]
    [InlineData("POST", "/system/power/restart")]
    [InlineData("POST", "/system/power/logout")]
    [InlineData("GET", "/system/audio/devices")]
    public void Allows_PanelAndControlSurface(string method, string path)
        => Assert.True(RelayHttpAllowlist.IsAllowed(method, path));

    [Theory]
    [InlineData("GET", "/ws")]                       // multiplex socket upgrade
    [InlineData("GET", "/lighting/output")]          // 60fps binary stream
    [InlineData("GET", "/lighting/screen/monitors")] // screen-mirror sub-path
    [InlineData("POST", "/lighting/screen/effect")]  // screen-mirror sub-path
    [InlineData("GET", "/service/stop")]             // off-allowlist (localhost-only control)
    [InlineData("GET", "/pawnio")]                   // off-allowlist
    [InlineData("GET", "/")]                          // SPA shell
    [InlineData("GET", "/panelX")]                   // not a /panel segment boundary
    [InlineData("POST", "/system/open-path")]        // opens arbitrary local files - LAN-only
    [InlineData("POST", "/system/input/keys")]       // raw keystroke injection - desktop only
    [InlineData("POST", "/system/input/text")]
    [InlineData("POST", "/system/audio/play")]       // plays an arbitrary local file - desktop only
    [InlineData("GET", "/panel/phone/pair-qr")]      // mints pair tokens - desktop only
    [InlineData("POST", "/system/pick-path")]        // opens a native OS dialog on the host - desktop-only
    [InlineData("POST", "/devices/firmware/flash")]  // irreversible flash - brick risk over a lossy tunnel
    [InlineData("POST", "/devices/firmware/flash/")] // trailing slash still routes to the flash handler
    [InlineData("POST", "/Devices/Firmware/Flash")]  // deny is case-insensitive
    [InlineData("POST", "/devices/firmware/flash//")] // repeated trailing slash
    [InlineData("POST", "/system/pick-path/")]
    [InlineData("POST", "/devices\\firmware\\flash")]  // backslash separator must not slip the deny
    [InlineData("POST", "/devices/firmware/flash\\")]  // trailing backslash
    public void Rejects_SocketHighBandwidthAndOffAllowlist(string method, string path)
        => Assert.False(RelayHttpAllowlist.IsAllowed(method, path));

    [Theory]
    [InlineData("GET", "/devices/firmware/status")] // firmware READ stays tunnelable (only the flash is denied)
    [InlineData("GET", "/devices/all")]
    public void Allows_DeviceReadsAlongsideFirmwareFlashDeny(string method, string path)
        => Assert.True(RelayHttpAllowlist.IsAllowed(method, path));

    [Theory]
    [InlineData("CONNECT", "/panel/status")]
    [InlineData("TRACE", "/panel/status")]
    [InlineData("HEAD", "/panel/status")]
    public void Rejects_DisallowedMethods(string method, string path)
        => Assert.False(RelayHttpAllowlist.IsAllowed(method, path));

    [Fact]
    public void Rejects_EmptyOrRelativePath()
    {
        Assert.False(RelayHttpAllowlist.IsAllowed("GET", ""));
        Assert.False(RelayHttpAllowlist.IsAllowed("GET", "panel/status"));
        Assert.False(RelayHttpAllowlist.IsAllowed("GET", null));
    }
}
