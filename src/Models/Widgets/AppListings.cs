using System.Collections.Generic;

namespace Nexus.Service.Models.Widgets;

/// <summary>
/// Listing payload returned by <c>GET /apps-api/installed</c>. Describes an
/// installed app: its capabilities, settings schema, and sizes. The widget
/// facet renders from the bundle's <c>widget.mjs</c>; there is no view tree.
/// </summary>
public sealed class AppInstalledListing
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public string? Category { get; set; } // "device" -> listed under Devices
    public string? IconUrl { get; set; }
    public List<string> Surfaces { get; set; } = new();
    public string? Runtime { get; set; } // "sdk"
    public bool Page { get; set; }       // app declares an expanded page surface
    public AppManifestCapabilities Capabilities { get; set; } = new();
    public AppManifestViewport? Viewport { get; set; }
    public List<AppManifestSettingEntry> Settings { get; set; } = new();
    public List<string> Sizes { get; set; } = new();
    public string? DefaultSize { get; set; }
    public string Source { get; set; } = ""; // "user" | "bundled"
    public bool Preinstalled { get; set; }   // OEM bake-in: active at first boot, no user install
}

public sealed class AppInstalledListingResponse
{
    public List<AppInstalledListing> Apps { get; set; } = new();
}

/// <summary>Marketplace catalogue entry: an app that could be installed
/// (or already is) into the user apps dir.</summary>
public sealed class AppCatalogEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public string? IconUrl { get; set; }
    public List<string> Surfaces { get; set; } = new();
    public AppManifestCapabilities Capabilities { get; set; } = new();
    public string Source { get; set; } = ""; // "bundled" | "user"
    public bool Installed { get; set; }      // true if a copy exists under user widgets
    public bool Preinstalled { get; set; }   // OEM bake-in: active at first boot, no user install
}

public sealed class AppCatalogResponse
{
    public List<AppCatalogEntry> Entries { get; set; } = new();
}

public sealed class AppInstallRequest
{
    public string Id { get; set; } = "";
}

public sealed class AppInstallResponse
{
    public bool Installed { get; set; }
    public string Id { get; set; } = "";
    public string? Error { get; set; }
}

/// <summary>
/// Result of <c>POST /apps-api/installed/{id}/code-session</c>. The session
/// id is a short-lived URL-path token; the worker's module imports inherit it
/// automatically, so the host doesn't need to embed Bearer auth in module
/// import URLs.
/// </summary>
public sealed class AppCodeSessionResponse
{
    public string SessionId { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public int ExpiresInSeconds { get; set; }
    public string? Error { get; set; }
}
