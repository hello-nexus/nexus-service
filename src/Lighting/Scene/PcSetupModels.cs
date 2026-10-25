using System.Collections.Generic;
using System.Text.Json;

namespace Nexus.Service.Lighting.Scene;

/// <summary>
/// The PC setup Build and Lighting share: one record on this machine, in
/// <c>pc-setup.json</c> next to the lighting scene. Build owns the items;
/// Lighting may only move and turn them.
///
/// Coordinate contract, identical to <see cref="SceneObject"/> so the service
/// copies nothing: millimeters; world origin at the desk surface, +X to the
/// seated user's right, +Z toward the user; <c>x</c> and <c>z</c> are the
/// center of the item's footprint (the same point as
/// <c>SceneObject.Position</c> X and Z); <c>yaw</c> is degrees about +Y,
/// counter-clockwise seen from above, the rotation three.js applies as
/// rotation.y (an item's front at +Z turns toward +X for a positive yaw).
/// A client whose own convention differs converts once at its boundary.
/// </summary>
public sealed class PcSetup
{
    public int Version { get; set; } = 1;
    public PcSetupCase Case { get; set; } = new();

    /// <summary>Opaque to the service: the part corrections and additions Build keeps for the case.</summary>
    public JsonElement? Parts { get; set; }

    public List<PcSetupItem> Items { get; set; } = new();
    public PcSetupResolved Resolved { get; set; } = new();
}

public sealed class PcSetupCase
{
    public PcSetupPlace? Place { get; set; }
}

public sealed class PcSetupItem
{
    public string Id { get; set; } = "";

    /// <summary>A custom kind or laptop; the service only bounds its shape.</summary>
    public string Kind { get; set; } = "other";

    public PcSetupCatalog? Catalog { get; set; }
    public PcSetupCustom? Custom { get; set; }
    public bool Owned { get; set; }
    public PcSetupDetected? Detected { get; set; }
    public string? Replaces { get; set; }
    public PcSetupPlace? Place { get; set; }
}

public sealed class PcSetupCatalog
{
    public string Category { get; set; } = "";
    public string PartId { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>Snapshot of dimensions, screenSize and aspect; Lighting lays the item out from it without the catalog. Opaque to the service.</summary>
    public JsonElement? Specs { get; set; }
}

public sealed class PcSetupCustom
{
    public string Name { get; set; } = "";
    public float? WidthMm { get; set; }
    public float? DepthMm { get; set; }

    /// <summary>Contract: a migrated scene item's height and screen radius, kept on the item so a backup carries them; nexus-build reads both when it rebuilds the item.</summary>
    public float? HeightMm { get; set; }
    public float? CurveRadiusMm { get; set; }

    public float? Price { get; set; }

    /// <summary>"straight" (default when absent) or "l". Only a desk uses it.</summary>
    public string? Shape { get; set; }

    /// <summary>The L's second arm, same units and caps as the width and depth.</summary>
    public float? ReturnWidthMm { get; set; }
    public float? ReturnDepthMm { get; set; }

    /// <summary>Which side of the desk the return arm sits on: "left" or "right".</summary>
    public string? ReturnSide { get; set; }

    /// <summary>
    /// The lighting scene's kind an item migrated from (strip, light, deskmat, box, headset and so on), kept because the record's own kinds
    /// are fewer: a client lays the item out as that kind so devices bound to it keep sampling. Absent on items Build made.
    /// </summary>
    public string? SceneKind { get; set; }
}

public sealed class PcSetupDetected
{
    public string Key { get; set; } = "";
    public bool? Hidden { get; set; }
}

/// <summary>Footprint-center position and yaw, see <see cref="PcSetup"/> for units and direction.</summary>
public sealed class PcSetupPlace
{
    public float X { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
    public bool? Curved { get; set; }
    public bool? Main { get; set; }

    /// <summary>Opaque to the service.</summary>
    public JsonElement? KeptSize { get; set; }
}

/// <summary>What a client's shared layout produced; the lighting engine samples LEDs from <see cref="Objects"/>.</summary>
public sealed class PcSetupResolved
{
    public List<SceneObject> Objects { get; set; } = new();

    /// <summary>Opaque to the service.</summary>
    public JsonElement? CaseLayout { get; set; }
}

/// <summary>The file: a revision that increments on every save, and the record.</summary>
public sealed class PcSetupFile
{
    public int Rev { get; set; }
    public PcSetup Setup { get; set; } = new();
}
