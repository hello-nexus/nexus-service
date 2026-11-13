using System;
using System.Collections.Generic;

namespace Nexus.Service.Persistence;

/// <summary>One onboard layer Nexus created on the Galleon 100 SD, with the exact body it last wrote so a later run can tell it from a layer someone else edited.</summary>
public sealed class GalleonOwnedLayer
{
    public int Id { get; set; }
    /// <summary>The body last confirmed written; empty for a layer created but not yet written.</summary>
    public string BodyB64 { get; set; } = "";
    /// <summary>A body about to be written, persisted before the write so a crash mid-write still leaves the layer recognizable as ours.</summary>
    public string PendingB64 { get; set; } = "";
}

/// <summary>
/// Onboard lighting state for one keyboard serial. The backup half is the raw bytes read before Nexus
/// wrote anything and is kept after an undo; the owned half is cleared by an undo. ProfileRes and ListRes
/// also bind the owned layers to the profile whose list references them.
/// </summary>
public sealed class GalleonOnboardRecord
{
    public int ProfileRes { get; set; }
    public string ProfileB64 { get; set; } = "";
    public int ListRes { get; set; }
    public string ListB64 { get; set; } = "";
    public string RegistryB64 { get; set; } = "";

    public List<GalleonOwnedLayer> Owned { get; set; } = new();
    /// <summary>The layer list bytes Nexus last wrote.</summary>
    public string WrittenListB64 { get; set; } = "";

    public bool HasBackup() => ProfileB64.Length > 0 && ListB64.Length > 0 && RegistryB64.Length > 0;

    public GalleonOnboardRecord Clone()
    {
        var copy = (GalleonOnboardRecord)MemberwiseClone();
        copy.Owned = new List<GalleonOwnedLayer>(Owned.Count);
        foreach (var o in Owned) copy.Owned.Add(new GalleonOwnedLayer { Id = o.Id, BodyB64 = o.BodyB64, PendingB64 = o.PendingB64 });
        return copy;
    }
}
