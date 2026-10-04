using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Scene;

/// <summary>Bounds what a request can store in the scene, so a crafted body cannot bloat the file or feed the engine non-finite numbers.</summary>
public static partial class SceneValidation
{
    public const int MaxObjects = 64;
    public const int MaxAnchorsPerObject = 128;
    public const int MaxBindings = 512;
    public const int MaxTargetsPerBinding = 16;
    public const int MaxModelBytes = 16 * 1024 * 1024;
    private const float MaxExtentMm = 100_000f;
    private const int MaxLabel = 80;

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,64}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[a-z0-9_-]{1,32}$")]
    private static partial Regex KindPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>Null when valid, else the first problem. Normalises rotations to quarter turns and trims labels in place.</summary>
    public static string? Validate(List<SceneObject>? objects, List<SceneBinding>? bindings)
    {
        if (objects is null || bindings is null)
        {
            return "objects and bindings are required";
        }
        if (objects.Count > MaxObjects)
        {
            return $"at most {MaxObjects} objects";
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in objects)
        {
            if (o is null || !IsValidId(o.Id) || !ids.Add(o.Id))
            {
                return "each object needs a unique id";
            }
            if (!KindPattern().IsMatch(o.Kind ?? "") || (o.Source != "user" && o.Source != "build"))
            {
                return $"object {o.Id}: bad kind or source";
            }
            // Labels are display text (a catalog title can run long), so they are trimmed rather than refused.
            o.Label = Trim(o.Label);
            if (!Point(o.Position) || !Finite(o.Yaw) || !Extent(o.Size))
            {
                return $"object {o.Id}: bad position, yaw or size";
            }
            if (o.Anchors is null || o.Anchors.Count > MaxAnchorsPerObject)
            {
                return $"object {o.Id}: at most {MaxAnchorsPerObject} anchors";
            }
            var anchorIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in o.Anchors)
            {
                if (a is null || !IsValidId(a.Id) || !anchorIds.Add(a.Id))
                {
                    return $"object {o.Id}: each anchor needs a unique id";
                }
                if (!KindPattern().IsMatch(a.Kind ?? "") || (a.Shape != "ring" && a.Shape != "rect"))
                {
                    return $"anchor {a.Id}: bad kind or shape";
                }
                a.Label = Trim(a.Label);
                if (!Point(a.Center) || !Direction(a.Right) || !Direction(a.Up)
                    || !Positive(a.Width) || !Positive(a.Height))
                {
                    return $"anchor {a.Id}: bad geometry";
                }
            }
        }
        if (bindings.Count > MaxBindings)
        {
            return $"at most {MaxBindings} bindings";
        }
        var devices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in bindings)
        {
            if (b is null || string.IsNullOrEmpty(b.DeviceId) || b.DeviceId.Length > 256 || !devices.Add(b.DeviceId))
            {
                return "each binding needs a unique device id";
            }
            if (b.Targets is null || b.Targets.Count is 0 or > MaxTargetsPerBinding)
            {
                return $"binding {b.DeviceId}: 1 to {MaxTargetsPerBinding} targets";
            }
            foreach (var t in b.Targets)
            {
                if (t is null || !IsValidId(t.ObjectId) || !IsValidId(t.AnchorId))
                {
                    return $"binding {b.DeviceId}: bad target";
                }
            }
            b.Rotation = (int)(MathF.Round(((b.Rotation % 360) + 360) % 360 / 90f) * 90) % 360;
        }
        return null;
    }

    public static string? ValidateCamera(SceneCamera? camera)
    {
        if (camera is null)
        {
            return "camera is required";
        }
        if (!Point(camera.Position) || !Point(camera.Target) || !Finite(camera.Fov) || camera.Fov < 5f || camera.Fov > 120f)
        {
            return "camera needs finite position and target and a fov between 5 and 120 degrees";
        }
        return null;
    }

    /// <summary>
    /// Null when the bytes are a scene model: a JSON object with version 1 and a
    /// <c>shapes</c> object keyed by object id. Fills <paramref name="shapeIds"/>
    /// with those ids; the shapes themselves are drawn by the dashboard only.
    /// </summary>
    public static string? ValidateModel(byte[] json, HashSet<string> shapeIds)
    {
        if (json.Length == 0 || json.Length > MaxModelBytes)
        {
            return "model must be JSON under the size cap";
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetDouble() != 1
                || !root.TryGetProperty("shapes", out var shapes) || shapes.ValueKind != JsonValueKind.Object)
            {
                return "model needs version 1 and a shapes object";
            }
            foreach (var shape in shapes.EnumerateObject())
            {
                if (!IsValidId(shape.Name) || shape.Value.ValueKind != JsonValueKind.Object)
                {
                    return "model shapes must be objects keyed by object id";
                }
                if (!shapeIds.Add(shape.Name) || shapeIds.Count > MaxObjects)
                {
                    return $"model has at most {MaxObjects} distinct shapes";
                }
            }
        }
        catch (JsonException)
        {
            return "model is not valid JSON";
        }
        return null;
    }

    private static string? Trim(string? label) => label is { Length: > MaxLabel } ? label[..MaxLabel] : label;

    private static bool Finite(float v) => float.IsFinite(v);

    private static bool Point(float[]? v) =>
        v is { Length: 3 } && Array.TrueForAll(v, x => float.IsFinite(x) && MathF.Abs(x) <= MaxExtentMm);

    private static bool Extent(float[]? v) => v is { Length: 3 } && Array.TrueForAll(v, Positive);

    private static bool Positive(float v) => float.IsFinite(v) && v > 0f && v <= MaxExtentMm;

    private static bool Direction(float[]? v) =>
        Point(v) && v![0] * v[0] + v[1] * v[1] + v[2] * v[2] > 1e-6f;
}
