using System.Collections.Generic;
using Nexus.Service.Models.Profiles;

namespace Nexus.Service.Models.Cloud;

// Wire shapes for api.hellonexus.com (CloudApiClient) and the local /cloud/...
// routes (CloudRoutes). Field names are camelCase on the wire via
// AppJsonContext's naming policy, matching the nexus-api contract.

public sealed class CloudAvatarDto
{
    public string Large { get; set; } = "";
    public string Small { get; set; } = "";
}

public sealed class CloudAccountDto
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public bool EmailVerified { get; set; }
    public string Username { get; set; } = "";
    public bool IsPrivate { get; set; }
    public CloudAvatarDto? Avatar { get; set; }
    public string CreatedAt { get; set; } = "";
}

public sealed class CloudAuthSession
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public CloudAccountDto? Account { get; set; }
}

public sealed class CloudRegisterRequest
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string Username { get; set; } = "";
}

public sealed class CloudLoginRequest
{
    public string Identifier { get; set; } = "";
    public string Password { get; set; } = "";
    public string? DeviceName { get; set; }
    public string? InstallId { get; set; }
}

public sealed class CloudRefreshRequest
{
    public string RefreshToken { get; set; } = "";
}

public sealed class CloudLogoutRequest
{
    public string RefreshToken { get; set; } = "";
}

public sealed class CloudRecoveryStartRequest
{
    public string Email { get; set; } = "";
    public string GrantId { get; set; } = "";
    public string DeviceSecret { get; set; } = "";

    /// <summary>Asks the api for a verification code this device displays; an api without the code omits it from the response.</summary>
    public bool WantsCode { get; set; }
}

public sealed class CloudRecoveryStartResponse
{
    public string? Code { get; set; }
}

public sealed class CloudRecoveryPollRequest
{
    public string GrantId { get; set; } = "";
    public string DeviceSecret { get; set; } = "";
}

/// <summary>Either {status:"pending"|"expired"} or a one-time session grant - all fields optional since the server sends only the relevant subset.</summary>
public sealed class CloudRecoveryPollResponse
{
    public string? Status { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public CloudAccountDto? Account { get; set; }
}

public sealed class CloudChangePasswordRequest
{
    public string? CurrentPassword { get; set; }
    public string NewPassword { get; set; } = "";
}

public sealed class CloudChangeUsernameRequest
{
    public string Username { get; set; } = "";
}

public sealed class CloudSetPrivateRequest
{
    public bool IsPrivate { get; set; }
}

public sealed class CloudDeleteAccountRequest
{
    public string? CurrentPassword { get; set; }
}

public sealed class CloudDevicePutRequest
{
    public string Hostname { get; set; } = "";
    public Dictionary<string, string> Specs { get; set; } = new();
    public string AppVersion { get; set; } = "";
    public string Os { get; set; } = "";
}

/// <summary>One row of GET /account/devices - the machine names behind the installIds on profile rows.</summary>
public sealed class CloudDeviceDto
{
    public string InstallId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public Dictionary<string, string> Specs { get; set; } = new();
    public string? AppVersion { get; set; }
    public string? Os { get; set; }
    public bool Manual { get; set; }
    public string LastSeenAt { get; set; } = "";
}

public sealed class CloudProfileSummaryDto
{
    /// <summary>The machine that owns this profile. The list endpoint returns every machine's rows; a sync pass keeps only its own.</summary>
    public string InstallId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Revision { get; set; }
    public long SizeBytes { get; set; }
    public string UpdatedAt { get; set; } = "";
    public string UpdatedByInstallId { get; set; } = "";
}

public sealed class CloudProfileDto
{
    public string InstallId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Revision { get; set; }
    public long SizeBytes { get; set; }
    public string UpdatedAt { get; set; } = "";
    public string UpdatedByInstallId { get; set; } = "";
    public ProfileExport? Payload { get; set; }
}

public sealed class CloudPutProfileRequest
{
    public string Name { get; set; } = "";
    public int BaseRevision { get; set; }
    public ProfileExport? Payload { get; set; }
}

/// <summary>200 -> Revision set; 409 -> the CurrentRevision/UpdatedAt/UpdatedByInstallId/Name conflict fields are set instead. Both live on one type since AOT source-gen deserialization can't pick a shape dynamically.</summary>
public sealed class CloudPutProfileResult
{
    public int? Revision { get; set; }
    public int? CurrentRevision { get; set; }
    public string? UpdatedAt { get; set; }
    public string? UpdatedByInstallId { get; set; }
    public string? Name { get; set; }
}

/// <summary>nexus-api's POST /account/avatar returns {large, small} top-level, not nested under an "avatar" key.</summary>
public sealed class CloudAvatarUploadResponse
{
    public string Large { get; set; } = "";
    public string Small { get; set; } = "";
}

/// <summary>Error body shape assumed for non-2xx cloud responses: a stable machine code (e.g. "email_unverified", "invalid_credentials") plus a human message. RetryAt (ISO timestamp) is set for cooldown errors like username_cooldown.</summary>
public sealed class CloudErrorBody
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? RetryAt { get; set; }
}
