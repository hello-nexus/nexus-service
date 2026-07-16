#if WINDOWS
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Request to rotate the Y70 panel display. Orientation strings are the
/// Windows display-orientation set: "Landscape", "Portrait",
/// "LandscapeFlipped", "PortraitFlipped".
/// </summary>
public sealed class DisplayOrientationRequest
{
    public string Orientation { get; set; } = "";
    /// <summary>
    /// Stable display id to rotate (promoted-monitor panels). Empty =
    /// the Y70 path (find the panel by its DDC controller names).
    /// </summary>
    public string DisplayId { get; set; } = "";
    /// <summary>
    /// "#rrggbb" panel background colour for the pre-rotation cover, resolved
    /// service-side from <c>PanelDeviceRegistry</c> (the helper has no
    /// registry access). Empty = fall back to opaque black. Only consulted
    /// on the <see cref="DisplayId"/> path; the Y70 path never covers.
    /// </summary>
    public string CoverColorHex { get; set; } = "";
}

/// <summary>Helper reply: did the rotation apply, and why not if it did not.</summary>
public sealed class DisplayOrientationResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
}

/// <summary>
/// Service-side outbound facade for the <c>displayOrientation.*</c> RPCs.
/// Mirrors <see cref="BrightnessCommands"/>: the service runs as LocalSystem
/// in Session 0 and cannot drive <c>ChangeDisplaySettingsEx</c> against the
/// user's monitor, so the helper does the actual Win32 call in the active
/// console session.
/// </summary>
[SupportedOSPlatform("windows")]
public static class OrientationCommands
{
    public static Task<DisplayOrientationResult> SetAsync(
        HelperRegistry r, string orientation, CancellationToken ct = default)
        => SendAsync(r, new DisplayOrientationRequest { Orientation = orientation }, ct);

    public static Task<DisplayOrientationResult> SetForDisplayAsync(
        HelperRegistry r, string displayId, string orientation, string coverColorHex, CancellationToken ct = default)
        => SendAsync(r, new DisplayOrientationRequest { Orientation = orientation, DisplayId = displayId, CoverColorHex = coverColorHex }, ct);

    private static async Task<DisplayOrientationResult> SendAsync(
        HelperRegistry r, DisplayOrientationRequest request, CancellationToken ct)
    {
        var conn = r.GetAny();
        if (conn is null)
        {
            return new DisplayOrientationResult { Ok = false, Error = "no helper connected" };
        }
        var res = await conn.SendCommandAsync(
            "displayOrientation.set",
            request,
            AppJsonContext.Default.DisplayOrientationRequest,
            timeoutMs: 4000,
            ct: ct).ConfigureAwait(false);
        if (res is null || !res.Ok || res.Payload is null)
        {
            return new DisplayOrientationResult { Ok = false, Error = "helper rpc failed" };
        }
        try
        {
            return JsonSerializer.Deserialize(res.Payload.Value, AppJsonContext.Default.DisplayOrientationResult)
                ?? new DisplayOrientationResult { Ok = false, Error = "empty reply" };
        }
        catch
        {
            return new DisplayOrientationResult { Ok = false, Error = "malformed reply" };
        }
    }
}

/// <summary>
/// Helper-side handler. Runs in the user's interactive session, so
/// <c>ChangeDisplaySettingsEx</c> sees the user's monitors and the rotation
/// takes effect immediately.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OrientationHandler
{
    private readonly IDisplayOrientationProvider _provider;

    public OrientationHandler(IDisplayOrientationProvider provider) { _provider = provider; }

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register("displayOrientation.set", (env, _) =>
        {
            DisplayOrientationRequest req = new();
            if (env.Payload is { } payload)
            {
                try
                {
                    req = JsonSerializer.Deserialize(payload, AppJsonContext.Default.DisplayOrientationRequest)
                          ?? new DisplayOrientationRequest();
                }
                catch { }
            }
            var (ok, err) = string.IsNullOrEmpty(req.DisplayId)
                ? _provider.SetY70Orientation(req.Orientation)
                : _provider.SetDisplayOrientation(req.DisplayId, req.Orientation, req.CoverColorHex);
            return Reply(env, new DisplayOrientationResult { Ok = ok, Error = err });
        });
    }

    private static Task<HelperResult> Reply(HelperEnvelope env, DisplayOrientationResult value)
        => Task.FromResult(new HelperResult
        {
            Id = env.Id ?? "",
            Ok = true,
            Payload = JsonSerializer.SerializeToElement(value, AppJsonContext.Default.DisplayOrientationResult),
        });
}
#endif
