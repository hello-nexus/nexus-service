using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Lighting.Scene;
using Nexus.Service.Persistence;

namespace Nexus.Service.Models.Lighting;

public sealed class LightingSceneResponse
{
    public List<SceneObject> Objects { get; set; } = new();
    public List<SceneBinding> Bindings { get; set; } = new();
    public SceneView View { get; set; } = new();

    /// <summary>Content hash of the imported scene model; null when none is imported.</summary>
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

    /// <summary>The objects' shapes for the dashboard to draw (version 1, shapes by object id); null keeps no model.</summary>
    public JsonElement? Model { get; set; }
}

public sealed class PutSceneViewBody
{
    public bool? Enabled { get; set; }
    public SceneCamera? Camera { get; set; }

    /// <summary>Drives the engine without saving; an editor streams these while the camera moves.</summary>
    public bool Draft { get; set; }

    /// <summary>Identifies one editor (one open page); its <see cref="Seq"/> numbers are compared only with each other.</summary>
    public string? Session { get; set; }

    /// <summary>Increases with every view request from one editor session, so a draft that lands after its commit is dropped.</summary>
    public long? Seq { get; set; }
}
