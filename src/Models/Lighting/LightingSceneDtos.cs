using System.Collections.Generic;
using Nexus.Service.Lighting.Scene;
using Nexus.Service.Persistence;

namespace Nexus.Service.Models.Lighting;

public sealed class LightingSceneResponse
{
    public List<SceneObject> Objects { get; set; } = new();
    public List<SceneBinding> Bindings { get; set; } = new();
    public SceneView View { get; set; } = new();

    /// <summary>Content hash of the imported case model; null when none is imported.</summary>
    public string? ModelRev { get; set; }
    public string? CaseId { get; set; }
}

/// <summary>The editor's whole scene: every object (moved build objects included) and every binding.</summary>
public sealed class PutLightingSceneBody
{
    public List<SceneObject> Objects { get; set; } = new();
    public List<SceneBinding> Bindings { get; set; } = new();
}

/// <summary>A case scene exported by the Build page; replaces the previous import and keeps everything placed by hand.</summary>
public sealed class ImportLightingSceneBody
{
    public string? CaseId { get; set; }
    public List<SceneObject> Objects { get; set; } = new();

    /// <summary>Binary glTF of the case, base64; null keeps no model.</summary>
    public string? ModelBase64 { get; set; }
}

public sealed class PutSceneViewBody
{
    public bool? Enabled { get; set; }
    public SceneCamera? Camera { get; set; }

    /// <summary>Drives the engine without saving; an editor streams these while the camera moves.</summary>
    public bool Draft { get; set; }
}
