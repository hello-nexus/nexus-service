using System;
using System.Collections.Generic;
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
    private readonly IConfigStore _config;
    private readonly LightingSceneStore _store;
    private readonly LightingEngine _engine;
    private readonly object _lock = new();
    private SceneCamera? _draft;
    // Drafts and commits from one editor stream over separate requests; a draft
    // numbered at or below that editor's last commit arrived late and is
    // dropped. Numbers are per editor session, so clocks never get compared.
    private readonly Dictionary<string, long> _lastCommitBySession = new(StringComparer.Ordinal);
    private const int MaxTrackedSessions = 32;
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

    /// <summary>Points the engine at a camera without persisting it; false when the draft came in after its editor's newer commit.</summary>
    public bool SetDraftCamera(SceneCamera camera, string? session = null, long? seq = null)
    {
        lock (_lock)
        {
            if (session is not null && seq is { } s
                && _lastCommitBySession.TryGetValue(session, out var committed) && s <= committed)
            {
                return false;
            }
            _draft = camera;
        }
        Apply();
        return true;
    }

    /// <summary>Ends any draft; a numbered commit also refuses its editor's drafts numbered at or below it.</summary>
    public void ClearDraft(string? session = null, long? seq = null)
    {
        lock (_lock)
        {
            _draft = null;
            if (session is not null && seq is { } s)
            {
                if (_lastCommitBySession.Count >= MaxTrackedSessions && !_lastCommitBySession.ContainsKey(session))
                {
                    _lastCommitBySession.Clear();
                }
                _lastCommitBySession[session] = Math.Max(s, _lastCommitBySession.GetValueOrDefault(session, long.MinValue));
            }
        }
        Apply();
    }

    /// <summary>Rebuilds the engine projection from the stored scene and the current view.</summary>
    public void Apply()
    {
        lock (_lock)
        {
            try
            {
                var saved = _config.Load().Lighting.SceneView ?? new SceneView();
                _appliedViewSig = Signature(saved);
                var view = _draft is null || !saved.Enabled
                    ? saved
                    : new SceneView { Enabled = true, Camera = _draft };
                // A view read from a shared or hand-edited profile is not trusted to be well formed.
                var camera = SceneValidation.ValidateCamera(view.Camera) is null
                    ? SceneMath.Camera(view, LightingEngine.CanvasUnitsW, LightingEngine.CanvasUnitsH)
                    : null;
                if (camera is null)
                {
                    _engine.SetScene(null);
                    return;
                }
                var placements = SceneMath.Placements(_store.Load());
                _engine.SetScene(placements.Count == 0 ? null : new SceneProjection(camera.Value, placements));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[lighting-scene] apply failed: {ex.GetType().Name}: {ex.Message}");
                _engine.SetScene(null);
            }
        }
    }

    // Every settings write lands here; only a change to the view needs a rebuild.
    private void OnConfigChanged()
    {
        lock (_lock)
        {
            if (Signature(_config.Load().Lighting.SceneView) == _appliedViewSig)
            {
                return;
            }
            _draft = null;
        }
        Apply();
    }

    private static string Signature(SceneView? v)
    {
        if (v?.Camera is not { } c)
        {
            return v?.Enabled == true ? "on" : "off";
        }
        return FormattableString.Invariant(
            $"{v.Enabled}|{string.Join(',', c.Position ?? [])}|{string.Join(',', c.Target ?? [])}|{c.Fov}");
    }
}
