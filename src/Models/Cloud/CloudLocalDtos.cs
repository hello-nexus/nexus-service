using System.Collections.Generic;
using Nexus.Service.Models;

namespace Nexus.Service.Models.Cloud;

// Local /cloud/... route shapes - what the dashboard sees. Cloud creds/tokens
// never appear here; only account metadata and sync status.

public sealed class CloudAccountSummaryDto
{
    public string AccountId { get; set; } = "";
    public string Email { get; set; } = "";
    public string Username { get; set; } = "";
    public CloudAvatarDto? Avatar { get; set; }
    public bool IsPrivate { get; set; }
    public bool EmailVerified { get; set; }
    public bool Active { get; set; }
    public string LastSyncAt { get; set; } = "";
}

public sealed class CloudAccountsResponse : ApiResponse
{
    public List<CloudAccountSummaryDto> Accounts { get; set; } = new();
    public string? ActiveAccountId { get; set; }
}

/// <summary>Failure envelope carrying a retryAt hint (username_cooldown and similar upstream cooldown errors). The dashboard reads result.body.retryAt.</summary>
public sealed class CloudFailureResponse : ApiResponse
{
    public string? RetryAt { get; set; }
}

public sealed class CloudRegisterBody
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string Username { get; set; } = "";
}

public sealed class CloudLoginBody
{
    public string Identifier { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class CloudLoginResponse : ApiResponse
{
    public CloudAccountSummaryDto? Account { get; set; }
}

public sealed class CloudLogoutBody
{
    public string AccountId { get; set; } = "";
}

public sealed class CloudRecoveryStartBody
{
    public string Email { get; set; } = "";
}

public sealed class CloudRecoveryStatusResponse : ApiResponse
{
    /// <summary>"idle" | "pending" | "approved" | "expired"</summary>
    public string Status { get; set; } = "idle";
    public bool RecoveryFresh { get; set; }
}

public sealed class CloudPasswordBody
{
    public string? CurrentPassword { get; set; }
    public string NewPassword { get; set; } = "";
}

public sealed class CloudUsernameBody
{
    public string Username { get; set; } = "";
}

public sealed class CloudSetPrivateBody
{
    public bool IsPrivate { get; set; }
}

public sealed class CloudDeleteAccountBody
{
    public string? CurrentPassword { get; set; }
}

public sealed class CloudAvatarResponse : ApiResponse
{
    public CloudAvatarDto? Avatar { get; set; }
}

public sealed class CloudSyncConflictDto
{
    public string ProfileId { get; set; } = "";
    public string Name { get; set; } = "";
    public string LocalUpdatedAt { get; set; } = "";
    /// <summary>The machine holding the local copy - always this one. Named so the comparison sheet reads "HYTEY70" rather than a GUID.</summary>
    public string LocalHostname { get; set; } = "";
    public int CloudRevision { get; set; }
    public string CloudUpdatedAt { get; set; } = "";
    public string CloudName { get; set; } = "";
    /// <summary>Machine name behind UpdatedByInstallId; empty when the writer is not a machine this one can name, and the UI falls back to the id.</summary>
    public string CloudHostname { get; set; } = "";
    public string UpdatedByInstallId { get; set; } = "";
}

public sealed class CloudSyncProfileDto
{
    public string ProfileId { get; set; } = "";
    public string Name { get; set; } = "";
    public string LastSyncedAt { get; set; } = "";
    public int Revision { get; set; }
}

public sealed class CloudSyncStatusResponse : ApiResponse
{
    /// <summary>"idle" | "syncing" | "dirty" | "offline" | "error"</summary>
    public string State { get; set; } = "idle";
    public string LastSyncAt { get; set; } = "";
    public List<CloudSyncConflictDto> Conflicts { get; set; } = new();
    public List<CloudSyncProfileDto> Profiles { get; set; } = new();
}

public sealed class CloudSyncResolveBody
{
    public string ProfileId { get; set; } = "";
    /// <summary>"local" | "cloud"</summary>
    public string Choice { get; set; } = "";
}

/// <summary>One profile belonging to some machine on the account, as the import picker lists it.</summary>
public sealed class CloudLibraryProfileDto
{
    public string ProfileId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Revision { get; set; }
    public long SizeBytes { get; set; }
    public string UpdatedAt { get; set; } = "";
}

/// <summary>A machine on the account, named by its reported hostname rather than its installId.</summary>
public sealed class CloudLibraryMachineDto
{
    public string InstallId { get; set; } = "";
    public string Hostname { get; set; } = "";
    /// <summary>True for the machine serving this request; the UI hides it from the "import from" list.</summary>
    public bool IsThisMachine { get; set; }
    public string LastSeenAt { get; set; } = "";
    public List<CloudLibraryProfileDto> Profiles { get; set; } = new();
}

public sealed class CloudLibraryResponse : ApiResponse
{
    public List<CloudLibraryMachineDto> Machines { get; set; } = new();
}

/// <summary>409 body for an import that clashes: carries the LOCAL name it clashed with, which is the derived "&lt;name&gt; (&lt;hostname&gt;)" and not the name shown on the source row.</summary>
public sealed class CloudImportConflictResponse : ApiResponse
{
    public string Name { get; set; } = "";
}

/// <summary>Copies another machine's profile in as a NEW local profile; nothing existing is touched, so there is nothing to select.</summary>
public sealed class CloudImportRequest
{
    public string InstallId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    /// <summary>Set after the user answers the name-conflict prompt; overwrites the local profile of the same name in place.</summary>
    public bool ReplaceExisting { get; set; }
}

/// <summary>Revision frame for the "cloud/accounts" multiplex topic; subscribers refetch GET /cloud/accounts.</summary>
public sealed class CloudAccountsChangedFrame
{
    public long Revision { get; set; }
}
