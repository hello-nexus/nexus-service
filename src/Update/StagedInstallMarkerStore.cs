using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Update;

/// <summary>
/// Marker file written before each install attempt so a failed apply can be
/// detected on the next boot without a manifest fetch.
/// </summary>
public sealed class StagedInstallMarker
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("installer_path")] public string InstallerPath { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    /// <summary>"pending" = downloaded+verified, not yet launched. "attempted" = installer was launched.</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "pending";
    /// <summary>When true, the new instance opens the dashboard after confirming the version advanced.</summary>
    [JsonPropertyName("reopen_dashboard")] public bool ReopenDashboard { get; set; }
}

/// <summary>
/// Read/write/delete for the pending-install marker file at
/// <c>{StagingDir}/pending-install.json</c>.
/// </summary>
public static class StagedInstallMarkerStore
{
    public const string StatePending = "pending";
    public const string StateAttempted = "attempted";

    public const string MarkerFileName = "pending-install.json";

    public static string MarkerPath =>
        Path.Combine(UpdateDownloader.StagingDir, MarkerFileName);

    public static StagedInstallMarker? Read()
    {
        try
        {
            var path = MarkerPath;
            if (!File.Exists(path))
            {
                return null;
            }

            if (!IsMarkerTrusted(path))
            {
                Console.Error.WriteLine("[ota-marker] rejecting marker not owned by SYSTEM/Administrators");
                Delete();
                return null;
            }

            var json = File.ReadAllBytes(path);
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.StagedInstallMarker);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The marker directs a SYSTEM-privileged install, so it is trusted only when
    /// a privileged principal wrote it. On the LocalSystem service, require the
    /// file owner to be SYSTEM or Administrators: a non-admin cannot create a file
    /// owned by either, so a planted marker is rejected regardless of the staging
    /// dir's ACL (race-free). Fails closed (untrusted) on any read error.
    /// Off-Windows, or when running interactively (not SYSTEM), the LocalSystem
    /// escalation does not apply and the marker is accepted.
    /// </summary>
    private static bool IsMarkerTrusted(string path)
    {
        if (!OperatingSystem.IsWindows())
            return true;
        if (!WindowsDirectorySecurity.IsLocalSystem())
            return true;
        try
        {
            var owner = new FileSecurity(path, AccessControlSections.Owner)
                .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner is not null
                && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
                    || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        }
        catch
        {
            return false;
        }
    }

    public static void Write(StagedInstallMarker marker)
    {
        try
        {
            UpdateDownloader.EnsureSecureStagingDir();
            var json = JsonSerializer.SerializeToUtf8Bytes(marker, AppJsonContext.Default.StagedInstallMarker);
            File.WriteAllBytes(MarkerPath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ota-marker] write failed: {ex.Message}");
        }
    }

    public static void Delete()
    {
        try
        {
            var path = MarkerPath;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ota-marker] delete failed: {ex.Message}");
        }
    }
}
