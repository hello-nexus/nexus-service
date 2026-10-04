using System.Collections.Generic;

namespace Nexus.Service.Lighting.Scene;

/// <summary>
/// The 3D lighting scene: desk objects, the surfaces devices sit on, and which
/// device sits where. Machine config (own file), never the synced profile.
/// Millimetres. World: +Y up, desk at y = 0, +Z toward the seated user, +X to
/// their right. Object-local: same axes, front facing +Z, origin at the centre
/// of the object's footprint.
/// </summary>
public sealed class LightingSceneDoc
{
    public int Version { get; set; } = 1;
    public List<SceneObject> Objects { get; set; } = new();
    public List<SceneBinding> Bindings { get; set; } = new();

    /// <summary>Content hash of the imported case model, null when none was imported.</summary>
    public string? ModelRev { get; set; }

    /// <summary>Catalog case id the imported model was built from.</summary>
    public string? CaseId { get; set; }
}

public sealed class SceneObject
{
    public string Id { get; set; } = "";

    /// <summary>case, keyboard, mouse, mousepad, headset, monitor, speaker, strip or box.</summary>
    public string Kind { get; set; } = "box";

    public string? Label { get; set; }

    /// <summary>"build" for objects the Build page exported (replaced on every import), "user" for objects placed in the lighting editor.</summary>
    public string Source { get; set; } = "user";

    /// <summary>World position of the object's origin.</summary>
    public float[] Position { get; set; } = [0f, 0f, 0f];

    /// <summary>Turn about +Y in degrees, counter-clockwise seen from above.</summary>
    public float Yaw { get; set; }

    /// <summary>Bounding size along local X, Y, Z.</summary>
    public float[] Size { get; set; } = [100f, 20f, 100f];

    /// <summary>True when the imported case model draws this object rather than a plain box.</summary>
    public bool HasModel { get; set; }

    public List<SceneAnchor> Anchors { get; set; } = new();
}

/// <summary>A flat surface a device's LED map is laid onto: u runs along <see cref="Right"/>, v runs against <see cref="Up"/>.</summary>
public sealed class SceneAnchor
{
    public string Id { get; set; } = "";

    /// <summary>fan, radiator, gpu, ram, board, pump, psu, strip, panel or surface.</summary>
    public string Kind { get; set; } = "surface";

    public string? Label { get; set; }

    /// <summary>Centre in object-local space.</summary>
    public float[] Center { get; set; } = [0f, 0f, 0f];

    /// <summary>Unit vector in object-local space along the map's u axis.</summary>
    public float[] Right { get; set; } = [1f, 0f, 0f];

    /// <summary>Unit vector in object-local space toward the map's top edge.</summary>
    public float[] Up { get; set; } = [0f, 1f, 0f];

    public float Width { get; set; } = 100f;
    public float Height { get; set; } = 100f;

    /// <summary>"ring" for a fan face, "rect" otherwise; drawing only.</summary>
    public string Shape { get; set; } = "rect";
}

/// <summary>One placed device. Several targets split its LEDs into equal consecutive runs (a fan chain that reports as one device).</summary>
public sealed class SceneBinding
{
    public string DeviceId { get; set; } = "";
    public List<SceneTarget> Targets { get; set; } = new();

    /// <summary>Clockwise turn of the LED map on its surface: 0, 90, 180 or 270.</summary>
    public int Rotation { get; set; }

    /// <summary>Mirrors the LED map left to right on its surface.</summary>
    public bool Flip { get; set; }
}

public sealed class SceneTarget
{
    public string ObjectId { get; set; } = "";
    public string AnchorId { get; set; } = "";
}
