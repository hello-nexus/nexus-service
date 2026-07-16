using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Widgets;

/// <summary>
/// Parsed <c>manifest.json</c> for an installed app under the <c>nexus.app/1</c>
/// schema. Field names match the on-disk JSON. An app's widget facet is a
/// sandboxed remote-component bundle (<c>widget.mjs</c>); there is no declarative
/// view tree. See <c>plans/third-party-app-sdk.md</c> for the contract.
/// </summary>
public sealed class AppManifest
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional nav categorization. <c>"device"</c> lists the app under Devices
    /// (its page surface becomes the device page) instead of Apps.
    /// </summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("author")]
    public AppManifestAuthor? Author { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("min_nexus_version")]
    public string MinNexusVersion { get; set; } = "";

    [JsonPropertyName("surfaces")]
    public List<string> Surfaces { get; set; } = new();

    /// <summary>
    /// Render runtime. Must be <c>"sdk"</c>: the panel loads the built
    /// <c>widget.mjs</c> into a sandboxed worker and reconciles its remote
    /// component tree into host components. Carried through to the listing.
    /// </summary>
    [JsonPropertyName("runtime")]
    public string? Runtime { get; set; }

    /// <summary>
    /// True when the widget ships an expanded "page" surface (a second render
    /// of the bundle via <c>mount({ cell, page })</c>). The dashboard makes such
    /// a widget click-through into a full section view. Default false.
    /// </summary>
    [JsonPropertyName("page")]
    public bool Page { get; set; }

    /// <summary>
    /// Allowed grid sizes. Same alphabet as the panel engine:
    /// <c>1x1</c>, <c>2x2</c>, <c>4x2</c>, <c>4x4</c>. The first entry is
    /// the default if <see cref="DefaultSize"/> is unset. Anything outside
    /// the alphabet is dropped by the panel registry's marketplace filter.
    /// </summary>
    [JsonPropertyName("sizes")]
    public List<string> Sizes { get; set; } = new();

    [JsonPropertyName("default_size")]
    public string? DefaultSize { get; set; }

    [JsonPropertyName("viewport")]
    public AppManifestViewport? Viewport { get; set; }

    [JsonPropertyName("capabilities")]
    public AppManifestCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("settings")]
    public List<AppManifestSettingEntry> Settings { get; set; } = new();

    /// <summary>
    /// True when this app should be treated as installed + active at first boot
    /// (OEM pre-install) rather than waiting for a user "install" of the bundled
    /// copy. Honored only for bundled apps; ignored on user copies.
    /// </summary>
    [JsonPropertyName("preinstalled")]
    public bool Preinstalled { get; set; }

    /// <summary>
    /// Optional device-driver block: declares a native sidecar executable the host
    /// fetches from the app store and runs. Honored only for a <b>bundled</b> app
    /// (the registry drops it from user installs); the widget facet always
    /// loads regardless. See <c>plans/third-party-app-sdk.md</c> §"Raw .exe".
    /// </summary>
    [JsonPropertyName("driver")]
    public AppManifestDriver? Driver { get; set; }

    /// <summary>
    /// Optional OEM gate for <see cref="Preinstalled"/>: when present, the app
    /// is preinstalled only on a machine whose SMBIOS system manufacturer
    /// matches one of the listed names. Absent means no gate (unchanged
    /// behavior).
    /// </summary>
    [JsonPropertyName("oem")]
    public AppManifestOem? Oem { get; set; }
}

public sealed class AppManifestOem
{
    [JsonPropertyName("manufacturer")]
    public List<string>? Manufacturer { get; set; }
}

/// <summary>
/// Device-driver descriptor. The host resolves a USB match to a variant, then
/// fetches+runs the variant's binary via the external-tool manager.
/// </summary>
public sealed class AppManifestDriver
{
    /// <summary>Stable tool id (e.g. <c>"acme-cooler"</c>). Names the cache folder + the store path.</summary>
    [JsonPropertyName("toolId")]
    public string ToolId { get; set; } = "";

    /// <summary>
    /// <see cref="Nexus.Service.Devices.IDeviceHandler.Id"/> of the device this driver
    /// drives, so the handler's Nexus Control gate also governs the driver process.
    /// Null leaves the driver ungated: it runs whenever its device is present.
    /// </summary>
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    /// <summary>USB match that triggers the driver (vendor + product ids, hex strings).</summary>
    [JsonPropertyName("match")]
    public AppManifestDriverMatch? Match { get; set; }

    /// <summary>Map of matched PID (hex string) → variant/OEM name (the store sub-path).</summary>
    [JsonPropertyName("variants")]
    public Dictionary<string, string> Variants { get; set; } = new();

    /// <summary>Base URL of the per-variant tool manifest: <c>&lt;base&gt;/&lt;variant&gt;/latest.json</c>.</summary>
    [JsonPropertyName("manifestUrlBase")]
    public string ManifestUrlBase { get; set; } = "";

    /// <summary>Glob for the dev-only offline fallback (e.g. <c>MyDriver*.exe</c>).</summary>
    [JsonPropertyName("filePattern")]
    public string FilePattern { get; set; } = "";

    [JsonPropertyName("launch")]
    public AppManifestDriverLaunch? Launch { get; set; }

    /// <summary>Install medium: "host-exe" (default) or "android-adb".</summary>
    [JsonPropertyName("target")]
    public string? Target { get; set; }

    /// <summary>Android package name for android-adb installs.</summary>
    [JsonPropertyName("package")]
    public string? Package { get; set; }
}

public sealed class AppManifestDriverMatch
{
    [JsonPropertyName("vid")] public string Vid { get; set; } = "";
    [JsonPropertyName("pids")] public List<string> Pids { get; set; } = new();
}

public sealed class AppManifestDriverLaunch
{
    /// <summary><c>"system"</c> (LocalSystem/Session 0, pre-login - default) or <c>"user"</c>.</summary>
    [JsonPropertyName("session")] public string Session { get; set; } = "system";
    [JsonPropertyName("hidden")] public bool Hidden { get; set; } = true;
}

public sealed class AppManifestAuthor
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
}

public sealed class AppManifestViewport
{
    [JsonPropertyName("min")] public List<int>? Min { get; set; }
    [JsonPropertyName("preferred")] public List<int>? Preferred { get; set; }
    [JsonPropertyName("max")] public List<int>? Max { get; set; }
    [JsonPropertyName("aspect")] public string? Aspect { get; set; }
}

public sealed class AppManifestCapabilities
{
    [JsonPropertyName("sensors.read")]
    public List<string> SensorsRead { get; set; } = new();

    [JsonPropertyName("rgb.read")]
    public bool RgbRead { get; set; }

    [JsonPropertyName("rgb.write")]
    public bool RgbWrite { get; set; }

    /// <summary>HTTPS hosts the widget may fetch from. Phase 2 uses the
    /// host-mediated proxy (`/apps-api/proxy`) for both Tier 1 declarative
    /// fetch sources and Tier 2 worker `nexus.net.fetch` calls.</summary>
    [JsonPropertyName("net.fetch")]
    public List<string> NetFetch { get; set; } = new();

    /// <summary>
    /// Host-action allowlist. Widgets may POST to /apps-api/dispatch
    /// only with action names that appear in this list. Names are
    /// dotted (e.g. "displays.list", "displays.setBrightness"); the
    /// server-side registry knows which controller each routes to.
    /// </summary>
    [JsonPropertyName("dispatch")]
    public List<string> Dispatch { get; set; } = new();

    /// <summary>
    /// Service routes the app may upload files to via the host-mediated MediaImport
    /// component. The host validates an upload's path against this allowlist before
    /// posting; the worker cannot upload to a path not listed here.
    /// </summary>
    [JsonPropertyName("mediaImport")]
    public List<string> MediaImport { get; set; } = new();

    [JsonPropertyName("config")]
    public bool Config { get; set; } = true;

    /// <summary>
    /// Opt-in Tier 2 capability. When <c>true</c>, the bundle must ship
    /// a <c>worker.js</c> alongside the manifest; the host spawns a Web
    /// Worker per widget instance and exposes the <c>nexus.*</c> API there.
    /// Stored as the JSON discriminator string (currently only "worker").
    /// </summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>Convenience: true when <see cref="Code"/> is "worker".</summary>
    [JsonIgnore]
    public bool WorkerCode => string.Equals(Code, "worker", System.StringComparison.Ordinal);
}

public sealed class AppManifestSettingEntry
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("default")] public JsonElement? Default { get; set; }
    [JsonPropertyName("min")] public double? Min { get; set; }
    [JsonPropertyName("max")] public double? Max { get; set; }
    [JsonPropertyName("step")] public double? Step { get; set; }
    [JsonPropertyName("filter")] public string? Filter { get; set; }
    [JsonPropertyName("options")] public List<string>? Options { get; set; }
    // Parallel to Options, for the icon-select control: a display label and an
    // icon name (mapped host-side to a lucide glyph) per option. Lets an SDK
    // widget declare a visual switcher (e.g. the clock's design picker).
    [JsonPropertyName("optionLabels")] public List<string>? OptionLabels { get; set; }
    [JsonPropertyName("optionIcons")] public List<string>? OptionIcons { get; set; }
}
