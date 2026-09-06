using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Widgets;

/// <summary>
/// Install one version of a store app. The caller names what to install and what
/// it must hash to; it never supplies a URL, so a page cannot point the service
/// at an arbitrary host (the URL is composed from the configured assets base).
/// </summary>
public sealed class StoreInstallRequest
{
    [JsonPropertyName("appId")] public string? AppId { get; set; }

    /// <summary>Semver of the version to install, matching the artifact key.</summary>
    [JsonPropertyName("version")] public string? Version { get; set; }

    /// <summary>Lowercase hex SHA-256 of the artifact, from the store catalog. Required: it is the trust pin.</summary>
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }

    /// <summary>Artifact size in bytes. Checked before hashing; 0 skips the pre-check.</summary>
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class StoreInstallResponse
{
    [JsonPropertyName("appId")] public string AppId { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }

    /// <summary>
    /// Machine-readable failure: invalid_app_id, invalid_version, missing_hash,
    /// artifact_unavailable, hash_mismatch, bad_archive, manifest_mismatch,
    /// no_user_apps_dir, or install_failed.
    /// </summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

/// <summary>
/// What the cloud hands back for an entitled download: where the artifact is,
/// what it must hash to, and when this account first acquired the app.
/// </summary>
public sealed class StoreDownloadGrant
{
    [JsonPropertyName("appId")] public string AppId { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("acquiredAt")] public DateTimeOffset? AcquiredAt { get; set; }
}

/// <summary>
/// One entry of Manage purchases: the cloud's ownership record joined with what
/// this machine actually has on disk. Either half can be missing - an app
/// acquired on another machine has no local facts, and an app sideloaded here
/// has no entitlement.
/// </summary>
public sealed class StorePurchase
{
    [JsonPropertyName("appId")] public string AppId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("tagline")] public string Tagline { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("iconUrl")] public string? IconUrl { get; set; }

    /// <summary>When the account first got the app. Null for a local-only install.</summary>
    [JsonPropertyName("acquiredAt")] public DateTimeOffset? AcquiredAt { get; set; }

    /// <summary>0 while every app is free.</summary>
    [JsonPropertyName("priceCents")] public int PriceCents { get; set; }

    /// <summary>False once the store stops listing it; the purchase still stands. Null for a local-only row, where the store knows nothing about the app either way.</summary>
    [JsonPropertyName("listed")] public bool? Listed { get; set; }

    [JsonPropertyName("installedVersion")] public string? InstalledVersion { get; set; }
    [JsonPropertyName("installedAt")] public DateTimeOffset? InstalledAt { get; set; }

    /// <summary>Bytes the installed app occupies on disk. Null when not installed here.</summary>
    [JsonPropertyName("sizeBytes")] public long? SizeBytes { get; set; }
}

public sealed class StoreLibraryResponse
{
    /// <summary>False when no cloud account is linked - the client asks the user to sign in.</summary>
    [JsonPropertyName("signedIn")] public bool SignedIn { get; set; }

    /// <summary>True when the account's cloud library could not be read; local installs are still listed.</summary>
    [JsonPropertyName("offline")] public bool Offline { get; set; }

    [JsonPropertyName("purchases")] public List<StorePurchase> Purchases { get; set; } = new();
}

/// <summary>One row of the cloud's <c>GET /store/library</c> - an account's claim on an app.</summary>
public sealed class CloudStoreEntitlement
{
    [JsonPropertyName("appId")] public string AppId { get; set; } = "";
    [JsonPropertyName("acquiredAt")] public DateTimeOffset? AcquiredAt { get; set; }
    [JsonPropertyName("priceCents")] public int PriceCents { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("tagline")] public string Tagline { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("iconUrl")] public string? IconUrl { get; set; }
    [JsonPropertyName("listed")] public bool Listed { get; set; }
}

public sealed class CloudStoreLibraryResponse
{
    [JsonPropertyName("entitlements")] public List<CloudStoreEntitlement> Entitlements { get; set; } = new();
}
