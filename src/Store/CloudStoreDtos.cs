using System.Collections.Generic;

namespace Nexus.Service.Store;

// Device grant flow

/// <summary>Response from POST /auth/device/start</summary>
public sealed class CloudDeviceGrantStartResponse
{
    public string DeviceCode { get; set; } = "";
    public string UserCode { get; set; } = "";
    public string VerificationUri { get; set; } = "";
    public string? VerificationUriComplete { get; set; }
    public int IntervalSec { get; set; } = 5;
    public string ExpiresAt { get; set; } = "";
}

/// <summary>Request body for POST /auth/device/poll</summary>
public sealed class CloudDeviceGrantPollRequest
{
    public string DeviceCode { get; set; } = "";
}

/// <summary>Response from POST /auth/device/poll</summary>
public sealed class CloudDeviceGrantPollResponse
{
    /// <summary>"pending" | "approved" | "expired" | "denied"</summary>
    public string Status { get; set; } = "";
    public string? AccessToken { get; set; }
    public CloudAccountInfo? Account { get; set; }
}

// Account

/// <summary>Account info from GET /account/me</summary>
public sealed class CloudAccountInfo
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
}

/// <summary>Response from GET /account/library</summary>
public sealed class CloudLibraryResponse
{
    public List<CloudEntitlement> Entitlements { get; set; } = new();
    public List<CloudInstallRecord> Installs { get; set; } = new();
}

public sealed class CloudEntitlement
{
    public string AppId { get; set; } = "";
    public string? Version { get; set; }
}

public sealed class CloudInstallRecord
{
    public string AppId { get; set; } = "";
    public string? InstallId { get; set; }
    public string? Version { get; set; }
    public string? DeviceLabel { get; set; }
}

// Store

/// <summary>Response from GET /store/apps/:appId/download</summary>
public sealed class CloudAppDownloadInfo
{
    public string Url { get; set; } = "";
    public string Format { get; set; } = "";
    public string? Version { get; set; }
}

/// <summary>Body for POST /account/installs</summary>
public sealed class CloudInstallReport
{
    public string AppId { get; set; } = "";
    public string InstallId { get; set; } = "";
    public string? Version { get; set; }
    public string? DeviceLabel { get; set; }
}

// Service API DTOs (response to the panel/browser)

/// <summary>Response to POST /apps-api/account/link/start</summary>
public sealed class StoreAccountLinkStartResponse
{
    public string UserCode { get; set; } = "";
    public string VerificationUri { get; set; } = "";
    public string? VerificationUriComplete { get; set; }
}

/// <summary>Response to GET /apps-api/account</summary>
public sealed class StoreAccountStatusResponse
{
    public bool Linked { get; set; }
    public bool LinkPending { get; set; }
    public StoreAccountInfo? Account { get; set; }
}

public sealed class StoreAccountInfo
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
}

/// <summary>Response to POST /apps-api/install and DELETE /apps-api/install/{appId}</summary>
public sealed class StoreInstallResponse
{
    public bool Ok { get; set; }
    public string? Reason { get; set; }
    public string? AppId { get; set; }
}

/// <summary>Request body for POST /apps-api/install</summary>
public sealed class StoreInstallRequest
{
    public string AppId { get; set; } = "";
}
