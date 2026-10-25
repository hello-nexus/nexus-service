using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexus.Service.Lighting.Scene;

/// <summary>A rejected record: a stable <see cref="Code"/> for clients and a readable <see cref="Message"/>.</summary>
public sealed record PcSetupError(string Code, string Message);

/// <summary>Bounds what a request can store in the record, so a crafted body cannot bloat the file or feed the engine non-finite numbers.</summary>
public static partial class PcSetupValidation
{
    // Resolved objects feed the scene, which caps at SceneValidation.MaxObjects; the case is one of them.
    public const int MaxItems = SceneValidation.MaxObjects - 1;
    public const int MaxKind = 32;
    public const int MaxName = 80;
    public const int MaxTitle = 200;
    public const int MaxKey = 128;
    public const int MaxCategory = 32;
    public const int MaxPartId = 64;
    public const int MaxOpaqueParts = 96 * 1024;
    public const int MaxOpaqueSpecs = 2 * 1024;
    public const int MaxOpaqueCaseLayout = 64 * 1024;
    public const int MaxOpaqueKeptSize = 256;
    public const int MaxRecordBytes = 224 * 1024;
    public const string CaseId = "case";
    private const float MaxExtentMm = 100_000f;
    private const float MaxPrice = 1_000_000_000f;

    [GeneratedRegex("^[a-z0-9_-]{1,32}$")]
    private static partial Regex KindPattern();

    [GeneratedRegex("^[a-z-]{1,32}$")]
    private static partial Regex MountPattern();

    /// <summary>Null when <paramref name="mount"/> is absent or a valid radiator face name.</summary>
    public static PcSetupError? RadiatorMount(string? mount) =>
        mount is null || MountPattern().IsMatch(mount) ? null : new("bad_radiator_mount", "radiatorMount must be 1 to 32 characters of a-z and -");

    public static PcSetupError? Validate(PcSetup? setup)
    {
        if (setup is null)
        {
            return new("setup_required", "setup is required");
        }
        if (setup.Version != 1)
        {
            return new("unsupported_version", "setup version must be 1");
        }
        if (setup.Items is null || setup.Case is null || setup.Resolved is null)
        {
            return new("setup_incomplete", "case, items and resolved are required");
        }
        if (setup.Items.Count > MaxItems)
        {
            return new("too_many_items", $"at most {MaxItems} items");
        }
        if (Place(setup.Case.Place, "case") is { } casePlace)
        {
            return casePlace;
        }
        if (RadiatorMount(setup.Case.RadiatorMount) is { } mountError)
        {
            return mountError;
        }
        if (Opaque(setup.Parts, MaxOpaqueParts, "parts") is { } parts)
        {
            return parts;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal) { CaseId };
        foreach (var item in setup.Items)
        {
            if (Item(item, ids) is { } itemError)
            {
                return itemError;
            }
        }
        return Resolved(setup.Resolved)
            ?? (Size(setup) > MaxRecordBytes ? new("record_too_large", $"the record is over {MaxRecordBytes / 1024} KiB") : null);
    }

    /// <summary>Validates a resolved block alone (the placements PATCH replaces it without touching items).</summary>
    public static PcSetupError? Resolved(PcSetupResolved? resolved)
    {
        if (resolved?.Objects is null)
        {
            return new("resolved_required", "resolved.objects is required");
        }
        foreach (var o in resolved.Objects)
        {
            if (o is not null && SceneValidation.ValidateCurve(o) is { } curveError)
            {
                return new("invalid_curve", curveError);
            }
        }
        if (SceneValidation.Validate(resolved.Objects, new List<SceneBinding>()) is { } sceneError)
        {
            return new("invalid_resolved", sceneError);
        }
        return Opaque(resolved.CaseLayout, MaxOpaqueCaseLayout, "resolved.caseLayout");
    }

    /// <summary>The scene kinds an item can keep from the lighting scene it migrated from.</summary>
    private static readonly HashSet<string> SceneKinds = new(StringComparer.Ordinal)
    {
        "keyboard", "mouse", "mousepad", "deskmat", "headset", "monitor", "speaker", "strip", "light", "box",
    };

    public static bool IsSceneKind(string? kind) => kind is not null && SceneKinds.Contains(kind);

    public static bool IsFinitePlace(float x, float z, float yaw) =>
        float.IsFinite(x) && float.IsFinite(z) && float.IsFinite(yaw) && MathF.Abs(x) <= MaxExtentMm && MathF.Abs(z) <= MaxExtentMm;

    public static int Size(PcSetup setup) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(setup, Nexus.Service.Serialization.PersistenceJsonContext.Default.PcSetup));

    private static PcSetupError? Item(PcSetupItem? item, HashSet<string> ids)
    {
        if (item is null || !SceneValidation.IsValidId(item.Id))
        {
            return new("invalid_item", "each item needs an id of 1 to 64 letters, digits or . _ : -");
        }
        if (!ids.Add(item.Id))
        {
            return new("duplicate_id", $"item id {item.Id} is used twice");
        }
        if (!KindPattern().IsMatch(item.Kind ?? ""))
        {
            return new("invalid_item", $"item {item.Id}: bad kind");
        }
        if (item.Replaces is { Length: > MaxPartId })
        {
            return Long(item.Id, "replaces");
        }
        if (item.Catalog is { } c)
        {
            if (c.Category is null || c.PartId is null || c.Title is null)
            {
                return new("invalid_item", $"item {item.Id}: catalog needs category, partId and title");
            }
            if (c.Category.Length > MaxCategory || c.PartId.Length > MaxPartId || c.Title.Length > MaxTitle)
            {
                return Long(item.Id, "catalog");
            }
            if (Opaque(c.Specs, MaxOpaqueSpecs, $"item {item.Id} catalog.specs") is { } specs)
            {
                return specs;
            }
        }
        if (item.Custom is { } custom)
        {
            if (custom.Name is null || custom.Name.Length > MaxName)
            {
                return custom.Name is null ? new("invalid_item", $"item {item.Id}: custom needs a name") : Long(item.Id, "custom.name");
            }
            if (custom.SceneKind is not null && !IsSceneKind(custom.SceneKind))
            {
                return new("invalid_item", $"item {item.Id}: unknown sceneKind");
            }
            if (!Dimension(custom.WidthMm) || !Dimension(custom.DepthMm))
            {
                return new("non_finite_number", $"item {item.Id}: width and depth must be finite and positive");
            }
            if (custom.HeightMm is { } height && !IsValidHeight(height))
            {
                return new("non_finite_number", $"item {item.Id}: height must be finite and positive");
            }
            if (custom.CurveRadiusMm is { } radius && !IsValidCurveRadius(radius))
            {
                return new("invalid_curve", $"item {item.Id}: curve radius is out of range");
            }
            if (custom.Shape is not null && custom.Shape is not ("straight" or "l"))
            {
                return new("invalid_item", $"item {item.Id}: shape must be straight or l");
            }
            if (custom.ReturnSide is not null && custom.ReturnSide is not ("left" or "right"))
            {
                return new("invalid_item", $"item {item.Id}: returnSide must be left or right");
            }
            if (!Dimension(custom.ReturnWidthMm) || !Dimension(custom.ReturnDepthMm))
            {
                return new("non_finite_number", $"item {item.Id}: return width and depth must be finite and positive");
            }
            if (custom.Price is { } price && (!float.IsFinite(price) || price < 0f || price > MaxPrice))
            {
                return new("non_finite_number", $"item {item.Id}: price must be finite and not negative");
            }
        }
        if (item.Detected is { } d)
        {
            if (string.IsNullOrEmpty(d.Key) || d.Key.Length > MaxKey)
            {
                return Long(item.Id, "detected.key");
            }
        }
        return Place(item.Place, $"item {item.Id}");
    }

    private static PcSetupError? Place(PcSetupPlace? place, string owner)
    {
        if (place is null)
        {
            return null;
        }
        if (!IsFinitePlace(place.X, place.Z, place.Yaw))
        {
            return new("non_finite_number", $"{owner}: place needs finite x, z and yaw within {MaxExtentMm} mm");
        }
        return Opaque(place.KeptSize, MaxOpaqueKeptSize, $"{owner} place.keptSize");
    }

    private static PcSetupError? Opaque(JsonElement? value, int maxBytes, string what)
    {
        if (value is not { } el || el.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }
        return Encoding.UTF8.GetByteCount(el.GetRawText()) > maxBytes
            ? new("value_too_large", $"{what} is over {maxBytes} bytes")
            : null;
    }

    /// <summary>The bounds <see cref="Validate"/> applies to a custom item's height.</summary>
    public static bool IsValidHeight(float v) => float.IsFinite(v) && v > 0f && v <= MaxExtentMm;

    /// <summary>The bounds <see cref="Validate"/> applies to a custom item's screen radius.</summary>
    public static bool IsValidCurveRadius(float v) =>
        float.IsFinite(v) && v >= SceneValidation.MinCurveRadiusMm && v <= SceneValidation.MaxCurveRadiusMm;

    private static bool Dimension(float? v) => v is null || (float.IsFinite(v.Value) && v.Value > 0f && v.Value <= MaxExtentMm);

    private static PcSetupError Long(string id, string field) => new("string_too_long", $"{id}: {field} is too long");
}
