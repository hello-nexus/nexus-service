using System.Collections.Generic;

namespace Nexus.Service.Lighting.Scene;

/// <summary>Builds the first record from a lighting scene that predates it. Pure: never touches the scene file.</summary>
public static class PcSetupMigration
{
    /// <summary>Null when the scene has no objects, so there is nothing to carry over.</summary>
    public static PcSetup? FromScene(LightingSceneDoc doc)
    {
        if (doc.Objects.Count == 0)
        {
            return null;
        }
        var setup = new PcSetup();
        var caseSeen = false;
        foreach (var o in doc.Objects)
        {
            if (o.Kind == "case")
            {
                if (!caseSeen)
                {
                    caseSeen = true;
                    setup.Case.Place = PlaceOf(o);
                }
                continue;
            }
            if (o.Source != "user")
            {
                continue;
            }
            setup.Items.Add(new PcSetupItem
            {
                Id = o.Id,
                Kind = MapKind(o.Kind),
                Custom = new PcSetupCustom
                {
                    Name = string.IsNullOrWhiteSpace(o.Label) ? o.Kind : o.Label!,
                    WidthMm = o.Size.Length == 3 && o.Size[0] > 0f ? o.Size[0] : null,
                    DepthMm = o.Size.Length == 3 && o.Size[2] > 0f ? o.Size[2] : null,
                },
                Owned = true,
                Place = PlaceOf(o),
            });
        }
        setup.Resolved.Objects = new List<SceneObject>(doc.Objects);
        return setup;
    }

    /// <summary>The scene's kinds on the record's kind set.</summary>
    public static string MapKind(string kind) => kind switch
    {
        "headset" => "headphones",
        "deskmat" or "strip" or "light" or "box" => "other",
        _ => kind,
    };

    private static PcSetupPlace PlaceOf(SceneObject o) => new()
    {
        X = o.Position.Length == 3 ? o.Position[0] : 0f,
        Z = o.Position.Length == 3 ? o.Position[2] : 0f,
        Yaw = o.Yaw,
    };
}
