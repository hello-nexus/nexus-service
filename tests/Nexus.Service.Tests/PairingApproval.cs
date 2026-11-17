using Microsoft.AspNetCore.Http;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;

namespace Nexus.Service.Tests;

/// <summary>
/// Test-side stand-in for the desktop user: every new claim now parks on the
/// host's Allow, so fixtures that only need a minted session drive the claim,
/// the host decision and the phone's collect poll in one call.
/// </summary>
internal static class PairingApproval
{
    public static PanelPhoneClaimResponse ClaimApproved(this PanelPhonePairingService service, string pairToken, HttpContext context)
        => service.ClaimApproved(pairToken, "", "", context);

    public static PanelPhoneClaimResponse ClaimApproved(this PanelPhonePairingService service, string pairToken, string? deviceId, HttpContext context)
        => service.ClaimApproved(pairToken, deviceId, "", context);

    public static PanelPhoneClaimResponse ClaimApproved(
        this PanelPhonePairingService service, string pairToken, string? deviceId, string? deviceName, HttpContext context)
    {
        var claim = service.Claim(pairToken, deviceId, deviceName, context, supportsSasApproval: true);
        if (!claim.NeedsApproval)
            return claim;

        service.HostDecisionPairCode(claim.RequestId!, approved: true);
        var confirm = service.ConfirmCore(
            claim.RequestId!, true, context.Request.IsHttps, context.Connection.RemoteIpAddress?.ToString() ?? "", relayPairToken: null);
        return new PanelPhoneClaimResponse
        {
            Paired = confirm.Status == "approved",
            Token = confirm.Token,
            MachineName = confirm.MachineName,
            SpkiFingerprint = string.IsNullOrEmpty(confirm.SpkiFingerprint) ? null : confirm.SpkiFingerprint,
            Error = confirm.Status == "approved" ? "" : confirm.Status,
        };
    }

    public static PanelPhonePairingService.ClaimResult ClaimCoreApproved(
        this PanelPhonePairingService service,
        string pairToken,
        string deviceName,
        string userAgent,
        string remoteAddress,
        string deviceId,
        bool overRelay,
        bool claimedOverHttps)
    {
        var claim = service.ClaimCore(
            pairToken, deviceName, userAgent, remoteAddress, deviceId, overRelay, claimedOverHttps, supportsSasApproval: true);
        if (!claim.NeedsApproval)
            return claim;

        service.HostDecisionPairCode(claim.RequestId, approved: true);
        var confirm = overRelay
            ? service.ConfirmRelayClaim(pairToken, claim.RequestId, true)
            : service.ConfirmCore(claim.RequestId, true, claimedOverHttps, remoteAddress, relayPairToken: null);
        return confirm.Status == "approved"
            ? new PanelPhonePairingService.ClaimResult(
                true, confirm.Token, confirm.MachineName, confirm.SpkiFingerprint, "", false, "", "", 0)
            : PanelPhonePairingService.ClaimResult.Fail(confirm.Status);
    }
}
