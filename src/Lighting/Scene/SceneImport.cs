using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Scene;

public static class SceneImport
{
    /// <summary>
    /// Swaps the build-sourced objects for a fresh export. An object the user
    /// already moved keeps its place on the desk, hand-placed objects stay, and
    /// binding targets that pointed at an anchor the new export lacks are
    /// dropped (a binding left with none is removed). A first imported case
    /// replaces a hand-placed generic one, taking over its desk spot and every
    /// placement on a slot key both share.
    /// </summary>
    public static LightingSceneDoc Merge(LightingSceneDoc doc, List<SceneObject> imported, bool hasModel)
    {
        var importedCase = imported.Find(o => o is not null && o.Kind == "case");
        var retarget = new Dictionary<string, string>(StringComparer.Ordinal);
        SceneObject? genericCase = null;
        // Only the first import takes a generic case over; one added by hand next to a Build case stays.
        if (importedCase is not null && !doc.Objects.Exists(o => o.Source == "build" && o.Kind == "case"))
        {
            foreach (var o in doc.Objects)
            {
                if (o.Kind == "case" && o.Source == "user")
                {
                    genericCase ??= o;
                    retarget[o.Id] = importedCase.Id;
                }
            }
        }

        var previous = new Dictionary<string, SceneObject>(StringComparer.Ordinal);
        foreach (var o in doc.Objects)
        {
            if (o.Source == "build")
            {
                previous[o.Id] = o;
            }
        }
        var objects = doc.Objects.FindAll(o => o.Source != "build" && !retarget.ContainsKey(o.Id));
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
            else if (o == importedCase && genericCase is not null)
            {
                o.Position = genericCase.Position;
                o.Yaw = genericCase.Yaw;
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
            foreach (var t in b.Targets)
            {
                if (retarget.TryGetValue(t.ObjectId, out var to))
                {
                    t.ObjectId = to;
                }
            }
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
