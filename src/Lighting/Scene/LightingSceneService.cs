using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Scene;

/// <summary>
/// Keeps the engine's scene projection in step with the scene document and the
/// live 3D view. The view lives in settings (so presets and profiles carry it);
/// a camera draft from an editor drag overrides it in memory until the next
/// committed view.
/// </summary>
public sealed class LightingSceneService : IHostedService
{
    public const float CanvasW = 1000f;
    public const float CanvasH = 600f;

    private readonly IConfigStore _config;
    private readonly LightingSceneStore _store;
    private readonly LightingEngine _engine;
    private readonly object _lock = new();
    private SceneCamera? _draft;
    private string _appliedViewSig = "";

    public LightingSceneService(IConfigStore config, LightingSceneStore store, LightingEngine engine)
    {
        _config = config;
        _store = store;
        _engine = engine;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _config.OnChanged += OnConfigChanged;
        _store.Changed += Apply;
        Apply();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _config.OnChanged -= OnConfigChanged;
        _store.Changed -= Apply;
        return Task.CompletedTask;
    }

    /// <summary>Points the engine at a camera without persisting it; the next committed view clears it.</summary>
    public void SetDraftCamera(SceneCamera camera)
    {
        lock (_lock)
        {
            _draft = camera;
        }
        Apply();
    }

    public void ClearDraft()
    {
        lock (_lock)
        {
            _draft = null;
        }
        Apply();
    }

    /// <summary>Rebuilds the engine projection from the stored scene and the current view.</summary>
    public void Apply()
    {
        lock (_lock)
        {
            var saved = _config.Load().Lighting.SceneView;
            _appliedViewSig = Signature(saved);
            var view = _draft is null || !saved.Enabled
                ? saved
                : new SceneView { Enabled = true, Camera = _draft };
            var camera = SceneMath.Camera(view, CanvasW, CanvasH);
            if (camera is null)
            {
                _engine.SetScene(null);
                return;
            }
            var placements = SceneMath.Placements(_store.Load());
            _engine.SetScene(placements.Count == 0 ? null : new SceneProjection(camera.Value, placements));
        }
    }

    // Every settings write lands here; only a change to the view needs a rebuild.
    private void OnConfigChanged()
    {
        string sig;
        lock (_lock)
        {
            sig = Signature(_config.Load().Lighting.SceneView);
            if (sig == _appliedViewSig)
            {
                return;
            }
            _draft = null;
        }
        Apply();
    }

    private static string Signature(SceneView v)
    {
        if (v.Camera is not { } c)
        {
            return v.Enabled ? "on" : "off";
        }
        return FormattableString.Invariant(
            $"{v.Enabled}|{string.Join(',', c.Position)}|{string.Join(',', c.Target)}|{c.Fov}");
    }
}
