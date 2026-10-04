using System;
using System.Collections.Generic;
using System.Numerics;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Scene;

/// <summary>A device LED map laid on a surface in world space: a map point (u, v) sits at Center + (u - 0.5) * AxisU + (v - 0.5) * AxisV.</summary>
public readonly record struct SceneQuad(Vector3 Center, Vector3 AxisU, Vector3 AxisV)
{
    public Vector3 At(float u, float v) => Center + (u - 0.5f) * AxisU + (v - 0.5f) * AxisV;
}

/// <summary>
/// A perspective camera matching three.js PerspectiveCamera.lookAt with up +Y,
/// projecting onto the lighting canvas (the engine's logical width x height).
/// The web editor draws the same view, so both sides must stay in step; the
/// shared projection vectors test pins it.
/// </summary>
public readonly struct SceneCameraBasis
{
    public const float Near = 1f;

    public Vector3 Position { get; }
    public Vector3 Right { get; }
    public Vector3 Up { get; }
    public Vector3 Forward { get; }
    // 1 / tan(fovY / 2).
    public float Focal { get; }
    public float Aspect { get; }
    public float CanvasW { get; }
    public float CanvasH { get; }

    public SceneCameraBasis(Vector3 position, Vector3 target, float fovDegrees, float canvasW, float canvasH)
    {
        Position = position;
        CanvasW = canvasW;
        CanvasH = canvasH;
        Aspect = canvasW / canvasH;
        Focal = 1f / MathF.Tan(Math.Clamp(fovDegrees, 1f, 170f) * (MathF.PI / 360f));
        var forward = target - position;
        if (forward.LengthSquared() < 1e-12f)
        {
            forward = -Vector3.UnitZ;
        }
        forward = Vector3.Normalize(forward);
        var right = Vector3.Cross(forward, Vector3.UnitY);
        if (right.LengthSquared() < 1e-12f)
        {
            // Straight up or down: three.js nudges the view axis off +Y the same way.
            var z = -forward;
            z.Z += 0.0001f;
            forward = -Vector3.Normalize(z);
            right = Vector3.Cross(forward, Vector3.UnitY);
        }
        Right = Vector3.Normalize(right);
        Up = Vector3.Cross(Right, forward);
        Forward = forward;
    }

    /// <summary>Canvas-unit position of a world point; false when it sits behind the camera.</summary>
    public bool Project(Vector3 world, out float x, out float y)
    {
        var d = world - Position;
        var depth = Vector3.Dot(d, Forward);
        if (depth < Near)
        {
            x = 0f;
            y = 0f;
            return false;
        }
        var ndcX = Focal / Aspect * Vector3.Dot(d, Right) / depth;
        var ndcY = Focal * Vector3.Dot(d, Up) / depth;
        x = (ndcX * 0.5f + 0.5f) * CanvasW;
        y = (0.5f - ndcY * 0.5f) * CanvasH;
        return true;
    }
}

/// <summary>Everything the engine needs to sample placed devices: the camera and each placed device's surfaces, keyed by device id.</summary>
public sealed class SceneProjection
{
    public SceneProjection(SceneCameraBasis camera, IReadOnlyDictionary<string, SceneQuad[]> placements)
    {
        Camera = camera;
        Placements = placements;
    }

    public SceneCameraBasis Camera { get; }
    public IReadOnlyDictionary<string, SceneQuad[]> Placements { get; }
}

public static class SceneMath
{
    public static Vector3 Vec(float[]? v) =>
        v is { Length: 3 } ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;

    /// <summary>Object-local direction to world; yaw turns counter-clockwise seen from above, as three.js rotation.y.</summary>
    public static Vector3 RotateYaw(Vector3 v, float yawDegrees)
    {
        var r = yawDegrees * (MathF.PI / 180f);
        var c = MathF.Cos(r);
        var s = MathF.Sin(r);
        return new Vector3(c * v.X + s * v.Z, v.Y, -s * v.X + c * v.Z);
    }

    /// <summary>The world-space surface a device's map lies on for one anchor, after the binding's turn and mirror.</summary>
    public static SceneQuad AnchorQuad(SceneObject obj, SceneAnchor anchor, int rotation, bool flip)
    {
        var right = SafeNormalize(Vec(anchor.Right), Vector3.UnitX);
        var down = -SafeNormalize(Vec(anchor.Up), Vector3.UnitY);
        var turn = (((rotation % 360) + 360) % 360) / 90;
        // Clockwise as seen facing the surface: u's direction swings toward v's.
        var (e1, e2) = turn switch
        {
            1 => (down, -right),
            2 => (-right, -down),
            3 => (-down, right),
            _ => (right, down),
        };
        // A quarter turn swaps which edge each axis spans, so the map still fills the surface.
        var lenU = turn % 2 == 0 ? anchor.Width : anchor.Height;
        var lenV = turn % 2 == 0 ? anchor.Height : anchor.Width;
        var axisU = e1 * lenU;
        if (flip)
        {
            axisU = -axisU;
        }
        var axisV = e2 * lenV;
        var center = RotateYaw(Vec(anchor.Center), obj.Yaw) + Vec(obj.Position);
        return new SceneQuad(center, RotateYaw(axisU, obj.Yaw), RotateYaw(axisV, obj.Yaw));
    }

    /// <summary>Every bound device's surfaces in target order; bindings whose targets all miss are left out.</summary>
    public static Dictionary<string, SceneQuad[]> Placements(LightingSceneDoc doc)
    {
        var objects = new Dictionary<string, SceneObject>(StringComparer.Ordinal);
        foreach (var o in doc.Objects)
        {
            objects[o.Id] = o;
        }
        var result = new Dictionary<string, SceneQuad[]>(StringComparer.Ordinal);
        foreach (var binding in doc.Bindings)
        {
            var quads = new List<SceneQuad>(binding.Targets.Count);
            foreach (var target in binding.Targets)
            {
                if (!objects.TryGetValue(target.ObjectId, out var obj))
                {
                    continue;
                }
                var anchor = obj.Anchors.Find(a => a.Id == target.AnchorId);
                if (anchor is null)
                {
                    continue;
                }
                quads.Add(AnchorQuad(obj, anchor, binding.Rotation, binding.Flip));
            }
            if (quads.Count > 0)
            {
                result[binding.DeviceId] = quads.ToArray();
            }
        }
        return result;
    }

    /// <summary>Null when the view is off or has no camera yet.</summary>
    public static SceneCameraBasis? Camera(SceneView? view, float canvasW, float canvasH)
    {
        if (view is not { Enabled: true, Camera: { } cam })
        {
            return null;
        }
        return new SceneCameraBasis(Vec(cam.Position), Vec(cam.Target), cam.Fov, canvasW, canvasH);
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback) =>
        v.LengthSquared() < 1e-12f ? fallback : Vector3.Normalize(v);
}
