using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Scene;

public static class SceneImport
{
    /// <summary>
    /// Swaps the build-sourced objects for a fresh export, positions included:
    /// Build reads the scene's positions before it edits them, so its export is
    /// the newest. Binding targets that pointed at an anchor the new export lacks
    /// are dropped (a binding left with none is removed). <paramref name="shapeIds"/>
    /// names the objects the imported model draws.
    /// </summary>
    public static LightingSceneDoc Merge(LightingSceneDoc doc, List<SceneObject> imported, IReadOnlySet<string> shapeIds)
    {
        var objects = doc.Objects.FindAll(o => o.Source != "build");
        foreach (var o in imported)
        {
            if (o is null)
            {
                continue;
            }
            o.Source = "build";
            o.HasModel = shapeIds.Contains(o.Id);
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
