using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Scene;

public static class SceneImport
{
    /// <summary>
    /// Swaps the build-sourced objects for a fresh export. An object the user
    /// already moved keeps its place on the desk, hand-placed objects stay, and
    /// binding targets that pointed at an anchor the new export lacks are
    /// dropped (a binding left with none is removed).
    /// </summary>
    public static LightingSceneDoc Merge(LightingSceneDoc doc, List<SceneObject> imported, bool hasModel)
    {
        var previous = new Dictionary<string, SceneObject>(StringComparer.Ordinal);
        foreach (var o in doc.Objects)
        {
            if (o.Source == "build")
            {
                previous[o.Id] = o;
            }
        }
        var objects = doc.Objects.FindAll(o => o.Source != "build");
        foreach (var o in imported)
        {
            if (o is null)
            {
                continue;
            }
            o.Source = "build";
            o.HasModel = hasModel && o.Kind == "case";
            if (previous.TryGetValue(o.Id, out var before))
            {
                o.Position = before.Position;
                o.Yaw = before.Yaw;
            }
            objects.Add(o);
        }

        var anchors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in objects)
        {
            foreach (var a in o.Anchors ?? new List<SceneAnchor>())
            {
                anchors.Add(o.Id + "\n" + a.Id);
            }
        }
        var bindings = new List<SceneBinding>();
        foreach (var b in doc.Bindings)
        {
            b.Targets = b.Targets.FindAll(t => anchors.Contains(t.ObjectId + "\n" + t.AnchorId));
            if (b.Targets.Count > 0)
            {
                bindings.Add(b);
            }
        }
        return new LightingSceneDoc
        {
            Version = doc.Version,
            Objects = objects,
            Bindings = bindings,
            ModelRev = doc.ModelRev,
            CaseId = doc.CaseId,
        };
    }
}
